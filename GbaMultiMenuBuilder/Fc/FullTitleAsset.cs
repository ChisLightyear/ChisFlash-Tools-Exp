using System.Buffers.Binary;
using System.Text;

namespace GbaMultiMenuBuilder.Fc;

// GLB2 v2 optional title asset, referenced by record offset 88. All fields LE.
public static class FullTitleAsset
{
    public const int HeaderBytes=32,Height=12,TileWidth=256,TileBytes=384,TileRecordBytes=388;
    private static readonly UTF8Encoding Utf8=new(false,true);
    public static uint Crc(ReadOnlySpan<byte> data){uint crc=uint.MaxValue;foreach(byte value in data){crc^=value;for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0u);}return ~crc;}
    private static int Number(ReadOnlySpan<byte> data,int at)=>checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[at..]));
    public static int EstimateBytes(string name)
    {
        name=string.IsNullOrWhiteSpace(name)?"UNKNOWN":name.Trim();int width=0;
        foreach(var rune in name.EnumerateRunes())width=checked(width+(Rune.IsControl(rune)||rune.Value==32?4:rune.Value<128?6:12));
        return checked(HeaderBytes+Utf8.GetByteCount(name)+(width+TileWidth-1)/TileWidth*TileRecordBytes);
    }
    public static byte[] Write(FcTitleBitmap title)
    {
        int tiles=(title.ScrollWidth+TileWidth-1)/TileWidth;
        byte[] name=Utf8.GetBytes(title.Name),result=new byte[checked(HeaderBytes+name.Length+tiles*TileRecordBytes)];
        "TTL1"u8.CopyTo(result);BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4),1);BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6),HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8),checked((uint)result.Length));BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12),checked((uint)title.ScrollWidth));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16),checked((uint)name.Length));BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20),Crc(title.Scroll));BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24),Crc(name));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28),Crc(result.AsSpan(0,28)));name.CopyTo(result,HeaderBytes);
        int stride=(title.ScrollWidth+7)/8;
        for(int tile=0;tile<tiles;tile++){
            var target=result.AsSpan(HeaderBytes+name.Length+tile*TileRecordBytes,TileRecordBytes);
            int copied=Math.Min(32,stride-tile*32);
            for(int y=0;y<Height;y++)title.Scroll.AsSpan(y*stride+tile*32,copied).CopyTo(target.Slice(y*32,copied));
            BinaryPrimitives.WriteUInt32LittleEndian(target[TileBytes..],Crc(target[..TileBytes]));
        }
        return result;
    }
    public static (int Bytes,int NameBytes,int Width) Header(ReadOnlySpan<byte> header)
    {
        if(header.Length<HeaderBytes||!header[..4].SequenceEqual("TTL1"u8)||BinaryPrimitives.ReadUInt16LittleEndian(header[4..])!=1||BinaryPrimitives.ReadUInt16LittleEndian(header[6..])!=HeaderBytes||Crc(header[..28])!=BinaryPrimitives.ReadUInt32LittleEndian(header[28..]))throw new InvalidDataException("Invalid complete-title header.");
        int width=Number(header,12),names=Number(header,16),bytes=Number(header,8);
        if(width<1||names<1||names>(long)width*4||bytes>FcMbcxCodec.PrefixBytes||bytes!=HeaderBytes+(long)names+((long)width+TileWidth-1)/TileWidth*TileRecordBytes)throw new InvalidDataException("Invalid complete-title dimensions or lengths.");
        return(bytes,names,width);
    }
    public static string Read(ReadOnlySpan<byte> asset,bool verifyBitmap)
    {
        var header=Header(asset);if(asset.Length<header.Bytes)throw new InvalidDataException("Truncated complete-title asset.");
        var name=asset.Slice(HeaderBytes,header.NameBytes);if(Crc(name)!=BinaryPrimitives.ReadUInt32LittleEndian(asset[24..]))throw new InvalidDataException("Complete-title name CRC mismatch.");
        if(verifyBitmap){
            int stride=(header.Width+7)/8,tiles=(header.Width+TileWidth-1)/TileWidth;byte[] bitmap=new byte[stride*Height];
            for(int tile=0;tile<tiles;tile++){
                var source=asset.Slice(HeaderBytes+header.NameBytes+tile*TileRecordBytes,TileRecordBytes);
                if(Crc(source[..TileBytes])!=BinaryPrimitives.ReadUInt32LittleEndian(source[TileBytes..]))throw new InvalidDataException("Complete-title tile CRC mismatch.");
                int copied=Math.Min(32,stride-tile*32);for(int y=0;y<Height;y++)source.Slice(y*32,copied).CopyTo(bitmap.AsSpan(y*stride+tile*32,copied));
            }
            if(Crc(bitmap)!=BinaryPrimitives.ReadUInt32LittleEndian(asset[20..]))throw new InvalidDataException("Complete-title bitmap CRC mismatch.");
        }
        string result=Utf8.GetString(name);int expectedWidth=0;foreach(var rune in result.EnumerateRunes())expectedWidth=checked(expectedWidth+(Rune.IsControl(rune)||rune.Value==32?4:rune.Value<128?6:12));
        if(string.IsNullOrWhiteSpace(result)||EstimateBytes(result)!=header.Bytes||expectedWidth!=header.Width)throw new InvalidDataException("Complete-title name and geometry disagree.");return result;
    }
}
