namespace GbaMultiMenuBuilder.Models;

public sealed record MenuPreviewModel(
    string Title,
    string Summary,
    CompilationFamily Family,
    string LanguageCode,
    int FontSizePx,
    int GamesPerPage,
    int SelectedIndex,
    IReadOnlyList<MenuPreviewEntry> Entries,
    byte[]? BackgroundBitmapData = null,
    byte[]? BackgroundPaletteData = null);

public sealed record MenuPreviewEntry(
    int Index,
    string DisplayTitle,
    string HeaderTitle,
    bool Enabled,
    bool Included,
    bool Selected,
    int PageIndex,
    int RowIndex,
    string SecondaryInfo,
    bool UsesDedicatedSave,
    bool HasSaveSupport,
    IReadOnlyList<ushort>? EncodedNameCodes,
    IReadOnlyDictionary<ushort, byte[]>? GlyphBitmaps,
    int GlyphSizePx,
    string DetailTypeText,
    string DetailRomText,
    string DetailRamText,
    string DetailSaveText,
    string DetailBankText = "");
