namespace ChisFlashWaterClient;

public static class GbcNorImageWriter
{
    public const int BankBytes = 0x4000;

    /// <summary>
    /// NOR 绝对扇区地址 → GBC 总线上的擦除命令地址。
    /// bank 0 的 0x0000-0x3FFF 是固定窗口，其余 bank 映射到 0x4000-0x7FFF。
    /// 8 KiB 小扇区共用一个 bank 时，命令地址必须带 bank 内偏移，否则只会擦到该 bank 的第一个扇区。
    /// </summary>
    public static int SectorRomBank(int sectorOffset) => sectorOffset / BankBytes;

    public static uint SectorCommandAddress(int sectorOffset)
    {
        int bank = SectorRomBank(sectorOffset);
        return bank == 0
            ? (uint)sectorOffset
            : (uint)(0x4000 + (sectorOffset & (BankBytes - 1)));
    }

    /// <summary>
    /// 固定扇区（均匀布局）写入。保留原签名，供 32 MiB / 旧几何路径与离线自测继续使用。
    /// </summary>
    public static void Write(
        byte[] image, int detectedCapacity, int sectorBytes, int sectorCount,
        Action<int> eraseSector, Action<int, byte[]> programBank,
        Action restoreBank, Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(eraseSector);
        ArgumentNullException.ThrowIfNull(programBank);
        ArgumentNullException.ThrowIfNull(restoreBank);
        if (image.Length <= 0 || detectedCapacity != image.Length ||
            sectorBytes < BankBytes || sectorBytes % BankBytes != 0 || image.Length % sectorBytes != 0 ||
            (long)sectorBytes * sectorCount != image.Length)
            throw new InvalidOperationException("Invalid NOR image or geometry; no write was performed.");

        List<(int Offset, int Size)> sectors = new(sectorCount);
        for (int sector = 0; sector < sectorCount; sector++)
            sectors.Add((sector * sectorBytes, sectorBytes));

        WriteCore(image, detectedCapacity, sectors,
            (offset, _) => eraseSector(offset), programBank, restoreBank, progress, cancellationToken);
    }

    /// <summary>均匀扇区：128 × 64 KiB，无小扇区。</summary>
    public static void WriteUniform(
        byte[] image, int detectedCapacity, Action<int, int> eraseSector,
        Action<int, byte[]> programBank, Action restoreBank,
        Action<int, int, string>? progress = null, CancellationToken cancellationToken = default) =>
        WriteForLayout(image, detectedCapacity, GbcNorSectorLayout.Uniform,
            eraseSector, programBank, restoreBank, progress, cancellationToken);

    /// <summary>头部小扇区：8 × 8 KiB + 127 × 64 KiB。</summary>
    public static void WriteBootAtStart(
        byte[] image, int detectedCapacity, Action<int, int> eraseSector,
        Action<int, byte[]> programBank, Action restoreBank,
        Action<int, int, string>? progress = null, CancellationToken cancellationToken = default) =>
        WriteForLayout(image, detectedCapacity, GbcNorSectorLayout.BootAtStart,
            eraseSector, programBank, restoreBank, progress, cancellationToken);

    /// <summary>尾部小扇区：127 × 64 KiB + 8 × 8 KiB。</summary>
    public static void WriteBootAtEnd(
        byte[] image, int detectedCapacity, Action<int, int> eraseSector,
        Action<int, byte[]> programBank, Action restoreBank,
        Action<int, int, string>? progress = null, CancellationToken cancellationToken = default) =>
        WriteForLayout(image, detectedCapacity, GbcNorSectorLayout.BootAtEnd,
            eraseSector, programBank, restoreBank, progress, cancellationToken);

    /// <summary>
    /// 产品擦写入口：吃烧录时现场读 CFI 生成的逐扇区表（盖中盖口径、不设失败门槛）。
    /// 表可能为空或不完整，照样继续：空表就是擦 0 次、写入照跑。
    /// 差异只在擦除用的扇区表，写入阶段完全一致（一份内核、不区分扇区）。
    /// </summary>
    public static void WriteForSectorMap(
        byte[] image, int detectedCapacity,
        IReadOnlyList<(int Offset, int Size)> sectors,
        Action<int, int> eraseSector, Action<int, byte[]> programBank,
        Action restoreBank, Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sectors);
        WriteCore(image, detectedCapacity, sectors,
            eraseSector, programBank, restoreBank, progress, cancellationToken);
    }

    /// <summary>
    /// 离线对照入口：吃按 NOR ID 查出的静态布局表。11-18 起产品路径不再调用，
    /// 保留供取证与离线自测比对动态表结果。
    /// </summary>
    public static void WriteForLayout(
        byte[] image, int detectedCapacity, GbcNorSectorLayout layout,
        Action<int, int> eraseSector, Action<int, byte[]> programBank,
        Action restoreBank, Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(int Offset, int Size)> sectors = GbcNorIdCatalog.GetSectors(layout);
        if (sectors.Count == 0)
            throw new InvalidOperationException("Unrecognized NOR sector layout; no write was performed.");

        WriteCore(image, detectedCapacity, sectors,
            eraseSector, programBank, restoreBank, progress, cancellationToken);
    }

    private static void WriteCore(
        byte[] image, int detectedCapacity,
        IReadOnlyList<(int Offset, int Size)> sectors,
        Action<int, int> eraseSector, Action<int, byte[]> programBank,
        Action restoreBank, Action<int, int, string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(eraseSector);
        ArgumentNullException.ThrowIfNull(programBank);
        ArgumentNullException.ThrowIfNull(restoreBank);
        // 盖中盖口径：不设读数失败门槛。只保留两条让循环能跑的算术前提：
        // 镜像非空、长度是 16 KiB bank 的整数倍；镜像长度 ≤ 现场读到的容量。
        // 扇区表不再校验（是否连续 / 大小合法 / 是否铺满），读到的值直接下发。
        if (image.Length <= 0 || image.Length > detectedCapacity || image.Length % BankBytes != 0)
            throw new InvalidOperationException("Invalid NOR image or geometry; no write was performed.");

        int banks = image.Length / BankBytes;
        int total = sectors.Count + banks;
        int completed = 0;
        cancellationToken.ThrowIfCancellationRequested();
        Exception? operationFailure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            restoreBank();
            foreach ((int offset, int size) in sectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                eraseSector(offset, size);
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
