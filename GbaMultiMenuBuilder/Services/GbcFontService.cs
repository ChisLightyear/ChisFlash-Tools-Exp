using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GbaMultiMenuBuilder.Services;

internal sealed record GbcBuiltInFontOption(
    string FileName,
    string DisplayName,
    string FullPath);

internal sealed record GbcFontBitmapFormat(
    int GlyphSizePx,
    int GlyphBytes)
{
    public int RowBytes => GlyphSizePx <= 0 ? 0 : GlyphBytes / GlyphSizePx;
}

internal sealed record GbcFontGlyphCacheResult(
    Dictionary<int, byte[]> Glyphs,
    GbcFontBitmapFormat Format,
    ushort[] MissingCodepoints);

internal static class GbcFontService
{
    public const int GlyphBytes = 8;
    public const int CanvasWidth = 8;
    public const int CanvasHeight = 8;
    public const int LargeGlyphSize = 12;
    public const int LargeGlyphBytes = 24;

    private const int LegacyGlyphSize = 8;
    private const string BuiltInFontsRelativeDirectory = "Assets\\Fonts";
    private const string DefaultBuiltInFontFileName = "mplus_hzk_12.ttf";
    private const byte GbdkFontCompressedFlag = 0x04;
    private const int GbdkFontHeaderLength = 32;
    private const int GbdkFontMapLength = 96;
    private const int GbdkFontGlyphBytes = 8;

    private static readonly string[] PreferredTtfFontNames =
    [
        "mplus_hzk_12.ttf",
        "fusion-pixel-12px-proportional.ttf",
        "fusion-pixel-8px-proportional.ttf",
        "fusion-pixel-8px-proportional.otf",
        "fusion-pixel-8px-monospaced.ttf",
        "fusion-pixel-8px-monospaced.otf",
        "GuanZhi_8px.ttf",
    ];

    private static readonly string[] FontSearchPaths =
    [
        Path.Combine(AppContext.BaseDirectory, "Assets", "xiaodianzhen-8px.bdf"),
        Path.Combine(AppContext.BaseDirectory, "xiaodianzhen-8px.bdf"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "zpix.bdf"),
        Path.Combine(AppContext.BaseDirectory, "zpix.bdf"),
    ];

