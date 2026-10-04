using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GbaMultiMenuBuilder.Md;
using GbaMultiMenuBuilder.Snes;

namespace GbaMultiMenuBuilder.Fc;

public static class GameLibraryDirectory
{
    public const int Base=0x20000,HeaderBytes=64,RecordBytes=96,Version=2,LaunchVersion=2,SaveMapAbi=1;
    public const uint FcCore=0x20001,MdCore=0x20002,SnesCore=0x20003;
    public static bool IsLibrary(ReadOnlySpan<byte> data)=>data.Length>=4&&data[..4].SequenceEqual("GLB2"u8);
    private static int U16(ReadOnlySpan<byte> data,int offset)=>BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static int U32(ReadOnlySpan<byte> data,int offset)=>checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]));
    private static void W16(Span<byte> data,int offset,int value)=>BinaryPrimitives.WriteUInt16LittleEndian(data[offset..],checked((ushort)value));
    private static void W32(Span<byte> data,int offset,int value)=>BinaryPrimitives.WriteUInt32LittleEndian(data[offset..],checked((uint)value));
    private static uint Crc(ReadOnlySpan<byte> data){uint crc=uint.MaxValue;foreach(byte value in data){crc^=value;for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0u);}return ~crc;}
    private static void Seal(Span<byte> data)=>BinaryPrimitives.WriteUInt32LittleEndian(data[^4..],Crc(data[..^4]));
    private static void Verify(ReadOnlySpan<byte> data){if(Crc(data[..^4])!=BinaryPrimitives.ReadUInt32LittleEndian(data[^4..]))throw new InvalidDataException("Library CRC mismatch.");}
    public static string StableId(FcGame game)=>game.HasRomData?Convert.ToHexString(SHA256.HashData(game.Data).AsSpan(0,16)):game.DirectoryIdentity??throw new InvalidOperationException("Load ROM content before calculating its identity.");
    private static int PayloadBytes(FcGame game)=>game.IsNes?128:32;
    private static int SaveMode(FcGame game)=>game.Info.SaveBanks==0?0:game.Info.Battery?1:2;
    public static void ValidateCapacity(IReadOnlyList<FcGame> games)
    {
        long bytes=Base+HeaderBytes+(long)games.Count*RecordBytes+games.Sum(game=>(long)16+PayloadBytes(game)+game.Info.SaveBanks+512+1024+128);
        if(games.Count>ushort.MaxValue||bytes>FcMbcxCodec.PrefixBytes)throw new InvalidDataException("Game library metadata exceeds the 1 MiB prefix.");
        var known=games.Where(game=>game.HasRomData||game.DirectoryIdentity is not null).Select(StableId).ToArray();
        if(known.Distinct(StringComparer.Ordinal).Count()!=known.Length)throw new InvalidDataException("Duplicate ROM content identity.");
    }
    private static void ValidateMap(FcGame game,byte[] map,HashSet<int> dedicated)
    {
        int banks=game.Info.SaveBanks;
        if(game.Info.StoredRamBytes<0||banks>60||map.Length!=banks||map.Distinct().Count()!=banks||map.Any(bank=>bank>=64))
            throw new InvalidDataException("Invalid MIX SRAM mapping length or bank.");
        if(game.Info.Battery){foreach(byte bank in map)if(bank>=60||!dedicated.Add(bank))throw new InvalidDataException("Dedicated MIX SRAM mappings overlap.");}
        else if(banks>4||!map.SequenceEqual(Enumerable.Range(60,banks).Select(bank=>(byte)bank)))throw new InvalidDataException("Invalid shared MIX SRAM mapping.");
    }
    public static byte[] Write(IReadOnlyList<FcPlacement> entries)
    {
        if(entries.Any(entry=>!entry.Game.HasRomData||!entry.Game.Info.CanPack))throw new InvalidOperationException("Load valid ROM content before rebuilding the library.");
        ValidateCapacity(entries.Select(entry=>entry.Game).ToArray());
        byte[] prefix=Enumerable.Repeat((byte)255,FcMbcxCodec.PrefixBytes).ToArray();
        var header=prefix.AsSpan(Base,HeaderBytes);header.Clear();"GLB2"u8.CopyTo(header);W16(header,4,Version);W16(header,6,RecordBytes);W16(header,8,entries.Count);
        W32(header,12,Base+HeaderBytes);W32(header,20,entries.Count==0?FcMbcxCodec.PrefixBytes:entries.Max(entry=>checked(entry.Offset+entry.AllocatedBytes)));W32(header,28,524288);
        int next=Base+HeaderBytes+entries.Count*RecordBytes;HashSet<int> dedicated=[];List<(int Start,int End)> ranges=[];
        for(int index=0;index<entries.Count;index++)
        {
            var placement=entries[index];var game=placement.Game;var md=game.Md;
            ValidateMap(game,placement.SaveMap,dedicated);
            if(placement.Offset<FcMbcxCodec.PrefixBytes||placement.Offset%16384!=0||placement.AllocatedBytes%16384!=0||placement.AllocatedBytes<game.RomBytes||placement.Offset>FcMbcxCodec.CapacityBytes-placement.AllocatedBytes||ranges.Any(range=>placement.Offset<range.End&&range.Start<placement.Offset+placement.AllocatedBytes))throw new InvalidDataException("Invalid MIX ROM placement.");
            ranges.Add((placement.Offset,placement.Offset+placement.AllocatedBytes));
            int core=checked((int)(game.IsNes?FcCore:game.Snes is not null?SnesCore:MdCore));
            byte[] payload;
            if(game.IsNes)payload=FcMbcxCodec.BuildGameConfig(placement);
            else if(game.Snes is not null)payload=SnesMbcxCodec.BuildConfig(placement);
            else {if(md is null||game.Info.StoredRamBytes!=md.SaveBytes||game.Info.Battery!=(md.SaveBytes>0)||MdMbcxCodec.Describe(game.Data,game.Name)!=md)throw new InvalidDataException("MD ROM configuration mismatch.");payload=new byte[32];W16(payload,0,placement.Offset/16384);W16(payload,2,md.Flags);W32(payload,4,game.Data.Length/2);W32(payload,12,md.SaveBytes);}
            int launch=next;next+=16+payload.Length;
            var block=prefix.AsSpan(launch,16+payload.Length);block.Clear();"LNC2"u8.CopyTo(block);W32(block,4,core);W16(block,8,LaunchVersion);W16(block,10,payload.Length);BinaryPrimitives.WriteUInt32LittleEndian(block[12..],Crc(payload));payload.CopyTo(block[16..]);
            int mapAddress=placement.SaveMap.Length==0?0:next;placement.SaveMap.CopyTo(prefix.AsSpan(next));next+=placement.SaveMap.Length;
            int list=next;next+=512;int detail=next;next+=1024;int info=next;next+=128;
            var bitmap=FcTitleRenderer.Render(game.Name);bitmap.List.CopyTo(prefix.AsSpan(list,512));bitmap.Detail.CopyTo(prefix.AsSpan(detail,1024));
            var title=prefix.AsSpan(info,128);title.Clear();string name=string.IsNullOrWhiteSpace(game.Name)?"UNKNOWN":game.Name.Trim();if(name.Length>46)name=name[..46];if(char.IsHighSurrogate(name[^1]))name=name[..^1];W16(title,0,name.Length);Encoding.Unicode.GetBytes(name).CopyTo(title[2..]);
            if(game.IsNes)payload.AsSpan(96,32).CopyTo(title[96..]);
            else {string Size(int bytes)=>bytes==0?"NONE    ":(bytes/1024).ToString().PadLeft(6)+"KB";Encoding.ASCII.GetBytes(game.Snes is not null?"SNES    ":"MD      ").CopyTo(title[96..]);Encoding.ASCII.GetBytes(Size(game.Data.Length)).CopyTo(title[104..]);Encoding.ASCII.GetBytes(Size(game.Info.StoredRamBytes)).CopyTo(title[112..]);Encoding.ASCII.GetBytes(game.Snes is not null?(game.Snes.RegionDisplay=="PAL"?"PAL     ":game.Snes.RegionDisplay=="NTSC"?"NTSC    ":"UNKNOWN "):(md!.Flags&1)!=0?"PAL     ":"NTSC    ").CopyTo(title[120..]);}
            var record=prefix.AsSpan(Base+HeaderBytes+index*RecordBytes,RecordBytes);record.Clear();SHA256.HashData(game.Data).AsSpan(0,16).CopyTo(record);
            W32(record,16,core);W16(record,20,game.IsNes?3:game.Snes is not null?5:4);W16(record,22,Version);W32(record,24,placement.Offset);W32(record,28,game.Data.Length);W32(record,32,placement.AllocatedBytes);
            W32(record,36,launch);W16(record,40,block.Length);W32(record,44,list);W32(record,48,detail);W32(record,52,info);
            W32(record,60,game.Info.StoredRamBytes);W16(record,64,SaveMode(game));record[66]=game.IsNes?(byte)7:(byte)3;W32(record,68,game.IsNes?checked((int)FcSnapshotPlan.Layout):0);
            W32(record,72,mapAddress);W16(record,76,placement.SaveMap.Length);W16(record,78,SaveMapAbi);W32(record,80,8192);BinaryPrimitives.WriteUInt32LittleEndian(record[84..],Crc(placement.SaveMap));Seal(record);
        }
        W32(header,16,next);Seal(header);return prefix;
    }
    public static List<FcGame> Read(byte[] prefix,int capacity,Func<int,int,byte[]> read,CancellationToken cancellation,bool directoryOnly=false)
    {
        if(prefix.Length<FcMbcxCodec.PrefixBytes)throw new InvalidDataException("Truncated MIX v2 prefix.");
        var header=prefix.AsSpan(Base,HeaderBytes);
        if(!IsLibrary(header)||U16(header,4)!=Version)throw new InvalidDataException("Only MIX GLB2 v2 is supported; rebuild from the original ROMs. No legacy migration.");
        Verify(header);
        int count=U16(header,8),records=U32(header,12),end=U32(header,16),limit=U32(header,20);
        if(U16(header,6)!=RecordBytes||U16(header,10)!=0||records!=Base+HeaderBytes||end<records+count*RecordBytes||end>prefix.Length||limit>Math.Min(capacity,FcMbcxCodec.CapacityBytes)||limit<FcMbcxCodec.PrefixBytes||U32(header,24)!=0||U32(header,28)!=524288||header[32..60].IndexOfAnyExcept((byte)0)>=0)throw new InvalidDataException("Invalid MIX v2 header.");
        List<FcGame> games=[];List<(int Start,int End)> romRanges=[],assetRanges=[];HashSet<string> identities=new(StringComparer.Ordinal);HashSet<int> dedicated=[];
        ReadOnlySpan<byte> Asset(int address,int bytes)
        {
            int floor=records+count*RecordBytes;
            if(address<floor||bytes<=0||address>end-bytes||assetRanges.Any(range=>address<range.End&&range.Start<address+bytes))throw new InvalidDataException("Invalid or overlapping MIX v2 metadata.");
            assetRanges.Add((address,address+bytes));return prefix.AsSpan(address,bytes);
        }
        for(int index=0;index<count;index++)
        {
            cancellation.ThrowIfCancellationRequested();var record=prefix.AsSpan(records+index*RecordBytes,RecordBytes);Verify(record);
            int core=U32(record,16),platform=U16(record,20),offset=U32(record,24),length=U32(record,28),allocated=U32(record,32),launchBytes=U16(record,40);
            bool nes=core==(int)FcCore&&platform==3,md=core==(int)MdCore&&platform==4,snes=core==(int)SnesCore&&platform==5;
            if(!nes&&!md&&!snes)throw new InvalidDataException("Unsupported MIX v2 core/platform adapter.");
            if(U16(record,22)!=Version||U16(record,42)!=0||U32(record,56)!=0||record[67]!=0||U32(record,88)!=0||length<16||length>allocated||offset%16384!=0||allocated%16384!=0||offset<FcMbcxCodec.PrefixBytes||offset>limit-allocated||romRanges.Any(range=>offset<range.End&&range.Start<offset+allocated))throw new InvalidDataException("Invalid MIX v2 record.");
            romRanges.Add((offset,offset+allocated));
            if(record[66]!=(nes?7:3)||U16(record,64)>2||U32(record,68)!=(nes?(int)FcSnapshotPlan.Layout:0))throw new InvalidDataException("Invalid MIX v2 capabilities or state layout.");
            int mapCount=U16(record,76),mapAddress=U32(record,72);
            if(mapCount>60||U16(record,78)!=SaveMapAbi||U32(record,80)!=8192||mapCount==0&&mapAddress!=0)throw new InvalidDataException("Invalid MIX v2 SRAM map descriptor.");
            byte[] map=mapCount==0?[]:Asset(mapAddress,mapCount).ToArray();
            if(BinaryPrimitives.ReadUInt32LittleEndian(record[84..])!=Crc(map))throw new InvalidDataException("MIX v2 SRAM map CRC mismatch.");
            var block=Asset(U32(record,36),launchBytes);int payloadBytes=nes?128:32;
            if(launchBytes!=payloadBytes+16||!block[..4].SequenceEqual("LNC2"u8)||U32(block,4)!=core||U16(block,8)!=LaunchVersion||U16(block,10)!=payloadBytes||BinaryPrimitives.ReadUInt32LittleEndian(block[12..])!=Crc(block[16..]))throw new InvalidDataException("Invalid MIX v2 launch block.");
            var payload=block[16..];_ = Asset(U32(record,44),512);_ = Asset(U32(record,48),1024);var title=Asset(U32(record,52),128);int chars=U16(title,0);
            if(chars is <1 or >46)throw new InvalidDataException("Invalid MIX v2 title.");string name=Encoding.Unicode.GetString(title.Slice(2,chars*2));
            string identity=Convert.ToHexString(record[..16]);if(record[..16].IndexOfAnyExcept((byte)0)<0||!identities.Add(identity))throw new InvalidDataException("Empty or duplicate ROM identity.");
            FcGame game;
            if(nes)game=FcMbcxCodec.ReadDirectoryGame(payload,name,length,offset,allocated,map,identity);
            else if(snes)game=SnesMbcxCodec.ReadDirectoryGame(payload,name,length,offset,allocated,map,identity);
            else {
                if(U16(payload,0)!=offset/16384||U32(payload,4)*2L!=length||U32(payload,8)!=0||payload[16..].IndexOfAnyExcept((byte)0)>=0)throw new InvalidDataException("Invalid MD v2 launch configuration.");
                game=MdMbcxCodec.ReadDirectoryGame(name,length,offset,allocated,U16(payload,2),U32(payload,12),map,identity);
            }
            if(U32(record,60)!=game.Info.StoredRamBytes||U16(record,64)!=SaveMode(game))throw new InvalidDataException("MIX v2 SRAM metadata mismatch.");
            ValidateMap(game,map,dedicated);
            if(!directoryOnly)game=FcMbcxCodec.LoadRom(game,read,cancellation);
            games.Add(game);
        }
        return games;
    }
    public sealed record SaveImage(string GameId,int SaveType,int Bytes,byte[] Data);
    public static IReadOnlyList<SaveImage> ExportSaves(IReadOnlyList<FcGame> games,ReadOnlySpan<byte> sram)
    {
        if(sram.Length!=524288)throw new InvalidDataException("Expected a 512 KiB SRAM image.");List<SaveImage> result=[];HashSet<string> identities=new(StringComparer.Ordinal);HashSet<int> banks=[];
        foreach(var game in games.Where(game=>game.Info.Battery&&game.Info.StoredRamBytes>0))
        {
            ValidateMap(game,game.OriginalSaveMap,new HashSet<int>());
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
        HashSet<int> allocated=[];foreach(var entry in entries)ValidateMap(entry.Game,entry.SaveMap,allocated);
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
