using System.Buffers.Binary;

namespace GbaMultiMenuBuilder.Fc;

/// <summary>Card-owned register sequences; the MCU executes the same operations for every core.</summary>
public static class LaunchSequence
{
    public const ushort Flag=1,Version=1;
    public const uint Magic=0x31515343; // CSQ1
    public const int MaxBlockBytes=1024,FooterBytes=16,OperationBytes=8,MaxControlOperations=8;
    private const byte Write=1,Wait=2,Region=3,WaitRegion=4,Reject=5;
    private readonly record struct Op(byte Kind,byte Arg,ushort Address,ushort Value,ushort Mask);
    private static ushort U16(ReadOnlySpan<byte> p,int n)=>BinaryPrimitives.ReadUInt16LittleEndian(p[n..]);
    private static uint U32(ReadOnlySpan<byte> p,int n)=>BinaryPrimitives.ReadUInt32LittleEndian(p[n..]);
    public static int MaximumPayloadBytes(bool nes)=>(nes?128:32)+FooterBytes+(nes?41:12)*OperationBytes;

    public static byte[] Build(uint core,ReadOnlySpan<byte> config,ReadOnlySpan<byte> map)
    {
        List<Op> prepare=[],start=[],stop=[];
        void W(List<Op> ops,int a,uint v)=>ops.Add(new(Write,0,(ushort)a,(ushort)v,0));
        void Pair(int a,uint v){W(prepare,a,v);W(prepare,a+1,v>>16);}
        void Poll(List<Op> ops,int a,int mask,int value,int ms)=>ops.Add(new(Wait,checked((byte)(ms/20)),(ushort)a,(ushort)value,(ushort)mask));
        void SetRegion(int a,uint literal,int mask,int shift)=>prepare.Add(new(Region,(byte)shift,(ushort)a,(ushort)literal,(ushort)mask));
        bool representable=true;
        if(core==GameLibraryDirectory.FcCore)
        {
            if(config.Length!=128)throw new InvalidDataException("Invalid NES configuration length.");
            byte flags=config[5];uint prg=U32(config,16),chr=U32(config,24),ram=U32(config,32);
            ulong packed=0;for(int i=0;i<Math.Min(map.Length,8);i++)packed|=(ulong)map[i]<<(i*6);
            W(prepare,0x1122,U32(config,8)&0xfff);
            W(prepare,0x1123,(uint)(config[6]&15)|((uint)(flags&1)<<4)|((uint)(flags&2)<<4)|((uint)(flags&4)<<4)|((uint)(flags&8)<<4));
            Pair(0x1124,prg);Pair(0x1126,chr);Pair(0x1128,U32(config,28));W(prepare,0x112a,ram);
            W(prepare,0x112b,(uint)packed);W(prepare,0x112c,(uint)(packed>>16));W(prepare,0x112d,(uint)(packed>>32));W(prepare,0x112e,(uint)map.Length);
            Pair(0x112f,U32(config,12));W(prepare,0x1131,U32(config,56));
            W(prepare,0x1132,(U32(config,56)>>16)|((uint)(flags&0x20)<<9)|((uint)(flags&0x40)<<9));
            W(prepare,0x1133,U32(config,44));W(prepare,0x1134,(U32(config,44)>>16)|((U32(config,60)>>16)<<5));W(prepare,0x1135,U32(config,60));
            Pair(0x1136,U32(config,64));Pair(0x1138,U32(config,68));Pair(0x113a,U32(config,72));
            Pair(0x113c,U32(config,36));Pair(0x113e,U32(config,20));SetRegion(0x1023,0,3,0);
            prepare.Add(new(WaitRegion,0,0x1023,0,0xffff));W(prepare,0x1150,1);Poll(prepare,0x1121,8,8,3000);
            W(start,0x1120,1);W(start,0x0081,0);Poll(start,0x1121,0x11,0x11,500);
            W(stop,0x0081,1);Poll(stop,0x100f,2,2,300);W(stop,0x1120,0);Poll(stop,0x1121,0x21,0x20,1000);
            // Packing remains available for every encodable mapper. This
            // sequence targets the current register widths, not a mapper list.
            representable=map.Length<=8 && ram<=32768 && chr<=524288 && U32(config,28)<=524288 &&
                prg>0 && prg<0x2000000 && !(config[5]>=128) && !(config[6]>=16);
        }
        else if(core==GameLibraryDirectory.MdCore)
        {
            if(config.Length!=32)throw new InvalidDataException("Invalid MD configuration length.");
            uint ram=U32(config,12);
            W(prepare,0x1122,U16(config,0));Pair(0x1123,U32(config,4));SetRegion(0x1125,U16(config,2),1,0);
            W(prepare,0x1126,0);W(prepare,0x1127,ram!=0?0x8000u:0u);W(prepare,0x1128,ram==0?0:ram-1);
            W(prepare,0x1120,1);Poll(prepare,0x1121,21,21,200);
            W(start,0x1120,3);W(stop,0x1120,0);Poll(stop,0x1121,1,0,1000);
            representable=map.Length<=8 && ram<=65536;
        }
        else if(core==GameLibraryDirectory.SnesCore)
        {
            if(config.Length!=32)throw new InvalidDataException("Invalid SNES configuration length.");
            uint rom=U32(config,4),ram=U32(config,8),mask=rom==0x300000?0x3fffff:rom-1;
            W(prepare,0x1122,U32(config,0)/16384);Pair(0x1123,mask);
            SetRegion(0x1125,(config[24]==2?1u:0u)|(rom==0x300000?2u:0u)|(ram!=0?8u:0u),4,2);
            Pair(0x1127,ram==0?0:ram-1);W(prepare,0x1120,1);Poll(prepare,0x1121,7,6,500);
            W(start,0x1120,3);Poll(start,0x1121,1,1,500);W(stop,0x1120,0);Poll(stop,0x1121,3,2,1000);
            representable=config[24] is 1 or 2 && config[26]<=2 && rom<=0x400000 &&
                (rom!=0 && ((rom&(rom-1))==0 || rom==0x300000)) && ram<=131072;
        }
        else throw new InvalidDataException("Unknown launch sequence producer.");
        // Preserve unsupported hardware configurations in the directory;
        // the launch sequence reports a bounded runtime error, not a pack ban.
        if(!representable){prepare.Clear();prepare.Add(new(Reject,0,0,4,0));}
        Op[] operations=[..prepare,..start,..stop];
        byte[] result=new byte[config.Length+operations.Length*OperationBytes+FooterBytes];config.CopyTo(result);
        for(int i=0;i<operations.Length;i++)
        {
            var op=operations[i];var output=result.AsSpan(config.Length+i*OperationBytes,OperationBytes);
            output[0]=op.Kind;output[1]=op.Arg;BinaryPrimitives.WriteUInt16LittleEndian(output[2..],op.Address);
            BinaryPrimitives.WriteUInt16LittleEndian(output[4..],op.Value);BinaryPrimitives.WriteUInt16LittleEndian(output[6..],op.Mask);
        }
        var footer=result.AsSpan(result.Length-FooterBytes);BinaryPrimitives.WriteUInt32LittleEndian(footer,Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(footer[4..],Version);BinaryPrimitives.WriteUInt16LittleEndian(footer[6..],(ushort)config.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(footer[8..],(ushort)prepare.Count);footer[10]=(byte)start.Count;footer[11]=(byte)stop.Count;
        BinaryPrimitives.WriteUInt16LittleEndian(footer[12..],2);
        if(result.Length>MaximumPayloadBytes(core==GameLibraryDirectory.FcCore))throw new InvalidDataException("Launch sequence capacity contract changed.");
        _=Configuration(result,config.Length);return result;
    }

    public static ReadOnlySpan<byte> Configuration(ReadOnlySpan<byte> payload,int expectedBytes)
    {
        if(payload.Length<FooterBytes || payload.Length>MaxBlockBytes-16)throw new InvalidDataException("Invalid launch sequence size.");
        var footer=payload[^FooterBytes..];int config=U16(footer,6),prepare=U16(footer,8),start=footer[10],stop=footer[11];
        if(U32(footer,0)!=Magic || U16(footer,4)!=Version || config!=expectedBytes || prepare==0 || start==0 || stop==0 ||
           start>MaxControlOperations || stop>MaxControlOperations || U16(footer,12)==0 || U16(footer,14)!=0 ||
           config+(prepare+start+stop)*OperationBytes+FooterBytes!=payload.Length)throw new InvalidDataException("Invalid launch sequence footer.");
        for(int i=0;i<prepare+start+stop;i++)
        {
            var op=payload.Slice(config+i*OperationBytes,OperationBytes);int address=U16(op,2),value=U16(op,4),mask=U16(op,6);
            bool valid=op[0]==Reject?op[1]==0&&address==0&&value is >0 and <=255&&mask==0:
                address>=0x0080&&address<0x2000&&!(address>=0x1100&&address<=0x1104)&&
                (op[0]==Write?op[1]==0&&mask==0:op[0]==Wait?op[1]!=0&&mask!=0&&(value&~mask)==0:
                 op[0] is Region or WaitRegion&&op[1]<16&&mask!=0);
            if(!valid)throw new InvalidDataException("Invalid launch sequence operation.");
        }
        return payload[..config];
    }
}
