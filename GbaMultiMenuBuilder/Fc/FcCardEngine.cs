namespace GbaMultiMenuBuilder.Fc;

public interface IFcCardIo : IDisposable
{
    string DeviceId {get;}
    int Capacity {get;}
    int SectorBytes {get;}
    bool Writable {get;}
    byte[] Read(int address,int length,CancellationToken ct);
    void RewriteSector(int address,byte[] data,CancellationToken ct);
    byte[] ReadSave(int address,int length,CancellationToken ct)=>throw new NotSupportedException("This backend cannot read MIX SRAM.");
    void WriteSave(int address,byte[] data,CancellationToken ct)=>throw new NotSupportedException("This backend cannot restore MIX SRAM.");
    void InitializeSave(int address,int length,byte fill,CancellationToken ct)=>throw new NotSupportedException("This backend cannot initialize newly allocated MIX SRAM.");
}
public static class FcCardEngine
{
    public static FcCardSnapshot Scan(IFcCardIo io,CancellationToken ct,IProgress<FcProgress> progress)
    {
        Validate(io);progress.Report(new("Reading MIX directory and configuration",0));
        var metadata=FcDirectoryReader.Read(io,ct);byte[] prefix=metadata.Prefix;bool fc=FcMbcxCodec.IsFcPrefix(prefix);
        List<FcGame> games=[];string? warning=!fc&&FcMbcxCodec.HasFcIdentity(prefix.AsSpan(FcMbcxCodec.ConfigOffset,64))?
            "Unsupported MIX card format; explicit rebuild required.":null;
        if(fc)try{games=FcMbcxCodec.Read(prefix,io.Capacity,(_,_)=>throw new InvalidOperationException("Directory scan must not read ROM content."),ct,true);}
        catch(InvalidDataException ex){fc=false;warning="Damaged MIX directory; explicit conversion/rebuild required: "+ex.Message;}
        progress.Report(new(fc?$"MIX: {games.Count} entries; ROM content not read":"Non-MIX card: conversion requires confirmation",100));
        return new(io.DeviceId,Math.Min(io.Capacity,FcMbcxCodec.CapacityBytes),io.SectorBytes,prefix,games,fc,io.Writable,warning,metadata.Ranges);
    }
    private static void ValidateSnapshot(IFcCardIo io,FcCardSnapshot expected,CancellationToken ct,bool requireMix=true)
    {
        Validate(io);
        if((requireMix&&!expected.IsFc)||io.DeviceId!=expected.DeviceId||Math.Min(io.Capacity,FcMbcxCodec.CapacityBytes)!=expected.Capacity||io.SectorBytes!=expected.SectorBytes)
            throw new InvalidOperationException("The selected programmer/card changed; scan again.");
        var ranges=expected.MetadataRanges??new[]{new FcMetadataRange(0,FcMbcxCodec.PrefixBytes)};
        foreach(var range in ranges){ct.ThrowIfCancellationRequested();if(!io.Read(range.Address,range.Bytes,ct).AsSpan().SequenceEqual(expected.Prefix.AsSpan(range.Address,range.Bytes)))throw new InvalidOperationException("Card directory changed; scan again before accessing content or saves.");}
    }
    public static IReadOnlyList<FcGame> LoadGames(IFcCardIo io,FcCardSnapshot expected,IReadOnlyList<FcGame> games,CancellationToken ct,IProgress<FcProgress> progress)
    {
        ValidateSnapshot(io,expected,ct);List<FcGame> loaded=[];int pending=games.Count(game=>!game.HasRomData),done=0;
        foreach(var game in games){
            ct.ThrowIfCancellationRequested();
            if(!game.HasRomData){
                if(!expected.Games.Any(original=>original.Id==game.Id&&original.OriginalOffset==game.OriginalOffset&&original.RomBytes==game.RomBytes&&original.DirectoryIdentity==game.DirectoryIdentity&&original.OriginalConfig.AsSpan().SequenceEqual(game.OriginalConfig)))throw new InvalidOperationException("ROM source is not part of the scanned card.");
                progress.Report(new($"Reading ROM {done+1}/{pending}: {game.Name}",done*100/Math.Max(1,pending)));
            }
            loaded.Add(FcMbcxCodec.LoadRom(game,(address,bytes)=>io.Read(address,bytes,ct),ct));
            if(!game.HasRomData)progress.Report(new("Required ROM content loaded",++done*100/Math.Max(1,pending)));
        }
        return loaded;
    }
    public static IReadOnlyList<GameLibraryDirectory.SaveImage> ExportSaves(IFcCardIo io,FcCardSnapshot expected,CancellationToken ct,IProgress<FcProgress> progress)
    {
        ValidateSnapshot(io,expected,ct);progress.Report(new("Reading MIX SRAM",0));
        var needingIdentity=expected.Games.Where(game=>game.Info.Battery&&game.Info.StoredRamBytes>0&&!game.HasRomData&&game.DirectoryIdentity is null).ToArray();
        IReadOnlyList<FcGame> loaded=needingIdentity.Length==0?[]:LoadGames(io,expected,needingIdentity,ct,progress);
        var games=expected.Games.Select(game=>loaded.FirstOrDefault(item=>item.Id==game.Id)??game).ToArray();
        var result=GameLibraryDirectory.ExportSaves(games,io.ReadSave(0,524288,ct));
        progress.Report(new("MIX saves exported",100));return result;
    }
    public static void ImportSaves(IFcCardIo io,FcCardSnapshot expected,IReadOnlyList<GameLibraryDirectory.SaveImage> saves,CancellationToken ct,IProgress<FcProgress> progress)
    {
        ValidateSnapshot(io,expected,ct);
        if(!io.Writable||!expected.Writable)throw new InvalidOperationException("Card is write-protected.");
        var legacy=expected.Games.Where(game=>game.Info.Battery&&game.Info.StoredRamBytes>0&&!game.HasRomData&&game.DirectoryIdentity is null).ToArray();
        IReadOnlyList<FcGame> loaded=legacy.Length==0?[]:LoadGames(io,expected,legacy,ct,progress);
        var entries=expected.Games.Select(game=>new FcPlacement(loaded.FirstOrDefault(item=>item.Id==game.Id)??game,game.OriginalOffset,game.OriginalAllocated,game.OriginalSaveMap)).ToArray();
        GameLibraryDirectory.ImportSaves(entries,saves,new byte[524288]);
        byte[] current=io.ReadSave(0,524288,ct),target=GameLibraryDirectory.ImportSaves(entries,saves,current);
        for(int bank=0;bank<60;bank++)
        {
            ct.ThrowIfCancellationRequested();int address=bank*8192;
            if(!current.AsSpan(address,8192).SequenceEqual(target.AsSpan(address,8192)))
            {
                io.WriteSave(address,target.AsSpan(address,8192).ToArray(),ct);
                if(!io.ReadSave(address,8192,ct).AsSpan().SequenceEqual(target.AsSpan(address,8192)))throw new IOException("MIX save readback verification failed.");
            }
            progress.Report(new("Restoring MIX SRAM",(bank+1)*100/60));
        }
    }
    private static void Validate(IFcCardIo io)
    {
        if(io.Capacity<FcMbcxCodec.PrefixBytes||io.SectorBytes<16384||io.SectorBytes>FcMbcxCodec.ConfigOffset||FcMbcxCodec.PrefixBytes%io.SectorBytes!=0)
            throw new InvalidDataException("Unsupported MBCX capacity/sector geometry.");
    }
    public static FcCardSnapshot RebuildConfiguration(IFcCardIo io,FcCardSnapshot expected,CancellationToken ct,IProgress<FcProgress> progress)
    {
        if(!expected.IsFc)throw new InvalidOperationException("Read a valid MIX card before rebuilding its configuration.");
        var games=expected.Games.Select(game=>{var copy=game.Copy();copy.Enabled=true;return copy;}).ToArray();
        return Write(io,expected,games,false,ct,progress,true);
    }
    public static FcCardSnapshot Write(IFcCardIo io,FcCardSnapshot expected,IReadOnlyList<FcGame> games,bool convert,CancellationToken ct,IProgress<FcProgress> progress,bool configurationOnly=false)
    {
        Validate(io);
        if(!io.Writable||!expected.Writable)throw new InvalidOperationException("Card is write-protected or its capacity is unavailable.");
        if(io.DeviceId!=expected.DeviceId||Math.Min(io.Capacity,FcMbcxCodec.CapacityBytes)!=expected.Capacity||io.SectorBytes!=expected.SectorBytes)
            throw new InvalidOperationException("The selected programmer/card changed; scan again.");
        if(!expected.IsFc&&!convert)throw new InvalidOperationException("Explicit MIX v2 replacement confirmation is required.");
        ValidateSnapshot(io,expected,ct,false);
        var loaded=games.Any(game=>game.Enabled&&!game.HasRomData)?LoadGames(io,expected,games.Where(game=>game.Enabled).ToArray(),ct,progress):games;
        games=loaded;
        var originals=expected.Games.Select(original=>{var match=loaded.FirstOrDefault(game=>game.Id==original.Id&&game.HasRomData);return match is null?original:original.WithData(match.Data,match.DirectoryIdentity,original.IsNes&&original.OriginalConfig.Length==128?FcMbcxCodec.ReadOriginalInfo(original,match.Data):original.Info);}).ToArray();
        byte[] fullPrefix=io.Read(0,FcMbcxCodec.PrefixBytes,ct);
        if(fullPrefix.Length!=FcMbcxCodec.PrefixBytes)throw new EndOfStreamException("Incomplete MIX prefix read before rebuild.");
        foreach(var range in expected.MetadataRanges??new[]{new FcMetadataRange(0,FcMbcxCodec.PrefixBytes)})if(!fullPrefix.AsSpan(range.Address,range.Bytes).SequenceEqual(expected.Prefix.AsSpan(range.Address,range.Bytes)))throw new InvalidOperationException("Card directory changed during ROM loading; scan again before writing.");
        expected=expected with {Prefix=fullPrefix,Games=originals,MetadataRanges=null};
        var layout=FcMbcxCodec.Plan(games,expected.Capacity,expected.IsFc);
        if(configurationOnly&&(layout.Entries.Count!=expected.Games.Count||layout.Entries.Any(placement=>{var original=expected.Games.SingleOrDefault(game=>game.Id==placement.Game.Id);return original is null||placement.Offset!=original.OriginalOffset||placement.AllocatedBytes!=original.OriginalAllocated||placement.Game.Name!=original.Name||!placement.SaveMap.SequenceEqual(original.OriginalSaveMap)||!placement.Game.Data.AsSpan().SequenceEqual(original.Data)||placement.Game.Md!=original.Md||!NesConfiguration.SameGeometry(placement.Game.Info,original.Info); })))
            throw new InvalidOperationException("Configuration rebuild cannot change card games, ROM locations or save mappings.");
        foreach(var placement in layout.Entries)
        {
            var old=expected.Games.FirstOrDefault(game=>game.Id==placement.Game.Id);
            if(old is null||!old.Info.Battery||old.OriginalSaveMap.Length==0)continue;
            if(!placement.Game.Info.Battery||placement.Game.Info.StoredRamBytes<old.Info.StoredRamBytes||
                !placement.SaveMap.Take(old.OriginalSaveMap.Length).SequenceEqual(old.OriginalSaveMap))
                throw new InvalidDataException("MIX save type, size or existing bank prefix cannot change implicitly.");
        }
        foreach(var p in layout.Entries.Where(p=>p.Game.IsNes))
        {
            var old=expected.Games.FirstOrDefault(g=>g.Id==p.Game.Id);
            if(old is null)continue;
            NesConfiguration.Validate(old,p.Game.Info);
            if(old.Info.Battery&&old.OriginalSaveMap.Length>0&&
                !p.SaveMap.Take(old.OriginalSaveMap.Length).SequenceEqual(old.OriginalSaveMap))
                throw new InvalidDataException("Configuration update would move an existing MIX save.");
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
                .OrderBy(address=>address<=FcMbcxCodec.ConfigOffset&&address+io.SectorBytes>FcMbcxCodec.ConfigOffset?1:0).ToArray();
            for(int i=0;i<addresses.Length;i++)
            {
                ct.ThrowIfCancellationRequested();int a=addresses[i];
                Rewrite(a,target.AsSpan(a,io.SectorBytes).ToArray());
                target.AsSpan(a,io.SectorBytes).CopyTo(current.AsSpan(a,io.SectorBytes));
                progress.Report(new(phase,(i+1)*100/addresses.Length));
            }
        }
        Prefix(invalidPrefix,"Invalidate MIX v2 directory");
        int index=0;
        foreach(var pair in sectors.OrderBy(p=>p.Key))
        {ct.ThrowIfCancellationRequested();Rewrite(pair.Key,pair.Value);progress.Report(new("Writing NES / MD / SNES ROM data",++index*100/sectors.Count));}
        foreach(var placement in layout.Entries.Where(placement=>placement.Game.Info.Battery))
        {
            var old=expected.Games.FirstOrDefault(game=>game.Id==placement.Game.Id&&game.Info.Battery);
            if(old is not null&&!placement.SaveMap.Take(old.OriginalSaveMap.Length).SequenceEqual(old.OriginalSaveMap))throw new InvalidDataException("Existing MIX save banks cannot be moved implicitly.");
            int oldBytes=old?.Info.StoredRamBytes??0,newBytes=placement.Game.Info.StoredRamBytes;
            byte fill=placement.Game.IsNes||(placement.Game.Md is not null&&(placement.Game.Md.Flags&8)!=0)?(byte)0:(byte)255;
            for(int logical=oldBytes;logical<newBytes;)
            {
                ct.ThrowIfCancellationRequested();int within=logical%8192,bytes=Math.Min(newBytes-logical,8192-within);
                io.InitializeSave(placement.SaveMap[logical/8192]*8192+within,bytes,fill,ct);logical+=bytes;
            }
        }
        ct.ThrowIfCancellationRequested();Prefix(finalPrefix,"Commit MIX directories");
        progress.Report(new("MIX v2 directory committed",100));
        var committed=layout.Entries.Select(p=>new FcGame {Id=p.Game.Id,Name=p.Game.Name,Data=p.Game.Data,Info=p.Game.Info,Md=p.Game.Md,Snes=p.Game.Snes,OriginalOffset=p.Offset,
            OriginalAllocated=p.AllocatedBytes,OriginalSaveMap=p.SaveMap,OriginalBattery=p.Game.Info.Battery,OriginalStoredRamBytes=p.Game.Info.StoredRamBytes}).ToList();
        return new(io.DeviceId,expected.Capacity,io.SectorBytes,current,committed,true,true);
    }
}
