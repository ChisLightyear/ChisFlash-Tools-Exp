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
    public string Compatibility=>Info.Error??Info.Warning??"OK";
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
    public const int ConfigOffset=0x20000,GameConfigBytes=128;
    public static int Align(int n,int unit)=>checked((n+unit-1)/unit*unit);
    private static ushort U16(ReadOnlySpan<byte> b,int p)=>BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
    private static uint U32(ReadOnlySpan<byte> b,int p)=>BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
    private static void W16(Span<byte>b,int p,int n)=>BinaryPrimitives.WriteUInt16LittleEndian(b[p..],checked((ushort)n));
    private static void W32(Span<byte>b,int p,int n)=>BinaryPrimitives.WriteUInt32LittleEndian(b[p..],checked((uint)n));
    private static void W32(Span<byte>b,int p,uint n)=>BinaryPrimitives.WriteUInt32LittleEndian(b[p..],n);
    public static int FilePrefixBytes(FcGame game)=>16+((game.Data[6]&4)!=0?512:0);
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
        if(!config[..4].SequenceEqual("NES2"u8)||config[4]!=1)throw new InvalidDataException("Unsupported NES payload layout; rebuild with the matching tool.");
        int lead=16+((config[5]&8)!=0?512:0),body=stored.Length-lead;
        if(body<0)throw new InvalidDataException("Truncated NES payload.");
        byte[] data=new byte[stored.Length];stored.AsSpan(body,lead).CopyTo(data);stored.AsSpan(0,body).CopyTo(data.AsSpan(lead));
        if(!data.AsSpan(0,4).SequenceEqual("NES\x1a"u8))throw new InvalidDataException("Invalid preserved NES header.");
        return data;
    }
    public static bool HasFcIdentity(ReadOnlySpan<byte> config)=>GameLibraryDirectory.IsLibrary(config)||config.Length>=36&&config[..7].SequenceEqual("MBCXCFG"u8)&&config.Slice(32,4).SequenceEqual("FC02"u8);
    public static bool IsFcPrefix(ReadOnlySpan<byte> prefix)=>prefix.Length>=PrefixBytes&&GameLibraryDirectory.IsLibrary(prefix.Slice(ConfigOffset,64));
    public static FcLayout Plan(IReadOnlyList<FcGame> games,int capacity,bool preserveLocations)
    {
        var rows=games.Where(g=>g.Enabled).ToList();capacity=Math.Min(capacity,CapacityBytes);

        GameLibraryDirectory.ValidateCapacity(rows);
        if(capacity<PrefixBytes)throw new InvalidDataException("NES: card capacity too small.");
        if(rows.Select(g=>g.Id).Distinct().Count()!=rows.Count)throw new InvalidDataException("Duplicate NES entry identity.");
        bool[] used=new bool[capacity/RomBankBytes];Array.Fill(used,true,0,PrefixBytes/RomBankBytes);
        Dictionary<Guid,(int Offset,int Size)> locations=[];
        foreach(var g in rows)
        {
            if(!g.Info.CanPack)throw new InvalidDataException($"{g.Name}: {g.Info.Error}");
            if(g.Snes is not null)SnesMbcxCodec.ValidateGame(g);
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
            if(g.Info.StoredRamBytes<0||n<0)throw new InvalidDataException("Invalid MIX RAM size.");
            if(n>60||(!g.Info.Battery&&n>4))throw new InvalidDataException($"{g.Name}: SRAM pool exhausted (60 dedicated / 4 shared banks).");
            if(n==0){saves[g.Id]=[];continue;}
            if(!g.Info.Battery){saves[g.Id]=Enumerable.Range(60,n).Select(i=>(byte)i).ToArray();shared=Math.Max(shared,n);continue;}
            var old=g.OriginalSaveMap.Take(n).ToArray();
            if(preserveLocations&&g.OriginalBattery&&old.Length>0)
            {
                if(old.Any(b=>b>=60)||old.Distinct().Count()!=old.Length||old.Any(b=>saveUsed[b]))
                    throw new InvalidDataException("Invalid or overlapping existing MIX save banks.");
                saves[g.Id]=old;foreach(byte b in old)saveUsed[b]=true;
            }

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
            map=Enumerable.Range(0,60).Where(i=>!saveUsed[i]).Take(g.Info.SaveBanks).Select(i=>(byte)i).ToArray();
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
        byte[] b=new byte[GameConfigBytes];"NES2"u8.CopyTo(b);b[4]=1;
        b[5]=(byte)((h[6]&1)|((h[6]&8)>>2)|(info.Battery?4:0)|(trainer?8:0)|(info.Mapper is 252 or 268?16:0)|((info.PrgBytes&(info.PrgBytes-1))==0?32:0)|((chrCapacity&(chrCapacity-1))==0?64:0));
        b[6]=checked((byte)info.Submapper);W16(b,8,info.Mapper);
        int prgStart=p.Offset;
        W32(b,12,prgStart);W32(b,16,info.PrgBytes);
        W32(b,20,info.ChrBytes==0?0:checked(prgStart+info.PrgBytes));W32(b,24,info.ChrBytes);
        W32(b,28,checked(info.ChrRamBytes+info.SaveChrRamBytes));W32(b,32,info.StoredRamBytes);
        W32(b,36,trainer?checked(p.Offset+p.Game.Data.Length-512):0);W16(b,40,trainer?512:0);W32(b,44,0x100000/chrCapacity);
        W32(b,56,0x2000000/info.PrgBytes);
        W32(b,60,chrCapacity-1);
        var snapshot=FcSnapshotPlan.Create(info.Mapper,info.ChrBytes,info.ChrRamBytes+info.SaveChrRamBytes,info.StoredRamBytes);
        W32(b,64,snapshot.TotalBytes);W32(b,68,snapshot.WritableChrBytes);W32(b,72,snapshot.SpillBase);W32(b,76,FcSnapshotPlan.Layout);
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
    internal static NesRomInfo ReadGameConfigMetadata(ReadOnlySpan<byte> c,int dataBytes,bool originalTrainer,int offset,NesRomInfo parsed,int rawWork=0)
    {
        if(c.Length!=128||!c[..4].SequenceEqual("NES2"u8)||c[4]!=1||c[7]!=0||(c[5]&0x80)!=0||c[10]!=0||c[11]!=0||c.Slice(48,8).IndexOfAnyExcept((byte)0)>=0)
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
           chrCapacity<=0||U32(c,44)!=0x100000/chrCapacity||U32(c,60)!=chrCapacity-1||((c[5]&64)!=0)!=((chrCapacity&(chrCapacity-1))==0)||U32(c,56)!=0x2000000/prg||((c[5]&32)!=0)!=((prg&(prg-1))==0)||ram>60*8192)
            throw new InvalidDataException("NES game configuration does not match its allocated ROM/SRAM ranges.");
        var snapshot=FcSnapshotPlan.Create(mapper,chr,cram,ram);
        if(U32(c,64)!=snapshot.TotalBytes||U32(c,68)!=snapshot.WritableChrBytes||U32(c,72)!=snapshot.SpillBase||U32(c,76)!=FcSnapshotPlan.Layout)
            throw new InvalidDataException("Invalid NES snapshot placement; rebuild the card configuration.");
        return parsed with {Mapper=mapper,Submapper=sub,PrgBytes=prg,ChrBytes=chr,WorkRamBytes=work,
            SaveRamBytes=ram-work-(battery?saveChr:0),ChrRamBytes=cram-saveChr,SaveChrRamBytes=saveChr,Battery=battery,CorrectionKind=kind,
            MetadataSource=kind==3?"Manual configuration":kind==1?"Known content correction":kind==2?"Mesen DB (validated)":"Card configuration",
            Warning=kind==3?null:parsed.CorrectionKind!=0&&(parsed.Mapper!=mapper||parsed.StoredRamBytes!=ram)?"Known correction available: rebuild configuration":parsed.Warning,
            Error=NesRomHeaderService.IsMapperRepresentable(mapper,sub)?null:"Card mapper/submapper is outside the encodable range."};
    }
    internal static FcGame ReadDirectoryGame(ReadOnlySpan<byte> config,string name,int length,int offset,int allocated,byte[] bankMap,string identity)
    {
        int ram=checked((int)U32(config,32)),work=(config[5]&4)!=0?0:ram;
        var parsed=new NesRomInfo(U16(config,8),config[6],checked((int)U32(config,16)),checked((int)U32(config,24)),work,ram-work,checked((int)U32(config,28)),0,(config[5]&4)!=0,false,0,"Card configuration",null);
        var info=ReadGameConfigMetadata(config,length,(config[5]&8)!=0,offset,parsed,work);
        return new(){Name=name,Info=info,OriginalOffset=offset,OriginalAllocated=allocated,OriginalSaveMap=bankMap,OriginalBattery=info.Battery,OriginalStoredRamBytes=info.StoredRamBytes,OriginalRomBytes=length,OriginalConfig=config.ToArray(),DirectoryIdentity=identity};
    }
    internal static NesRomInfo ReadOriginalInfo(FcGame game,byte[] data)
    {
        var raw=NesRomHeaderService.Parse(data,false);
        return ReadGameConfigMetadata(game.OriginalConfig,data.Length,(data[6]&4)!=0,game.OriginalOffset,NesRomHeaderService.Parse(data),raw.WorkRamBytes);
    }
    public static FcGame LoadRom(FcGame game,Func<int,int,byte[]> read,CancellationToken cancellation)
    {
        if(game.HasRomData)return game.Copy();
        cancellation.ThrowIfCancellationRequested();byte[] stored=read(game.OriginalOffset,game.RomBytes);
        if(stored.Length!=game.RomBytes)throw new EndOfStreamException("Incomplete MIX ROM read.");
        cancellation.ThrowIfCancellationRequested();
        byte[] data;NesRomInfo info=game.Info;
        if(game.IsNes){
            data=RestoreNesFile(stored,game.OriginalConfig);info=ReadOriginalInfo(game,data);
        }else if(game.Snes is not null){
            data=stored;SnesMbcxCodec.ValidateGame(game.WithData(data));
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
        throw new InvalidDataException("Only MIX GLB2 v2 images are supported; rebuild from the original ROMs.");
    }
    public static void Export(string path,IReadOnlyList<FcGame> games,CancellationToken ct,IProgress<FcProgress> progress)
    {
        var layout=Plan(games,CapacityBytes,false);if(layout.Entries.Count==0)throw new InvalidDataException("Add at least one NES / MD / SNES ROM.");
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
