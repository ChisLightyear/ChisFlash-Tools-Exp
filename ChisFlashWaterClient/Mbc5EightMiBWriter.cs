namespace ChisFlashWaterClient;

public static class Mbc5EightMiBWriter
{
    public const int BankBytes = 0x4000;

    public static void Write(
        byte[] image, int detectedCapacity, int sectorBytes, int sectorCount,
        Action<int> eraseSector, Action<int, byte[]> programBank, Func<int, byte[]> readBank,
        Action restoreBank, Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(eraseSector);
        ArgumentNullException.ThrowIfNull(programBank);
        ArgumentNullException.ThrowIfNull(readBank);
        ArgumentNullException.ThrowIfNull(restoreBank);
        if (image.Length != Mbc5EightMiBLayout.CapacityBytes || detectedCapacity != image.Length ||
            sectorBytes < BankBytes || sectorBytes % BankBytes != 0 || image.Length % sectorBytes != 0 ||
            (long)sectorBytes * sectorCount != image.Length)
            throw new InvalidOperationException("Invalid 8 MiB NOR geometry; no write was performed.");
        int banks = image.Length / BankBytes;
        int total = sectorCount + banks * 2;
        int completed = 0;
        cancellationToken.ThrowIfCancellationRequested();
        Exception? operationFailure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            restoreBank();
            for (int sector = 0; sector < sectorCount; sector++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                eraseSector(sector * sectorBytes);
                progress?.Invoke(++completed, total, "erase");
            }
            for (int step = 0; step < banks; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int bank = (step + 64) % banks;
                byte[] data = image.AsSpan(bank * BankBytes, BankBytes).ToArray();
                if (data.Any(value => value != 0xFF))
                    programBank(bank, data);
                progress?.Invoke(++completed, total, "write");
            }
            for (int bank = 0; bank < banks; bank++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] actual = readBank(bank);
                if (!actual.AsSpan().SequenceEqual(image.AsSpan(bank * BankBytes, BankBytes)))
                    throw new IOException($"NOR verification failed at physical ROM bank {bank}.");
                progress?.Invoke(++completed, total, "verify");
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            try { restoreBank(); }
            catch when (operationFailure is not null) { }
        }
    }
}
