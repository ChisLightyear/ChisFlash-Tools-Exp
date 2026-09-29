using GbaMultiMenuBuilder.Models;

namespace GbaMultiMenuBuilder.Services;

public static class AppConfigService
{
    private const string ConfigFileName = "config.ini";
    private const string UiSection = "ui";
    private const string BuildSection = "build";
    private const string PathsSection = "paths";
    private const string WindowSection = "window";
    private const string GridSection = "grid";

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

    public static AppSettings LoadSettings()
    {
        var family = LoadCompilationFamily() ?? CompilationFamily.Gba;
        return LoadSettings(family);
    }

    public static AppSettings LoadSettings(CompilationFamily family)
    {
        var document = IniDocument.Load(ConfigPath);
        var legacyGbcBackground = family == CompilationFamily.Gbc
            ? ReadString(document, GetFamilySection(PathsSection, family), "background")
            : null;
        var legacyGbcFontName = ResolveLegacyGbcFontName(legacyGbcBackground);
        return new AppSettings
        {
            LanguageCode = ReadString(document, UiSection, "language"),
            BaseMenuPath = ReadFamilyString(document, PathsSection, family, "base_menu"),
            OutputPath = ReadFamilyString(document, PathsSection, family, "output_path"),
            BackgroundPath = family == CompilationFamily.Gba
                ? ReadFamilyString(document, PathsSection, family, "background")
                : legacyGbcFontName is null ? legacyGbcBackground : null,
            GbcFontName = family == CompilationFamily.Gbc
                ? ReadString(document, GetFamilySection(PathsSection, family), "font_name")
                    ?? legacyGbcFontName
                : null,
            CartridgeId = ReadFamilyInt(document, BuildSection, family, "cartridge_id"),
            MinRomSize = ReadFamilyInt(document, BuildSection, family, "min_rom_size"),
            FlashCartMode = ReadFamilyEnum<FlashCartMode>(document, BuildSection, family, "flashcart_mode"),
            GbaMenuType = ReadFamilyEnum<GbaMenuType>(document, BuildSection, family, "gba_menu_type"),
            BatteryPresent = ReadFamilyBool(document, BuildSection, family, "battery_present"),
            SplitOutput = ReadFamilyBool(document, BuildSection, family, "split_output"),
            MinimalMenuPackage = ReadFamilyBool(document, BuildSection, family, "minimal_menu_package"),
            ApplySramPatch = ReadFamilyBool(document, BuildSection, family, "apply_sram_patch"),
            WindowState = NormalizeWindowState(ReadEnum<FormWindowState>(document, WindowSection, "state")),
            WindowLeft = ReadInt(document, WindowSection, "left"),
            WindowTop = ReadInt(document, WindowSection, "top"),
            WindowWidth = ReadInt(document, WindowSection, "width"),
            WindowHeight = ReadInt(document, WindowSection, "height"),
            GameGridColumnWidths = ReadIntList(document, GridSection, "game_column_widths"),
        };
    }

    public static void SaveSettings(AppSettings settings)
    {
        var family = LoadCompilationFamily() ?? CompilationFamily.Gba;
        SaveSettings(family, settings);
    }

    public static void SaveSettings(CompilationFamily family, AppSettings settings)
    {
        var document = IniDocument.Load(ConfigPath);

        SetEnum<CompilationFamily>(document, UiSection, "compilation_family", family);
        SetString(document, UiSection, "language", settings.LanguageCode);
        SetString(document, GetFamilySection(PathsSection, family), "base_menu", settings.BaseMenuPath);
        SetString(document, GetFamilySection(PathsSection, family), "output_path", settings.OutputPath);
        if (family == CompilationFamily.Gbc)
        {
            SetString(document, GetFamilySection(PathsSection, family), "background", settings.BackgroundPath);
            SetString(document, GetFamilySection(PathsSection, family), "font_name", settings.GbcFontName);
        }
        else
        {
            SetString(document, GetFamilySection(PathsSection, family), "background", settings.BackgroundPath);
            document.Remove(GetFamilySection(PathsSection, family), "font_name");
        }
        SetInt(document, GetFamilySection(BuildSection, family), "cartridge_id", settings.CartridgeId);
        SetInt(document, GetFamilySection(BuildSection, family), "min_rom_size", settings.MinRomSize);
        SetEnum(document, GetFamilySection(BuildSection, family), "flashcart_mode", settings.FlashCartMode);
        SetEnum(document, GetFamilySection(BuildSection, family), "gba_menu_type", settings.GbaMenuType);
        SetBool(document, GetFamilySection(BuildSection, family), "battery_present", settings.BatteryPresent);
        SetBool(document, GetFamilySection(BuildSection, family), "split_output", settings.SplitOutput);
        SetBool(document, GetFamilySection(BuildSection, family), "minimal_menu_package", settings.MinimalMenuPackage);
        SetBool(document, GetFamilySection(BuildSection, family), "apply_sram_patch", settings.ApplySramPatch);
        SetEnum(document, WindowSection, "state", NormalizeWindowState(settings.WindowState));
        SetInt(document, WindowSection, "left", settings.WindowLeft);
        SetInt(document, WindowSection, "top", settings.WindowTop);
        SetInt(document, WindowSection, "width", settings.WindowWidth);
        SetInt(document, WindowSection, "height", settings.WindowHeight);
        SetIntList(document, GridSection, "game_column_widths", settings.GameGridColumnWidths);

        document.Save(ConfigPath);
    }

