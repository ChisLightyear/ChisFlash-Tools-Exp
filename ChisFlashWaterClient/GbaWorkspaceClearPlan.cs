using ClientText = ChisFlashWaterClient.Services.LocalizedText;
namespace ChisFlashWaterClient;

public static class GbaWorkspaceClearPlan
{
    internal static byte[] BuildRemovedGameSector(int sectorSize)
    {
        var data = new byte[sectorSize];
        ReadOnlySpan<byte> pattern = "CHIS"u8;
        for (int offset = 0; offset < data.Length; offset += pattern.Length)
            pattern[..Math.Min(pattern.Length, data.Length - offset)].CopyTo(data.AsSpan(offset));
        return data;
    }

    internal static (uint Address, int SizeBytes) PlanHeadErase(WaterCartScanResult scan)
    {
        if (scan.MenuConfig.IsDetected || scan.WlMapping.IsDetected ||
            scan.Games.Any(game => game.IsMenu) || !scan.Games.Any(game => game.StartBucket == 0 && !game.IsMenu))
            throw new InvalidOperationException(ClientText.T("client", "clear_analyze_first",
                "Analyze the cartridge contents before clearing its games."));

        return (0, GetHeadEraseSectorSize(scan.Geometry));
    }

    internal static uint[] PlanMappedGameHeadErases(WaterCartScanResult scan, IReadOnlySet<WaterCartEntry> removed)
    {
        if (removed.Count == 0) return [];

        int sectorSize = GetHeadEraseSectorSize(scan.Geometry);
        var occupied = scan.Entries.Where(entry => !removed.Contains(entry)).SelectMany(entry => entry.PhysicalBuckets).ToHashSet();
        var addresses = new SortedSet<uint>();
        foreach (var game in removed)
        {
            int bucket = game.StartBucket;
            long address = (long)bucket * WaterCartScanner.BucketSizeBytes;
            if (game.IsMenu || !scan.Entries.Contains(game) || bucket <= 0 ||
                occupied.Contains(bucket) || address % sectorSize != 0 || address + sectorSize > scan.Geometry.DeviceSizeBytes)
                throw new InvalidOperationException(ClientText.Tf("client", "game_head_erase_range_invalid",
                    "Cannot erase the boot header for {0} separately. Analyze the cartridge again and check the game layout.", game.Title));
            addresses.Add((uint)address);
        }
        return addresses.ToArray();
    }

    private static int GetHeadEraseSectorSize(NorGeometryInfo geometry)
    {
        // GBA CFI 将最小扇区大小记在 SectorSizeBytes，混合几何不能用该值确认首扇区大小。
        if (geometry.SectorSizeBytes <= 0 ||
            geometry.SectorSizeBytes > WaterCartScanner.BucketSizeBytes ||
            geometry.SectorSizeBytes > geometry.DeviceSizeBytes ||
            geometry.RegionCount != 1 && geometry.LargestSectorSizeBytes != geometry.SectorSizeBytes)
            throw new InvalidOperationException(ClientText.T("client", "nor_geometry_invalid",
                "The NOR capacity or sector size is invalid. Analyze the cartridge again."));

        return geometry.SectorSizeBytes;
    }

    internal static WaterCartScanResult BuildHeadRemovedScan(WaterCartScanResult scan) =>
        WaterCartScanner.RebuildCachedScanResult(scan,
            gamesOverride: scan.Games.Where(game => game.StartBucket != 0).ToArray());

    public static byte[] PreserveStatusSector(byte[] original, byte[] updated)
    {
        if (original.Length != updated.Length || original.Length < 11)
            throw new InvalidOperationException(ClientText.T("client", "config_sector_invalid", "The cartridge configuration sector length is invalid."));
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

    // 合卡只清菜单、目录、状态和映射。单卡工作区沿用原游戏区擦除范围。
    public static uint[] BuildEraseSectors(WaterCartScanResult scan, IEnumerable<WaterNorWriteSegment>? replacementSegments = null, bool clearGameArea = false)
    {
        int sector = scan.Geometry.SectorSizeBytes;
        int capacity = scan.Geometry.DeviceSizeBytes;
        if (sector <= 0 || capacity <= 0 || capacity % sector != 0)
            throw new InvalidOperationException(ClientText.T("client", "nor_geometry_invalid", "The NOR capacity or sector size is invalid. Analyze the cartridge again."));
        bool hasSystemArea = scan.MenuConfig.IsDetected || scan.WlMapping.IsDetected || scan.Games.Any(game => game.IsMenu);
        int start = hasSystemArea ? WaterCartScanner.BucketSizeBytes : 0;
        if (start > capacity || start % sector != 0)
            throw new InvalidOperationException(ClientText.T("client", "system_sector_unaligned", "The menu system area is not aligned to NOR sectors. The game area cannot be cleared."));
        MenuFlashStatusInfo status = scan.MenuConfig.Status;
        if (hasSystemArea && !scan.MenuConfig.IsDetected)
            status = scan.WlMapping.IsDetected || scan.IsChm1
                ? WaterMagicUpgradeBuilder.NormalizeWlStatus(status)
                : WaterLegacyMenuRepairBuilder.CreateWriteStatus(scan.Geometry, false);
        int menuEnd = hasSystemArea ? checked(status.StatusAddress + status.SectorSize) : 0;
        if (hasSystemArea && (menuEnd <= 0 || menuEnd > start || menuEnd % sector != 0))
            throw new InvalidOperationException(ClientText.T("client", "menu_config_bounds_invalid", "The menu and configuration boundaries are invalid. Analyze the cartridge configuration first."));
        bool clearMapping = scan.WlMapping.IsDetected;
        if (clearMapping && (WaterWlMappingCodec.Offset % sector != 0 || WaterWlMappingCodec.SectorSize % sector != 0))
            throw new InvalidOperationException(ClientText.T("client", "mapping_sector_unaligned", "The mapping area is not aligned to NOR sectors. The mapping cannot be cleared."));
        var replacements = (replacementSegments ?? []).ToArray();
        int end = clearGameArea ? capacity : start;
        return Enumerable.Range(0, end / sector)
            .Select(index => checked((uint)(index * sector)))
            .Where(address => address >= start || address < menuEnd ||
                clearMapping && address >= WaterWlMappingCodec.Offset && address < WaterWlMappingCodec.Offset + WaterWlMappingCodec.SectorSize)
            // 新ROM自己的擦写流程会覆盖这些扇区，避免清空阶段重复擦除。
            .Where(address => !replacements.Any(segment => segment.Data.Length > 0 &&
                address < (long)segment.StartAddress + segment.Data.Length && (long)address + sector > segment.StartAddress))
            .ToArray();
    }
}
