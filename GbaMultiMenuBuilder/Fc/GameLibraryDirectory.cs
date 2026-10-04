using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GbaMultiMenuBuilder.Md;

namespace GbaMultiMenuBuilder.Fc;

public static class GameLibraryDirectory
{
    public const int Base=0x20000,HeaderBytes=64,RecordBytes=96;
    public const uint FcCore=0x20001,MdCore=0x20002;
    public static bool IsLibrary(ReadOnlySpan<byte> data)=>data.Length>=4&&data[..4].SequenceEqual("GLB2"u8);
    private static int U16(ReadOnlySpan<byte> data,int offset)=>BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static int U32(ReadOnlySpan<byte> data,int offset)=>checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]));
    private static void W16(Span<byte> data,int offset,int value)=>BinaryPrimitives.WriteUInt16LittleEndian(data[offset..],checked((ushort)value));
    private static void W32(Span<byte> data,int offset,int value)=>BinaryPrimitives.WriteUInt32LittleEndian(data[offset..],checked((uint)value));
    private static uint Crc(ReadOnlySpan<byte> data){uint crc=uint.MaxValue;foreach(byte value in data){crc^=value;for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0u);}return ~crc;}
    private static void Seal(Span<byte> data)=>BinaryPrimitives.WriteUInt32LittleEndian(data[^4..],Crc(data[..^4]));
    private static void Verify(ReadOnlySpan<byte> data){if(Crc(data[..^4])!=BinaryPrimitives.ReadUInt32LittleEndian(data[^4..]))throw new InvalidDataException("Library CRC mismatch.");}
    public static string StableId(FcGame game)=>game.HasRomData?Convert.ToHexString(SHA256.HashData(game.Data).AsSpan(0,16)):game.DirectoryIdentity??throw new InvalidOperationException("Load the legacy ROM before calculating its content identity.");
    public static void ValidateCapacity(IReadOnlyList<FcGame> games)
    {
        int bytes=checked(Base+HeaderBytes+games.Count*RecordBytes+games.Sum(game=>16+(game.Md is null?128:32)+512+1024+128));
        if(games.Count>ushort.MaxValue||bytes>FcMbcxCodec.PrefixBytes)throw new InvalidDataException("Game library metadata exceeds the 1 MiB prefix.");
        var known=games.Where(game=>game.HasRomData||game.DirectoryIdentity is not null).Select(StableId).ToArray();
        if(known.Distinct(StringComparer.Ordinal).Count()!=known.Length)throw new InvalidDataException("Duplicate ROM content identity.");
    }
    public static byte[] Write(IReadOnlyList<FcPlacement> entries)
    {
        if(entries.Any(entry=>entry.Game.Snes is not null))throw new InvalidDataException(GbaMultiMenuBuilder.Snes.SnesRomImportService.LaunchUnavailable);
        if(entries.Any(entry=>!entry.Game.HasRomData))throw new InvalidOperationException("Load ROM content before rebuilding the library.");
        ValidateCapacity(entries.Select(entry=>entry.Game).ToArray());
        byte[] prefix=Enumerable.Repeat((byte)255,FcMbcxCodec.PrefixBytes).ToArray();
        var header=prefix.AsSpan(Base,HeaderBytes);header.Clear();"GLB2"u8.CopyTo(header);W16(header,4,1);W16(header,6,RecordBytes);W16(header,8,entries.Count);
        W32(header,12,Base+HeaderBytes);W32(header,20,entries.Count==0?FcMbcxCodec.PrefixBytes:entries.Max(entry=>checked(entry.Offset+entry.AllocatedBytes)));W32(header,28,524288);
        int next=Base+HeaderBytes+entries.Count*RecordBytes;
        for(int index=0;index<entries.Count;index++)
        {
            var placement=entries[index];var game=placement.Game;var md=game.Md;int core=checked((int)(md is null?FcCore:MdCore));
            byte[] payload;
            if(md is null)payload=FcMbcxCodec.BuildGameConfig(placement);
            else {payload=new byte[32];W16(payload,0,placement.Offset/16384);W16(payload,2,md.Flags);W32(payload,4,game.Data.Length/2);W32(payload,8,placement.SaveMap.Length==0?0:placement.SaveMap[0]*8192);W32(payload,12,md.SaveBytes);}
            int launch=next;next+=16+payload.Length;
            var block=prefix.AsSpan(launch,16+payload.Length);block.Clear();"LNC1"u8.CopyTo(block);W32(block,4,core);W16(block,8,1);W16(block,10,payload.Length);BinaryPrimitives.WriteUInt32LittleEndian(block[12..],Crc(payload));payload.CopyTo(block[16..]);
            int list=next;next+=512;int detail=next;next+=1024;int info=next;next+=128;
            var bitmap=FcTitleRenderer.Render(game.Name);bitmap.List.CopyTo(prefix.AsSpan(list,512));bitmap.Detail.CopyTo(prefix.AsSpan(detail,1024));
            var title=prefix.AsSpan(info,128);title.Clear();string name=string.IsNullOrWhiteSpace(game.Name)?"UNKNOWN":game.Name.Trim();if(name.Length>46)name=name[..46];if(char.IsHighSurrogate(name[^1]))name=name[..^1];W16(title,0,name.Length);Encoding.Unicode.GetBytes(name).CopyTo(title[2..]);
            if(md is null)payload.AsSpan(96,32).CopyTo(title[96..]);
            else {string Size(int bytes)=>bytes==0?"NONE    ":(bytes/1024).ToString().PadLeft(6)+"KB";Encoding.ASCII.GetBytes((md.Flags&64)!=0?"PIER    ":"MD      ").CopyTo(title[96..]);Encoding.ASCII.GetBytes(Size(game.Data.Length)).CopyTo(title[104..]);Encoding.ASCII.GetBytes(Size(md.SaveBytes)).CopyTo(title[112..]);Encoding.ASCII.GetBytes((md.Flags&1)!=0?"PAL     ":"NTSC    ").CopyTo(title[120..]);}
            var record=prefix.AsSpan(Base+HeaderBytes+index*RecordBytes,RecordBytes);record.Clear();SHA256.HashData(game.Data).AsSpan(0,16).CopyTo(record);
            W32(record,16,core);W16(record,20,md is null?3:4);W16(record,22,1);W32(record,24,placement.Offset);W32(record,28,game.Data.Length);W32(record,32,placement.AllocatedBytes);
            W32(record,36,launch);W16(record,40,block.Length);W32(record,44,list);W32(record,48,detail);W32(record,52,info);
            W32(record,56,placement.SaveMap.Length==0?0:placement.SaveMap[0]*8192);W32(record,60,game.Info.StoredRamBytes);W16(record,64,game.Info.SaveBanks==0?0:game.Info.Battery?1:2);
            record[66]=md is null?(byte)7:(byte)3;W32(record,68,md is null?checked((int)FcSnapshotPlan.Layout):0);Seal(record);
        }
        W32(header,16,next);Seal(header);return prefix;
    }
    public static List<FcGame> Read(byte[] prefix,int capacity,Func<int,int,byte[]> read,CancellationToken cancellation,bool directoryOnly=false)
    {
        if(prefix.Length<FcMbcxCodec.PrefixBytes)throw new InvalidDataException("Truncated library prefix.");
        var header=prefix.AsSpan(Base,HeaderBytes);Verify(header);
        int count=U16(header,8),records=U32(header,12),end=U32(header,16),limit=U32(header,20);
        if(!IsLibrary(header)||U16(header,4)!=1||U16(header,6)!=96||U16(header,10)!=0||records!=Base+64||end<records+count*96||end>prefix.Length||limit>FcMbcxCodec.CapacityBytes||limit>capacity||limit<FcMbcxCodec.PrefixBytes||U32(header,24)!=0||U32(header,28)!=524288||header[32..60].IndexOfAnyExcept((byte)0)>=0)throw new InvalidDataException("Invalid game library header.");
        List<FcGame> games=[];List<(int Start,int End)> romRanges=[],assetRanges=[];HashSet<string> identities=new(StringComparer.Ordinal);
        ReadOnlySpan<byte> Asset(int address,int bytes)
        {
            int floor=records+count*96;
            if(address<floor||bytes<=0||address>end-bytes||assetRanges.Any(range=>address<range.End&&range.Start<address+bytes))throw new InvalidDataException("Invalid or overlapping library metadata.");
            assetRanges.Add((address,address+bytes));return prefix.AsSpan(address,bytes);
        }
        for(int index=0;index<count;index++)
        {
            cancellation.ThrowIfCancellationRequested();var record=prefix.AsSpan(records+index*96,96);Verify(record);
            int core=U32(record,16),platform=U16(record,20),offset=U32(record,24),length=U32(record,28),allocated=U32(record,32),launchBytes=U16(record,40);
            if((core!=(int)FcCore||platform!=3)&&(core!=(int)MdCore||platform!=4))throw new InvalidDataException("Unsupported installed-core adapter.");
            if(U16(record,22)!=1||U16(record,42)!=0||record[67]!=0||record[72..92].IndexOfAnyExcept((byte)0)>=0||length<16||length>allocated||offset%16384!=0||allocated%16384!=0||offset<FcMbcxCodec.PrefixBytes||offset>Math.Min(capacity,limit)-allocated||romRanges.Any(range=>offset<range.End&&range.Start<offset+allocated))throw new InvalidDataException("Invalid game library record.");
            romRanges.Add((offset,offset+allocated));int expectedMask=core==(int)FcCore?7:3;
            if(record[66]==0||(record[66]&~expectedMask)!=0||U16(record,64)>2)throw new InvalidDataException("Invalid game capabilities.");
            var block=Asset(U32(record,36),launchBytes);int payloadBytes=core==(int)FcCore?128:32;
            if(launchBytes!=payloadBytes+16||!block[..4].SequenceEqual("LNC1"u8)||U32(block,4)!=core||U16(block,8)!=1||U16(block,10)!=payloadBytes||BinaryPrimitives.ReadUInt32LittleEndian(block[12..])!=Crc(block[16..]))throw new InvalidDataException("Invalid launch block.");
            var payload=block[16..];_ = Asset(U32(record,44),512);_ = Asset(U32(record,48),1024);var title=Asset(U32(record,52),128);int chars=U16(title,0);
            if(chars is <1 or >46)throw new InvalidDataException("Invalid library title.");string name=Encoding.Unicode.GetString(title.Slice(2,chars*2));
            string directoryIdentity=Convert.ToHexString(record[..16]);if(record[..16].IndexOfAnyExcept((byte)0)<0)throw new InvalidDataException("Empty content identity.");
            FcGame game;
            if(directoryOnly){
                if(core==(int)FcCore){byte[] entry=new byte[128];W16(entry,0,payload[7]);entry[10]=payload[10];payload.Slice(48,8).CopyTo(entry.AsSpan(110));W32(entry,122,U32(payload,32));game=FcMbcxCodec.ReadDirectoryGame(payload,name,length,offset,allocated,entry,directoryIdentity);if(U32(record,68)!=(int)FcSnapshotPlan.Layout)throw new InvalidDataException("FC state layout mismatch.");}
                else {if(U16(payload,0)!=offset/16384||U32(payload,4)*2!=length||U32(record,68)!=0)throw new InvalidDataException("Invalid MD launch configuration.");game=MdMbcxCodec.ReadDirectoryGame(name,length,offset,allocated,U16(payload,2),U32(payload,12),U32(payload,8),directoryIdentity);}
            }else {
            byte[] stored=read(offset,length);if(stored.Length!=length)throw new EndOfStreamException();
            if(core==(int)FcCore)
            {
                byte[] data=FcMbcxCodec.RestoreNesFile(stored,payload);byte[] oldEntry=new byte[128];W16(oldEntry,0,payload[7]);oldEntry[10]=payload[10];oldEntry[12]=(byte)((payload[5]&4)!=0?1:0);payload.Slice(48,8).CopyTo(oldEntry.AsSpan(110));W32(oldEntry,122,U32(payload,32));
                var info=FcMbcxCodec.ReadGameConfig(payload,data,offset,NesRomHeaderService.Parse(data),oldEntry);
                game=new(){Name=name,Data=data,Info=info,OriginalOffset=offset,OriginalAllocated=allocated,OriginalBattery=info.Battery,OriginalStoredRamBytes=info.StoredRamBytes,OriginalSaveMap=payload.Slice(48,payload[10]).ToArray()};
                if(U32(record,68)!=(int)FcSnapshotPlan.Layout)throw new InvalidDataException("FC state layout mismatch.");
            }
            else
            {
                var parsed=MdMbcxCodec.CreateGame(stored,name);var md=parsed.Md!;int save=U32(payload,8),bytes=U32(payload,12);
                if(U16(payload,0)!=offset/16384||U32(payload,4)*2!=length||U16(payload,2)!=md.Flags||bytes!=md.SaveBytes||save%8192!=0||save>524288-bytes||U32(record,68)!=0)throw new InvalidDataException("Invalid MD launch configuration.");
                game=new(){Name=name,Data=stored,Info=parsed.Info,Md=md,OriginalOffset=offset,OriginalAllocated=allocated,OriginalBattery=parsed.Info.Battery,OriginalStoredRamBytes=parsed.Info.StoredRamBytes,OriginalSaveMap=Enumerable.Range(save/8192,parsed.Info.SaveBanks).Select(bank=>(byte)bank).ToArray()};
            }
            }
            string identity=StableId(game);
            if(!Convert.FromHexString(identity).AsSpan().SequenceEqual(record[..16])||!identities.Add(identity))throw new InvalidDataException("ROM content identity mismatch or duplicate.");
            int first=game.OriginalSaveMap.Length==0?0:game.OriginalSaveMap[0]*8192;
            if(U32(record,56)!=first||U32(record,60)!=game.Info.StoredRamBytes||U16(record,64)!=(game.Info.SaveBanks==0?0:game.Info.Battery?1:2))throw new InvalidDataException("Save identity mismatch.");games.Add(game);
        }
        var dedicated=games.Where(game=>game.Info.Battery).SelectMany(game=>game.OriginalSaveMap).ToArray();
        if(dedicated.Any(bank=>bank>=60)||dedicated.Distinct().Count()!=dedicated.Length)throw new InvalidDataException("Battery saves overlap.");return games;
    }
    public sealed record SaveImage(string GameId,int SaveType,int Bytes,byte[] Data);
    public static IReadOnlyList<SaveImage> ExportSaves(IReadOnlyList<FcGame> games,ReadOnlySpan<byte> sram)
    {
        if(sram.Length!=524288)throw new InvalidDataException("Expected a 512 KiB SRAM image.");List<SaveImage> result=[];HashSet<string> identities=new(StringComparer.Ordinal);HashSet<int> banks=[];
        foreach(var game in games.Where(game=>game.Info.Battery&&game.Info.StoredRamBytes>0))
        {
            string identity=StableId(game);if(!identities.Add(identity))throw new InvalidDataException("Duplicate save content identity.");
            byte[] data=new byte[game.Info.StoredRamBytes];int offset=0;
            foreach(byte bank in game.OriginalSaveMap){if(bank>=60||!banks.Add(bank))throw new InvalidDataException("Invalid or overlapping save mapping.");int bytes=Math.Min(8192,data.Length-offset);if(bytes<=0)break;sram.Slice(bank*8192,bytes).CopyTo(data.AsSpan(offset));offset+=bytes;}
            if(offset!=data.Length)throw new InvalidDataException("Incomplete save bank mapping.");result.Add(new(identity,1,data.Length,data));
        }
        return result;
    }
    public static byte[] ImportSaves(IReadOnlyList<FcPlacement> entries,IReadOnlyList<SaveImage> saves,ReadOnlySpan<byte> current)
    {
        if(current.Length!=524288)throw new InvalidDataException("Expected a 512 KiB SRAM image.");byte[] result=current.ToArray();HashSet<string> identities=new(StringComparer.Ordinal);HashSet<int> banks=[];
        foreach(var save in saves)
        {
            if(save is null||save.GameId is null||save.Data is null||save.Bytes<=0||!identities.Add(save.GameId)||save.SaveType!=1||save.Bytes!=save.Data.Length)throw new InvalidDataException("Invalid save archive identity.");
            var matches=entries.Where(entry=>entry.Game.Info.Battery&&entry.Game.Info.StoredRamBytes>0&&StableId(entry.Game)==save.GameId).ToArray();
            if(matches.Length!=1||!matches[0].Game.Info.Battery||matches[0].Game.Info.StoredRamBytes!=save.Bytes)throw new InvalidDataException("Save target type or size changed.");
            var placement=matches[0];int offset=0;
            foreach(byte bank in placement.SaveMap){if(bank>=60||!banks.Add(bank))throw new InvalidDataException("Save target mappings overlap.");int bytes=Math.Min(8192,save.Bytes-offset);if(bytes>0)save.Data.AsSpan(offset,bytes).CopyTo(result.AsSpan(bank*8192));offset+=Math.Max(0,bytes);}
            if(offset!=save.Bytes)throw new InvalidDataException("Incomplete save target mapping.");
        }
        return result;
    }
}
