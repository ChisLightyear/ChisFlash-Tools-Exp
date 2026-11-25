namespace ChisFlashWaterClient;

/// <summary>
/// 一次 GBC/MAX CFI 读里的一个 Erase Block Region：扇区数与该组扇区字节数。
/// 顺序按 CFI 表原样保留，不按大小排序、不按物理地址重排、不修正读到的值。
/// </summary>
public readonly record struct GbcEraseRegionInfo(int SectorCount, int SectorSizeBytes);

/// <summary>
/// 烧录时现场读到的一份 CFI 几何：容量、写缓冲、逐扇区表。原样保留读到的值，不校验、不拒绝。
/// </summary>
public readonly record struct GbcCfiBurnGeometry(
    int DeviceSizeBytes,
    int BufferWriteBytes,
    IReadOnlyList<(int Offset, int Size)> Sectors);

/// <summary>
/// GBC 单卡扇区几何。产品烧录用 ReadBurnGeometry（盖中盖口径、不设失败门槛）；
/// TryBuild 只作离线对照与取证，产品擦写路径不再调用。
/// NOR ID 完全不参与：没读到 / 全 00 / 全 FF 只是型号显示 Unknown。
/// </summary>
public static class GbcCfiSectorMap
{
    public const int MaxRegions = 4;
    /// <summary>CFI 0x9E：02 bottom boot / 03 top boot / 04、05 uniform。</summary>
    public const byte BottomBootFlag = 0x02;
    public const byte TopBootFlag = 0x03;
    /// <summary>仅供离线对照的最小扇区尺寸。</summary>
    public const int MinimumSectorSizeBytes = 0x2000;
    /// <summary>一次 CFI 窗口读 0x0000..0x00FF。</summary>
    public const int CfiWindowBytes = 0x100;

    /// <summary>
    /// 盖中盖 mbc5_romGetSize() + mbc5_sectorInfo() 口径：把一次窗口读直接解析成容量、
    /// 写缓冲与逐扇区表，由烧录入口执行原版的空间检查。
    /// 02 bottom boot 按 CFI 顺序（小扇区组在头），03 top boot 交换两组（小扇区组在尾），
    /// 其余一律按 region 1 等步长铺（与盖中盖 else 分支一致）。
    /// </summary>
    public static GbcCfiBurnGeometry ReadBurnGeometry(byte[] cfi)
    {
        int deviceSizeExponent = cfi[0x4E];
        // 复现原版 .NET Framework 在 64 位下将超出 int 范围的容量转换为 int.MinValue 的行为。
        int deviceSizeBytes = deviceSizeExponent < 31 ? 1 << deviceSizeExponent : int.MinValue;
        int bufferField = (cfi[0x56] << 8) | cfi[0x54];
        int bufferWriteBytes = bufferField == 0 ? 0 : (int)Math.Pow(2, cfi[0x54]);

        int regionCount = cfi[0x58];
        int count0 = ((cfi[0x5C] << 8) | cfi[0x5A]) + 1;
        int size0 = ((cfi[0x60] << 8) | cfi[0x5E]) * 256;
        int count1 = ((cfi[0x64] << 8) | cfi[0x62]) + 1;
        int size1 = ((cfi[0x68] << 8) | cfi[0x66]) * 256;
        byte bootFlag = cfi[0x9E];

        List<(int Offset, int Size)> sectors = [];
        if (bootFlag == BottomBootFlag && regionCount == 2)
        {
            for (int i = 0; i < count0; i++)
                sectors.Add((i * size0, size0));
            int base1 = count0 * size0;
            for (int i = 0; i < count1; i++)
                sectors.Add((base1 + i * size1, size1));
        }
        else if (bootFlag == TopBootFlag && regionCount == 2)
        {
            for (int i = 0; i < count1; i++)
                sectors.Add((i * size1, size1));
            int base0 = count1 * size1;
            for (int i = 0; i < count0; i++)
                sectors.Add((base0 + i * size0, size0));
        }
        else
        {
            for (int i = 0; i < count0; i++)
                sectors.Add((i * size0, size0));
        }

        return new GbcCfiBurnGeometry(deviceSizeBytes, bufferWriteBytes, sectors);
    }

    internal static GbcCfiBurnGeometry ReadBurnGeometry(byte[] cfi, NorGeometryInfo measured, int imageLength)
    {
        if (cfi.AsSpan().IndexOfAnyExcept((byte)0) >= 0 && cfi.AsSpan().IndexOfAnyExcept((byte)0xFF) >= 0)
            return ReadBurnGeometry(cfi);
        int capacity = !measured.IsCfiWindowBlank && measured.DeviceSizeBytes > 1 ? measured.DeviceSizeBytes : imageLength;
        if (!measured.IsCfiWindowBlank && TryBuild(measured.EraseRegions, measured.GbcBootBlockFlag, capacity, out var sectors))
            return new(capacity, measured.BufferWriteBytes, sectors);
        int size = !measured.IsCfiWindowBlank && measured.SectorSizeBytes > 0 ? measured.SectorSizeBytes : WaterWlMappingCodec.SectorSize;
        return new(capacity, measured.BufferWriteBytes, Enumerable.Range(0, (imageLength + size - 1) / size)
            .Select(index => (index * size, size)).ToArray());
    }

    /// <summary>
    /// 生成按地址递增排列、铺满 deviceSizeBytes 的扇区表。
    /// region 明细缺失 / 非法 / 铺不满容量一律返回 false，不猜、不回落固定几何。
    /// </summary>
    public static bool TryBuild(
        IReadOnlyList<GbcEraseRegionInfo> regions,
        byte bootFlag,
        int deviceSizeBytes,
        out IReadOnlyList<(int Offset, int Size)> sectors)
    {
        sectors = [];
        if (deviceSizeBytes <= 0 || regions.Count is < 1 or > MaxRegions)
            return false;

        // boot 卡的 CFI region 1 恒为 8K 小扇区组，region 表顺序并不代表物理地址顺序：
        // 02 bottom boot = 小扇区组在头（按 CFI 顺序）；03 top boot = 大扇区组在头，
        // 小扇区组在尾，故交换两组。其余情形（含均匀卡）按 CFI 顺序逐组铺满。
        IReadOnlyList<GbcEraseRegionInfo> ordered = regions;
        if (regions.Count == 2 && bootFlag == TopBootFlag)
            ordered = [regions[1], regions[0]];

        List<(int Offset, int Size)> map = [];
        long nextOffset = 0;
        foreach (GbcEraseRegionInfo region in ordered)
        {
            if (region.SectorCount <= 0 || region.SectorSizeBytes <= 0 ||
                region.SectorSizeBytes % MinimumSectorSizeBytes != 0)
                return false;

            for (int i = 0; i < region.SectorCount; i++)
            {
                long offset = nextOffset;
                if (offset % region.SectorSizeBytes != 0 ||
                    offset + region.SectorSizeBytes > deviceSizeBytes)
                    return false;

                map.Add(((int)offset, region.SectorSizeBytes));
                nextOffset = offset + region.SectorSizeBytes;
            }
        }

        if (map.Count == 0 || nextOffset != deviceSizeBytes)
            return false;

        sectors = map;
        return true;
    }
}
