using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace GbaMultiMenuBuilder.Md;

public enum MdCartridgeKind { ReadOnlyCandidate, PierSolar, WritableRom, Svp, Sega32X, Disc, Invalid }
public enum MdRomEncoding { Unknown, BigEndian, WordSwapped, CopierHeader, Smd, SmdWithoutHeader }

// An admission policy, not a complete cartridge database or gameplay guarantee.
public sealed record MdCartridgeAssessment(MdCartridgeKind Kind, string ProductCode, string Message)
{
    public bool Excluded => Kind is not (MdCartridgeKind.ReadOnlyCandidate or MdCartridgeKind.PierSolar);
}

public static class MdCartridgePolicy
{
    internal const int PrefixBytes = 512 + 16384;
    private const string MdRouteRequired = "请在主窗口仅选择MD游戏，使用MD合卡通道。Select only MD games in the main window to build an MD cartridge.";

    // These files may enter the inspection path, never the existing GBA/GB/NES builder.
    public static bool IsDiagnosticExtension(string extension) => extension.ToLowerInvariant() is
        ".md" or ".gen" or ".smd" or ".32x" or ".bin" or ".iso" or ".cue";

    private static bool IsExistingRom(string name) => Path.GetExtension(name).ToLowerInvariant() is
        ".gba" or ".agb" or ".gb" or ".gbc" or ".nes";

    public static MdCartridgeAssessment? Inspect(ReadOnlySpan<byte> prefix, long length, string name)
    {
        if(prefix.Length>0xb2&&prefix[0xb2]==0x96&&prefix.Slice(4,4).SequenceEqual(new byte[]{0x24,0xff,0xae,0x51}))return null;
        if(prefix.Length>=0x10c&&prefix.Slice(0x104,8).SequenceEqual(new byte[]{0xce,0xed,0x66,0x66,0xcc,0x0d,0x00,0x0b}))return null;
        string extension = Path.GetExtension(name).ToLowerInvariant();
        if (prefix.StartsWith("SEGADISCSYSTEM"u8) ||
            (prefix.Length >= 32 && prefix[16..].StartsWith("SEGADISCSYSTEM"u8)) || extension is ".iso" or ".cue")
            return new(MdCartridgeKind.Disc, "", "不支持 Mega-CD／光盘镜像。Mega-CD and disc images are not supported.");

        Span<byte> header = stackalloc byte[512];
        if (!TryDecodeHeader(prefix, length, header, out _)) return ExplicitInvalid(extension);
        string console = Text(header.Slice(0x100, 16));
        string product = Text(header.Slice(0x180, 14));
        if (console.Contains("32X", StringComparison.Ordinal) || extension == ".32x")
            return new(MdCartridgeKind.Sega32X, product, "不支持 32X 卡带。32X cartridges are not supported.");
        if (HasId(product, "MK-1229") || HasId(product, "G-7001"))
            return new(MdCartridgeKind.Svp, product, "不支持 SVP 卡带《Virtua Racing》。SVP / Virtua Racing is not supported.");
        // Literal question marks are present in this board's product ID;
        // do not reject every unrelated T-68xxxxx product as a wildcard.
        if (HasId(product, "T-68???-"))
            return new(MdCartridgeKind.WritableRom, product,
                "不支持《Game no Kanzume Otokuyou》的运行时写 ROM 功能。Writable-ROM cartridges are excluded from the MBCX read-only profile.");
        if (HasId(product, "T-574013") || HasId(product, "T-574023"))
            return new(MdCartridgeKind.PierSolar, product, "Pier Solar 在保留范围内，待核心验证。Pier Solar remains in scope; core validation pending.");
        return new(MdCartridgeKind.ReadOnlyCandidate, product,
            "未发现已列出的排除类型；这不代表所有特殊板型均已识别。No listed exclusion detected; unknown special boards are not qualified.");
    }