    private static readonly Regex PixelSizeTokenRegex = new(
        @"(?<!\d)(8|12)(?:px)?(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Lazy<IReadOnlyDictionary<ushort, byte[]>> GbdkDefaultAsciiGlyphs =
        new(LoadGbdkDefaultAsciiGlyphs);

    public static IReadOnlyDictionary<ushort, byte[]> GetGbdkDefaultAsciiGlyphs() =>
        GbdkDefaultAsciiGlyphs.Value;

    private static IReadOnlyDictionary<ushort, byte[]> LoadGbdkDefaultAsciiGlyphs()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "menu_en.gb"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "menu_en.gb"),
        };

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var rom = File.ReadAllBytes(path);
                for (var offset = 0; offset <= rom.Length - (2 + GbdkFontHeaderLength + GbdkFontMapLength + GbdkFontGlyphBytes); offset++)
                {
                    if ((rom[offset] & GbdkFontCompressedFlag) != GbdkFontCompressedFlag)
                    {
                        continue;
                    }

                    var mapOffset = offset + 2 + GbdkFontHeaderLength;
                    if (rom[mapOffset] != 0 || rom[mapOffset + 0x5E] != 0x5E)
                    {
                        continue;
                    }

                    var glyphOffset = mapOffset + GbdkFontMapLength;
                    var result = new Dictionary<ushort, byte[]>(0x5F);
                    for (var code = 0x20; code <= 0x7E; code++)
                    {
                        var glyphIndex = rom[mapOffset + (code - 0x20)];
                        if (glyphIndex >= GbdkFontMapLength)
                        {
                            continue;
                        }

                        var glyph = new byte[GbdkFontGlyphBytes];
                        Buffer.BlockCopy(rom, glyphOffset + (glyphIndex * GbdkFontGlyphBytes), glyph, 0, glyph.Length);
                        result[(ushort)code] = glyph;
                    }

                    if (result.Count == 0x5F)
                    {
                        return result;
                    }
                }
            }
            catch (IOException)
            {
                // A preview can still be opened with its fallback renderer if the asset is unavailable.
            }
            catch (UnauthorizedAccessException)
            {
                // A preview can still be opened with its fallback renderer if the asset is unavailable.
            }
        }

        var fallback = new Dictionary<ushort, byte[]>(0x5F);
        var format = MakeFormat(LegacyGlyphSize);
        for (var code = 0x20; code <= 0x7E; code++)
        {
            fallback[(ushort)code] = RenderGdiAsciiGlyph(code, format);
        }

        return fallback;
    }

    public static string GetBuiltInFontsDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, BuiltInFontsRelativeDirectory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static string GetDefaultBuiltInFontFileName() => DefaultBuiltInFontFileName;

    public static IReadOnlyList<GbcBuiltInFontOption> GetBuiltInFontOptions()
    {
        var directory = GetBuiltInFontsDirectory();
        var options = Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => IsSupportedBuiltInFontFileName(Path.GetFileName(path)))
            .Select(path => new GbcBuiltInFontOption(
                Path.GetFileName(path),
                GetBuiltInFontDisplayName(Path.GetFileName(path)),
                path))
            .OrderBy(static option => GetBuiltInFontSortOrder(option.FileName))
            .ThenBy(static option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return options;
    }

    public static string? NormalizeBuiltInFontName(string? configuredValue) =>
        ResolveBuiltInFontOption(configuredValue)?.FileName;

    public static string? FindFontPath(string? explicitPath)
    {
        var builtInOption = ResolveBuiltInFontOption(explicitPath);
        if (builtInOption is not null)
        {
            return builtInOption.FullPath;
        }

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            foreach (var candidate in EnumerateExplicitFontCandidates(explicitPath))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var basePath in EnumerateSearchBasePaths())
        {
            foreach (var fontPath in EnumerateFontCandidates(basePath))
            {
                if (File.Exists(fontPath))
                {
                    return fontPath;
                }
            }
        }

        return null;
    }

    public static bool IsTtfFile(string fontPath) =>
        fontPath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
        fontPath.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

    public static GbcFontBitmapFormat DetectBitmapFormat(string fontPath)
    {
        var glyphSizePx = DetectGlyphSizePx(fontPath);
        var rowBytes = (glyphSizePx + 7) / 8;
        return new GbcFontBitmapFormat(glyphSizePx, glyphSizePx * rowBytes);
    }

    public static byte[] RenderGlyphBitmap(ushort codepoint, string fontPath)
    {
        var result = BuildGlyphCacheDetailed([codepoint], fontPath);
        return result.Glyphs.TryGetValue(codepoint, out var glyph)
            ? glyph
            : CreateFallbackGbcGlyphBitmap(result.Format.GlyphSizePx);
    }

    public static Dictionary<int, byte[]> BuildGlyphCache(IEnumerable<int> codepoints, string fontPath) =>
        BuildGlyphCacheDetailed(codepoints, fontPath).Glyphs;

    public static GbcFontGlyphCacheResult BuildGlyphCacheDetailed(IEnumerable<int> codepoints, string fontPath)
    {
        var orderedCodes = codepoints.OrderBy(static code => code).ToArray();
        var cache = new Dictionary<int, byte[]>(orderedCodes.Length);
        var missingCodepoints = new List<ushort>();
        var format = DetectBitmapFormat(fontPath);

        if (IsTtfFile(fontPath))
        {
            using var privateFonts = new PrivateFontCollection();
            privateFonts.AddFontFile(fontPath);
            if (privateFonts.Families.Length == 0)
            {
                throw new InvalidOperationException($"Failed to load TTF font: {fontPath}");
            }

            foreach (var code in orderedCodes)
            {
                if (code is < 0 or > ushort.MaxValue)
                {
                    cache[code] = CreateFallbackGbcGlyphBitmap(format.GlyphSizePx);
                    continue;
                }

                try
                {
                    cache[code] = RenderTtfGlyph((ushort)code, privateFonts.Families[0], format);
                }
                catch
                {
                    if (code >= 0x80)
                    {
                        missingCodepoints.Add((ushort)code);
                    }

                    cache[code] = CreateFallbackGbcGlyphBitmap(format.GlyphSizePx);
                }
            }

            return new GbcFontGlyphCacheResult(cache, format, missingCodepoints.ToArray());
        }

        var bdfGlyphs = LoadAllBdfGlyphs(fontPath);
        foreach (var code in orderedCodes)
        {
            if (code is < 0 or > ushort.MaxValue)
            {
                cache[code] = CreateFallbackGbcGlyphBitmap(format.GlyphSizePx);
                continue;
            }

            if (bdfGlyphs.TryGetValue((ushort)code, out var bdfGlyph))
            {
                cache[code] = RenderBdfGlyphBitmapBytes(bdfGlyph, format);
            }
            else if (code is >= 0x20 and <= 0x7E)
            {
                cache[code] = RenderGdiAsciiGlyph(code, format);
            }
            else
            {
                missingCodepoints.Add((ushort)code);
                cache[code] = CreateFallbackGbcGlyphBitmap(format.GlyphSizePx);
            }
        }

        return new GbcFontGlyphCacheResult(cache, format, missingCodepoints.ToArray());
    }

    public static byte[] CreateFallbackGbcGlyphBitmap() =>
        CreateFallbackGbcGlyphBitmap(LegacyGlyphSize);

    public static byte[] CreateFallbackGbcGlyphBitmap(int glyphSizePx)
    {
        var format = MakeFormat(glyphSizePx);
        var glyph = new byte[format.GlyphBytes];
        if (glyphSizePx <= 0)
        {
            return glyph;
        }

        for (var x = 0; x < glyphSizePx; x++)
        {
            SetGlyphPixel(glyph, format, x, 0);
            SetGlyphPixel(glyph, format, x, glyphSizePx - 1);
        }

        for (var y = 1; y < glyphSizePx - 1; y++)
        {
            SetGlyphPixel(glyph, format, 0, y);
            SetGlyphPixel(glyph, format, glyphSizePx - 1, y);
        }

        return glyph;
    }

    private static IEnumerable<string> EnumerateSearchBasePaths()
    {
        yield return GetBuiltInFontsDirectory();
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }

    private static GbcBuiltInFontOption? ResolveBuiltInFontOption(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return null;
        }

        var trimmed = configuredValue.Trim();
        var fileName = Path.GetFileName(trimmed);

        return GetBuiltInFontOptions().FirstOrDefault(option =>
            string.Equals(option.FileName, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.FileName, fileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.FullPath, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateExplicitFontCandidates(string explicitPath)
    {
        var normalized = Path.GetFullPath(explicitPath);
        if (Directory.Exists(normalized))
        {
            foreach (var candidate in EnumerateFontCandidates(normalized))
            {
                yield return candidate;
            }

            yield break;
        }

        if (File.Exists(normalized))
        {
            yield return normalized;
        }
    }

    private static IEnumerable<string> EnumerateFontCandidates(string basePath)
    {
        if (Directory.Exists(basePath))
        {
            foreach (var path in Directory.EnumerateFiles(basePath, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsSupportedBuiltInFontFileName(Path.GetFileName(path)))
                {
                    yield return path;
                }
            }
        }

        foreach (var name in PreferredTtfFontNames)
        {
            yield return Path.Combine(basePath, name);
        }

        foreach (var bdfPath in EnumerateBdfCandidates(basePath))
        {
            yield return bdfPath;
        }
    }

    private static bool IsSupportedBuiltInFontFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".bdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetBuiltInFontDisplayName(string fileName) =>
        fileName.ToLowerInvariant() switch
        {
            "mplus_hzk_12.ttf" => "M+ HZK 12px",
            "fusion-pixel-12px-proportional.ttf" => "Fusion Pixel 12px Proportional",
            "fusion-pixel-8px-proportional.ttf" => "Fusion Pixel 8px Proportional",
            "fusion-pixel-8px-proportional.otf" => "Fusion Pixel 8px Proportional",
            "fusion-pixel-8px-monospaced.ttf" => "Fusion Pixel 8px Monospaced",
            "fusion-pixel-8px-monospaced.otf" => "Fusion Pixel 8px Monospaced",
            "guanzhi_8px.ttf" => "GuanZhi 8px",
            _ => Path.GetFileNameWithoutExtension(fileName),
        };

    private static int GetBuiltInFontSortOrder(string fileName) =>
        fileName.ToLowerInvariant() switch
        {
            "mplus_hzk_12.ttf" => 0,
            "fusion-pixel-12px-proportional.ttf" => 1,
            "fusion-pixel-8px-proportional.ttf" => 2,
            "fusion-pixel-8px-proportional.otf" => 3,
            "fusion-pixel-8px-monospaced.ttf" => 4,
            "fusion-pixel-8px-monospaced.otf" => 5,
            "guanzhi_8px.ttf" => 6,
            _ => 100,
        };

    private static IEnumerable<string> EnumerateBdfCandidates(string basePath)
    {
        foreach (var name in FontSearchPaths)
        {
            if (Path.IsPathRooted(name))
            {
                yield return name;
                continue;
            }

            yield return Path.Combine(basePath, name);
        }

        var directory = new DirectoryInfo(basePath);
        while (directory is not null)
        {
            yield return Path.Combine(directory.FullName, "xiaodianzhen-pixel-font-master", "build", "outputs", "xiaodianzhen-8px.bdf");
            yield return Path.Combine(directory.FullName, "ChisFlash_MBCX", "xiaodianzhen-pixel-font-master", "build", "outputs", "xiaodianzhen-8px.bdf");
            yield return Path.Combine(directory.FullName, "zpix-pixel-font", "dist", "zpix.bdf");
            yield return Path.Combine(directory.FullName, "ChisFlash_MBCX", "zpix-pixel-font", "dist", "zpix.bdf");
            directory = directory.Parent;
        }
    }

    private static int DetectGlyphSizePx(string fontPath)
    {
        var fromName = TryDetectGlyphSizePx(Path.GetFileNameWithoutExtension(fontPath));
        if (fromName.HasValue)
        {
            return fromName.Value;
        }

        if (!string.IsNullOrWhiteSpace(fontPath) &&
            fontPath.EndsWith(".bdf", StringComparison.OrdinalIgnoreCase))
        {
            return DetectBdfGlyphSizePx(fontPath);
        }

        return LegacyGlyphSize;
    }

    private static int DetectBdfGlyphSizePx(string fontPath)
    {
        if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
        {
            return LegacyGlyphSize;
        }

        foreach (var rawLine in File.ReadLines(fontPath))
        {
            var line = rawLine.Trim();
            if (!(line.StartsWith("FONTBOUNDINGBOX ", StringComparison.Ordinal) ||
                  line.StartsWith("BBX ", StringComparison.Ordinal)))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            {
                continue;
            }

            return Math.Max(width, height) > LegacyGlyphSize
                ? LargeGlyphSize
                : LegacyGlyphSize;
        }

        return LegacyGlyphSize;
    }

    private static int? TryDetectGlyphSizePx(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var match = PixelSizeTokenRegex.Match(name);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return value >= LargeGlyphSize
            ? LargeGlyphSize
            : LegacyGlyphSize;
    }

    private static GbcFontBitmapFormat MakeFormat(int glyphSizePx)
    {
        var normalizedSize = glyphSizePx > LegacyGlyphSize
            ? LargeGlyphSize
            : LegacyGlyphSize;
        return normalizedSize == LargeGlyphSize
            ? new GbcFontBitmapFormat(LargeGlyphSize, LargeGlyphBytes)
            : new GbcFontBitmapFormat(LegacyGlyphSize, GlyphBytes);
    }

    private static byte[] RenderTtfGlyph(ushort codepoint, FontFamily fontFamily, GbcFontBitmapFormat format)
    {
        if (codepoint == 0x20)
        {
            return new byte[format.GlyphBytes];
        }

        var text = codepoint is >= 0x20 and <= 0x7E
            ? ((char)codepoint).ToString()
            : char.ConvertFromUtf32(codepoint);

        foreach (var fontSizePx in GetCandidateRenderSizes(format.GlyphSizePx))
        {
            var glyph = RenderTextGlyph(text, fontFamily.Name, fontSizePx, format, usePrivateFontFamily: true, fontFamily);
            if (glyph is not null)
            {
                return glyph;
            }
        }

        return new byte[format.GlyphBytes];
    }

    private static byte[] RenderGdiAsciiGlyph(int code, GbcFontBitmapFormat format)
    {
        var glyph = new byte[format.GlyphBytes];
        if (code is < 0x20 or > 0x7E)
        {
            return glyph;
        }

        var candidateSizes = format.GlyphSizePx > LegacyGlyphSize
            ? new[] { 11f, 10f, 9f, 8f }
            : new[] { 7f, 6f, 5f };

        foreach (var fontSizePx in candidateSizes)
        {
            var rendered = RenderTextGlyph(
                ((char)code).ToString(),
                "Consolas",
                fontSizePx,
                format,
                usePrivateFontFamily: false,
                fontFamily: null);
            if (rendered is not null)
            {
                return rendered;
            }
        }

        return glyph;
    }

    private static byte[]? RenderTextGlyph(
        string text,
        string fontFamilyName,
        float fontSizePx,
        GbcFontBitmapFormat format,
        bool usePrivateFontFamily,
        FontFamily? fontFamily)
    {
        var scanCanvasPx = format.GlyphSizePx * 2;
        using var bitmap = new Bitmap(scanCanvasPx, scanCanvasPx);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        using var brush = new SolidBrush(Color.White);
        using var font = usePrivateFontFamily && fontFamily is not null
            ? new Font(fontFamily, fontSizePx, FontStyle.Regular, GraphicsUnit.Pixel)
            : new Font(fontFamilyName, fontSizePx, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.DrawString(text, font, brush, 0, 0);

        var left = scanCanvasPx;
        var top = scanCanvasPx;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < scanCanvasPx; y++)
        {
            for (var x = 0; x < scanCanvasPx; x++)
            {
                if (bitmap.GetPixel(x, y).R <= 128)
                {
                    continue;
                }

                if (x < left) left = x;
                if (y < top) top = y;
                if (x > right) right = x;
                if (y > bottom) bottom = y;
            }
        }

        if (left > right || top > bottom)
        {
            return null;
        }

        var width = right - left + 1;
        var height = bottom - top + 1;
        if (width > format.GlyphSizePx || height > format.GlyphSizePx)
        {
            return null;
        }

        var glyph = new byte[format.GlyphBytes];
        var offsetY = Math.Max(0, format.GlyphSizePx - height);
        for (var sy = 0; sy < height; sy++)
        {
            var targetY = offsetY + sy;
            if (targetY >= format.GlyphSizePx)
            {
                break;
            }

            for (var sx = 0; sx < width; sx++)
            {
                if (bitmap.GetPixel(left + sx, top + sy).R > 128)
                {
                    SetGlyphPixel(glyph, format, sx, targetY);
                }
            }
        }

        return glyph;
    }

    private static float[] GetCandidateRenderSizes(int glyphSizePx) =>
        glyphSizePx > LegacyGlyphSize
            ? [12f, 11f, 10f, 9f, 8f]
            : [8f, 7f, 6f, 5f];

    private static void SetGlyphPixel(byte[] glyph, GbcFontBitmapFormat format, int x, int y)
    {
        if (x < 0 || y < 0 || x >= format.GlyphSizePx || y >= format.GlyphSizePx)
        {
            return;
        }

        var offset = (y * format.RowBytes) + (x >> 3);
        glyph[offset] |= (byte)(0x80 >> (x & 0x07));
    }

    private static Dictionary<ushort, BdfRawGlyph> LoadAllBdfGlyphs(string fontBdfPath)
    {
        var glyphs = new Dictionary<ushort, BdfRawGlyph>();
        if (string.IsNullOrWhiteSpace(fontBdfPath) || !File.Exists(fontBdfPath))
        {
            return glyphs;
        }

        BdfGlyphBuilder? current = null;
        var bitmapLines = new List<string>();
        var inBitmap = false;

        foreach (var rawLine in File.ReadLines(fontBdfPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("STARTCHAR ", StringComparison.Ordinal))
            {
                current = new BdfGlyphBuilder();
                bitmapLines.Clear();
                inBitmap = false;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith("ENCODING ", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out var encoding) &&
                encoding is >= 0 and <= ushort.MaxValue)
            {
                current.Codepoint = (ushort)encoding;
                continue;
            }

            if (line.StartsWith("BBX ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) &&
                    int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) &&
                    int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var xo) &&
                    int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var yo))
                {
                    current.Width = w;
                    current.Height = h;
                    current.XOffset = xo;
                    current.YOffset = yo;
                }

                continue;
            }

            if (string.Equals(line, "BITMAP", StringComparison.Ordinal))
            {
                inBitmap = true;
                bitmapLines.Clear();
                continue;
            }

            if (string.Equals(line, "ENDCHAR", StringComparison.Ordinal))
            {
                if (current.Codepoint.HasValue &&
                    current.Width.HasValue &&
                    current.Height.HasValue &&
                    current.XOffset.HasValue &&
                    current.YOffset.HasValue)
                {
                    glyphs[current.Codepoint.Value] = new BdfRawGlyph(
                        current.Codepoint.Value,
                        current.Width.Value,
                        current.Height.Value,
                        current.XOffset.Value,
                        current.YOffset.Value,
                        bitmapLines.ToArray());
                }

                current = null;
                inBitmap = false;
                bitmapLines.Clear();
                continue;
            }

            if (inBitmap)
            {
                bitmapLines.Add(line);
            }
        }

        return glyphs;
    }

    private static byte[] RenderBdfGlyphBitmapBytes(BdfRawGlyph glyph, GbcFontBitmapFormat format)
    {
        var rows = new byte[format.GlyphBytes];
        if (glyph.Width <= 0 || glyph.Height <= 0)
        {
            return rows;
        }

        var startX = glyph.XOffset;
        var startY = Math.Max(0, format.GlyphSizePx - glyph.Height + glyph.YOffset);
        var sourceRowCount = Math.Min(glyph.BitmapHexRows.Length, glyph.Height);
        for (var sourceRow = 0; sourceRow < sourceRowCount; sourceRow++)
        {
            var targetY = startY + sourceRow;
            if (targetY < 0 || targetY >= format.GlyphSizePx)
            {
                continue;
            }

            var sourceBits = string.IsNullOrWhiteSpace(glyph.BitmapHexRows[sourceRow])
                ? 0
                : int.Parse(glyph.BitmapHexRows[sourceRow], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            for (var sourceX = 0; sourceX < glyph.Width; sourceX++)
            {
                var targetX = startX + sourceX;
                if (targetX < 0 || targetX >= format.GlyphSizePx)
                {
                    continue;
                }

                if ((sourceBits & (1 << (glyph.Width - 1 - sourceX))) != 0)
                {
                    SetGlyphPixel(rows, format, targetX, targetY);
                }
            }
        }

        return rows;
    }

    private sealed record BdfRawGlyph(
        ushort Codepoint,
        int Width,
        int Height,
        int XOffset,
        int YOffset,
        string[] BitmapHexRows);

    private sealed class BdfGlyphBuilder
    {
        public ushort? Codepoint { get; set; }

        public int? Width { get; set; }

        public int? Height { get; set; }

        public int? XOffset { get; set; }

        public int? YOffset { get; set; }
    }
}
