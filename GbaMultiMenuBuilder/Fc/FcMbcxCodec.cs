using System.Buffers.Binary;
using System.Text;
using GbaMultiMenuBuilder.Md;
using GbaMultiMenuBuilder.Snes;

namespace GbaMultiMenuBuilder.Fc;

public sealed class FcGame
{
    public MdGameConfig? Md {get;init;}
    public SnesRomInfo? Snes {get;init;}
    public bool IsNes=>Md is null&&Snes is null&&Info.Mapper>=0;
    public string Platform=>Snes is not null?"SNES":Md is not null?"MD":Info.Mapper>=0?"NES":Info.MetadataSource.ToUpperInvariant();
    public Guid Id {get;init;}=Guid.NewGuid();
    public bool Enabled {get;set;}=true;
    public string Name {get;set;}="";
    public byte[] Data {get;init;}=[];
    public int OriginalRomBytes {get;init;}
    public byte[] OriginalConfig {get;init;}=[];
    public string? DirectoryIdentity {get;init;}
    public bool HasRomData=>Data.Length>0;
    public int RomBytes=>HasRomData?Data.Length:OriginalRomBytes;
    public required NesRomInfo Info {get;set;}
    public int OriginalOffset {get;init;}=-1;
    public int OriginalAllocated {get;init;}
    public byte[] OriginalSaveMap {get;init;}=[];
    public bool OriginalBattery {get;init;}
    public int OriginalStoredRamBytes {get;init;}=-1;
    public int Mapper=>Info.Mapper;
    public string MapperDisplay=>Snes?.MappingDisplay??(Md is null?Info.Mapper.ToString():"—");
    public string HeaderMapperDisplay=>!IsNes||Data.Length<16?"—":
        (((Data[6]>>4)|(Data[7]&240)|((Data[7]&12)==8?(Data[8]&15)<<8:0))).ToString();
    public string Prg=>$"{Info.PrgBytes/1024} KiB";
    public string Chr=>IsNes?$"{Info.ChrBytes/1024} KiB":"—";
    public string Ram=>$"{Info.StoredRamBytes} B / {Info.SaveBanks*8} KiB";
    public string Battery=>Info.Battery?"Yes":"No";
    public string SaveMode=>Info.SaveBanks==0?"None":Info.Battery?"Dedicated":"Shared";
    public string SaveBankList {get;set;}="—";
    public string Metadata=>Info.MetadataSource;
    public string Compatibility=>Snes is not null&&Snes.Warning is not null?(Info.Error+" "+Snes.Warning):Info.Error??Info.Warning??"OK";
    public string Snapshot
    {
        get
        {
            if(!IsNes)return "—";
            var plan=FcSnapshotPlan.Create(Info.Mapper,Info.ChrBytes,Info.ChrRamBytes+Info.SaveChrRamBytes,Info.StoredRamBytes);
            if(!plan.Enabled)return "Unavailable";
            return plan.SpillBytes==0?$"{plan.TotalBytes/1024d:F1} KiB":$"128 + {plan.SpillBytes/1024d:F1} KiB";
        }
    }
    public int AllocatedBytes=>FcMbcxCodec.Align(RomBytes,FcMbcxCodec.RomBankBytes);
    public FcGame Copy()=>WithData(Data,DirectoryIdentity);
    public FcGame WithData(byte[] data,string? identity=null,NesRomInfo? info=null)=>new(){Id=Id,Enabled=Enabled,Name=Name,Data=data,Info=info??Info,Md=Md,Snes=Snes,OriginalOffset=OriginalOffset,OriginalAllocated=OriginalAllocated,OriginalSaveMap=OriginalSaveMap,OriginalBattery=OriginalBattery,OriginalStoredRamBytes=OriginalStoredRamBytes,OriginalRomBytes=RomBytes,OriginalConfig=OriginalConfig,DirectoryIdentity=identity??DirectoryIdentity};
}
public sealed record FcPlacement(FcGame Game,int Offset,int AllocatedBytes,byte[] SaveMap);
public sealed record FcLayout(IReadOnlyList<FcPlacement> Entries,int EndBytes,int DedicatedBanks,int SharedBanks);
public sealed record FcMetadataRange(int Address,int Bytes);
public sealed record FcCardSnapshot(string DeviceId,int Capacity,int SectorBytes,byte[] Prefix,IReadOnlyList<FcGame> Games,bool IsFc,bool Writable,string? Warning=null,IReadOnlyList<FcMetadataRange>? MetadataRanges=null);
public sealed record FcProgress(string Message,int Percent);
public interface IFcCardBackend
{
    FcCardSnapshot RebuildConfiguration(FcCardSnapshot expected,CancellationToken cancellation,IProgress<FcProgress> progress)=>throw new NotSupportedException("Configuration-only rebuild is unavailable.");
    IReadOnlyList<FcGame> LoadGames(FcCardSnapshot expected,IReadOnlyList<FcGame> games,CancellationToken cancellation,IProgress<FcProgress> progress)=>throw new NotSupportedException("ROM loading is unavailable.");
    IReadOnlyList<GameLibraryDirectory.SaveImage> ExportSaves(FcCardSnapshot expected,CancellationToken cancellation,IProgress<FcProgress> progress)=>throw new NotSupportedException("Save export is unavailable.");
    void ImportSaves(FcCardSnapshot expected,IReadOnlyList<GameLibraryDirectory.SaveImage> saves,CancellationToken cancellation,IProgress<FcProgress> progress)=>throw new NotSupportedException("Save import is unavailable.");
    FcCardSnapshot Scan(CancellationToken cancellation,IProgress<FcProgress> progress);
    FcCardSnapshot Write(FcCardSnapshot expected,IReadOnlyList<FcGame> games,bool convert,CancellationToken cancellation,IProgress<FcProgress> progress);
}

