namespace GbaMultiMenuBuilder.Models;

public sealed record CartridgeDefinition(
    int Id,
    string Name,
    int FlashSize,
    int SectorSize,
    int BlockSize)
{
    public const int WaterlineCompatibleId = 7;
    public const int S70Gl02GId = 5;
    public const int Gbc32MiBId = 101;
    public const int Gbc128MiBId = 102;
    public const int Gbc256MiBId = 103;

    public static IReadOnlyList<CartridgeDefinition> All { get; } =
    [
        new(1, "MSP55LV100S", 0x04000000, 0x00020000, 0x00080000),
        new(6, "S29GL512", 0x04000000, 0x00020000, 0x00080000),
        new(2, "6600M0U0BE", 0x10000000, 0x00040000, 0x00080000),
        new(3, "MSP54LV100", 0x08000000, 0x00020000, 0x00080000),
        new(WaterlineCompatibleId, "S29GL01GS", 0x08000000, 0x00020000, 0x00080000),
        new(4, "F0095H0", 0x20000000, 0x00040000, 0x00080000),
        new(S70Gl02GId, "S70GL02G", 0x10000000, 0x00020000, 0x00080000),
        new(Gbc32MiBId, "32MiB", 32 * 1024 * 1024, 0x00020000, 0x00080000),
        new(Gbc128MiBId, "128MiB", 128 * 1024 * 1024, 0x00020000, 0x00080000),
        new(Gbc256MiBId, "256MiB", 256 * 1024 * 1024, 0x00020000, 0x00080000),
    ];

    private static readonly IReadOnlyList<CartridgeDefinition> GbaUiSelectable =
        All
            .Where(static cartridge => cartridge.Id == WaterlineCompatibleId || cartridge.Id == S70Gl02GId)
            .ToList();

    private static readonly IReadOnlyList<CartridgeDefinition> GbcUiSelectable =
        All
            .Where(static cartridge =>
                cartridge.Id == Gbc32MiBId ||
                cartridge.Id == Gbc128MiBId ||
                cartridge.Id == Gbc256MiBId)
            .ToList();

    public static IReadOnlyList<CartridgeDefinition> GetUiSelectable(CompilationFamily family) =>
        family == CompilationFamily.Gbc
            ? GbcUiSelectable
            : GbaUiSelectable;

    public string UiDisplayName => Id switch
    {
        WaterlineCompatibleId => "128MiB（01G）",
        S70Gl02GId => "256MiB（02G）",
        Gbc32MiBId => "32MiB",
        Gbc128MiBId => "128MiB",
        Gbc256MiBId => "256MiB",
        _ => Name,
    };

    public override string ToString() => Name;
}
