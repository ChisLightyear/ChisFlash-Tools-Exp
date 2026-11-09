using ClientText = ChisFlashWaterClient.Services.LocalizedText;
using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public static class ChisFlash10Image
{
    public const int CapacityBytes = 32 * 1024 * 1024;

    public static DetectedSaveTypeInfo Analyze(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 192 || rom.Length > CapacityBytes)
            throw new InvalidDataException(ClientText.T("client", "chis10_rom_size", "1.0V: the game ROM must be between 192 bytes and 32 MiB."));
        return SaveTypeDetectorService.Detect(rom);
    }

    public static byte[] Build(ReadOnlySpan<byte> rom)
    {
        // 与盖中盖 mission_programRom 一致：只对齐为偶数字节，末字节补 00。
        byte[] image = new byte[rom.Length + (rom.Length & 1)];
        rom.CopyTo(image);
        return image;
    }
}
