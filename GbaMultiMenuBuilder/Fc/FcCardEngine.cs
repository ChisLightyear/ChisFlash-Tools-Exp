namespace GbaMultiMenuBuilder.Fc;

public interface IFcCardIo : IDisposable
{
    string DeviceId {get;}
    int Capacity {get;}
    int SectorBytes {get;}
    bool Writable {get;}
    byte[] Read(int address,int length,CancellationToken ct);
    void RewriteSector(int address,byte[] data,CancellationToken ct);
    void InitializeSave(int address,int length,byte fill,CancellationToken ct)=>throw new NotSupportedException("This backend cannot initialize newly allocated MIX SRAM.");
}
public static class FcCardEngine
{
    public static FcCardSnapshot Scan(IFcCardIo io,CancellationToken ct,IProgress<FcProgress> progress)
    {
        Validate(io);progress.Report(new("Reading MBCX directory",0));
        byte[] prefix=io.Read(0,FcMbcxCodec.PrefixBytes,ct);bool fc=FcMbcxCodec.IsFcPrefix(prefix);
        List<FcGame> games=[];string? warning=!fc&&FcMbcxCodec.HasFcIdentity(prefix.AsSpan(FcMbcxCodec.ConfigOffset,64))?
            "Unsupported FC card format; import the ROMs again and rebuild with the current PC tool.":null;
        if(fc)try{games=FcMbcxCodec.Read(prefix,io.Capacity,(a,n)=>io.Read(a,n,ct),ct);}
        catch(InvalidDataException ex){fc=false;warning="Damaged FC directory; explicit conversion/rebuild required: "+ex.Message;}

        progress.Report(new(fc?$"FC: {games.Count} ROMs":"Non-FC card: conversion requires confirmation",100));
        return new(io.DeviceId,Math.Min(io.Capacity,FcMbcxCodec.CapacityBytes),io.SectorBytes,prefix,games,fc,io.Writable,warning);
    }
    private static void Validate(IFcCardIo io)
    {
        if(io.Capacity<FcMbcxCodec.PrefixBytes||io.SectorBytes<16384||io.SectorBytes>FcMbcxCodec.ConfigOffset||FcMbcxCodec.PrefixBytes%io.SectorBytes!=0)
            throw new InvalidDataException("Unsupported MBCX capacity/sector geometry.");
    }
    public static FcCardSnapshot Write(IFcCardIo io,FcCardSnapshot expected,IReadOnlyList<FcGame> games,bool convert,CancellationToken ct,IProgress<FcProgress> progress)
    {
        Validate(io);
        if(!io.Writable||!expected.Writable)throw new InvalidOperationException("Card is write-protected or its capacity is unavailable.");
        if(io.DeviceId!=expected.DeviceId||Math.Min(io.Capacity,FcMbcxCodec.CapacityBytes)!=expected.Capacity||io.SectorBytes!=expected.SectorBytes)
            throw new InvalidOperationException("The selected programmer/card changed; scan again.");
        if(!expected.IsFc&&!convert)throw new InvalidOperationException("Explicit FC conversion confirmation is required.");
        if(!io.Read(0,FcMbcxCodec.PrefixBytes,ct).AsSpan().SequenceEqual(expected.Prefix))throw new InvalidOperationException("Card directory changed; scan again before writing.");
        var layout=FcMbcxCodec.Plan(games,expected.Capacity,expected.IsFc);
        foreach(var p in layout.Entries.Where(p=>p.Game.Md is null))
        {
            var old=expected.Games.FirstOrDefault(g=>g.Id==p.Game.Id);
            if(old is null)continue;
            NesConfiguration.Validate(old,p.Game.Info);
            if(old.Info.Battery&&old.OriginalSaveMap.Length>0&&
                !p.SaveMap.Take(old.OriginalSaveMap.Length).SequenceEqual(old.OriginalSaveMap))
                throw new InvalidDataException("Configuration update would move an existing FC save.");
        }
        Dictionary<int,byte[]> sectors=[];
        foreach(var p in layout.Entries)
        {
            bool unchanged=expected.IsFc&&expected.Games.Any(g=>g.Id==p.Game.Id&&g.OriginalOffset==p.Offset&&g.Data.AsSpan().SequenceEqual(p.Game.Data));
            if(unchanged)continue;
            int end=p.Offset+p.AllocatedBytes;
            for(int address=p.Offset;address<end;)
            {
                ct.ThrowIfCancellationRequested();int sector=address/io.SectorBytes*io.SectorBytes;
                if(!sectors.TryGetValue(sector,out var data)){data=io.Read(sector,io.SectorBytes,ct);sectors[sector]=data;}
                int n=Math.Min(end-address,io.SectorBytes-(address-sector));
                FcMbcxCodec.CopyStoredData(p.Game,address-p.Offset,data.AsSpan(address-sector,n));
                address+=n;
            }
        }
        // Font loading/rendering must finish before the first NOR erase.
        // A missing 12px font cannot leave a partially rewritten card.
        byte[] finalPrefix=FcMbcxCodec.BuildPrefix(layout.Entries,expected.IsFc?expected.Prefix:null);
        if(sectors.Count==0&&finalPrefix.AsSpan().SequenceEqual(expected.Prefix))
        {progress.Report(new("MIX configuration is already current; no writes",100));return expected;}
        byte[] invalidPrefix=(byte[])expected.Prefix.Clone();
        invalidPrefix.AsSpan(FcMbcxCodec.ConfigOffset,64).Fill(255);
        invalidPrefix.AsSpan(Md.MdMbcxCodec.Config,64).Fill(255);
        byte[] current=(byte[])expected.Prefix.Clone();
        void Rewrite(int address,byte[] data)
        {
            ct.ThrowIfCancellationRequested();io.RewriteSector(address,data,ct);
            if(!io.Read(address,data.Length,ct).AsSpan().SequenceEqual(data))throw new IOException("MIX NOR readback verification failed.");
        }
        void Prefix(byte[] target,string phase)
        {
            var addresses=Enumerable.Range(0,FcMbcxCodec.PrefixBytes/io.SectorBytes).Select(i=>i*io.SectorBytes)
                .Where(a=>!target.AsSpan(a,io.SectorBytes).SequenceEqual(current.AsSpan(a,io.SectorBytes)))
                .OrderBy(a=>(a<=FcMbcxCodec.ConfigOffset&&a+io.SectorBytes>FcMbcxCodec.ConfigOffset)||
                            (a<=Md.MdMbcxCodec.Config&&a+io.SectorBytes>Md.MdMbcxCodec.Config)?1:0).ToArray();
            for(int i=0;i<addresses.Length;i++)
            {
                ct.ThrowIfCancellationRequested();int a=addresses[i];
                Rewrite(a,target.AsSpan(a,io.SectorBytes).ToArray());
                target.AsSpan(a,io.SectorBytes).CopyTo(current.AsSpan(a,io.SectorBytes));
                progress.Report(new(phase,(i+1)*100/addresses.Length));
            }
        }
        // Both readers are hidden before any payload/SRAM changes. Header
        // sectors are committed last, after all directories and bitmaps.
        Prefix(invalidPrefix,"Invalidate FC/MD directories");
        int index=0;
        foreach(var pair in sectors.OrderBy(p=>p.Key))
        {ct.ThrowIfCancellationRequested();Rewrite(pair.Key,pair.Value);progress.Report(new("Writing FC / MD ROM data",++index*100/sectors.Count));}
        foreach(var p in layout.Entries.Where(p=>p.Game.Md is null&&p.Game.Info.Battery))
        {
            var old=expected.Games.FirstOrDefault(g=>g.Id==p.Game.Id&&g.Info.Battery);
            if(old is null)continue;
            int oldBytes=old.Info.StoredRamBytes;
            if(p.Game.Info.StoredRamBytes>oldBytes&&oldBytes%8192!=0)
                io.InitializeSave(p.SaveMap[oldBytes/8192]*8192+oldBytes%8192,
                    Math.Min(p.Game.Info.StoredRamBytes-oldBytes,8192-oldBytes%8192),0,ct);
            // Only appended banks are initialized. Existing save bytes and all
            // other FC/MD SRAM allocations remain untouched.
            foreach(byte bank in p.SaveMap.Skip(old.OriginalSaveMap.Length))
                io.InitializeSave(bank*8192,8192,0,ct);
        }
        foreach(var p in layout.Entries.Where(p=>p.Game.Md is {SaveBytes:>0}))
        {
            bool preserve=expected.IsFc&&expected.Games.Any(g=>g.Id==p.Game.Id&&g.Md==p.Game.Md&&
                g.Data.AsSpan().SequenceEqual(p.Game.Data)&&g.OriginalSaveMap.SequenceEqual(p.SaveMap));
            if(!preserve)io.InitializeSave(p.SaveMap[0]*8192,p.Game.Md!.SaveBytes,(p.Game.Md.Flags&8)!=0?(byte)0:(byte)255,ct);
        }
        ct.ThrowIfCancellationRequested();Prefix(finalPrefix,"Commit MIX directories");
        progress.Report(new("FC directory committed",100));
        // Use the verified committed plan as the new snapshot. Existing saves
        // remain untouched; only added FC RAM and new MD saves are initialized.
        var committed=layout.Entries.Select(p=>new FcGame {Name=p.Game.Name,Data=p.Game.Data,Info=p.Game.Info,Md=p.Game.Md,OriginalOffset=p.Offset,
            OriginalAllocated=p.AllocatedBytes,OriginalSaveMap=p.SaveMap,OriginalBattery=p.Game.Info.Battery,OriginalStoredRamBytes=p.Game.Info.StoredRamBytes}).ToList();
        return new(io.DeviceId,expected.Capacity,io.SectorBytes,current,committed,true,true);
    }
}
