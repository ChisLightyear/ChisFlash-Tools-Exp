using System.Buffers.Binary;
using System.Text;
using GbaMultiMenuBuilder.Fc;
namespace GbaMultiMenuBuilder.Md;
public sealed record MdCardImage(byte[] Data, int SaveBytes, int Games);
public sealed record MdGameConfig(int Flags, int SaveBytes);
public static class MdMbcxCodec
{
    public const int Capacity=32*1024*1024, Config=0x30000, Directory=0x30040, RomStart=0x100000;
    private static uint Be32(ReadOnlySpan<byte> b)=>BinaryPrimitives.ReadUInt32BigEndian(b);
    public static MdGameConfig Describe(byte[] rom,string title)
    {
        var assessment=MdCartridgePolicy.Inspect(rom,rom.Length,title+".md");
        if(assessment is null || assessment.Excluded || rom.Length<512 || (rom.Length&1)!=0)
            throw new InvalidDataException(title+": unsupported MD ROM.");
        string id=Encoding.ASCII.GetString(rom,0x180,14).ToUpperInvariant();
        string region=Encoding.ASCII.GetString(rom,0x1f0,16).ToUpperInvariant();
        bool Match(params string[] ids)=>ids.Any(id.Contains);
        // Common headers contain J/U/E letters, full region words, or a hexadecimal bit mask.
        if(region.Contains("EUROPE"))region="E";
        else if(region.Contains("JAPAN"))region="J";
        else if(region.Contains("USA"))region="U";
        int regions=0;
        if(region.Contains('J'))regions|=1;if(region.Contains('U'))regions|=4;if(region.Contains('E'))regions|=8;
        if(regions==0 && int.TryParse(region.Trim(' ', '\0'),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out int hex))regions=hex&15;
        if(regions==0)regions=4;
        int flags=(regions&4)!=0?2:(regions&1)!=0?0:(regions&8)!=0?3:1;
        if(Match("T-081276","T-81406","T-081586","T-81576","T-81476"))flags|=4;
        if(id.Contains(" GM 0000"))flags|=8;
        if(Match("MK-1215","G-4060","00001211","MK-1228","G-5538","00004076","T-12046","T-12053","G-4524"))flags|=16;
        if(Match("T-113016"))flags|=32;
        if(assessment.Kind==MdCartridgeKind.PierSolar)flags|=64;
        if(Match("T-35036","T-25073","MK-1137-"))flags|=128;
        int bytes=0;
        if(rom[0x1b0]=='R'&&rom[0x1b1]=='A'){
            uint start=Be32(rom.AsSpan(0x1b4)),end=Be32(rom.AsSpan(0x1b8));
            if(end<start||end-start>=0x20000)throw new InvalidDataException($"{title}: 不支持此存档范围。");
            uint required=(end-start)/2+1;bytes=2;while(bytes<required)bytes*=2;
        }
        if((flags&28)!=0)bytes=Math.Max(bytes,8192);
        if((flags&64)!=0)bytes=4096;
        if((flags&32)!=0)bytes=0;

        return new(flags,bytes);
    }
    public static FcGame CreateGame(byte[] rom,string title)
    {
        var md=Describe(rom,title);
        return new FcGame {Name=title,Data=rom,Md=md,
            Info=new NesRomInfo(-1,0,rom.Length,0,0,md.SaveBytes,0,0,md.SaveBytes>0,false,0,"MD header",null)};
    }
    internal static FcGame ReadDirectoryGame(string name,int length,int offset,int allocated,int flags,int saveBytes,int saveAddress,string? identity=null)
    {
        int banks=(saveBytes+8191)/8192;
        if(length<512||(length&1)!=0||flags<0||flags>255||saveBytes<0||saveBytes>65536||(saveBytes!=0&&(saveBytes<2||(saveBytes&(saveBytes-1))!=0))||saveAddress<0||saveAddress%8192!=0||saveAddress>60*8192-banks*8192)
            throw new InvalidDataException("Invalid MD directory configuration.");
        return new(){Name=name,Md=new(flags,saveBytes),Info=new NesRomInfo(-1,0,length,0,0,saveBytes,0,0,saveBytes>0,false,0,"MD card configuration",null),OriginalOffset=offset,OriginalAllocated=allocated,OriginalRomBytes=length,OriginalBattery=saveBytes>0,OriginalStoredRamBytes=saveBytes,OriginalSaveMap=Enumerable.Range(saveAddress/8192,banks).Select(bank=>(byte)bank).ToArray(),DirectoryIdentity=identity};
    }
    public static MdCardImage Build(IReadOnlyList<MdRomCandidate> games)
    {
        if(games.Count is <1 or >99)throw new InvalidDataException("MD requires 1–99 games.");
        var rows=games.Select(g=>CreateGame(MdRomImportService.ReadRom(g),g.Title)).ToArray();
        var plan=FcMbcxCodec.Plan(rows,Capacity,false);
        byte[] data=new byte[plan.EndBytes];Array.Fill(data,(byte)255);
        FcMbcxCodec.BuildPrefix(plan.Entries).CopyTo(data,0);
        foreach(var p in plan.Entries)FcMbcxCodec.CopyStoredData(p.Game,0,data.AsSpan(p.Offset,p.AllocatedBytes));
        return new(data,rows.Sum(g=>g.Md!.SaveBytes),rows.Length);
    }
}
