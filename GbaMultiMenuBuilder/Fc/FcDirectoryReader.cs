using System.Buffers.Binary;
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
            if(bytes==0||ranges.Any(range=>address>=range.Address&&address+bytes<=range.Address+range.Bytes))return;
            byte[] data=io.Read(address,bytes,cancellation);if(data.Length!=bytes)throw new EndOfStreamException("Incomplete MIX metadata read.");
            data.CopyTo(prefix,address);ranges.Add(new(address,bytes));
        }
        int Word(int address)=>BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(address));
        int Number(int address)=>checked((int)BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(address)));
        Capture(GameLibraryDirectory.Base,GameLibraryDirectory.HeaderBytes);
        if(GameLibraryDirectory.IsLibrary(prefix.AsSpan(GameLibraryDirectory.Base))&&Word(GameLibraryDirectory.Base+4)==GameLibraryDirectory.Version)
        {
            int count=Word(GameLibraryDirectory.Base+8),records=Number(GameLibraryDirectory.Base+12),end=Number(GameLibraryDirectory.Base+16),floor=checked(records+count*GameLibraryDirectory.RecordBytes);
            if(Word(GameLibraryDirectory.Base+6)!=GameLibraryDirectory.RecordBytes||records!=GameLibraryDirectory.Base+64||floor>end||end>prefix.Length)throw new InvalidDataException("Invalid MIX v2 metadata bounds.");
            Capture(records,count*GameLibraryDirectory.RecordBytes);
            for(int index=0;index<count;index++){
                int record=records+index*GameLibraryDirectory.RecordBytes,launch=Number(record+36),bytes=Word(record+40),info=Number(record+52),map=Number(record+72),banks=Word(record+76);
                if((bytes<16||bytes>(Word(record+42)==LaunchSequence.Flag?LaunchSequence.MaxBlockBytes:144))||launch<floor||launch>end-bytes||info<floor||info>end-128||banks>60||banks==0&&map!=0||banks>0&&(map<floor||map>end-banks))throw new InvalidDataException("Invalid MIX v2 launch/name/map range.");
                Capture(launch,bytes);Capture(info,128);if(banks>0)Capture(map,banks);
                int fullTitle=Number(record+88);
                if(fullTitle!=0){
                    if(fullTitle<floor||fullTitle>end-FullTitleAsset.HeaderBytes)throw new InvalidDataException("Complete-title header outside metadata.");
                    Capture(fullTitle,FullTitleAsset.HeaderBytes);var title=FullTitleAsset.Header(prefix.AsSpan(fullTitle,FullTitleAsset.HeaderBytes));
                    if(fullTitle>end-title.Bytes)throw new InvalidDataException("Complete-title asset outside metadata.");
                    Capture(fullTitle+FullTitleAsset.HeaderBytes,title.NameBytes);
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
