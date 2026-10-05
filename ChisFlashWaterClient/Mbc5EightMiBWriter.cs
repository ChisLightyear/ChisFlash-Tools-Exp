namespace ChisFlashWaterClient;

public static class Mbc5EightMiBWriter
{
    public const int BankBytes = GbcNorImageWriter.BankBytes;

    public static void Write(
        byte[] image, int detectedCapacity, int sectorBytes, int sectorCount,
        Action<int> eraseSector, Action<int, byte[]> programBank, Func<int, byte[]> readBank,
        Action restoreBank, Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length != Mbc5EightMiBLayout.CapacityBytes)
            throw new InvalidOperationException("Invalid 8 MiB NOR image; no write was performed.");
        GbcNorImageWriter.Write(image, detectedCapacity, sectorBytes, sectorCount,
            eraseSector, programBank, readBank, restoreBank, progress, cancellationToken);
    }
}