public static class FcMbcxCodec
{
    public const int RomBankBytes=16384,SaveBankBytes=8192,PrefixBytes=0x100000,CapacityBytes=0x2000000;
    public const int ConfigOffset=0x20000,DirectoryOffset=0x40000,EntriesOffset=0x40040,EntryBytes=128,MaxGames=99;
    public const int GameConfigOffset=0xc0000,GameConfigBytes=128,GameConfigSlots=2048;
    public const int TitleListOffset=0x50000,TitleDetailOffset=0x60000,TitleSlots=128,TitleBytes=512,TitleDetailBytes=1024;
    public static int Align(int n,int unit)=>checked((n+unit-1)/unit*unit);
    private static ushort U16(ReadOnlySpan<byte> b,int p)=>BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
    private static uint U32(ReadOnlySpan<byte> b,int p)=>BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
    private static void W16(Span<byte>b,int p,int n)=>BinaryPrimitives.WriteUInt16LittleEndian(b[p..],checked((ushort)n));
    private static void W32(Span<byte>b,int p,int n)=>BinaryPrimitives.WriteUInt32LittleEndian(b[p..],checked((uint)n));
    public static int FilePrefixBytes(FcGame game)=>16+((game.Data[6]&4)!=0?512:0);
    // Keep the original NES bytes losslessly, but rotate header/trainer behind
    // the payload so PRG begins exactly on a physical16KiB NOR bank boundary.
    public static void CopyStoredData(FcGame game,int offset,Span<byte> target)
    {
        if(offset<0)throw new ArgumentOutOfRangeException(nameof(offset));
        target.Fill(255);
        if(game.Md is not null||game.Snes is not null){int n=Math.Min(target.Length,Math.Max(0,game.Data.Length-offset));game.Data.AsSpan(offset,n).CopyTo(target);return;}
        int lead=FilePrefixBytes(game),body=game.Data.Length-lead;
        if(body<0)throw new InvalidDataException("Truncated NES header/trainer.");
        int count=Math.Min(target.Length,Math.Max(0,game.Data.Length-offset)),copied=0;
        if(offset<body){int n=Math.Min(count,body-offset);game.Data.AsSpan(lead+offset,n).CopyTo(target);copied=n;offset+=n;}
        if(copied<count)game.Data.AsSpan(offset-body,count-copied).CopyTo(target[copied..]);
    }
    internal static byte[] RestoreNesFile(byte[] stored,ReadOnlySpan<byte> config)
    {
        if(!config[..4].SequenceEqual("FC02"u8)||config[4]!=7)throw new InvalidDataException("Unsupported NES payload layout; rebuild with the matching tool.");
        int lead=16+((config[5]&8)!=0?512:0),body=stored.Length-lead;
        if(body<0)throw new InvalidDataException("Truncated NES payload.");
        byte[] data=new byte[stored.Length];stored.AsSpan(body,lead).CopyTo(data);stored.AsSpan(0,body).CopyTo(data.AsSpan(lead));
        if(!data.AsSpan(0,4).SequenceEqual("NES\x1a"u8))throw new InvalidDataException("Invalid preserved NES header.");
        return data;
    }
    // A type guard for the generic GB writer, not an old-format decoder.
    public static bool HasFcIdentity(ReadOnlySpan<byte> config)=>GameLibraryDirectory.IsLibrary(config)||config.Length>=36&&config[..7].SequenceEqual("MBCXCFG"u8)&&config.Slice(32,4).SequenceEqual("FC02"u8);
    public static bool IsFcHeaders(ReadOnlySpan<byte> config,ReadOnlySpan<byte> directory)
    {
        if(GameLibraryDirectory.IsLibrary(config))return true;
        if(config.Length<64||directory.Length<64)return false;
        return HasFcIdentity(config)&&directory[..7].SequenceEqual("MBCXROM"u8)&&
            config[7]==2&&directory[7]==3;
    }
    public static bool IsFcPrefix(ReadOnlySpan<byte> prefix)=>prefix.Length>=PrefixBytes&&(GameLibraryDirectory.IsLibrary(prefix.Slice(ConfigOffset,64))||IsFcHeaders(prefix.Slice(ConfigOffset,64),prefix.Slice(DirectoryOffset,64)));
    public static FcLayout Plan(IReadOnlyList<FcGame> games,int capacity,bool preserveLocations)
    {
        var rows=games.Where(g=>g.Enabled).ToList();capacity=Math.Min(capacity,CapacityBytes);
        if(rows.Any(game=>game.Snes is not null))throw new InvalidDataException(SnesRomImportService.LaunchUnavailable);
        GameLibraryDirectory.ValidateCapacity(rows);
        if(capacity<PrefixBytes)throw new InvalidDataException("NES: card capacity too small.");
        if(rows.Select(g=>g.Id).Distinct().Count()!=rows.Count)throw new InvalidDataException("Duplicate NES entry identity.");
        bool[] used=new bool[capacity/RomBankBytes];Array.Fill(used,true,0,PrefixBytes/RomBankBytes);
        Dictionary<Guid,(int Offset,int Size)> locations=[];
        foreach(var g in rows)
        {
            if(!g.Info.Supported)throw new InvalidDataException($"{g.Name}: {g.Info.Error}");
            if(g.RomBytes<16)throw new InvalidDataException("Empty NES ROM.");
            if(preserveLocations&&g.OriginalOffset>=PrefixBytes)
            {
                int bytes=g.OriginalAllocated;
                if(bytes<g.RomBytes||g.OriginalOffset%RomBankBytes!=0||bytes%RomBankBytes!=0||g.OriginalOffset>capacity-bytes)throw new InvalidDataException("Invalid existing NES ROM range.");
                for(int i=g.OriginalOffset/RomBankBytes;i<(g.OriginalOffset+bytes)/RomBankBytes;i++)
                {if(used[i])throw new InvalidDataException("Overlapping NES ROMs.");used[i]=true;}
                locations[g.Id]=(g.OriginalOffset,bytes);
            }
        }
        foreach(var g in rows.Where(g=>!locations.ContainsKey(g.Id)))
        {
            int need=g.AllocatedBytes/RomBankBytes,start=-1,run=0;
            for(int i=PrefixBytes/RomBankBytes;i<used.Length;i++)
            {run=used[i]?0:run+1;if(run==need){start=i-need+1;break;}}
            if(start<0)throw new InvalidDataException("NES: no contiguous ROM space (32 MiB maximum).");
            Array.Fill(used,true,start,need);locations[g.Id]=(start*RomBankBytes,g.AllocatedBytes);
        }
        // Same SRAM bank geometry as MBCX: 0..59 dedicated, 60..63 shared.
        bool[] saveUsed=new bool[60];Dictionary<Guid,byte[]> saves=[];int shared=0;
        if(preserveLocations)
        {
            var oldBanks=rows.Where(g=>g.OriginalBattery).SelectMany(g=>g.OriginalSaveMap).ToArray();
            if(oldBanks.Any(b=>b>=60)||oldBanks.Distinct().Count()!=oldBanks.Length)
                throw new InvalidDataException("Overlapping or invalid existing MIX save allocations.");
        }
        foreach(var g in rows)
        {
            int n=g.Info.SaveBanks;
            if(n>8||(!g.Info.Battery&&n>4))throw new InvalidDataException($"{g.Name}: SRAM bank-map capacity exceeded.");
            if(n==0){saves[g.Id]=[];continue;}
            if(!g.Info.Battery){saves[g.Id]=Enumerable.Range(60,n).Select(i=>(byte)i).ToArray();shared=Math.Max(shared,n);continue;}
            var old=g.OriginalSaveMap.Take(n).ToArray();
            // Reserve every existing NES prefix before allocating any new bank.
            // Expanding 8 -> 12 KiB must keep the original save at logical bank 0.
            if(preserveLocations&&g.OriginalBattery&&g.Md is null&&old.Length>0)
            {
                if(old.Any(b=>b>=60)||old.Distinct().Count()!=old.Length||old.Any(b=>saveUsed[b]))
                    throw new InvalidDataException("Invalid or overlapping existing NES save banks.");
                saves[g.Id]=old;foreach(byte b in old)saveUsed[b]=true;
            }
            else if(preserveLocations&&g.OriginalBattery&&old.Length==n&&old.All(b=>b<60)&&old.Distinct().Count()==n&&old.All(b=>!saveUsed[b])&&(g.Md is null||old.SequenceEqual(Enumerable.Range(old[0],n).Select(x=>(byte)x))))
            {saves[g.Id]=old;foreach(byte b in old)saveUsed[b]=true;}
        }
        foreach(var g in rows.Where(g=>saves.ContainsKey(g.Id)&&saves[g.Id].Length<g.Info.SaveBanks))
        {
            var extra=Enumerable.Range(0,60).Where(i=>!saveUsed[i]).Take(g.Info.SaveBanks-saves[g.Id].Length).Select(i=>(byte)i).ToArray();
            if(extra.Length!=g.Info.SaveBanks-saves[g.Id].Length)throw new InvalidDataException("No free SRAM bank to extend the existing save.");
            foreach(byte b in extra)saveUsed[b]=true;saves[g.Id]=saves[g.Id].Concat(extra).ToArray();
        }
        foreach(var g in rows.Where(g=>!saves.ContainsKey(g.Id)))
        {
            byte[] map;
            if(g.Md is not null){
                int n=g.Info.SaveBanks,start=Enumerable.Range(0,61-n).FirstOrDefault(i=>Enumerable.Range(i,n).All(j=>!saveUsed[j]),-1);
                map=start<0?[]:Enumerable.Range(start,n).Select(i=>(byte)i).ToArray();
            }else map=Enumerable.Range(0,60).Where(i=>!saveUsed[i]).Take(g.Info.SaveBanks).Select(i=>(byte)i).ToArray();
            if(map.Length!=g.Info.SaveBanks)throw new InvalidDataException("Dedicated SRAM exhausted (60 x 8 KiB); battery saves are never silently shared.");
            foreach(byte b in map)saveUsed[b]=true;saves[g.Id]=map;
        }
        var placements=rows.Select(g=>new FcPlacement(g,locations[g.Id].Offset,locations[g.Id].Size,saves[g.Id])).ToList();
        return new(placements,placements.Count==0?PrefixBytes:placements.Max(p=>p.Offset+p.AllocatedBytes),saveUsed.Count(v=>v),shared);
    }
    public static byte[] BuildPrefix(IReadOnlyList<FcPlacement> entries,byte[]? original=null)=>GameLibraryDirectory.Write(entries);
    public static byte[] BuildGameConfig(FcPlacement p)
    {
        var info=p.Game.Info;var h=p.Game.Data.AsSpan(0,16);
        bool trainer=(h[6]&4)!=0;int chrCapacity=info.ChrBytes==0?info.ChrRamBytes+info.SaveChrRamBytes:info.ChrBytes;
        byte[] b=new byte[GameConfigBytes];"FC02"u8.CopyTo(b);b[4]=7;
        b[5]=(byte)((h[6]&1)|((h[6]&8)>>2)|(info.Battery?4:0)|(trainer?8:0)|(info.Mapper is 252 or 268?16:0)|((info.PrgBytes&(info.PrgBytes-1))==0?32:0)|((chrCapacity&(chrCapacity-1))==0?64:0));
        b[6]=checked((byte)info.Submapper);W16(b,8,info.Mapper);b[10]=checked((byte)p.SaveMap.Length);
        int prgStart=p.Offset;
        W32(b,12,prgStart);W32(b,16,info.PrgBytes);
        W32(b,20,info.ChrBytes==0?0:checked(prgStart+info.PrgBytes));W32(b,24,info.ChrBytes);
        W32(b,28,checked(info.ChrRamBytes+info.SaveChrRamBytes));W32(b,32,info.StoredRamBytes);
        W32(b,36,trainer?checked(p.Offset+p.Game.Data.Length-512):0);W16(b,40,trainer?512:0);W32(b,44,0x100000/chrCapacity);
        b.AsSpan(48,8).Fill(255);p.SaveMap.CopyTo(b.AsSpan(48));W32(b,56,0x2000000/info.PrgBytes);
        W32(b,60,chrCapacity-1);
        var snapshot=FcSnapshotPlan.Create(info.Mapper,info.ChrBytes,info.ChrRamBytes+info.SaveChrRamBytes,info.StoredRamBytes);
        W32(b,64,snapshot.TotalBytes);W32(b,68,snapshot.WritableChrBytes);W32(b,72,snapshot.SpillBase);W32(b,76,(int)FcSnapshotPlan.Layout);
        // PC-only provenance/RAM split in formerly reserved bytes. FC02 v7
        // hardware ignores these words; no FPGA/card-format migration needed.
        "RC01"u8.CopyTo(b.AsSpan(80));b[84]=info.CorrectionKind;
        W32(b,88,info.WorkRamBytes);W32(b,92,info.SaveChrRamBytes);
        string SizeLabel(int bytes)
        {
            if(bytes==0)return "NONE    ";
            bool kib=bytes>=1024&&bytes%1024==0;string value=(kib?bytes/1024:bytes).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return value.PadLeft(6)+(kib?"KB":" B");
        }
        Encoding.ASCII.GetBytes(info.Mapper.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(6)+"  ").CopyTo(b.AsSpan(96,8));
        Encoding.ASCII.GetBytes(SizeLabel(info.PrgBytes)).CopyTo(b.AsSpan(104,8));
        Encoding.ASCII.GetBytes(SizeLabel(info.ChrBytes==0?info.ChrRamBytes+info.SaveChrRamBytes:info.ChrBytes)).CopyTo(b.AsSpan(112,8));
        Encoding.ASCII.GetBytes(SizeLabel(info.StoredRamBytes)).CopyTo(b.AsSpan(120,8));return b;
    }
    internal static NesRomInfo ReadGameConfig(ReadOnlySpan<byte> c,byte[] data,int offset,NesRomInfo parsed,ReadOnlySpan<byte> entry)
    {
        var raw=NesRomHeaderService.Parse(data,false);
        return ReadGameConfigMetadata(c,data.Length,(data[6]&4)!=0,offset,parsed,entry,raw.WorkRamBytes);
    }
    internal static NesRomInfo ReadGameConfigMetadata(ReadOnlySpan<byte> c,int dataBytes,bool originalTrainer,int offset,NesRomInfo parsed,ReadOnlySpan<byte> entry,int rawWork=0)
    {
        if(!c[..4].SequenceEqual("FC02"u8)||c[4]!=7||c[7]!=U16(entry,0)||(c[5]&0x80)!=0||c[10]>8)
            throw new InvalidDataException("Invalid NES game configuration; rewrite the card with the current PC tool.");
        int mapper=U16(c,8),sub=c[6],prg=checked((int)U32(c,16)),chr=checked((int)U32(c,24));
        int ram=checked((int)U32(c,32)),work=Math.Min(rawWork,ram),cram=checked((int)U32(c,28));bool battery=(c[5]&4)!=0,trainer=(c[5]&8)!=0;
        byte kind=0;int saveChr=0;
        if(c.Slice(80,4).SequenceEqual("RC01"u8))
        {
            kind=c[84];work=checked((int)U32(c,88));saveChr=checked((int)U32(c,92));
            if(kind>3||work<0||saveChr<0||saveChr>cram||(long)work+(battery?saveChr:0)>ram||(!battery&&saveChr!=0))
                throw new InvalidDataException("Invalid NES correction metadata.");
        }
        else if(mapper is 195 or 198&&ram>=12288&&battery)work=4096;
        int expectedPrg=offset;int chrCapacity=chr==0?cram:chr;
        if(U32(c,12)!=expectedPrg||
           (long)prg+chr>dataBytes-16-(trainer?512:0)||trainer!=originalTrainer||prg<=0||chr<0||work>ram||ram<0||
           U32(c,20)!=(chr==0?0:(long)expectedPrg+prg)||U32(c,36)!=(trainer?(long)offset+dataBytes-512:0)||U16(c,40)!=(trainer?512:0)||
           chrCapacity<=0||U32(c,44)!=0x100000/chrCapacity||U32(c,60)!=chrCapacity-1||((c[5]&64)!=0)!=((chrCapacity&(chrCapacity-1))==0)||U32(c,56)!=0x2000000/prg||((c[5]&32)!=0)!=((prg&(prg-1))==0)||c[10]!=entry[10]||!c.Slice(48,c[10]).SequenceEqual(entry.Slice(110,c[10]))||U32(c,32)!=U32(entry,122))
            throw new InvalidDataException("NES game configuration does not match its allocated ROM/SRAM ranges.");
        var snapshot=FcSnapshotPlan.Create(mapper,chr,cram,ram);
        if(U32(c,64)!=snapshot.TotalBytes||U32(c,68)!=snapshot.WritableChrBytes||U32(c,72)!=snapshot.SpillBase||U32(c,76)!=FcSnapshotPlan.Layout)
            throw new InvalidDataException("Invalid NES snapshot placement; rebuild the card configuration.");
        return parsed with {Mapper=mapper,Submapper=sub,PrgBytes=prg,ChrBytes=chr,WorkRamBytes=work,
            SaveRamBytes=ram-work-(battery?saveChr:0),ChrRamBytes=cram-saveChr,SaveChrRamBytes=saveChr,Battery=battery,CorrectionKind=kind,
            MetadataSource=kind==3?"Manual configuration":kind==1?"Known content correction":kind==2?"Mesen DB (validated)":"Card configuration",
            Warning=kind==3?null:parsed.CorrectionKind!=0&&(parsed.Mapper!=mapper||parsed.StoredRamBytes!=ram)?"Known correction available: rebuild configuration":parsed.Warning,
            Error=NesRomHeaderService.SupportsMapper(mapper,sub)?null:"Unsupported card mapper configuration."};
    }
    internal static FcGame ReadDirectoryGame(ReadOnlySpan<byte> config,string name,int length,int offset,int allocated,ReadOnlySpan<byte> entry,string? identity=null)
    {
        int ram=checked((int)U32(config,32)),work=(config[5]&4)!=0?0:ram;
        var parsed=new NesRomInfo(U16(config,8),config[6],checked((int)U32(config,16)),checked((int)U32(config,24)),work,ram-work,checked((int)U32(config,28)),0,(config[5]&4)!=0,false,0,"Card configuration",null);
        var info=ReadGameConfigMetadata(config,length,(config[5]&8)!=0,offset,parsed,entry,work);
        return new(){Name=name,Info=info,OriginalOffset=offset,OriginalAllocated=allocated,OriginalSaveMap=entry.Slice(110,config[10]).ToArray(),OriginalBattery=info.Battery,OriginalStoredRamBytes=info.StoredRamBytes,OriginalRomBytes=length,OriginalConfig=config.ToArray(),DirectoryIdentity=identity};
    }
    internal static NesRomInfo ReadOriginalInfo(FcGame game,byte[] data)
    {
        byte[] entry=new byte[128];var config=game.OriginalConfig.AsSpan();W16(entry,0,config[7]);entry[10]=config[10];config.Slice(48,8).CopyTo(entry.AsSpan(110));W32(entry,122,checked((int)U32(config,32)));
        return ReadGameConfig(config,data,game.OriginalOffset,NesRomHeaderService.Parse(data),entry);
    }
    public static FcGame LoadRom(FcGame game,Func<int,int,byte[]> read,CancellationToken cancellation)
    {
        if(game.HasRomData)return game.Copy();
        cancellation.ThrowIfCancellationRequested();byte[] stored=read(game.OriginalOffset,game.RomBytes);
        if(stored.Length!=game.RomBytes)throw new EndOfStreamException("Incomplete MIX ROM read.");
        cancellation.ThrowIfCancellationRequested();
        byte[] data;NesRomInfo info=game.Info;
        if(game.Md is null){
            data=RestoreNesFile(stored,game.OriginalConfig);info=ReadOriginalInfo(game,data);
        }else {
            data=stored;if(MdMbcxCodec.Describe(data,game.Name)!=game.Md)throw new InvalidDataException("MD ROM does not match its directory configuration.");
        }
        var loaded=game.WithData(data,info:info);string identity=GameLibraryDirectory.StableId(loaded);
        if(game.DirectoryIdentity is not null&&game.DirectoryIdentity!=identity)throw new InvalidDataException("ROM content identity mismatch.");
        return loaded.WithData(data,identity);
    }
    public static List<FcGame> Read(byte[] prefix,int capacity,Func<int,int,byte[]> read,CancellationToken ct,bool directoryOnly=false)
    {
        if(GameLibraryDirectory.IsLibrary(prefix.AsSpan(ConfigOffset)))return GameLibraryDirectory.Read(prefix,capacity,read,ct,directoryOnly);
        if(!IsFcPrefix(prefix))throw new InvalidDataException("Not an NES MBCX directory.");
        var d=prefix.AsSpan(DirectoryOffset,64);int count=U16(d,10),size=U16(d,12);uint entryStart=U32(d,16);
        if(count>99||size!=128||entryStart!=EntriesOffset||U32(d,20)!=GameConfigOffset||U16(d,24)!=GameConfigBytes||d[26]!=1||
            U32(d,28)!=TitleListOffset||U32(d,32)!=TitleDetailOffset||U16(d,36)!=TitleBytes||d[38]!=12||d[39]!=1||
            U16(d,40)!=TitleDetailBytes||d[42]!=32||d[43]!=2)throw new InvalidDataException("Invalid NES bitmap directory.");
        List<FcGame> games=[];List<(int Start,int End)> ranges=[];
        for(int i=0;i<count;i++)
        {
            ct.ThrowIfCancellationRequested();var e=prefix.AsSpan((int)entryStart+i*128,128);if(e[13]!=3||U16(e,0)!=i)throw new InvalidDataException("Invalid NES entry order/flags.");
            uint bank=U32(e,4);int banks=U16(e,8);
            if(bank<64||bank>2047||banks==0||bank+banks>2048)throw new InvalidDataException("Invalid NES bank range.");
            int offset=checked((int)bank*RomBankBytes),allocated=banks*RomBankBytes;
            if(offset>Math.Min(capacity,CapacityBytes)-allocated||ranges.Any(r=>offset<r.End&&offset+allocated>r.Start))throw new InvalidDataException("NES ranges overlap or exceed capacity.");
            int length=checked((int)U32(e,118));
            if(length<16||length>allocated)throw new InvalidDataException("Invalid NES file length.");
            var config=prefix.AsSpan(GameConfigOffset+(int)bank*GameConfigBytes,GameConfigBytes);
            int titleLength=U16(e,2);
            if(titleLength is <1 or >46||U16(e,14)!=0)throw new InvalidDataException("Invalid NES Unicode title length.");
            string title=Encoding.Unicode.GetString(e.Slice(16,titleLength*2));
            if(e[12]>1||((config[5]&4)!=0)!=(e[12]!=0))throw new InvalidDataException("Invalid legacy NES battery flag.");
            if(directoryOnly)games.Add(ReadDirectoryGame(config,title,length,offset,allocated,e));
            else {
                byte[] stored=read(offset,length);if(stored.Length!=length)throw new EndOfStreamException();byte[] data=RestoreNesFile(stored,config);
                var info=ReadGameConfig(config,data,offset,NesRomHeaderService.Parse(data),e);
                games.Add(new(){Name=title,Data=data,Info=info,OriginalOffset=offset,OriginalAllocated=allocated,OriginalSaveMap=e.Slice(110,Math.Min(8,(int)e[10])).ToArray(),OriginalBattery=e[12]!=0,OriginalStoredRamBytes=info.StoredRamBytes,OriginalRomBytes=length,OriginalConfig=config.ToArray()});
            }
            ranges.Add((offset,offset+allocated));
        }
        MixDirectory.Read(prefix,capacity,read,ct,games,directoryOnly);
        return games;
    }
    public static void Export(string path,IReadOnlyList<FcGame> games,CancellationToken ct,IProgress<FcProgress> progress)
    {
        var layout=Plan(games,CapacityBytes,false);if(layout.Entries.Count==0)throw new InvalidDataException("Add at least one NES / MD ROM.");
        string target=Path.GetFullPath(path),temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                var prefix=BuildPrefix(layout.Entries);file.Write(prefix);byte[] buffer=new byte[65536];
                for(int i=0;i<layout.Entries.Count;i++)
                {
                    ct.ThrowIfCancellationRequested();var entry=layout.Entries[i];file.Position=entry.Offset;
                    for(int off=0;off<entry.AllocatedBytes;off+=buffer.Length){ct.ThrowIfCancellationRequested();int n=Math.Min(buffer.Length,entry.AllocatedBytes-off);CopyStoredData(entry.Game,off,buffer.AsSpan(0,n));file.Write(buffer,0,n);}
                    progress.Report(new(entry.Game.Name,(i+1)*100/layout.Entries.Count));
                }
                file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();File.Move(temp,target,true);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
}
