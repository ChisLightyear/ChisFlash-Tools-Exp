using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GbaMultiMenuBuilder.Models;

namespace GbaMultiMenuBuilder.Services;

public static class SaveTypeOverrideService
{
    private static readonly object SyncRoot = new();
    private static OverrideConfig? _cachedConfig;
    private static DateTime _cachedLastWriteTimeUtc = DateTime.MinValue;

    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "save_type_overrides.ini");

    public static DetectedSaveTypeInfo? Resolve(string romPath, ReadOnlySpan<byte> romData)
    {
        var config = LoadConfig();
        if (config.IsEmpty)
        {
            return null;
        }

        var fileName = Path.GetFileName(romPath);
        if (config.FileNameRules.TryGetValue(fileName, out var fileNameMatch))
        {
            return fileNameMatch;
        }

        if (config.Sha1Rules.Count > 0)
        {
            var sha1 = Convert.ToHexString(SHA1.HashData(romData));
            if (config.Sha1Rules.TryGetValue(sha1, out var sha1Match))
            {
                return sha1Match;
            }
        }

        if (config.Crc32Rules.Count > 0)
        {
            var crc32 = ComputeCrc32Hex(romData);
            if (config.Crc32Rules.TryGetValue(crc32, out var crcMatch))
            {
                return crcMatch;
            }
        }

        return null;
    }

    private static OverrideConfig LoadConfig()
    {
        var configExists = File.Exists(ConfigPath);
        var lastWriteTimeUtc = configExists
            ? File.GetLastWriteTimeUtc(ConfigPath)
            : DateTime.MinValue;

        lock (SyncRoot)
        {
            if (_cachedConfig is not null && lastWriteTimeUtc == _cachedLastWriteTimeUtc)
            {
                return _cachedConfig;
            }

            _cachedConfig = configExists ? LoadConfiguredRules() : OverrideConfig.Empty;
            _cachedLastWriteTimeUtc = lastWriteTimeUtc;
            return _cachedConfig;
        }
    }

    private static OverrideConfig LoadConfiguredRules()
    {
        var document = IniDocument.Load(ConfigPath);
        return new OverrideConfig(
            LoadSectionRules(document, "FILE_NAME"),
            LoadSectionRules(document, "SHA1"),
            LoadSectionRules(document, "CRC32"));
    }

    private static Dictionary<string, DetectedSaveTypeInfo> LoadSectionRules(IniDocument document, string section)
    {
        var rules = new Dictionary<string, DetectedSaveTypeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.GetEntries(section))
        {
            if (!TryParseValue(entry.Value, out var info))
            {
                continue;
            }

            var key = entry.Key.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            rules[key] = info;
        }

        return rules;
    }

    private static bool TryParseValue(string value, out DetectedSaveTypeInfo info)
    {
        info = null!;
        var parts = value.Split(
            '|',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 2)
        {
            return false;
        }

        if (!Enum.TryParse(parts[0], true, out DetectedSaveTypeKind kind) || kind == DetectedSaveTypeKind.Unknown)
        {
            return false;
        }

        info = new DetectedSaveTypeInfo(kind, parts.Length > 1 ? parts[1] : null);
        return true;
    }

    private static string ComputeCrc32Hex(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xEDB88320
                    : crc >> 1;
            }
        }

        crc ^= 0xFFFFFFFF;
        return crc.ToString("X8", CultureInfo.InvariantCulture);
    }

    private sealed record OverrideConfig(
        Dictionary<string, DetectedSaveTypeInfo> FileNameRules,
        Dictionary<string, DetectedSaveTypeInfo> Sha1Rules,
        Dictionary<string, DetectedSaveTypeInfo> Crc32Rules)
    {
        public static OverrideConfig Empty { get; } =
            new(
                new Dictionary<string, DetectedSaveTypeInfo>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, DetectedSaveTypeInfo>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, DetectedSaveTypeInfo>(StringComparer.OrdinalIgnoreCase));

        public bool IsEmpty =>
            FileNameRules.Count == 0 &&
            Sha1Rules.Count == 0 &&
            Crc32Rules.Count == 0;
    }
}
