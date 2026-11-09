namespace ChisFlashWaterClient;

public static class GbaWorkspaceClearPlan
{
    public static byte[] PreserveStatusSector(byte[] original, byte[] updated)
    {
        if (original.Length != updated.Length || original.Length < 11)
            throw new InvalidOperationException("卡带配置扇区长度无效。");
        byte[] result = original.ToArray();
        // 只取消旧游戏的启动/存档选择，其余配置与保留字节逐字节保留。
        updated.AsSpan(6, 4).CopyTo(result.AsSpan(6, 4));
        return result;
    }

    public static WaterCartScanResult BuildClearedScanResult(WaterCartScanResult scan)
    {
        var cleared = WaterCartScanner.RebuildCachedScanResult(scan,
            wlMappingOverride: new(), menuConfigOverride: new(), gamesOverride: []);
        cleared.MenuBackgroundBitmapData = [];
        cleared.MenuBackgroundPaletteData = [];
        cleared.IsUniversalGbaMenuDetected = false;
        cleared.IsSimplifiedNsGbaMenuDetected = false;
        cleared.GbaMenuFirstScreenPreview = new();
        return cleared;
    }

    // 合卡清除菜单、目录、菜单配置、映射及游戏，系统区内的存档扇区仍保留。
    public static uint[] BuildEraseSectors(WaterCartScanResult scan, IEnumerable<WaterNorWriteSegment>? replacementSegments = null)
    {
        int sector = scan.Geometry.SectorSizeBytes;
        int capacity = scan.Geometry.DeviceSizeBytes;
        if (sector <= 0 || capacity <= 0 || capacity % sector != 0)
            throw new InvalidOperationException("NOR容量或扇区尺寸无效，请重新分析卡带。");
        bool hasSystemArea = scan.MenuConfig.IsDetected || scan.WlMapping.IsDetected || scan.Games.Any(game => game.IsMenu);
        int start = hasSystemArea ? WaterCartScanner.BucketSizeBytes : 0;
        if (start > capacity || start % sector != 0)
            throw new InvalidOperationException("菜单系统区与NOR扇区不对齐，不能清空游戏区。");
        int menuEnd = hasSystemArea ? checked(scan.MenuConfig.Status.StatusAddress + scan.MenuConfig.Status.SectorSize) : 0;
        if (hasSystemArea && (!scan.MenuConfig.IsDetected || menuEnd <= 0 || menuEnd > start || menuEnd % sector != 0))
            throw new InvalidOperationException("菜单与配置边界无效，请先分析卡带配置。");
        bool clearMapping = scan.WlMapping.IsDetected;
        if (clearMapping && (WaterWlMappingCodec.Offset % sector != 0 || WaterWlMappingCodec.SectorSize % sector != 0))
            throw new InvalidOperationException("映射区与NOR扇区不对齐，不能清空映射。");
        var replacements = (replacementSegments ?? []).ToArray();
        return Enumerable.Range(0, capacity / sector)
            .Select(index => checked((uint)(index * sector)))
            .Where(address => address >= start || address < menuEnd ||
                clearMapping && address >= WaterWlMappingCodec.Offset && address < WaterWlMappingCodec.Offset + WaterWlMappingCodec.SectorSize)
            // 新ROM自己的擦写流程会覆盖这些扇区，避免清空阶段重复擦除。
            .Where(address => !replacements.Any(segment => segment.Data.Length > 0 &&
                address < (long)segment.StartAddress + segment.Data.Length && (long)address + sector > segment.StartAddress))
            .ToArray();
    }
}
