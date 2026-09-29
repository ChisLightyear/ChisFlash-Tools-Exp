using System.Security.Cryptography;
using System.Text;
using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public enum WaterMulticartImageKind
{
    None,
    LegacyMenu,
    MagicMenu,
    HeaderOnly,
}

public sealed class WaterMulticartExtractedRomInfo
{
    public int OrderIndex { get; init; }
    public int EntrySlot { get; init; } = -1;
    public string FilePath { get; init; } = string.Empty;
    public string DisplayTitle { get; init; } = string.Empty;
    public string HeaderTitle { get; init; } = string.Empty;
    public string SourceBucketText { get; init; } = string.Empty;
    public int ExtractedSizeBytes { get; init; }
}

public sealed class WaterMulticartImportAnalysis
{
    public WaterMulticartImageKind Kind { get; init; }
    public string KindText { get; init; } = string.Empty;
    public WaterCartScanResult ScanResult { get; init; } = new();
    public List<WaterMulticartExtractedRomInfo> ExtractedRoms { get; init; } = [];
    public List<string> Messages { get; init; } = [];
}

public static class WaterMulticartRomAnalyzer
{
    private static readonly byte[] NintendoLogoGba =
    [
        0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21, 0x3D, 0x84, 0x82, 0x0A, 0x84, 0xE4, 0x09, 0xAD,
        0x11, 0x24, 0x8B, 0x98, 0xC0, 0x81, 0x7F, 0x21, 0xA3, 0x52, 0xBE, 0x19, 0x93, 0x09, 0xCE, 0x20,
        0x10, 0x46, 0x4A, 0x4A, 0xF8, 0x27, 0x31, 0xEC, 0x58, 0xC7, 0xE8, 0x33, 0x82, 0xE3, 0xCE, 0xBF,
        0x85, 0xF4, 0xDF, 0x94, 0xCE, 0x4B, 0x09, 0xC1, 0x94, 0x56, 0x8A, 0xC0, 0x13, 0x72, 0xA7, 0xFC,
        0x9F, 0x84, 0x4D, 0x73, 0xA3, 0xCA, 0x9A, 0x61, 0x58, 0x97, 0xA3, 0x27, 0xFC, 0x03, 0x98, 0x76,
        0x23, 0x1D, 0xC7, 0x61, 0x03, 0x04, 0xAE, 0x56, 0xBF, 0x38, 0x84, 0x00, 0x40, 0xA7, 0x0E, 0xFD,
        0xFF, 0x52, 0xFE, 0x03, 0x6F, 0x95, 0x30, 0xF1, 0x97, 0xFB, 0xC0, 0x85, 0x60, 0xD6, 0x80, 0x25,
        0xA9, 0x63, 0xBE, 0x03, 0x01, 0x4E, 0x38, 0xE2, 0xF9, 0xA2, 0x34, 0xFF, 0xBB, 0x3E, 0x03, 0x44,
        0x78, 0x00, 0x90, 0xCB, 0x88, 0x11, 0x3A, 0x94, 0x65, 0xC0, 0x7C, 0x63, 0x87, 0xF0, 0x3C, 0xAF,
        0xD6, 0x25, 0xE4, 0x8B, 0x38, 0x0A, 0xAC, 0x72, 0x21, 0xD4, 0xF8, 0x07
    ];

    public static WaterMulticartImportAnalysis? TryAnalyze(string sourceFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        byte[] imageBytes = File.ReadAllBytes(sourceFilePath);
        WaterCartScanResult scanResult = WaterCartScanner.ScanOfflineRomImage(imageBytes);

        bool hasMenuHeader = scanResult.Games.Any(game => game.IsMenu && game.StartBucket == 0);
        bool hasMetadata = scanResult.MenuConfig.IsDetected || scanResult.WlMapping.IsDetected;
        List<WaterCartEntry> sourceEntries = ResolveSourceEntries(scanResult);

        if (!hasMenuHeader || (!hasMetadata && sourceEntries.Count <= 1))
        {
            return null;
        }

        WaterMulticartImageKind kind = ResolveKind(scanResult);
        string kindText = ResolveKindText(kind);
        string cacheDirectory = GetCacheDirectory(sourceFilePath);
        Directory.CreateDirectory(cacheDirectory);

        List<WaterMulticartExtractedRomInfo> extractedRoms = [];
        int skippedEntries = 0;

        for (int i = 0; i < sourceEntries.Count; i++)
        {
            WaterCartEntry entry = sourceEntries[i];
            byte[] romBytes = ExtractEntryBytes(imageBytes, entry);
            if (!HasValidGbaHeader(romBytes))
            {
                skippedEntries++;
                continue;
            }

            string displayTitle = WaterMenuConfigCodec.NormalizeTitle(entry.Title);
            string headerTitle = WaterMenuConfigCodec.NormalizeTitle(WaterCartScanner.DecodeGbaHeaderTitle(romBytes));
            string outputFileName = $"{i + 1:000}_{SanitizeFileName(displayTitle)}.gba";
            string outputPath = Path.Combine(cacheDirectory, outputFileName);
            File.WriteAllBytes(outputPath, romBytes);

            extractedRoms.Add(new WaterMulticartExtractedRomInfo
            {
                OrderIndex = i,
                EntrySlot = entry.EntrySlot,
                FilePath = outputPath,
                DisplayTitle = displayTitle,
                HeaderTitle = headerTitle,
                SourceBucketText = entry.BucketRangeText,
                ExtractedSizeBytes = romBytes.Length,
            });
        }

        if (extractedRoms.Count <= 0)
        {
            throw new InvalidOperationException(T(
                "error",
                "multicart_extract_empty",
                "A multicart image was detected, but no usable game ROMs could be extracted from it."));
        }

        List<string> messages =
        [
            Tf(
                "log",
                "multicart_detected_summary",
                "Detected a multicart ROM: {0} | source={1} | extracted {2} game(s){3}.",
                Path.GetFileName(sourceFilePath),
                kindText,
                extractedRoms.Count,
                skippedEntries > 0 ? Tf("value", "multicart_skipped_suffix", " | skipped {0} invalid entrie(s)", skippedEntries) : string.Empty),
            T(
                "log",
                "multicart_detected_note",
                "The source menu/config was only used for identification. The actual target menu/config will be rebuilt later from the current workspace order and programmer mode."),
        ];

        return new WaterMulticartImportAnalysis
        {
            Kind = kind,
            KindText = kindText,
            ScanResult = scanResult,
            ExtractedRoms = extractedRoms,
            Messages = messages,
        };
    }

