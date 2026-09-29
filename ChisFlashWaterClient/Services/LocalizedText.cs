using System.Globalization;

namespace ChisFlashWaterClient.Services;

public static class LocalizedText
{
    private static UiLanguage? _currentLanguage;

    public static UiLanguage CurrentLanguage =>
        _currentLanguage ??= LanguageService.Load();

    public static void Initialize(UiLanguage language)
    {
        _currentLanguage = language;
    }

    public static void SetCurrent(UiLanguage language)
    {
        _currentLanguage = language;
        AppConfigService.SaveLanguageCode(language.Code);
    }

    public static string T(string section, string key, string fallback = "") =>
        CurrentLanguage.Get(section, key, fallback);

    public static string Tf(string section, string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(section, key, fallback), args);
}
