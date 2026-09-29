using System.IO.Compression;

namespace GbaMultiMenuBuilder.Md;

public sealed record MdRomCandidate(string SourcePath, int? ArchiveIndex, string EntryName,
    string Title, long SourceBytes, long RomBytes, MdRomEncoding Encoding,
    MdCartridgeAssessment Assessment, long ContainerBytes, DateTime ContainerModifiedUtc)
{
    public string SourceLabel => ArchiveIndex is null ? Path.GetFileName(SourcePath) : $"{Path.GetFileName(SourcePath)} / {EntryName}";
    public bool CanRead => !Assessment.Excluded && Encoding != MdRomEncoding.Unknown;
}

public static class MdRomImportService
{
    public const int MaxRomBytes = 32 * 1024 * 1024;
    public const string FileFilter = "MD ROM / ZIP (*.zip;*.md;*.bin;*.gen;*.smd;*.32x)|*.zip;*.md;*.bin;*.gen;*.smd;*.32x|All files (*.*)|*.*";

    public static bool IsInputFile(string name) => Path.GetExtension(name).ToLowerInvariant() is ".zip" or ".md" or ".bin" or ".gen" or ".smd" or ".32x";
    public static IEnumerable<string> ExpandPaths(IEnumerable<string> paths) => paths.SelectMany(path => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(IsInputFile)
        : new[] { path }).Distinct(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<MdRomCandidate> TryPreviewForMainWindow(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!IsInputFile(path)) return [];
        if (extension == ".zip")
        {
            using var archive = MdZipFile.OpenRead(path);
            if (!archive.Entries.Any(IsRomEntry)) return [];
        }
        else if (extension == ".bin")
        {
            using var input = File.OpenRead(path);
            byte[] prefix = new byte[(int)Math.Min(input.Length, MdCartridgePolicy.PrefixBytes)];input.ReadExactly(prefix);
            bool known = MdCartridgePolicy.Inspect(prefix, input.Length, path) is not null;
            bool gba = prefix.Length > 0xb2 && prefix[0xb2] == 0x96 && prefix.AsSpan(4,4).SequenceEqual(new byte[]{0x24,0xff,0xae,0x51});
            bool gb = prefix.Length >= 0x10c && prefix.AsSpan(0x104,8).SequenceEqual(new byte[]{0xce,0xed,0x66,0x66,0xcc,0x0d,0x00,0x0b});
            if (!known && (gba || gb || prefix.AsSpan().StartsWith("NES\x1a"u8))) return [];
            // Headerless BINs remain visible for review; never silently drop
            // hacks/homebrew or classify their contents as verified MD code.
        }
        return Preview(path);
    }

    public static bool HasLegacyRomEntries(string path)
    {
        if (!Path.GetExtension(path).Equals(".zip",StringComparison.OrdinalIgnoreCase)) return false;
        using var archive = MdZipFile.OpenRead(path);
        return archive.Entries.Any(e => Path.GetExtension(e.Name).ToLowerInvariant() is ".gba" or ".agb" or ".gb" or ".gbc" or ".nes");
    }