    private static List<WaterCartEntry> ResolveSourceEntries(WaterCartScanResult scanResult)
    {
        List<WaterCartEntry> entries = scanResult.Entries
            .Where(entry => !entry.IsMenu)
            .OrderBy(entry => entry.SortKey)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count > 0)
        {
            return entries;
        }

        return scanResult.Games
            .Where(game => !game.IsMenu)
            .OrderBy(game => game.StartBucket)
            .ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .Select(game => new WaterCartEntry
            {
                HeaderGame = game,
            })
            .ToList();
    }

    private static WaterMulticartImageKind ResolveKind(WaterCartScanResult scanResult)
    {
        if (scanResult.WlMapping.IsDetected)
        {
            return WaterMulticartImageKind.MagicMenu;
        }

        if (scanResult.MenuConfig.IsDetected)
        {
            return WaterMulticartImageKind.LegacyMenu;
        }

        return WaterMulticartImageKind.HeaderOnly;
    }

    private static string ResolveKindText(WaterMulticartImageKind kind) =>
        kind switch
        {
            WaterMulticartImageKind.MagicMenu => T("value", "multicart_kind_magic", "Magic / WL"),
            WaterMulticartImageKind.LegacyMenu => T("value", "multicart_kind_legacy", "Legacy Menu"),
            WaterMulticartImageKind.HeaderOnly => T("value", "multicart_kind_header_only", "Header-only Bundle"),
            _ => T("value", "unknown", "Unknown"),
        };

    private static byte[] ExtractEntryBytes(byte[] imageBytes, WaterCartEntry entry)
    {
        IReadOnlyList<int> physicalBuckets = entry.PhysicalBuckets;
        int logicalBucketCount = Math.Max(1, entry.LogicalBucketCount);
        byte[] extracted = Enumerable.Repeat((byte)0xFF, logicalBucketCount * WaterCartScanner.BucketSizeBytes).ToArray();

        for (int i = 0; i < logicalBucketCount && i < physicalBuckets.Count; i++)
        {
            int sourceOffset = physicalBuckets[i] * WaterCartScanner.BucketSizeBytes;
            if (sourceOffset < 0 || sourceOffset >= imageBytes.Length)
            {
                continue;
            }

            int copyLength = Math.Min(WaterCartScanner.BucketSizeBytes, imageBytes.Length - sourceOffset);
            Buffer.BlockCopy(imageBytes, sourceOffset, extracted, i * WaterCartScanner.BucketSizeBytes, copyLength);
        }

        int trimmedLength = GetTrimmedRomLength(extracted);
        if (trimmedLength < extracted.Length)
        {
            Array.Resize(ref extracted, trimmedLength);
        }

        return extracted;
    }

    private static bool HasValidGbaHeader(byte[] romBytes)
    {
        if (romBytes.Length < 0xA0)
        {
            return false;
        }

        return romBytes.AsSpan(4, NintendoLogoGba.Length).SequenceEqual(NintendoLogoGba);
    }

    private static int GetTrimmedRomLength(byte[] romBytes)
    {
        const int minimumLength = 0x200;

        int lastNonPaddingIndex = -1;
        for (int i = romBytes.Length - 1; i >= 0; i--)
        {
            if (romBytes[i] != 0xFF)
            {
                lastNonPaddingIndex = i;
                break;
            }
        }

        if (lastNonPaddingIndex < 0)
        {
            return Math.Min(romBytes.Length, minimumLength);
        }

        int trimmedLength = Math.Max(minimumLength, lastNonPaddingIndex + 1);
        int remainder = trimmedLength % 0x10;
        if (remainder != 0)
        {
            trimmedLength += 0x10 - remainder;
        }

        return Math.Min(trimmedLength, romBytes.Length);
    }

    private static string GetCacheDirectory(string sourceFilePath)
    {
        string rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChisFlashWaterClient",
            "multicart_cache");

        FileInfo sourceInfo = new(sourceFilePath);
        string signature = $"{Path.GetFullPath(sourceFilePath)}|{sourceInfo.Length}|{sourceInfo.LastWriteTimeUtc.Ticks}";
        string cacheKey = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(signature)));
        return Path.Combine(rootDirectory, cacheKey);
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(value.Length);
        foreach (char ch in value)
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        string sanitized = builder.ToString().Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "UNTITLED";
        }

        const int maxLength = 48;
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength];
        }

        return sanitized;
    }

    private static string T(string section, string key, string fallback) =>
        LocalizedText.T(section, key, fallback);

    private static string Tf(string section, string key, string fallback, params object[] args) =>
        LocalizedText.Tf(section, key, fallback, args);
}
