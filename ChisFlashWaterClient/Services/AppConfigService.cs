namespace ChisFlashWaterClient.Services;

public static class AppConfigService
{
    private const string ConfigFileName = "config.ini";
    private const string UiSection = "ui";
    private const string PathsGbaSection = "paths_gba";
    private const string PathsGbcSection = "paths_gbc";
    private const string BurnSection = "burn";

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

    public static string? LoadLanguageCode()
    {
        var document = IniDocument.Load(ConfigPath);
        var value = document.Get("ui", "language", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static void SaveLanguageCode(string languageCode)
    {
        var document = IniDocument.Load(ConfigPath);
        document.Set(UiSection, "language", languageCode);
        document.Save(ConfigPath);
    }

    public static string? LoadGbaMenuSelection()
    {
        var document = IniDocument.Load(ConfigPath);
        var value = document.Get(PathsGbaSection, "menu", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static void SaveGbaMenuSelection(string? selection)
    {
        var document = IniDocument.Load(ConfigPath);
        document.Set(PathsGbaSection, "menu", selection ?? string.Empty);
        document.Save(ConfigPath);
    }

    public static string? LoadGbcFontName()
    {
        var document = IniDocument.Load(ConfigPath);
        var value = document.Get(PathsGbcSection, "font_name", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static void SaveGbcFontName(string? fontName)
    {
        var document = IniDocument.Load(ConfigPath);
        document.Set(PathsGbcSection, "font_name", fontName ?? string.Empty);
        document.Save(ConfigPath);
    }

    public static string? LoadGbcBackgroundPath()
    {
        var document = IniDocument.Load(ConfigPath);
        var value = document.Get(PathsGbcSection, "background", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static void SaveGbcBackgroundPath(string? backgroundPath)
    {
        var document = IniDocument.Load(ConfigPath);
        document.Set(PathsGbcSection, "background", backgroundPath ?? string.Empty);
        document.Save(ConfigPath);
    }

    public static string? LoadGbcMenuLanguage()
    {
        var document = IniDocument.Load(ConfigPath);
        var value = document.Get(PathsGbcSection, "menu_language", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static void SaveGbcMenuLanguage(string? menuLanguage)
    {
        var document = IniDocument.Load(ConfigPath);
        document.Set(PathsGbcSection, "menu_language", menuLanguage ?? string.Empty);
        document.Save(ConfigPath);
    }

    public static bool LoadWaitcntPatchEnabled()
    {
        var document = IniDocument.Load(ConfigPath);
        var value = document.Get(BurnSection, "waitcnt_patch", "false");
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static void SaveWaitcntPatchEnabled(bool enabled)
    {
        var document = IniDocument.Load(ConfigPath);
        document.Set(BurnSection, "waitcnt_patch", enabled ? "true" : "false");
        document.Save(ConfigPath);
    }

}
