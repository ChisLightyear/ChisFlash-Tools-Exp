namespace GbaMultiMenuBuilder.Models;

public sealed record BuildRequest(
    string BaseMenuPath,
    string OutputPath,
    string? BackgroundPath,
    string? GbcFontPath,
    CartridgeDefinition Cartridge,
    int MinRomSize,
    FlashCartMode FlashCartMode,
    GbaMenuType GbaMenuType,
    bool BatteryPresent,
    bool SplitOutput,
    IReadOnlyList<BuildGameEntry> Games,
    CompilationFamily Family = CompilationFamily.Gba,
    bool MinimalMenuPackage = false);
