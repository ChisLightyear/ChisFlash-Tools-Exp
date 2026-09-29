using System.Security.Cryptography;
using System.Text;
using GbaMultiMenuBuilder.Models;

namespace GbaMultiMenuBuilder.Services;

public static class RomMetadataCacheService
{
    private const string CacheDirectoryName = "temp";
    private const string CacheFileName = "rom_metadata_cache.ini";
    private const string CacheVersion = "5";

    private static readonly object SyncRoot = new();
    private static readonly SramPatchService SramPatchService = new();
    private static readonly BatterylessPatchService BatterylessPatchService = new();
    private static IniDocument? _document;

    public static RomMetadataCacheEntry Resolve(string romPath)
    {
        if (TryResolveCached(romPath, out var cachedEntry))
        {
            return cachedEntry;
        }

        var normalizedPath = NormalizePath(romPath);
        if (!TryGetFileSnapshot(normalizedPath, out var fileName, out var fileLength, out var lastWriteUtcTicks))
        {
            return CreateUnknown(normalizedPath);
        }

        var resolvedEntry = Analyze(normalizedPath, fileName, fileLength, lastWriteUtcTicks);
        lock (SyncRoot)
        {
            SaveEntryLocked(resolvedEntry);
        }

        return resolvedEntry;
    }

    public static bool TryResolveCached(string romPath, out RomMetadataCacheEntry entry)
    {
        var normalizedPath = NormalizePath(romPath);
        if (!TryGetFileSnapshot(normalizedPath, out var fileName, out var fileLength, out var lastWriteUtcTicks))
        {
            entry = CreateUnknown(normalizedPath);
            return false;
        }

        lock (SyncRoot)
        {
            return TryLoadCachedEntryLocked(normalizedPath, fileName, fileLength, lastWriteUtcTicks, out entry);
        }
    }

    private static RomMetadataCacheEntry Analyze(
        string romPath,
        string fileName,
        long fileLength,
        long lastWriteUtcTicks)
    {
        try
        {
            var romData = File.ReadAllBytes(romPath);
            var sramAlreadyPatched = !string.IsNullOrWhiteSpace(SramPatchService.DetectAppliedPatchTarget(romData));
            var saveTypeInfo = SaveTypeDetectorService.Detect(romPath, romData);
            var batterylessInspection = BatterylessPatchService.Analyze(romData);
            return new RomMetadataCacheEntry(
                romPath,
                fileName,
                fileLength,
                lastWriteUtcTicks,
                GameMetadataReader.ShouldEnableMap256m(romData),
                saveTypeInfo.Kind,
                saveTypeInfo.Identifier ?? string.Empty,
                SramPatchService.DetectPatchTarget(romData) ?? string.Empty,
                sramAlreadyPatched,
                batterylessInspection.AlreadyPatched,
                batterylessInspection.HasPayloadSpace,
                batterylessInspection.CanExpand);
        }
        catch
        {
            return CreateUnknown(romPath, fileName, fileLength, lastWriteUtcTicks);
        }
    }

