using System.Security.Cryptography;

namespace GbaMultiMenuBuilder.Fc;

// Identification changes the PC-authored runtime configuration, never ROM bytes.
public static class NesConfiguration
{
    public static NesRomInfo Identify(ReadOnlySpan<byte> rom,NesRomInfo info)
    {
        if(rom.Length<16)return info;
        int payload=16+((rom[6]&4)!=0?512:0);
        // Same payload exists with legacy Mapper 4 and Mapper 198 headers.
        // Board identification only: this is not a gameplay qualification.
        string? fingerprint=info.ContentCrc switch {
            0xe07721b6=>"58C018502B05859E1B8F4E2858CFF952FAC8F927D8AE2F0E641CFD8D579916A1",
            0xd09b9aa0=>"371342B9B01B369DE087A8D047F1E430789983106F0203D4EEC918EEDA2DD763",
            _=>null};
        if(!info.Nes20&&(rom[7]&15)==0&&info.PrgBytes==655360&&info.ChrBytes==0&&
            fingerprint is not null&&rom.Length==payload+655360&&
            Convert.ToHexString(SHA256.HashData(rom[payload..])).Equals(fingerprint,StringComparison.Ordinal))
            return info with {Mapper=198,Submapper=0,WorkRamBytes=4096,SaveRamBytes=8192,
                ChrRamBytes=8192,SaveChrRamBytes=0,Battery=true,Error=null,CorrectionKind=1,
                MetadataSource="Known content correction",Warning="Board corrected; gameplay unverified"};
        if(info.Error is null&&!info.Nes20&&info.Mapper==4&&info.PrgBytes==655360&&info.ChrBytes==0&&
            StartupCopiesToExpansionRam(rom,payload,info.PrgBytes))
            return info with {SuggestedMapper=198,Warning="Startup copies code to $5100 RAM: Mapper 198 candidate; confirmation required"};
        if(info.Error is null&&info.CorrectionKind==0&&!info.Nes20)
            return info with {Warning=info.Warning??(info.Mapper==4&&info.PrgBytes>524288?
                "Legacy MMC3 over 512 KiB: check mapper":"Legacy header: board not independently confirmed")};
        return info;
    }

    // Bounded static interpretation of the reset path, not a string search in
    // arbitrary ROM data. Follow only known instructions in the fixed $E000
    // bank; any branch/unknown operation stops inference. Never auto-apply it.
    private static bool StartupCopiesToExpansionRam(ReadOnlySpan<byte> rom,int payload,int prg)
    {
        if(prg<8192||rom.Length<payload+prg)return false;
        var bank=rom.Slice(payload+prg-8192,8192);int pc=bank[8188]|bank[8189]<<8;
        int a=-1,x=-1,y=-1,command=-1,selected=-1;int[] zp=Enumerable.Repeat(-1,256).ToArray();HashSet<int> visited=[];
        for(int step=0;step<64;step++)
        {
            if(pc<0xe000||pc>0xfffc||!visited.Add(pc))return false;
            int p=pc-0xe000,op=bank[p],operand=bank[p+1],address=operand|(bank[p+2]<<8);
            switch(op)
            {
                case 0x78:case 0xd8:case 0x9a:case 0xea:pc++;break;
                case 0x4c:pc=address;break;
                case 0xa9:a=operand;pc+=2;break;
                case 0xa2:x=operand;pc+=2;break;
                case 0xa0:y=operand;pc+=2;break;
                case 0x85:zp[operand]=a;pc+=2;break;
                case 0x84:zp[operand]=y;pc+=2;break;
                case 0x86:zp[operand]=x;pc+=2;break;
                case 0x8d:
                    if(address==0x8000)command=a;
                    if(address==0x8001&&command>=0&&(command&7)==6)selected=a;
                    pc+=3;break;
                case 0xb1:
                    if(bank[p+2]!=0x91||y!=0||x!=4||selected!=0x40)return false;
                    int destination=bank[p+3];
                    return zp[operand]==0&&zp[(operand+1)&255]==0x80&&zp[destination]==0&&zp[(destination+1)&255]==0x51;
                default:return false;
            }
        }
        return false;
    }

    public static bool SameGeometry(NesRomInfo a,NesRomInfo b)=>
        a.Mapper==b.Mapper&&a.Submapper==b.Submapper&&a.PrgBytes==b.PrgBytes&&a.ChrBytes==b.ChrBytes&&
        a.WorkRamBytes==b.WorkRamBytes&&a.SaveRamBytes==b.SaveRamBytes&&a.ChrRamBytes==b.ChrRamBytes&&
        a.SaveChrRamBytes==b.SaveChrRamBytes&&a.Battery==b.Battery;

    public static NesRomInfo Reidentify(FcGame game)
    {
        if(game.Md is not null||game.Info.CorrectionKind==3)return game.Info;
        var detected=NesRomHeaderService.Parse(game.Data);
        // Preserve a card override when new identification offers only the same
        // ambiguous raw header. Explicit "automatic" in the editor can reset it.
        if(game.OriginalOffset>=0&&detected.CorrectionKind==0&&!SameGeometry(game.Info,detected))
            return game.Info with {Warning="Card override retained; review configuration manually"};
        return detected;
    }