    public static string ImportStatus(MdRomCandidate item, string? error = null) => error ?? item.Assessment.Kind switch
    {
        MdCartridgeKind.Svp => "MD · 排除：SVP", MdCartridgeKind.WritableRom => "MD · 排除：可写ROM",
        MdCartridgeKind.Sega32X => "MD · 排除：32X", MdCartridgeKind.Disc => "MD · 排除：光盘镜像",
        MdCartridgeKind.Invalid => "MD · 待确认格式/ROM头", _ => "MD · 已读取，待核心验证"
    };
    private static bool IsRomEntry(ZipArchiveEntry entry) => !string.IsNullOrEmpty(entry.Name) &&
        !entry.FullName.Replace('\\', '/').StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) &&
        !entry.Name.StartsWith("._", StringComparison.Ordinal) && IsInputFile(entry.Name) &&
        !entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<MdRomCandidate> Preview(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new FileInfo(Path.GetFullPath(path));
        if (!source.Exists) throw new FileNotFoundException("找不到ROM文件。ROM file not found.", path);
        List<MdRomCandidate> result = [];
        if (source.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = MdZipFile.OpenRead(source.FullName);
            var entries = archive.Entries.Select((entry, index) => (entry, index)).Where(e => IsRomEntry(e.entry)).ToList();
            foreach (var (entry, index) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string title = entries.Count == 1 ? Path.GetFileNameWithoutExtension(source.Name) : Path.GetFileNameWithoutExtension(entry.Name);
                if(entries.Count(e=>e.entry.FullName==entry.FullName)>1)title+=$" [{index+1}]";
                try
                {
                    using var input = entry.Open();
                    result.Add(Inspect(input, entry.Length, entry.FullName, title, source, index));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                { result.Add(Failed(entry.Length, entry.FullName, title, source, index, ex.Message)); }
            }
            result.RemoveAll(item => item.Encoding == MdRomEncoding.Unknown &&
                Path.GetFileName(item.EntryName).ToLowerInvariant() is "readme.md" or "license.md" or "changelog.md" or "notice.md");
            if (result.Count == 1) result[0] = result[0] with { Title = Path.GetFileNameWithoutExtension(source.Name) };
            if (result.Count == 0) throw new InvalidDataException("ZIP中没有MD ROM（.md / .bin / .gen / .smd / .32x）。No MD ROM found in ZIP.");
        }
        else
        {
            using var input = source.OpenRead();
            result.Add(Inspect(input, source.Length, source.Name, Path.GetFileNameWithoutExtension(source.Name), source, null));
        }
        return result;
    }

    private static MdRomCandidate Inspect(Stream input, long length, string name, string title, FileInfo source, int? index)
    {
        if (length > MaxRomBytes + 512L) return Failed(length, name, title, source, index, "ROM超过32 MiB地址窗口。ROM exceeds the 32 MiB window.");
        byte[] prefix = new byte[(int)Math.Min(MdCartridgePolicy.PrefixBytes, length)];
        input.ReadExactly(prefix);
        Span<byte> header = stackalloc byte[512];
        MdCartridgePolicy.TryDecodeHeader(prefix, length, header, out var encoding);
        var assessment = MdCartridgePolicy.Inspect(prefix, length, name) ??
            new(MdCartridgeKind.Invalid, "", "未识别到标准MD头；保留文件，待确认格式或板型。No recognizable MD header; format or board needs review.");
        long romBytes = length - (encoding is MdRomEncoding.Smd or MdRomEncoding.CopierHeader ? 512 : 0);
        if (romBytes > MaxRomBytes || romBytes % 2 != 0)
            return Failed(length, name, title, source, index, "ROM长度无效或超出32 MiB。Invalid ROM length or over 32 MiB.");
        return new(source.FullName, index, name, title, length, romBytes, encoding, assessment, source.Length, source.LastWriteTimeUtc);
    }

    private static MdRomCandidate Failed(long length, string name, string title, FileInfo source, int? index, string reason) =>
        new(source.FullName, index, name, title, length, 0, MdRomEncoding.Unknown,
            new(MdCartridgeKind.Invalid, "", reason), source.Length, source.LastWriteTimeUtc);

    // Extract only the selected entry into memory. Never materialize ZIP paths
    // on disk; the original archive and ROM remain untouched.
    public static byte[] ReadRom(MdRomCandidate candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!candidate.CanRead) throw new InvalidDataException(candidate.Assessment.Message);
        var source = new FileInfo(candidate.SourcePath);
        if (!source.Exists || source.Length != candidate.ContainerBytes || source.LastWriteTimeUtc != candidate.ContainerModifiedUtc)
            throw new InvalidDataException("源文件已改变，请重新导入。Source changed; import it again.");
        if (candidate.ArchiveIndex is int index)
        {
            using var archive = MdZipFile.OpenRead(source.FullName);
            if (index < 0 || index >= archive.Entries.Count) throw new InvalidDataException("ZIP目录已改变。ZIP entries changed.");
            var entry = archive.Entries[index];
            if (entry.FullName != candidate.EntryName || entry.Length != candidate.SourceBytes)
                throw new InvalidDataException("ZIP内容已改变，请重新导入。ZIP content changed; import it again.");
            using var input = entry.Open();
            return ReadAndNormalize(input, candidate, cancellationToken);
        }
        using var file = source.OpenRead();
        return ReadAndNormalize(file, candidate, cancellationToken);
    }

    private static byte[] ReadAndNormalize(Stream input, MdRomCandidate candidate, CancellationToken cancellationToken)
    {
        if (candidate.SourceBytes is < 512 or > MaxRomBytes + 512L) throw new InvalidDataException("Invalid ROM size.");
        byte[] original = new byte[checked((int)candidate.SourceBytes)];
        int read = 0;
        while (read < original.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = input.Read(original, read, Math.Min(65536, original.Length - read));
            if (count == 0) throw new InvalidDataException("ROM数据不完整。Truncated ROM data.");
            read += count;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("ROM长度已改变。ROM length changed.");
        var assessment = MdCartridgePolicy.Inspect(original, original.Length, candidate.EntryName);
        Span<byte> header = stackalloc byte[512];
        MdCartridgePolicy.TryDecodeHeader(original, original.Length, header, out var encoding);
        if (assessment is null || assessment.Excluded || encoding != candidate.Encoding || assessment.ProductCode != candidate.Assessment.ProductCode)
            throw new InvalidDataException("ROM类型已改变，请重新导入。ROM type changed; import it again.");
        if (encoding == MdRomEncoding.BigEndian) return original;
        if (encoding == MdRomEncoding.WordSwapped)
        {
            for (int i = 0; i < original.Length; i += 2) (original[i], original[i + 1]) = (original[i + 1], original[i]);
            return original;
        }
        int offset = encoding is MdRomEncoding.Smd or MdRomEncoding.CopierHeader ? 512 : 0;
        byte[] normalized = new byte[original.Length - offset];
        if (encoding == MdRomEncoding.CopierHeader) original.AsSpan(offset).CopyTo(normalized);
        else for (int page = 0; page < normalized.Length; page += 16384)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < 8192; i++) { normalized[page + i * 2] = original[offset + page + 8192 + i]; normalized[page + i * 2 + 1] = original[offset + page + i]; }
        }
        return normalized;
    }
}
