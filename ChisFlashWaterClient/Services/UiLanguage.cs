namespace ChisFlashWaterClient.Services;

public sealed class UiLanguage
{
    private readonly IniDocument _document;

    public UiLanguage(string code, string displayName, IniDocument document)
    {
        Code = code;
        DisplayName = displayName;
        _document = document;
    }

    public string Code { get; }

    public string DisplayName { get; }

    public string Get(string section, string key, string fallback = "") =>
        _document.Get(section, key, fallback);

    public override string ToString() => DisplayName;
}
