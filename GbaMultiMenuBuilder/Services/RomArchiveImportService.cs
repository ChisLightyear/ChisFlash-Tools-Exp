using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GbaMultiMenuBuilder.Md;
using GbaMultiMenuBuilder.Snes;

namespace GbaMultiMenuBuilder.Services;

public enum RomPlatform { Gba, Gbc, Fc, Md, Snes }
public sealed record ArchiveRomSource(string SourcePath, int? EntryIndex, string EntryName, string Title,
    long Length, RomPlatform Platform, MdRomCandidate? MdRom = null);

public static class RomArchiveImportService
{
    public static bool IsGameFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".gba" or ".agb" or ".gb" or ".gbc" or ".nes" or ".md" or ".gen" or ".smd" or ".bin" or ".32x" or ".sfc" or ".smc";
    public static IEnumerable<string> ExpandPaths(IEnumerable<string> paths) => paths.SelectMany(path => Directory.Exists(path)
        ? Directory.EnumerateFiles(path,"*",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint})
            .Where(p=>IsGameFile(p)||Path.GetExtension(p).Equals(".zip",StringComparison.OrdinalIgnoreCase))
        : new[]{path}).Distinct(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ArchiveRomSource> Preview(string path)
    {
        path=Path.GetFullPath(path);
        if(!Path.GetExtension(path).Equals(".zip",StringComparison.OrdinalIgnoreCase))
        {
            if(!IsGameFile(path))return [];
            using var file=File.OpenRead(path);var platform=Detect(file,path,file.Length);
            var md=platform==RomPlatform.Md?MdRomImportService.Preview(path).Single():null;
            return [new(path,null,Path.GetFileName(path),Path.GetFileNameWithoutExtension(path),file.Length,platform,md)];
        }
        using var zip=MdZipFile.OpenRead(path);List<ArchiveRomSource> result=[];IReadOnlyList<MdRomCandidate>? mdItems=null;
        for(int i=0;i<zip.Entries.Count;i++)
        {
            var entry=zip.Entries[i];string name=entry.FullName.Replace('\\','/');
            if(!IsGameFile(name)||string.IsNullOrEmpty(entry.Name)||name.StartsWith("__MACOSX/",StringComparison.OrdinalIgnoreCase)||entry.Name.StartsWith("._"))continue;
            using var stream=entry.Open();var platform=Detect(stream,entry.Name,entry.Length);
            MdRomCandidate? md=null;
            if(platform==RomPlatform.Md)
            {
                mdItems??=MdRomImportService.Preview(path);md=mdItems.FirstOrDefault(m=>m.ArchiveIndex==i);
                if(md is null)continue;
            }
            string title=Path.GetFileNameWithoutExtension(entry.Name);
            if(zip.Entries.Count(e=>e.FullName==entry.FullName)>1)title+=$" [{i+1}]";
            result.Add(new(path,i,entry.FullName,title,entry.Length,platform,md));
        }
        if(result.Count==1)result[0]=result[0] with{Title=Path.GetFileNameWithoutExtension(path)};
        return result;
    }

    private static RomPlatform Detect(Stream input,string name,long length)
    {
        byte[] prefix=new byte[512];int count=0;
        while(count<prefix.Length){int n=input.Read(prefix,count,prefix.Length-count);if(n==0)break;count+=n;}
        if(prefix.AsSpan(0,count).StartsWith("NES\x1a"u8))return RomPlatform.Fc;
        if(count>=0x10c&&prefix.AsSpan(0x104,8).SequenceEqual(new byte[]{0xce,0xed,0x66,0x66,0xcc,0x0d,0x00,0x0b}))return RomPlatform.Gbc;
        if(count>0xb2&&prefix[0xb2]==0x96&&prefix.AsSpan(4,4).SequenceEqual(new byte[]{0x24,0xff,0xae,0x51}))return RomPlatform.Gba;
        string extension=Path.GetExtension(name).ToLowerInvariant();
        if(extension is ".sfc" or ".smc")return RomPlatform.Snes;
        if(extension==".bin"&&length is >=32768 and <=SnesRomHeaderService.MaxRomBytes+512L&&
            !(count>=0x104&&prefix.AsSpan(0x100,4).SequenceEqual("SEGA"u8)))
        {
            byte[] snesPrefix=new byte[(int)Math.Min(length,0x10200)];
            prefix.AsSpan(0,count).CopyTo(snesPrefix);int used=count;
            while(used<snesPrefix.Length){int read=input.Read(snesPrefix,used,snesPrefix.Length-used);if(read==0)break;used+=read;}
            if(SnesRomHeaderService.Parse(snesPrefix.AsSpan(0,used),length).HasValidHeader)return RomPlatform.Snes;
        }
        return extension switch { ".gba" or ".agb"=>RomPlatform.Gba,".gb" or ".gbc"=>RomPlatform.Gbc,".nes"=>RomPlatform.Fc,_=>RomPlatform.Md };
    }

    // Existing GB/GBA/NES analyzers consume ordinary files. Cache only the
    // selected member under an application-owned name; never use ZIP paths.
    public static string Materialize(ArchiveRomSource source)
    {
        if(source.Platform==RomPlatform.Md)throw new InvalidOperationException("Use the MD byte-order-aware reader.");
        if(source.EntryIndex is not int index)return source.SourcePath;
        if(source.Length is <=0 or >536870912)throw new InvalidDataException("ROM archive member size is invalid or exceeds 512 MiB.");
        if(source.Platform==RomPlatform.Snes&&source.Length>SnesRomHeaderService.MaxRomBytes+512L)throw new InvalidDataException("SNES member exceeds 8 MiB plus a copier header.");
        using var zip=MdZipFile.OpenRead(source.SourcePath);
        if(index<0||index>=zip.Entries.Count)throw new InvalidDataException("ZIP changed; import it again.");
        var entry=zip.Entries[index];if(entry.FullName!=source.EntryName||entry.Length!=source.Length)throw new InvalidDataException("ZIP changed; import it again.");
        string extension=source.Platform switch{RomPlatform.Gba=>".gba",RomPlatform.Gbc=>".gbc",RomPlatform.Snes=>".sfc",_=>".nes"};
        string root=Path.Combine(AppContext.BaseDirectory,"imported_roms");Directory.CreateDirectory(root);
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.SourcePath+"|"+index)));
        string output=Path.Combine(root,key+extension),temporary=output+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var input=entry.Open())using(var file=File.Create(temporary))
            {
                byte[] block=new byte[65536];long total=0;int count;
                while((count=input.Read(block))>0){total+=count;if(total>source.Length)throw new InvalidDataException("ZIP member exceeded its declared size.");file.Write(block,0,count);}
                if(total!=source.Length)throw new InvalidDataException("Truncated ZIP member.");
            }
            File.Move(temporary,output,true);return output;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