    public static NesRomInfo Defaults(byte[] data,int mapper,int sub)
    {
        if(!NesRomHeaderService.IsMapperRepresentable(mapper,sub))throw new InvalidDataException("Mapper/submapper is outside the encodable range (0–4095 / 0–15).");
        if(data.Length<16||!data.AsSpan(0,4).SequenceEqual("NES\x1a"u8))throw new InvalidDataException("Missing NES header.");
        var copy=(byte[])data.Clone();bool nes2=(copy[7]&12)==8;int defaultWork=-1,defaultSave=0,defaultChr=0;
        copy[6]=(byte)((copy[6]&15)|((mapper&15)<<4));
        copy[7]=(byte)((mapper&240)|(nes2||mapper>255||sub!=0?8:0)|(copy[7]&3));
        if((copy[7]&12)==8)
        {
            copy[8]=(byte)((sub<<4)|(mapper>>8));if(!nes2)copy[9]=0;
            bool battery=(copy[6]&2)!=0;
            int declared=mapper is 176 or 178?32768:mapper==198&&!battery?4096:8192;
            int work=battery?0:declared,save=battery?(mapper==4&&sub==1?1024:declared):0;
            if(mapper==195||mapper==198&&battery)work=Math.Max(work,12288-save);
            bool chrRom=copy[5]!=0||(copy[9]&240)!=0;
            int chrRam=!chrRom?(mapper is 176 or 268?262144:mapper==30?32768:8192):mapper is 74 or 191 or 253?2048:mapper is 165 or 192 or 195?4096:mapper is 176 or 199?8192:0;
            static int Shift(int bytes){if(bytes==0)return 0;int n=0;while((64<<n)<bytes)n++;return n;}
            copy[10]=(byte)(Shift(work)|(Shift(save)<<4));copy[11]=(byte)Shift(chrRam);
            defaultWork=work;defaultSave=save;defaultChr=chrRam;
        }
        else {copy[8]=0;copy[9]=0;copy[10]=0;copy[11]=0;}
        var info=NesRomHeaderService.Parse(copy,false);
        // Defaults for incomplete legacy boards are obtained without requiring
        // a rewritten original file. Explicit sizes can be edited next.
        return defaultWork<0||info.Error is not null?info:info with {WorkRamBytes=defaultWork,SaveRamBytes=defaultSave,ChrRamBytes=defaultChr};
    }

    public static NesRomInfo Manual(FcGame game,int mapper,int sub,int work,int save,int chrRam,int saveChr,bool battery)
    {
        var basis=Defaults(game.Data,mapper,sub);
        if(basis.Error is not null)throw new InvalidDataException(basis.Error);
        var info=basis with {WorkRamBytes=work,SaveRamBytes=save,ChrRamBytes=chrRam,SaveChrRamBytes=saveChr,
            Battery=battery,CorrectionKind=3,MetadataSource="Manual configuration",Warning=null};
        Validate(game,info);
        return info;
    }

    public static void Validate(FcGame game,NesRomInfo info)
    {
        if(!game.IsNes)throw new InvalidDataException("Choose an NES game.");
        if(!info.CanPack||!NesRomHeaderService.IsMapperRepresentable(info.Mapper,info.Submapper))
            throw new InvalidDataException(info.Error??"Mapper/submapper is outside the encodable range (0–4095 / 0–15).");
        int lead=16+((game.Data[6]&4)!=0?512:0);
        if(info.PrgBytes<=0||info.ChrBytes<0||(long)lead+info.PrgBytes+info.ChrBytes>game.Data.Length)
            throw new InvalidDataException("ROM layout exceeds the original file.");
        if(info.WorkRamBytes<0||info.SaveRamBytes<0||info.ChrRamBytes<0||info.SaveChrRamBytes<0||
            (long)info.WorkRamBytes+info.SaveRamBytes+info.SaveChrRamBytes>60*8192||
            (long)info.ChrRamBytes+info.SaveChrRamBytes>int.MaxValue||
            (info.ChrBytes==0&&info.ChrRamBytes+info.SaveChrRamBytes==0)||
            (!info.Battery&&(info.SaveRamBytes!=0||info.SaveChrRamBytes!=0)))
            throw new InvalidDataException("Invalid RAM sizes or battery setting for the MIX SRAM pool.");
        // Save migration is deliberately not implicit in a configuration edit.
        if(game.OriginalOffset>=0&&game.OriginalSaveMap.Length>0&&
            (info.Battery!=game.OriginalBattery||info.StoredRamBytes<(game.OriginalStoredRamBytes>=0?game.OriginalStoredRamBytes:game.Info.StoredRamBytes)))
            throw new InvalidDataException("This edit would shrink or change an existing save allocation. Keep its RAM size and battery setting.");
    }

    public static string Describe(NesRomInfo i)=>$"Mapper {i.Mapper}/{i.Submapper}; WRAM {i.WorkRamBytes/1024d:g} KiB; save {i.SaveRamBytes/1024d:g} KiB; CHR-RAM {(i.ChrRamBytes+i.SaveChrRamBytes)/1024d:g} KiB";
    public static string Localize(string value,Func<string,string,string> text)
    {
        string? key=value switch{
            "Known content correction"=>"metadata_known","Manual configuration"=>"metadata_manual","Card configuration"=>"metadata_card",
            "Mesen DB (validated)"=>"metadata_db","Mesen DB + mapper fallback"=>"metadata_db_fallback","iNES / mapper fallback"=>"metadata_fallback",
            "Board corrected; gameplay unverified"=>"board_unverified","Legacy MMC3 over 512 KiB: check mapper"=>"legacy_large_mmc3",
            "Legacy header: board not independently confirmed"=>"legacy_unconfirmed","Known correction available: rebuild configuration"=>"correction_available",
            "Card override retained; review configuration manually"=>"override_retained",_=>null};
        if(value=="Startup copies code to $5100 RAM: Mapper 198 candidate; confirmation required")key="startup_candidate";
        return key is null?value:text(key,value);
    }
}
