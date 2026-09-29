namespace GbaMultiMenuBuilder.Models;

public sealed record AppSettings
{
    public string? LanguageCode { get; init; }

    public string? BaseMenuPath { get; init; }

    public string? OutputPath { get; init; }

    public string? BackgroundPath { get; init; }

    public string? GbcFontName { get; init; }

    public int? CartridgeId { get; init; }

    public int? MinRomSize { get; init; }

    public FlashCartMode? FlashCartMode { get; init; }

    public GbaMenuType? GbaMenuType { get; init; }

    public bool? BatteryPresent { get; init; }

    public bool? SplitOutput { get; init; }

    public bool? MinimalMenuPackage { get; init; }

    public bool? ApplySramPatch { get; init; }

    public FormWindowState? WindowState { get; init; }

    public int? WindowLeft { get; init; }

    public int? WindowTop { get; init; }

    public int? WindowWidth { get; init; }

    public int? WindowHeight { get; init; }

    public int[]? GameGridColumnWidths { get; init; }
}