    internal static bool TryDecodeHeader(ReadOnlySpan<byte> prefix, long length, Span<byte> header, out MdRomEncoding encoding)
    {
        encoding = MdRomEncoding.Unknown;
        if (prefix.Length < 512) return false;
        prefix[..512].CopyTo(header);
        if (HasSignature(header)) { encoding = MdRomEncoding.BigEndian; return true; }
        for (int i = 0; i < 512; i += 2) (header[i], header[i + 1]) = (header[i + 1], header[i]);
        if (HasSignature(header)) { encoding = MdRomEncoding.WordSwapped; return true; }
        // Older SMD copier headers often omit AA BB 06. Identify the decoded
        // ROM header and block geometry instead of trusting those marker bytes
        // or the filename; many .smd files are already ordinary big-endian ROMs.
        if (prefix.Length >= PrefixBytes && length > 512 && (length - 512) % 16384 == 0)
        {
            for (int i = 0; i < 256; i++) { header[2 * i] = prefix[512 + 8192 + i]; header[2 * i + 1] = prefix[512 + i]; }
            if (HasSignature(header)) { encoding = MdRomEncoding.Smd; return true; }
            prefix.Slice(512, 512).CopyTo(header);
            if (HasSignature(header)) { encoding = MdRomEncoding.CopierHeader; return true; }
        }
        if (prefix.Length >= 16384 && length % 16384 == 0)
        {
            for (int i = 0; i < 256; i++) { header[2 * i] = prefix[8192 + i]; header[2 * i + 1] = prefix[i]; }
            if (HasSignature(header)) { encoding = MdRomEncoding.SmdWithoutHeader; return true; }
        }
        return false;
    }

    private static bool HasSignature(ReadOnlySpan<byte> header) => Text(header.Slice(0x100, 16)).TrimStart().StartsWith("SEGA", StringComparison.Ordinal);

    private static MdCartridgeAssessment? ExplicitInvalid(string extension) => extension is ".md" or ".gen" or ".smd" or ".32x"
        ? new(MdCartridgeKind.Invalid, "", "无法识别有效 MD ROM 头或容器。Invalid or unrecognized MD ROM header/container.") : null;

    private static string Text(ReadOnlySpan<byte> data) =>
        new(Encoding.ASCII.GetString(data).Select(c => c is >= ' ' and <= '~' ? char.ToUpperInvariant(c) : ' ').ToArray());

    private static bool HasId(string product, string id) => Regex.IsMatch(product,
        @"(?:^|\s)" + Regex.Escape(id) + (id.EndsWith('-') ? @"(?=\d|\s|$)" : @"(?=-|\s|$)"), RegexOptions.CultureInvariant);

    public static void RejectImport(Stream input, long length, string name)
    {
        byte[] prefix = new byte[(int)Math.Min(PrefixBytes, Math.Max(0, length))];
        int read = 0;
        while (read < prefix.Length)
        {
            int next = input.Read(prefix, read, prefix.Length - read);
            if (next == 0) break;
            read += next;
        }
        MdCartridgeAssessment? result = Inspect(prefix.AsSpan(0, read), length, name);
        if (result is not null) throw new InvalidDataException($"{Path.GetFileName(name)}: {result.Message}" + (result.Excluded ? "" : " " + MdRouteRequired));
    }

    public static void RejectImport(string path)
    {
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = MdZipFile.OpenRead(path);
            // Preserve existing mixed archives; their selected entries are
            // checked by the existing importer before becoming game records.
            if (archive.Entries.Any(e => IsExistingRom(e.Name))) return;
            foreach (var entry in archive.Entries.Where(e => IsDiagnosticExtension(Path.GetExtension(e.Name))))
            {
                using var input = entry.Open();
                RejectImport(input, entry.Length, entry.Name);
            }
        }
        else
        {
            using var input = File.OpenRead(path);
            RejectImport(input, input.Length, path);
        }
    }
}

internal static class MdZipFile
{
    public static ZipArchive OpenRead(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var archive = ZipFile.OpenRead(path);
        try
        {
            if (!archive.Entries.Any(e => e.FullName.Contains('\ufffd'))) return archive;
        }
        catch { archive.Dispose(); throw; }
        archive.Dispose();
        // Old Chinese ROM packs commonly store GBK names without the UTF-8
        // flag. UTF-8-flagged entries still use UTF-8 in either archive reader.
        archive = ZipFile.Open(path, ZipArchiveMode.Read,
            Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));
        try { _ = archive.Entries.Count; return archive; }
        catch (DecoderFallbackException)
        {
            archive.Dispose();
            return ZipFile.Open(path, ZipArchiveMode.Read, Encoding.GetEncoding(437));
        }
        catch { archive.Dispose(); throw; }
    }
}
