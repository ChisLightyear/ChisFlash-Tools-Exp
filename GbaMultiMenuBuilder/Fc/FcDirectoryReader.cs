using System.Buffers.Binary;
using GbaMultiMenuBuilder.Md;
namespace GbaMultiMenuBuilder.Fc;

public static class FcDirectoryReader
{
    public static (byte[] Prefix,IReadOnlyList<FcMetadataRange> Ranges) Read(IFcCardIo io,CancellationToken cancellation)
    {
        byte[] prefix=Enumerable.Repeat((byte)255,FcMbcxCodec.PrefixBytes).ToArray();List<FcMetadataRange> ranges=[];
        void Capture(int address,int bytes)
        {
            cancellation.ThrowIfCancellationRequested();
            if(address<0||bytes<0||address>prefix.Length-bytes)throw new InvalidDataException("MIX metadata range exceeds the prefix.");
            if(bytes==0)return;
            if(ranges.Any(range=>address>=range.Address&&address+bytes<=range.Address+range.Bytes))return;
            byte[] data=io.Read(address,bytes,cancellation);if(data.Length!=bytes)throw new EndOfStreamException("Incomplete MIX metadata read.");
            data.CopyTo(prefix,address);ranges.Add(new(address,bytes));
        }
        int Word(int address)=>BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(address));
        int Number(int address)=>checked((int)BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(address)));
        Capture(FcMbcxCodec.ConfigOffset,64);
        if(GameLibraryDirectory.IsLibrary(prefix.AsSpan(FcMbcxCodec.ConfigOffset)))
        {
            int count=Word(GameLibraryDirectory.Base+8),records=Number(GameLibraryDirectory.Base+12),end=Number(GameLibraryDirectory.Base+16),floor=checked(records+count*96);
            if(Word(GameLibraryDirectory.Base+4)!=1||Word(GameLibraryDirectory.Base+6)!=96||records!=GameLibraryDirectory.Base+64||floor>end||end>prefix.Length)
                throw new InvalidDataException("Invalid GLB2 metadata bounds.");
            Capture(records,count*96);
            for(int index=0;index<count;index++){
                int record=records+index*96,launch=Number(record+36),bytes=Word(record+40),info=Number(record+52);
                if(bytes is <16 or >144||launch<floor||launch>end-bytes||info<floor||info>end-128)throw new InvalidDataException("Invalid GLB2 launch/name range.");
                Capture(launch,bytes);Capture(info,128);
            }
        }
        else
        {
            Capture(FcMbcxCodec.DirectoryOffset,64);Capture(MdMbcxCodec.Config,64);
            if(FcMbcxCodec.IsFcHeaders(prefix.AsSpan(FcMbcxCodec.ConfigOffset,64),prefix.AsSpan(FcMbcxCodec.DirectoryOffset,64)))
            {
                int count=Word(FcMbcxCodec.DirectoryOffset+10);
                if(count>99||Word(FcMbcxCodec.DirectoryOffset+12)!=128||Number(FcMbcxCodec.DirectoryOffset+16)!=FcMbcxCodec.EntriesOffset)throw new InvalidDataException("Invalid legacy FC directory bounds.");
                Capture(FcMbcxCodec.EntriesOffset,count*128);
                for(int index=0;index<count;index++){
                    int bank=Number(FcMbcxCodec.EntriesOffset+index*128+4);
                    if(bank is <64 or >2047)throw new InvalidDataException("Invalid legacy FC bank.");
                    Capture(FcMbcxCodec.GameConfigOffset+bank*128,128);
                }
                if(prefix.AsSpan(MdMbcxCodec.Config,4).SequenceEqual("MD02"u8)){
                    int mdCount=Word(MdMbcxCodec.Config+6);if(mdCount>99)throw new InvalidDataException("Invalid legacy MD count.");
                    Capture(MdMbcxCodec.Directory,mdCount*32);Capture(MixDirectory.Names,mdCount*128);
                }
            }
        }
        List<FcMetadataRange> merged=[];
        foreach(var range in ranges.OrderBy(range=>range.Address)){
            if(merged.Count>0&&range.Address<=merged[^1].Address+merged[^1].Bytes){var previous=merged[^1];merged[^1]=new(previous.Address,Math.Max(previous.Address+previous.Bytes,range.Address+range.Bytes)-previous.Address);}
            else merged.Add(range);
        }
        return (prefix,merged);
    }
}