    private static bool TryLoadCachedEntryLocked(
        string romPath,
        string fileName,
        long fileLength,
        long lastWriteUtcTicks,
        out RomMetadataCacheEntry entry)
    {
        var document = GetDocumentLocked();
        var section = BuildSectionName(romPath);

        if (!string.Equals(document.Get(section, "rom_path", string.Empty), romPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(document.Get(section, "file_name", string.Empty), fileName, StringComparison.OrdinalIgnoreCase) ||
            ReadLong(document, section, "file_length") != fileLength ||
            ReadLong(document, section, "last_write_utc_ticks") != lastWriteUtcTicks)
        {
            entry = null!;
            return false;
        }

        entry = new RomMetadataCacheEntry(
            romPath,
            fileName,
            fileLength,
            lastWriteUtcTicks,
            ReadBool(document, section, "map_256m") ?? false,
            ReadEnum<DetectedSaveTypeKind>(document, section, "save_type_kind") ?? DetectedSaveTypeKind.Unknown,
            document.Get(section, "save_type_identifier", string.Empty),
            document.Get(section, "sram_patch_target_identifier", string.Empty),
            ReadBool(document, section, "sram_already_patched") ?? false,
            ReadBool(document, section, "batteryless_already_patched") ?? false,
            ReadBool(document, section, "batteryless_has_payload_space") ?? false,
            ReadBool(document, section, "batteryless_can_expand") ?? false);
        return true;
    }

    private static void SaveEntryLocked(RomMetadataCacheEntry entry)
    {
        var document = GetDocumentLocked();
        var section = BuildSectionName(entry.RomPath);
        document.Set("meta", "version", CacheVersion);
        document.Set(section, "rom_path", entry.RomPath);
        document.Set(section, "file_name", entry.FileName);
        document.Set(section, "file_length", entry.FileLength.ToString());
        document.Set(section, "last_write_utc_ticks", entry.LastWriteUtcTicks.ToString());
        document.Set(section, "map_256m", entry.Map256m ? "true" : "false");
        document.Set(section, "save_type_kind", entry.SaveTypeKind.ToString());
        document.Set(section, "save_type_identifier", entry.SaveTypeIdentifier ?? string.Empty);
        document.Set(section, "sram_patch_target_identifier", entry.SramPatchTargetIdentifier ?? string.Empty);
        document.Set(section, "sram_already_patched", entry.SramAlreadyPatched ? "true" : "false");
        document.Set(section, "batteryless_already_patched", entry.BatterylessAlreadyPatched ? "true" : "false");
        document.Set(section, "batteryless_has_payload_space", entry.BatterylessHasPayloadSpace ? "true" : "false");
        document.Set(section, "batteryless_can_expand", entry.BatterylessCanExpand ? "true" : "false");
        document.Save(GetCachePath());
    }

    private static IniDocument GetDocumentLocked()
    {
        if (_document is not null)
        {
            return _document;
        }

        var loaded = IniDocument.Load(GetCachePath());
        var expectedDependencyStamp = BuildDependencyStamp();
        if (!string.Equals(loaded.Get("meta", "version", string.Empty), CacheVersion, StringComparison.Ordinal) ||
            !string.Equals(loaded.Get("meta", "dependency_stamp", string.Empty), expectedDependencyStamp, StringComparison.Ordinal))
        {
            loaded = new IniDocument();
            loaded.Set("meta", "version", CacheVersion);
            loaded.Set("meta", "dependency_stamp", expectedDependencyStamp);
        }

        _document = loaded;
        return _document;
    }

    private static RomMetadataCacheEntry CreateUnknown(
        string romPath,
        string? fileName = null,
        long fileLength = 0,
        long lastWriteUtcTicks = 0) =>
        new(
            romPath,
            fileName ?? Path.GetFileName(romPath),
            fileLength,
            lastWriteUtcTicks,
            false,
            DetectedSaveTypeKind.Unknown,
            string.Empty,
            string.Empty,
            false,
            false,
            false,
            false);

    private static bool TryGetFileSnapshot(
        string romPath,
        out string fileName,
        out long fileLength,
        out long lastWriteUtcTicks)
    {
        if (!File.Exists(romPath))
        {
            fileName = Path.GetFileName(romPath);
            fileLength = 0;
            lastWriteUtcTicks = 0;
            return false;
        }

        var fileInfo = new FileInfo(romPath);
        fileName = fileInfo.Name;
        fileLength = fileInfo.Length;
        lastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
        return true;
    }

    private static string NormalizePath(string romPath)
    {
        var sanitizedPath = romPath
            .Replace("\r", "r", StringComparison.Ordinal)
            .Replace("\n", "n", StringComparison.Ordinal)
            .Replace("\t", "t", StringComparison.Ordinal);
        try
        {
            return Path.GetFullPath(sanitizedPath.Trim());
        }
        catch
        {
            return sanitizedPath.Trim();
        }
    }

    private static string BuildSectionName(string romPath)
    {
        var pathBytes = Encoding.UTF8.GetBytes(romPath);
        return $"rom_{Convert.ToHexString(SHA1.HashData(pathBytes))}";
    }

    private static string GetCachePath() =>
        Path.Combine(AppContext.BaseDirectory, CacheDirectoryName, CacheFileName);

    private static string BuildDependencyStamp()
    {
        return MetadataDependencyStampService.Build(CacheVersion);
    }

    private static long? ReadLong(IniDocument document, string section, string key)
    {
        var value = document.Get(section, key, string.Empty);
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool? ReadBool(IniDocument document, string section, string key)
    {
        var value = document.Get(section, key, string.Empty);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static TEnum? ReadEnum<TEnum>(IniDocument document, string section, string key)
        where TEnum : struct, Enum
    {
        var value = document.Get(section, key, string.Empty);
        return Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : null;
    }
}
