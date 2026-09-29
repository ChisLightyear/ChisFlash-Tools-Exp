using System.Buffers.Binary;
using System.Text;
using GbaMultiMenuBuilder.Md;

namespace GbaMultiMenuBuilder.Fc;

// MIX uses the existing FC v7 reader plus a disjoint MD directory. Both
// platforms share the same 16 KiB ROM allocator and dedicated SRAM pool.
public static class MixDirectory
{
    public const int Names=0xb0000;
    public static int ListAddress(int slot)=>slot<128?FcMbcxCodec.TitleListOffset+slot*512:0x80000+(slot-128)*512;
    public static int DetailAddress(int slot)=>slot<128?FcMbcxCodec.TitleDetailOffset+slot*1024:0x90000+(slot-128)*1024;
    private static string Title(string name){name=string.IsNullOrWhiteSpace(name)?"UNKNOWN":name.Trim();if(name.Length>46)name=name[..46];if(char.IsHighSurrogate(name[^1]))name=name[..^1];return name;}
    private static int U16(ReadOnlySpan<byte> b,int p)=>BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
    private static int U32(ReadOnlySpan<byte> b,int p)=>checked((int)BinaryPrimitives.ReadUInt32LittleEndian(b[p..]));
    private static void W16(Span<byte> b,int p,int n)=>BinaryPrimitives.WriteUInt16LittleEndian(b[p..],checked((ushort)n));
    private static void W32(Span<byte> b,int p,int n)=>BinaryPrimitives.WriteUInt32LittleEndian(b[p..],checked((uint)n));
    public static bool IsMix(ReadOnlySpan<byte> prefix)=>prefix.Length>=FcMbcxCodec.PrefixBytes&&prefix.Slice(FcMbcxCodec.ConfigOffset+40,4).SequenceEqual("MIX1"u8);
    public static void Write(byte[] prefix,IReadOnlyList<FcPlacement> games,IReadOnlyList<FcPlacement> fc)
    {
        if(games.Count>99)throw new InvalidDataException("MIX supports at most 99 games per core.");
        "MIX1"u8.CopyTo(prefix.AsSpan(FcMbcxCodec.ConfigOffset+40));
        var header=prefix.AsSpan(MdMbcxCodec.Config,64);header.Clear();
        "MD02"u8.CopyTo(header);W16(header,4,1);W16(header,6,games.Count);
        prefix.AsSpan(MdMbcxCodec.Directory,99*32).Clear();
        prefix.AsSpan(0x80000,0x30000).Clear();
        prefix.AsSpan(Names,0x4000).Clear();
        Dictionary<string,int> slots=new(StringComparer.Ordinal);
        for(int i=0;i<fc.Count;i++)slots.TryAdd(Title(fc[i].Game.Name),i);
        int next=fc.Count;
        for(int i=0;i<games.Count;i++)
        {
            var p=games[i];var md=p.Game.Md!;var e=prefix.AsSpan(MdMbcxCodec.Directory+i*32,32);
            W16(e,0,p.Offset/16384);W16(e,2,md.Flags);W32(e,4,p.Game.Data.Length/2);
            W32(e,8,p.SaveMap.Length==0?0:p.SaveMap[0]*8192);W32(e,12,md.SaveBytes);
            string name=Title(p.Game.Name);
            if(!slots.TryGetValue(name,out int slot)){slot=next++;slots.Add(name,slot);}
            W32(e,16,ListAddress(slot));W32(e,20,Names+i*128);W32(e,24,DetailAddress(slot));W32(e,28,slot);
            var bitmap=FcTitleRenderer.Render(name);
            bitmap.List.CopyTo(prefix.AsSpan(ListAddress(slot)));bitmap.Detail.CopyTo(prefix.AsSpan(DetailAddress(slot)));
            var title=prefix.AsSpan(Names+i*128,128);W16(title,0,name.Length);Encoding.Unicode.GetBytes(name).CopyTo(title[2..]);
            string size(int n)=>n==0?"NONE    ":(n/1024).ToString().PadLeft(6)+"KB";
            Encoding.ASCII.GetBytes((md.Flags&64)!=0?"PIER    ":"MD      ").CopyTo(title[96..]);
            Encoding.ASCII.GetBytes(size(p.Game.Data.Length)).CopyTo(title[104..]);
            Encoding.ASCII.GetBytes(size(md.SaveBytes)).CopyTo(title[112..]);
            Encoding.ASCII.GetBytes((md.Flags&1)!=0?"PAL     ":"NTSC    ").CopyTo(title[120..]);
        }
    }
    public static void Read(byte[] prefix,int capacity,Func<int,int,byte[]> read,CancellationToken ct,List<FcGame> games)
    {
        if(!IsMix(prefix))return;
        var h=prefix.AsSpan(MdMbcxCodec.Config,64);
        if(!h[..4].SequenceEqual("MD02"u8)||U16(h,4)!=1||U16(h,6)>99)throw new InvalidDataException("Invalid MIX MD directory.");
        for(int i=0;i<U16(h,6);i++)
        {
            ct.ThrowIfCancellationRequested();var e=prefix.AsSpan(MdMbcxCodec.Directory+i*32,32);
            int offset=checked(U16(e,0)*16384),length=checked(U32(e,4)*2),size=FcMbcxCodec.Align(length,16384);
            if(length<512||offset<FcMbcxCodec.PrefixBytes||offset>Math.Min(capacity,FcMbcxCodec.CapacityBytes)-size||
               games.Any(g=>offset<g.OriginalOffset+g.OriginalAllocated&&offset+size>g.OriginalOffset))
                throw new InvalidDataException("MIX ROM ranges overlap or exceed capacity.");
            int slot=U32(e,28);
            if(slot<0||slot>=198||U32(e,16)!=ListAddress(slot)||U32(e,24)!=DetailAddress(slot)||U32(e,20)!=Names+i*128)throw new InvalidDataException("Invalid shared title address.");
            var name=prefix.AsSpan(Names+i*128,128);int chars=U16(name,0);
            if(chars is <1 or >46)throw new InvalidDataException("Invalid MD title length.");
            byte[] data=read(offset,length);if(data.Length!=length)throw new EndOfStreamException();
            var game=MdMbcxCodec.CreateGame(data,Encoding.Unicode.GetString(name.Slice(2,chars*2)));
            var md=game.Md!;int save=U32(e,8),bytes=U32(e,12),banks=game.Info.SaveBanks;
            if(md.Flags!=U16(e,2)||md.SaveBytes!=bytes||save%8192!=0||save<0||save>60*8192-banks*8192)
                throw new InvalidDataException("Invalid MD save configuration.");
            byte[] map=Enumerable.Range(save/8192,banks).Select(x=>(byte)x).ToArray();
            games.Add(new FcGame{Name=game.Name,Data=data,Info=game.Info,Md=md,OriginalOffset=offset,OriginalAllocated=size,
                OriginalBattery=game.Info.Battery,OriginalSaveMap=map});
        }
        var dedicated=games.Where(g=>g.Info.Battery).SelectMany(g=>g.OriginalSaveMap).ToArray();
        if(dedicated.Any(x=>x>=60)||dedicated.Distinct().Count()!=dedicated.Length)
            throw new InvalidDataException("FC/MD battery saves overlap.");
    }
}
