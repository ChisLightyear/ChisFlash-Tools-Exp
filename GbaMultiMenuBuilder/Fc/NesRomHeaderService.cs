using System.Globalization;
using System.Reflection;

namespace GbaMultiMenuBuilder.Fc;

public sealed record NesRomInfo(int Mapper, int Submapper, int PrgBytes, int ChrBytes,
    int WorkRamBytes, int SaveRamBytes, int ChrRamBytes, int SaveChrRamBytes,
    bool Battery, bool Nes20, uint ContentCrc, string MetadataSource, string? Error)
{
    public bool Supported => Error is null;
    public byte CorrectionKind { get; init; } // 0=header/card, 1=known content, 2=database, 3=manual
    public string? Warning { get; init; }
    public int? SuggestedMapper { get; init; }
    // CHR working RAM is graphics memory, not a cartridge save allocation.
    public int StoredRamBytes => Battery ? WorkRamBytes + SaveRamBytes + SaveChrRamBytes : WorkRamBytes + SaveRamBytes;
    public int SaveBanks => (StoredRamBytes + 8191) / 8192;
}

public static class NesRomHeaderService
{
    public static bool SupportsMapper(int mapper, int sub) => mapper switch
    {
        0 => sub == 0,
        1 => sub is >= 0 and <= 5,
        2 or 3 => sub is >= 0 and <= 2,
        4 => sub is >= 0 and <= 3,
        15 or 19 or 52 or 74 or 162 or 163 or 164 or 165 or 176 or 178 or
        195 or 198 or 199 or 227 or 240 or 241 or 242 or 245 or 249 or
        252 or 253 or 268 or 519 => sub == 0,
        _ => false
    };
    private sealed record DbEntry(int Mapper,int Submapper,int Prg,int Chr,int ChrRam,int Work,int Save,bool Battery,bool Validated,string System);
    private static readonly Lazy<Dictionary<uint,DbEntry>> Database=new(LoadDatabase);
    private static Dictionary<uint,DbEntry> LoadDatabase()
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ChisFlash.Fc.MesenNesDB")
            ?? throw new InvalidOperationException("Missing embedded Mesen NES database.");
        using var reader=new StreamReader(stream);
        Dictionary<uint,DbEntry> result=[];
        while(reader.ReadLine() is { } line)
        {
            if(string.IsNullOrWhiteSpace(line)||line.StartsWith('#'))continue;
            var v=line.Split(',');if(v.Length<18)continue;
            int Num(int i)=>int.TryParse(v[i],NumberStyles.Integer,CultureInfo.InvariantCulture,out int n)?n:0;
            if(uint.TryParse(v[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint crc))
                result[crc]=new(Num(5),Num(15),Num(6)*1024,Num(7)*1024,Num(8)*1024,Num(9)*1024,Num(10)*1024,Num(11)!=0,v[15].Length>0,v[1]);
        }
        return result;
    }
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc=uint.MaxValue;
        foreach(byte b in data){crc^=b;for(int i=0;i<8;i++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}
        return ~crc;
    }
    private static int Ram(int n)=>n==0?0:64<<n;
    private static int Size(byte value,int high,int unit,bool nes2)
    {
        if(nes2&&high==15){int exponent=value>>2;if(exponent>29)throw new InvalidDataException("ROM size exceeds supported range.");return checked((1<<exponent)*((value&3)*2+1));}
        return checked(((nes2?high<<8:0)|value)*unit);
    }
    public static NesRomInfo Parse(ReadOnlySpan<byte> rom, bool identify = true)
    {
        var info=ParseHeader(rom,identify);
        return identify ? NesConfiguration.Identify(rom,info) : info;
    }
    private static NesRomInfo ParseHeader(ReadOnlySpan<byte> rom,bool identify)
    {
        int mapper=0,sub=0,prg=0,chr=0,work=0,save=0,cram=0,csave=0;
        bool battery=false,nes2=false;uint crc=0;string source="Header";byte correction=0;
        try
        {
            if(rom.Length<16||!rom[..4].SequenceEqual("NES\x1a"u8))throw new InvalidDataException("Missing NES header.");
            var h=rom[..16];nes2=(h[7]&12)==8;
            if((h[7]&12)!=0&&!nes2)throw new InvalidDataException("Unsupported/dirty NES header; repair the header first.");
            mapper=(h[6]>>4)|(h[7]&240)|(nes2?(h[8]&15)<<8:0);sub=nes2?h[8]>>4:0;
            prg=Size(h[4],h[9]&15,16384,nes2);chr=Size(h[5],h[9]>>4,8192,nes2);
            int payload=16+((h[6]&4)!=0?512:0);
            if(payload>rom.Length)throw new InvalidDataException("Truncated trainer.");
            // Matches Mesen iNesLoader: excludes header/trainer, includes remaining file bytes.
            crc=Crc32(rom[payload..]);battery=(h[6]&2)!=0;
            if(nes2)
            {
                work=Ram(h[10]&15);save=Ram(h[10]>>4);cram=Ram(h[11]&15);csave=Ram(h[11]>>4);
                battery|=save>0||csave>0;source="NES 2.0";
                if(chr==0&&cram+csave==0&&mapper is 162 or 163 or 164){cram=8192;source="NES 2.0 / fixed board CHR-RAM";}
            }
            else
            {
                int w=-1,s=-1,c=-1;source="iNES / mapper fallback";
                if(Database.Value.TryGetValue(crc,out var db))
                {
                    // A validated content match can correct an ambiguous legacy mapper,
                    // but never guess a different payload layout or override NES 2.0.
                    if(identify&&db.Validated&&db.Prg==prg&&db.Chr==chr&&SupportsMapper(db.Mapper,db.Submapper))
                    {mapper=db.Mapper;sub=db.Submapper;correction=2;}
                    source=db.Validated?"Mesen DB (validated)":"Mesen DB + mapper fallback";
                    if(db.Validated||db.Work>0)w=db.Work;
                    if(db.Validated||db.Save>0)s=db.Save;
                    if(db.Validated||db.ChrRam>0)c=db.ChrRam;
                    battery=db.Validated?db.Battery:battery||db.Battery;
                    // Unvalidated entries and conflicting payload geometry never change
                    // the board. FPGA reads the emitted PC configuration.
                    if(db.Mapper!=mapper||db.Prg!=prg||db.Chr!=chr||(db.Submapper!=sub && !(sub==0 && mapper is 2 or 3 && db.Submapper==2)))
                        { w=s=c=-1; battery=(h[6]&2)!=0; source="Header (Mesen geometry differs)"; }
                    if(db.System is "VsSystem" or "Playchoice")
                        throw new InvalidDataException("Mesen database identifies an unsupported system/timing.");
                }
                int declaredRam=h[8]==0?(mapper is 176 or 178?32768:mapper==198&&!battery?4096:8192):h[8]*8192;
                work=w<0?(battery?0:declaredRam):w;
                save=s<0?(battery?(mapper==4&&sub==1?1024:declaredRam):0):s;
                cram=c<0?(chr==0?(mapper is 176 or 268?262144:8192):mapper is 74 or 253?2048:mapper is 165 or 195?4096:mapper is 176 or 199?8192:0):c;
                if(mapper==195 || mapper==198&&battery)work=Math.Max(work,12288-save);
            }
            // FC-001/FS304/cy2000 boards have fixed external work RAM even in
            // incomplete NES2 dumps that leave both RAM size nibbles at zero.
            if(mapper is 162 or 163 or 164 && work+save==0)
            {if(battery)save=8192;else work=8192;source+=" / fixed board WRAM";}
            // A trainer needs its $7000-$71FF window even when database RAM metadata is absent.
            if((h[6]&4)!=0 && work+save<4608)
            { if(battery)save=Math.Max(save,8192-work);else work=Math.Max(work,8192-save); }
            if((h[7]&3)!=0)throw new InvalidDataException("02 supports NTSC/PAL/Dendy NES/Famicom; special consoles are not supported.");
            if(!SupportsMapper(mapper,sub))throw new InvalidDataException($"Unsupported mapper/submapper: {mapper}/{sub}. Install the matching FC core for newly supported boards.");
            // This tool stores the complete file. Runtime RAM/ROM limits belong to
            // the installed core, not to the cartridge-image parser.
            if(prg<=0)throw new InvalidDataException("NES ROM declares no PRG data.");
            if((long)payload+prg+chr>rom.Length)throw new InvalidDataException("Truncated NES ROM.");
            if(chr==0&&cram+csave<=0)throw new InvalidDataException("NES ROM declares neither CHR-ROM nor CHR-RAM.");
            return new(mapper,sub,prg,chr,work,save,cram,csave,battery,nes2,crc,source,null){CorrectionKind=correction};
        }
        catch(Exception ex) when(ex is InvalidDataException or OverflowException)
        {return new(mapper,sub,prg,chr,work,save,cram,csave,battery,nes2,crc,source,ex.Message);}
    }
}
