using GbaMultiMenuBuilder.Models;

namespace GbaMultiMenuBuilder.Services;

public static class GameListSessionService
{
    private const string SessionFileName = "last_game_list.ini";
    private const string GbaSessionFileName = "last_game_list_gba.ini";
    private const string GbcSessionFileName = "last_game_list_gbc.ini";
    private const string SessionVersion = "7";

    private static string SessionPath => Path.Combine(AppContext.BaseDirectory, SessionFileName);
    private static string GbaSessionPath => Path.Combine(AppContext.BaseDirectory, GbaSessionFileName);
    private static string GbcSessionPath => Path.Combine(AppContext.BaseDirectory, GbcSessionFileName);

    public static IReadOnlyList<SavedGameEntry> Load()
    {
        var family = AppConfigService.LoadCompilationFamily() ?? CompilationFamily.Gba;
        return Load(family);
    }

    public static IReadOnlyList<SavedGameEntry> Load(CompilationFamily family)
    {
        var document = IniDocument.Load(GetSessionPath(family));
        var canUseSavedMetadata =
            string.Equals(document.Get("meta", "version", string.Empty), SessionVersion, StringComparison.Ordinal) &&
            string.Equals(
                document.Get("meta", "metadata_dependency_stamp", string.Empty),
                MetadataDependencyStampService.Build(SessionVersion),
                StringComparison.Ordinal);
        var count = ReadInt(document, "meta", "count") ?? 0;
        var result = new List<SavedGameEntry>(Math.Max(0, count));

        for (var i = 0; i < count; i++)
        {
            var section = $"game_{i}";
            var romPath = RepairPotentialIniEscapeCorruption(document.Get(section, "rom_path", string.Empty)).Trim();
            if (string.IsNullOrWhiteSpace(romPath))
            {
                continue;
            }

            result.Add(new SavedGameEntry(
                ReadBool(document, section, "enabled") ?? true,
                document.Get(section, "title", string.Empty),
                document.Get(section, "game_title", string.Empty),
                romPath,
                canUseSavedMetadata && (ReadBool(document, section, "metadata_identified") ?? false),
                document.Get(section, "save_slot", string.Empty),
                document.Get(section, "hidden_keys", string.Empty),
                ReadBool(document, section, "apply_sram_patch"),
                ReadBool(document, section, "apply_batteryless_patch"),
                canUseSavedMetadata && (ReadBool(document, section, "sram_already_patched") ?? false),
                canUseSavedMetadata && (ReadBool(document, section, "batteryless_already_patched") ?? false),
                canUseSavedMetadata && (ReadBool(document, section, "batteryless_has_payload_space") ?? false),
                canUseSavedMetadata && (ReadBool(document, section, "batteryless_can_expand") ?? false),
                ReadBool(document, section, "map_256m") ?? false,
                ReadInt(document, section, "title_font") ?? 1,
                canUseSavedMetadata ? ReadLong(document, section, "file_length") ?? 0 : 0,
                canUseSavedMetadata ? ReadLong(document, section, "last_write_utc_ticks") ?? 0 : 0,
                ReadBool(document, section, "gbc_dedicated_save_allocation"),
                canUseSavedMetadata ? ReadEnum<DetectedSaveTypeKind>(document, section, "save_type_kind") ?? DetectedSaveTypeKind.Unknown : DetectedSaveTypeKind.Unknown,
                canUseSavedMetadata ? document.Get(section, "save_type_identifier", string.Empty) : string.Empty,
                canUseSavedMetadata ? document.Get(section, "sram_patch_target_identifier", string.Empty) : string.Empty,
                RepairPotentialIniEscapeCorruption(document.Get(section, "cover_path", string.Empty)).Trim() is { Length: > 0 } coverPath
                    ? coverPath
                    : null,
                ReadBool(document, section, "cover_cleared") ?? false,
                ReadMdCandidate(document.Get(section, "md_source", string.Empty)),
                ReadOtherCandidate(document.Get(section, "other_rom_source", string.Empty))));
        }

        return result;
    }

    public static void Save(IEnumerable<GameEntryModel> games)
    {
        var family = AppConfigService.LoadCompilationFamily() ?? CompilationFamily.Gba;
        Save(family, games);
    }