    public static CompilationFamily? LoadCompilationFamily()
    {
        var document = IniDocument.Load(ConfigPath);
        return ReadEnum<CompilationFamily>(document, UiSection, "compilation_family");
    }

    public static void SaveCompilationFamily(CompilationFamily family)
    {
        var document = IniDocument.Load(ConfigPath);
        SetEnum<CompilationFamily>(document, UiSection, "compilation_family", family);
        document.Save(ConfigPath);
    }

    public static string? LoadLanguageCode()
    {
        var document = IniDocument.Load(ConfigPath);
        return ReadString(document, UiSection, "language");
    }

    public static void SaveLanguageCode(string languageCode)
    {
        var document = IniDocument.Load(ConfigPath);
        SetString(document, UiSection, "language", languageCode);
        document.Save(ConfigPath);
    }

    private static string GetFamilySection(string baseSection, CompilationFamily family) =>
        $"{baseSection}_{family.ToString().ToLowerInvariant()}";

    private static string? ResolveLegacyGbcFontName(string? legacyFontPath)
    {
        if (string.IsNullOrWhiteSpace(legacyFontPath))
        {
            return null;
        }

        var builtInName = GbcFontService.NormalizeBuiltInFontName(legacyFontPath);
        if (!string.IsNullOrWhiteSpace(builtInName))
        {
            return builtInName;
        }

        var extension = Path.GetExtension(legacyFontPath);
        if (!extension.Equals(".bdf", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var resolvedPath = GbcFontService.FindFontPath(legacyFontPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return null;
        }

        return Path.GetFileName(resolvedPath);
    }

    private static string? ReadFamilyString(IniDocument document, string baseSection, CompilationFamily family, string key)
    {
        var familyValue = ReadString(document, GetFamilySection(baseSection, family), key);
        return family == CompilationFamily.Gba
            ? familyValue ?? ReadString(document, baseSection, key)
            : familyValue;
    }

    private static int? ReadFamilyInt(IniDocument document, string baseSection, CompilationFamily family, string key)
    {
        var familyValue = ReadInt(document, GetFamilySection(baseSection, family), key);
        return family == CompilationFamily.Gba
            ? familyValue ?? ReadInt(document, baseSection, key)
            : familyValue;
    }

    private static bool? ReadFamilyBool(IniDocument document, string baseSection, CompilationFamily family, string key)
    {
        var familyValue = ReadBool(document, GetFamilySection(baseSection, family), key);
        return family == CompilationFamily.Gba
            ? familyValue ?? ReadBool(document, baseSection, key)
            : familyValue;
    }

    private static TEnum? ReadFamilyEnum<TEnum>(IniDocument document, string baseSection, CompilationFamily family, string key)
        where TEnum : struct, Enum
    {
        var familyValue = ReadEnum<TEnum>(document, GetFamilySection(baseSection, family), key);
        return family == CompilationFamily.Gba
            ? familyValue ?? ReadEnum<TEnum>(document, baseSection, key)
            : familyValue;
    }

    private static string? ReadString(IniDocument document, string section, string key)
    {
        var value = document.Get(section, key, string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? ReadInt(IniDocument document, string section, string key)
    {
        var value = document.Get(section, key, string.Empty);
        return int.TryParse(value, out var parsed) ? parsed : null;
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

    private static int[]? ReadIntList(IniDocument document, string section, string key)
    {
        var value = document.Get(section, key, string.Empty);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var parsed) || parsed <= 0)
            {
                return null;
            }

            result[i] = parsed;
        }

        return result;
    }

    private static void SetString(IniDocument document, string section, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            document.Remove(section, key);
            return;
        }

        document.Set(section, key, value.Trim());
    }

    private static void SetInt(IniDocument document, string section, string key, int? value)
    {
        if (!value.HasValue)
        {
            document.Remove(section, key);
            return;
        }

        document.Set(section, key, value.Value.ToString());
    }

    private static void SetBool(IniDocument document, string section, string key, bool? value)
    {
        if (!value.HasValue)
        {
            document.Remove(section, key);
            return;
        }

        document.Set(section, key, value.Value ? "true" : "false");
    }

    private static void SetEnum<TEnum>(IniDocument document, string section, string key, TEnum? value)
        where TEnum : struct, Enum
    {
        if (!value.HasValue)
        {
            document.Remove(section, key);
            return;
        }

        document.Set(section, key, value.Value.ToString());
    }

    private static void SetIntList(IniDocument document, string section, string key, IReadOnlyList<int>? values)
    {
        if (values is null || values.Count == 0)
        {
            document.Remove(section, key);
            return;
        }

        document.Set(section, key, string.Join(",", values));
    }

    private static FormWindowState? NormalizeWindowState(FormWindowState? state) =>
        state switch
        {
            FormWindowState.Maximized => FormWindowState.Maximized,
            FormWindowState.Normal => FormWindowState.Normal,
            _ => null,
        };
}
