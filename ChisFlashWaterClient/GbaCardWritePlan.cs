namespace ChisFlashWaterClient;

public static class GbaCardWritePlan
{
    public const int HeadBytes = 0x400000;

    public static byte[] CompleteMapping(byte[] image, bool waterLink)
    {
        if (!waterLink) return image;
        var scan = WaterCartScanner.ScanOfflineRomImage(image);
        if (!scan.MenuConfig.IsDetected || image.Length < HeadBytes)
            throw new InvalidDataException("水线合卡镜像缺少有效目录。");
        byte[] result = image.ToArray();
        WaterMagicUpgradeBuilder.BuildMappingSector(scan.MenuConfig).CopyTo(result, WaterWlMappingCodec.Offset);
        var status = WaterMagicUpgradeBuilder.NormalizeWlStatus(scan.MenuConfig.Status);
        WaterMenuConfigCodec.BuildStatusSector(status).CopyTo(result, status.StatusAddress);
        return result;
    }

    public static IReadOnlyList<WaterNorWriteSegment> Multicart(byte[] image, int capacity, int sectorBytes)
    {
        if (sectorBytes <= 0 || HeadBytes % sectorBytes != 0 || image.Length < HeadBytes || image.Length > capacity)
            throw new InvalidDataException("合卡镜像或NOR扇区尺寸不匹配。");
        int aligned = checked((image.Length + sectorBytes - 1) / sectorBytes * sectorBytes);
        if (aligned > capacity) throw new InvalidDataException("合卡镜像超过实际NOR容量。");
        byte[] data = Enumerable.Repeat((byte)255, aligned).ToArray();
        image.CopyTo(data, 0);
        var segments = new List<WaterNorWriteSegment>();
        if (aligned > HeadBytes)
            segments.Add(new() { StartAddress = HeadBytes, Data = data.AsSpan(HeadBytes).ToArray(), Label = "合卡游戏区" });
        // 游戏和存档准备完成后才提交菜单与目录。
        segments.Add(new() { StartAddress = 0, Data = data.AsSpan(0, HeadBytes).ToArray(), Label = "合卡菜单和目录" });
        return segments;
    }
}