    public static void Save(CompilationFamily family, IEnumerable<GameEntryModel> games)
    {
        var gameList = games
            .Where(static game => !game.IsLoadingPlaceholder)
            .ToList();
        var document = new IniDocument();
        document.Set("meta", "version", SessionVersion);
        document.Set("meta", "metadata_dependency_stamp", MetadataDependencyStampService.Build(SessionVersion));
        document.Set("meta", "count", gameList.Count.ToString());

        for (var i = 0; i < gameList.Count; i++)
        {
            var game = gameList[i];
            var section = $"game_{i}";
            document.Set(section, "enabled", game.Enabled ? "true" : "false");
            document.Set(section, "title", game.Title ?? string.Empty);
            document.Set(section, "game_title", game.GameTitle ?? string.Empty);
            document.Set(section, "rom_path", game.RomPath ?? string.Empty);
            if (game.MdRom is not null)
                document.Set(section, "md_source", Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(game.MdRom)));
            if (game.OtherRom is not null)
                document.Set(section, "other_rom_source", Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(game.OtherRom)));
            document.Set(section, "metadata_identified", "true");
            document.Set(section, "save_slot", game.SaveSlot ?? string.Empty);
            document.Set(section, "hidden_keys", game.HiddenKeys ?? string.Empty);
            document.Set(section, "apply_sram_patch", game.ApplySramPatch ? "true" : "false");
            if (game.ApplyBatterylessPatchOverride.HasValue)
            {
                document.Set(section, "apply_batteryless_patch", game.ApplyBatterylessPatchOverride.Value ? "true" : "false");
            }
            document.Set(section, "sram_already_patched", game.SramAlreadyPatched ? "true" : "false");
            document.Set(section, "batteryless_already_patched", game.BatterylessAlreadyPatched ? "true" : "false");
            document.Set(section, "batteryless_has_payload_space", game.BatterylessHasPayloadSpace ? "true" : "false");
            document.Set(section, "batteryless_can_expand", game.BatterylessCanExpand ? "true" : "false");
            document.Set(section, "map_256m", game.Map256m ? "true" : "false");
            document.Set(section, "title_font", Math.Max(1, game.TitleFont).ToString());
            if (!string.IsNullOrWhiteSpace(game.CoverPath))
            {
                document.Set(section, "cover_path", game.CoverPath);
            }
            document.Set(section, "cover_cleared", game.CoverCleared ? "true" : "false");
            if (TryGetFileSnapshot(game.RomPath ?? string.Empty, out var fileLength, out var lastWriteUtcTicks))
            {
                document.Set(section, "file_length", fileLength.ToString());
                document.Set(section, "last_write_utc_ticks", lastWriteUtcTicks.ToString());
            }

            if (game.GbcDedicatedSaveAllocationOverride.HasValue)
            {
                document.Set(section, "gbc_dedicated_save_allocation", game.GbcDedicatedSaveAllocationOverride.Value ? "true" : "false");
            }

            document.Set(section, "save_type_kind", game.SaveTypeKind.ToString());
            document.Set(section, "save_type_identifier", game.SaveTypeIdentifier ?? string.Empty);
            document.Set(section, "sram_patch_target_identifier", game.SramPatchTargetIdentifier ?? string.Empty);
        }

        document.Save(GetSessionPath(family));
    }

    private static Md.MdRomCandidate? ReadMdCandidate(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return System.Text.Json.JsonSerializer.Deserialize<Md.MdRomCandidate>(Convert.FromBase64String(value));
    }

    private static ArchiveRomSource? ReadOtherCandidate(string value) => string.IsNullOrEmpty(value) ? null :
        System.Text.Json.JsonSerializer.Deserialize<ArchiveRomSource>(Convert.FromBase64String(value));

    private static string GetSessionPath(CompilationFamily family) =>
        family switch
        {
            CompilationFamily.Gba when File.Exists(GbaSessionPath) => GbaSessionPath,
            CompilationFamily.Gba when File.Exists(SessionPath) => SessionPath,
            CompilationFamily.Gba => GbaSessionPath,
            CompilationFamily.Gbc => GbcSessionPath,
            _ => GbaSessionPath,
        };

    private static int? ReadInt(IniDocument document, string section, string key)
    {
        var value = document.Get(section, key, string.Empty);
        return int.TryParse(value, out var parsed) ? parsed : null;
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

    private static bool TryGetFileSnapshot(string romPath, out long fileLength, out long lastWriteUtcTicks)
    {
        fileLength = 0;
        lastWriteUtcTicks = 0;
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            return false;
        }

        var fileInfo = new FileInfo(romPath);
        fileLength = fileInfo.Length;
        lastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
        return true;
    }

    private static string RepairPotentialIniEscapeCorruption(string path) =>
        path
            .Replace("\r", "r", StringComparison.Ordinal)
            .Replace("\n", "n", StringComparison.Ordinal)
            .Replace("\t", "t", StringComparison.Ordinal);
}
