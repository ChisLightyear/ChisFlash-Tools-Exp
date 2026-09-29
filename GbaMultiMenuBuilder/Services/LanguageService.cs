namespace GbaMultiMenuBuilder.Services;

public static class LanguageService
{
    public const string DefaultLanguageCode = "zh_CN";

    public static string LanguagesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Languages");

    public static IReadOnlyList<UiLanguage> LoadAvailableLanguages()
    {
        if (!Directory.Exists(LanguagesDirectory))
        {
            return [];
        }

        var result = new List<UiLanguage>();
        foreach (var path in Directory.GetFiles(LanguagesDirectory, "*.ini", SearchOption.TopDirectoryOnly))
        {
            var document = IniDocument.Load(path);
            var code = document.Get("meta", "code", Path.GetFileNameWithoutExtension(path));
            var name = document.Get("meta", "name", code);
            result.Add(new UiLanguage(code, name, document));
        }

        return result
            .OrderBy(static language => language.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static UiLanguage Load(string? preferredCode = null)
    {
        EnsureLanguageDirectoryExists();

        var available = LoadAvailableLanguages();
        if (available.Count == 0)
        {
            throw new InvalidOperationException("No language ini files were found.");
        }

        var languageCode = preferredCode;
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            languageCode = AppConfigService.LoadLanguageCode() ?? DefaultLanguageCode;
        }

        var selected = available.FirstOrDefault(language =>
            string.Equals(language.Code, languageCode, StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(language =>
                string.Equals(language.Code, DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
            ?? available[0];

        AppConfigService.SaveLanguageCode(selected.Code);
        return selected;
    }

    private static void EnsureLanguageDirectoryExists()
    {
        Directory.CreateDirectory(LanguagesDirectory);
    }
}
