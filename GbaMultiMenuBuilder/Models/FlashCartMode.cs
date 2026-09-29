namespace GbaMultiMenuBuilder.Models;

public enum FlashCartMode
{
    NoBattery = 0,
    WithBattery = 1,
    WaterlineVirtualIron = 2,
    Custom = 3,
    Ferroelectric = 4,
    GbcMaxMbcx512K = 5,
    NsMenu = 6,
    SimplifiedNsMenu = 7,
    Chm1 = 8,
    Chm1NoBattery = 9,
    FcMbcx = 10,
}

public static class FlashCartModeExtensions
{
    public static bool IsChm1Mode(this FlashCartMode mode) =>
        mode is FlashCartMode.Chm1 or FlashCartMode.Chm1NoBattery;

    public static bool IsBatterylessMode(this FlashCartMode mode) =>
        mode is FlashCartMode.NoBattery or FlashCartMode.Chm1NoBattery;
}
