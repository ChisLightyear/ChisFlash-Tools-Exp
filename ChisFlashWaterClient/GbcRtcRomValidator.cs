using System.IO;
using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public readonly record struct GbcRtcRomInspection(byte CartridgeType, bool HasMbc3Rtc);

public static class GbcRtcRomValidator
{
    public const int MaximumGameBytes = 0x200000;

    public static GbcRtcRomInspection Inspect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x8000 || data.Length > MaximumGameBytes)
            throw new InvalidDataException("RTC_ROM_SIZE");
        if (data[0x148] is not (<= 8 or 0x52 or 0x53 or 0x54))
            throw new InvalidDataException("RTC_ROM_HEADER");
        var metadata = GameBoyRomHeaderService.Read("rtc.gb", data);
        if (!metadata.HasValidNintendoLogo)
            throw new InvalidDataException("RTC_ROM_HEADER");
        if (metadata.EffectiveRomSizeBytes > MaximumGameBytes)
            throw new InvalidDataException("RTC_ROM_SIZE");
        byte cartridgeType = data[0x147];
        return new(cartridgeType, cartridgeType is 0x0F or 0x10);
    }

    public static bool IsValid(ReadOnlySpan<byte> data)
    {
        try { Inspect(data); return true; }
        catch (InvalidDataException) { return false; }
    }
}
