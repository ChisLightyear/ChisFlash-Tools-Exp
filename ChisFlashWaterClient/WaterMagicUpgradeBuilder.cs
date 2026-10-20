using System.Buffers.Binary;
using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public sealed class WaterMagicUpgradePlan
{
    public WaterMenuConfigInfo MigratedMenuConfig { get; init; } = new();
    public byte[] MenuAreaImage { get; init; } = [];
    public byte[] ItemListSector { get; init; } = [];
    public byte[] StatusSector { get; init; } = [];
    public byte[] MappingSector { get; init; } = [];
    public List<WaterCartEntry> DroppedLegacyEntries { get; init; } = [];
    public List<WaterCartEntry> AddedHeaderOnlyEntries { get; init; } = [];
}

public sealed class WaterMagicRepairPlan
{
    public WaterMagicMetadataSyncInfo SyncInfo { get; init; } = new();
    public WaterMenuConfigInfo RepairedMenuConfig { get; init; } = new();
    public List<WlRomMapRecordInfo> RepairedRecords { get; init; } = [];
    public byte[] ItemListSector { get; init; } = [];
    public byte[] StatusSector { get; init; } = [];
    public byte[] MappingSector { get; init; } = [];
    public List<MenuItemConfigInfo> RemovedMenuOnlyItems { get; init; } = [];
}

public sealed class WaterMagicInstallPlan
{
    public WaterMenuConfigInfo InstalledMenuConfig { get; init; } = new();
    public byte[] HeadAreaImage { get; init; } = [];
    public byte[] ItemListSector { get; init; } = [];
    public byte[] StatusSector { get; init; } = [];
    public byte[] MappingSector { get; init; } = [];
    public List<WaterCartEntry> InstalledEntries { get; init; } = [];
    public WaterCartEntry? DroppedHeadGame { get; init; }
}

public static class WaterMagicUpgradeBuilder
{
    private const ushort BucketOwnerEntrySize = sizeof(ushort);
    private const ushort Flags = 0;
    private const int WlHeadAreaSize = WaterCartScanner.BucketSizeBytes;
    public const int WlItemListOffset = 0x0C0000;
    public const int WlStatusOffset = 0x0E0000;
    private const uint StatusMagic = 0x414D554B;

    public static WaterMagicMetadataSyncInfo AnalyzeMagicMetadataSync(WaterMenuConfigInfo menuConfig, WaterWlMappingInfo wlMapping)
    {
        WaterMagicMetadataSyncInfo syncInfo = new()
        {
            IsApplicable = menuConfig.IsDetected && wlMapping.IsDetected,
            MenuItemCount = menuConfig.ItemCount,
            MappingRecordCount = wlMapping.RecordCount,
        };

        if (!syncInfo.IsApplicable)
        {
            return syncInfo;
        }

        Dictionary<int, MenuItemConfigInfo> menuItemByIndex = menuConfig.Items
            .GroupBy(item => item.Index)
            .ToDictionary(group => group.Key, group => group.First());

        Dictionary<int, WlRomMapRecordInfo> recordBySlot = wlMapping.Records
            .GroupBy(record => record.EntrySlot)
            .ToDictionary(group => group.Key, group => group.First());

        syncInfo.MenuOnlySlots = menuItemByIndex.Keys
            .Except(recordBySlot.Keys)
            .OrderBy(slot => slot)
            .ToList();

        syncInfo.MappingOnlySlots = recordBySlot.Keys
            .Except(menuItemByIndex.Keys)
            .OrderBy(slot => slot)
            .ToList();

        syncInfo.SlotRangeMismatchSlots = menuItemByIndex.Keys
            .Intersect(recordBySlot.Keys)
            .Where(slot => !MenuItemMatchesRecord(menuItemByIndex[slot], recordBySlot[slot]))
            .OrderBy(slot => slot)
            .ToList();

        syncInfo.NonSequentialRecordSlots = wlMapping.Records
            .OrderBy(record => record.EntrySlot)
            .Select((record, expectedSlot) => new { record.EntrySlot, ExpectedSlot = expectedSlot })
            .Where(item => item.EntrySlot != item.ExpectedSlot)
            .Select(item => item.EntrySlot)
            .ToList();

        return syncInfo;
    }

    public static bool CanUpgradeToMagic(WaterCartScanResult? scanResult, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (scanResult is null)
        {
            errorMessage = LocalizedText.T("error", "scan_cart_first", "Please scan the cart first.");
            return false;
        }

        if (scanResult.WlMapping.IsDetected)
        {
            errorMessage = LocalizedText.T("error", "already_magic_layout", "This cart is already using the Magic layout.");
            return false;
        }

        if (!scanResult.MenuConfig.IsDetected || scanResult.MenuConfig.ItemCount <= 0)
        {
            errorMessage = LocalizedText.T("error", "no_legacy_menu_config", "No legacy menu config was detected for migration.");
            return false;
        }

        if (scanResult.Geometry.SectorSizeBytes != WaterWlMappingCodec.SectorSize)
        {
            errorMessage = LocalizedText.Tf(
                "error",
                "magic_sector_size_unsupported",
                "Current NOR sector size is 0x{0:X}; one-click migration is not supported.",
                scanResult.Geometry.SectorSizeBytes);
            return false;
        }

        int itemListAddress = scanResult.MenuConfig.Status.ItemListAddress;
        if (itemListAddress <= 0 || itemListAddress % scanResult.Geometry.SectorSizeBytes != 0)
        {
            errorMessage = LocalizedText.T("error", "legacy_itemlist_alignment_invalid", "Legacy itemlist address is not aligned to a valid NOR sector boundary.");
            return false;
        }

        return true;
    }

    public static bool CanRepairMagicMetadata(WaterCartScanResult? scanResult, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (scanResult is null)
        {
            errorMessage = LocalizedText.T("error", "scan_cart_first", "Please scan the cart first.");
            return false;
        }

        bool legacyMappingNeedsRepair = scanResult.WlMapping.LegacyVersionDetected;

        bool invalidWlMappingDetected =
            scanResult.WlMapping.Header.Magic == WaterWlMappingCodec.Magic &&
            !scanResult.WlMapping.IsDetected &&
            !legacyMappingNeedsRepair;

        bool canRebuildMappingFromFixedMenu = scanResult.MenuConfig.IsDetected &&
            scanResult.MenuConfig.Status.ItemListAddress == WlItemListOffset &&
            scanResult.MenuConfig.ItemCount > 0;

        if (!scanResult.WlMapping.IsDetected && !legacyMappingNeedsRepair)
        {
            if ((invalidWlMappingDetected || scanResult.WlMapping.RawSector.Length == 0) &&
                canRebuildMappingFromFixedMenu)
            {
                return true;
            }

            errorMessage = invalidWlMappingDetected && !string.IsNullOrWhiteSpace(scanResult.WlMapping.ValidationError)
                ? scanResult.WlMapping.ValidationError
                : LocalizedText.T("error", "magic_repair_requires_wl", "The current cart is not using the Magic WL layout.");
            return false;
        }

        bool canRebuildItemlistFromMapping = scanResult.WlMapping.IsDetected &&
            scanResult.WlMapping.RecordCount > 0 &&
            scanResult.WlMapping.RawSector.Length == WaterWlMappingCodec.SectorSize;

        if (!scanResult.MenuConfig.IsDetected && !canRebuildItemlistFromMapping)
        {
            errorMessage = LocalizedText.T("error", "magic_repair_requires_menu", "No menu itemlist was detected, so Magic metadata cannot be repaired.");
            return false;
        }

        if (scanResult.WlMapping.RawSector.Length != WaterWlMappingCodec.SectorSize)
        {
            errorMessage = LocalizedText.T("error", "wl_sector_length_invalid", "WL mapping sector data length is invalid.");
            return false;
        }

        int menuSectorSize = scanResult.MenuConfig.IsDetected
            ? scanResult.MenuConfig.Status.SectorSize
            : WaterWlMappingCodec.SectorSize;
        if (scanResult.Geometry.SectorSizeBytes <= 0 || menuSectorSize != scanResult.Geometry.SectorSizeBytes)
        {
            errorMessage = LocalizedText.T("error", "magic_repair_sector_mismatch", "Current menu itemlist sector size does not match the detected NOR sector size.");
            return false;
        }

        int itemListAddress = scanResult.MenuConfig.IsDetected
            ? scanResult.MenuConfig.Status.ItemListAddress
            : WlItemListOffset;
        if (itemListAddress <= 0 || itemListAddress % scanResult.Geometry.SectorSizeBytes != 0)
        {
            errorMessage = LocalizedText.T("error", "legacy_itemlist_alignment_invalid", "Legacy itemlist address is not aligned to a valid NOR sector boundary.");
            return false;
        }

        if (legacyMappingNeedsRepair ||
            ((invalidWlMappingDetected || scanResult.WlMapping.RawSector.Length == 0) &&
             canRebuildMappingFromFixedMenu))
        {
            return true;
        }

        WaterMagicMetadataSyncInfo syncInfo = GetMagicMetadataSync(scanResult);
        if (!scanResult.MenuConfig.IsDetected && canRebuildItemlistFromMapping)
        {
            return true;
        }

        if (!syncInfo.IsApplicable)
        {
            errorMessage = LocalizedText.T("error", "magic_metadata_sync_not_applicable", "The current scan does not contain both itemlist and WL mapping data.");
            return false;
        }

        if (!syncInfo.NeedsRepair)
        {
            errorMessage = LocalizedText.T("error", "magic_metadata_already_clean", "Magic metadata is already synchronized.");
            return false;
        }

        return true;
    }

    public static bool CanInstallMagicMenuFromHeaders(WaterCartScanResult? scanResult, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (scanResult is null)
        {
            errorMessage = LocalizedText.T("error", "scan_cart_first", "Please scan the cart first.");
            return false;
        }

        if (scanResult.MenuConfig.IsDetected)
        {
            errorMessage = LocalizedText.T(
                "error",
                "menu_config_already_present",
                "A menu config is already present on this cart. Use upgrade or repair instead.");
            return false;
        }

        if (scanResult.Geometry.SectorSizeBytes != WaterWlMappingCodec.SectorSize)
        {
            errorMessage = LocalizedText.Tf(
                "error",
                "magic_sector_size_unsupported",
                "Current NOR sector size is 0x{0:X}; one-click migration is not supported.",
                scanResult.Geometry.SectorSizeBytes);
            return false;
        }

        return true;
    }

    public static byte[] LoadMagicMenuRom(out string sourcePath)
    {
        foreach (string candidate in EnumerateMenuRomCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            sourcePath = candidate;
            return File.ReadAllBytes(candidate);
        }

        throw new FileNotFoundException(
            LocalizedText.T(
                "error",
                "magic_menu_not_found",
                "Magic menu ROM resource was not found. Make sure Assets\\wl_magic_menu.gba exists."));
    }

    public static byte[] BuildMenuAreaImage(byte[] menuRomBytes, WaterMenuConfigInfo menuConfig, NorGeometryInfo geometry)
    {
        if (menuRomBytes is null || menuRomBytes.Length <= 0)
        {
            throw new InvalidOperationException(LocalizedText.T("error", "magic_menu_empty", "Magic menu ROM resource is empty."));
        }

        int itemListAddress = menuConfig.Status.ItemListAddress;
        if (itemListAddress <= 0)
        {
            throw new InvalidOperationException(LocalizedText.T("error", "legacy_itemlist_invalid", "Legacy itemlist address is invalid."));
        }

        if (geometry.SectorSizeBytes <= 0 || itemListAddress % geometry.SectorSizeBytes != 0)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "legacy_itemlist_alignment_invalid", "Legacy itemlist address is not aligned to a valid NOR sector boundary."));
        }

        if (menuRomBytes.Length > itemListAddress)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "magic_menu_too_large",
                    "Magic menu ROM is too large and exceeds itemlist start 0x{0:X6}.",
                    itemListAddress));
        }

        byte[] menuArea = Enumerable.Repeat((byte)0xFF, itemListAddress).ToArray();
        Buffer.BlockCopy(menuRomBytes, 0, menuArea, 0, menuRomBytes.Length);
        return menuArea;
    }

    public static WaterMenuConfigInfo BuildMigratedMenuConfig(WaterCartScanResult scanResult, out List<WaterCartEntry> droppedLegacyEntries, out List<WaterCartEntry> addedHeaderOnlyEntries)
    {
        if (!CanUpgradeToMagic(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        droppedLegacyEntries = scanResult.Entries
            .Where(entry => !entry.IsMenu && entry.FoundInMenuConfig && !entry.FoundByHeaderScan)
            .OrderBy(entry => entry.MenuItem?.Index ?? int.MaxValue)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        addedHeaderOnlyEntries = scanResult.Entries
            .Where(entry => !entry.IsMenu && entry.FoundByHeaderScan && !entry.FoundInMenuConfig)
            .OrderBy(entry => entry.StartBucket)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<MenuItemConfigInfo> migratedItems = [];
        IEnumerable<WaterCartEntry> sourceEntries = scanResult.Entries
            .Where(entry => !entry.IsMenu && entry.FoundByHeaderScan)
            .OrderBy(entry => entry.MenuItem?.Index ?? int.MaxValue)
            .ThenBy(entry => entry.StartBucket)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase);

        foreach (WaterCartEntry entry in sourceEntries)
        {
            MenuItemConfigInfo item = entry.MenuItem is not null
                ? WaterMenuConfigCodec.Clone(entry.MenuItem)
                : new MenuItemConfigInfo
                {
                    Font = 0,
                    SaveType = 0,
                    SaveIndex = 0,
                    HiddenKeysMask = 0,
                    Title = entry.Title,
                };

            int startBucket = entry.MenuItem?.StartBucket ?? entry.HeaderGame?.StartBucket ?? entry.StartBucket;
            int coveredBucketCount = entry.MenuItem?.CoveredBucketCount
                ?? (entry.HeaderGame is not null
                    ? Math.Max(1, entry.HeaderGame.EndBucket - entry.HeaderGame.StartBucket + 1)
                    : Math.Max(1, entry.LogicalBucketCount));

            item.RomOffsetBlocks = checked((ushort)(startBucket * 8));
            item.RomSizeBlocks = checked((ushort)(coveredBucketCount * 8));
            item.Title = WaterMenuConfigCodec.NormalizeTitle(string.IsNullOrWhiteSpace(item.Title) ? entry.Title : item.Title);
            item.TitleLength = (byte)Math.Min(item.Title.Length, WaterMenuConfigCodec.TitleCharCapacity);
            migratedItems.Add(item);
        }

        if (migratedItems.Count <= 0)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "magic_no_header_backed_entries", "No header-confirmed ROM entries are available for Magic migration."));
        }

        return new WaterMenuConfigInfo
        {
            IsDetected = true,
            Status = NormalizeWlStatus(scanResult.MenuConfig.Status),
            Items = WaterMenuConfigCodec.ReindexItems(migratedItems, WlItemListOffset),
        };
    }

    public static WaterMagicUpgradePlan BuildUpgradePlan(byte[] menuRomBytes, WaterCartScanResult scanResult)
    {
        WaterMenuConfigInfo migratedMenuConfig = BuildMigratedMenuConfig(scanResult, out List<WaterCartEntry> droppedLegacyEntries, out List<WaterCartEntry> addedHeaderOnlyEntries);
        byte[] menuAreaImage = BuildMenuAreaImage(menuRomBytes, migratedMenuConfig, scanResult.Geometry);
        byte[] itemListSector = WaterMenuConfigCodec.BuildItemListSector(
            migratedMenuConfig.Items,
            migratedMenuConfig.Status.ItemListAddress,
            migratedMenuConfig.Status.SectorSize);
        byte[] statusSector = WaterMenuConfigCodec.BuildStatusSector(migratedMenuConfig.Status);
        byte[] mappingSector = BuildMappingSector(migratedMenuConfig);

        return new WaterMagicUpgradePlan
        {
            MigratedMenuConfig = migratedMenuConfig,
            MenuAreaImage = menuAreaImage,
            ItemListSector = itemListSector,
            StatusSector = statusSector,
            MappingSector = mappingSector,
            DroppedLegacyEntries = droppedLegacyEntries,
            AddedHeaderOnlyEntries = addedHeaderOnlyEntries,
        };
    }

    public static WaterMagicInstallPlan BuildInstallPlan(byte[] menuRomBytes, WaterCartScanResult scanResult)
    {
        if (!CanInstallMagicMenuFromHeaders(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        WaterMenuConfigInfo installedMenuConfig = BuildInstalledMenuConfig(
            scanResult,
            out WaterCartEntry? droppedHeadGame,
            out List<WaterCartEntry> installedEntries);

        byte[] itemListSector = WaterMenuConfigCodec.BuildItemListSector(
            installedMenuConfig.Items,
            installedMenuConfig.Status.ItemListAddress,
            installedMenuConfig.Status.SectorSize);
        byte[] statusSector = BuildStatusSector(installedMenuConfig.Status);
        byte[] mappingSector = BuildMappingSector(CreateMappingRecords(installedMenuConfig));
        byte[] headAreaImage = BuildHeadAreaImage(
            menuRomBytes,
            installedMenuConfig,
            itemListSector,
            statusSector,
            mappingSector);

        return new WaterMagicInstallPlan
        {
            InstalledMenuConfig = installedMenuConfig,
            HeadAreaImage = headAreaImage,
            ItemListSector = itemListSector,
            StatusSector = statusSector,
            MappingSector = mappingSector,
            InstalledEntries = installedEntries,
            DroppedHeadGame = droppedHeadGame,
        };
    }

    public static WaterMagicRepairPlan BuildRepairPlan(WaterCartScanResult scanResult)
    {
        if (!CanRepairMagicMetadata(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        if (!scanResult.WlMapping.IsDetected && scanResult.MenuConfig.IsDetected)
        {
            List<MenuItemConfigInfo> clonedItems = scanResult.MenuConfig.Items
                .OrderBy(item => item.Index)
                .Select(WaterMenuConfigCodec.Clone)
                .ToList();

            WaterMenuConfigInfo legacyRepairedMenuConfig = new()
            {
                IsDetected = true,
                Status = NormalizeWlStatus(scanResult.MenuConfig.Status),
            };
            legacyRepairedMenuConfig.Items = WaterMenuConfigCodec.ReindexItems(
                clonedItems,
                legacyRepairedMenuConfig.Status.ItemListAddress);

            byte[] legacyItemListSector = WaterMenuConfigCodec.BuildItemListSector(
                legacyRepairedMenuConfig.Items,
                legacyRepairedMenuConfig.Status.ItemListAddress,
                legacyRepairedMenuConfig.Status.SectorSize);
            byte[] legacyStatusSector = WaterMenuConfigCodec.BuildStatusSector(legacyRepairedMenuConfig.Status);

            List<WlRomMapRecordInfo> legacyRepairedRecords = CreateMappingRecords(legacyRepairedMenuConfig);
            byte[] legacyMappingSector = BuildMappingSector(legacyRepairedRecords);

            return new WaterMagicRepairPlan
            {
                SyncInfo = new WaterMagicMetadataSyncInfo
                {
                    IsApplicable = true,
                    MenuItemCount = legacyRepairedMenuConfig.ItemCount,
                    MappingRecordCount = 0,
                },
                RepairedMenuConfig = legacyRepairedMenuConfig,
                RepairedRecords = legacyRepairedRecords,
                ItemListSector = legacyItemListSector,
                StatusSector = legacyStatusSector,
                MappingSector = legacyMappingSector,
                RemovedMenuOnlyItems = [],
            };
        }

        WaterMagicMetadataSyncInfo syncInfo = GetMagicMetadataSync(scanResult);
        if (!scanResult.MenuConfig.IsDetected && scanResult.WlMapping.IsDetected)
        {
            return BuildRepairPlanFromMappingOnly(scanResult);
        }

        List<MenuItemConfigInfo> originalItems = scanResult.MenuConfig.Items
            .OrderBy(item => item.Index)
            .Select(WaterMenuConfigCodec.Clone)
            .ToList();

        Dictionary<int, MenuItemConfigInfo> menuByIndex = originalItems
            .GroupBy(item => item.Index)
            .ToDictionary(group => group.Key, group => group.First());

        Dictionary<int, DetectedNorGameInfo> headerByStartBucket = scanResult.Games
            .Where(game => !game.IsMenu)
            .GroupBy(game => game.StartBucket)
            .ToDictionary(group => group.Key, group => group.First());

        HashSet<int> consumedMenuIndexes = [];
        List<MenuItemConfigInfo> repairedItems = [];
        List<WlRomMapRecordInfo> repairedRecords = [];

        foreach (WlRomMapRecordInfo record in scanResult.WlMapping.Records.OrderBy(record => record.EntrySlot))
        {
            MenuItemConfigInfo? matchedMenuItem = FindBestMatchingMenuItem(originalItems, consumedMenuIndexes, record);
            if (matchedMenuItem is not null)
            {
                consumedMenuIndexes.Add(matchedMenuItem.Index);
            }

            menuByIndex.TryGetValue(record.EntrySlot, out MenuItemConfigInfo? fallbackSlotItem);
            if (matchedMenuItem is null && fallbackSlotItem is not null)
            {
                consumedMenuIndexes.Add(fallbackSlotItem.Index);
            }

            DetectedNorGameInfo? headerGame = null;
            if (record.PhysicalBuckets.Count > 0)
            {
                headerByStartBucket.TryGetValue(record.PhysicalBuckets[0], out headerGame);
            }

            repairedItems.Add(CreateRepairedMenuItem(matchedMenuItem, fallbackSlotItem, headerGame, record));
            repairedRecords.Add(new WlRomMapRecordInfo
            {
                EntrySlot = repairedRecords.Count,
                LogicalBucketCount = record.LogicalBucketCount,
                PhysicalBuckets = record.PhysicalBuckets.ToList(),
            });
        }

        WaterMenuConfigInfo repairedMenuConfig = new()
        {
            IsDetected = true,
            Status = NormalizeWlStatus(scanResult.MenuConfig.Status),
        };
        repairedMenuConfig.Items = WaterMenuConfigCodec.ReindexItems(
            repairedItems,
            repairedMenuConfig.Status.ItemListAddress);

        byte[] itemListSector = WaterMenuConfigCodec.BuildItemListSector(
            repairedMenuConfig.Items,
            repairedMenuConfig.Status.ItemListAddress,
            repairedMenuConfig.Status.SectorSize);
        byte[] statusSector = WaterMenuConfigCodec.BuildStatusSector(repairedMenuConfig.Status);

        byte[] mappingSector = BuildMappingSector(repairedRecords);

        List<MenuItemConfigInfo> removedMenuOnlyItems = scanResult.MenuConfig.Items
            .Where(item => syncInfo.MenuOnlySlots.Contains(item.Index))
            .OrderBy(item => item.Index)
            .Select(WaterMenuConfigCodec.Clone)
            .ToList();

        return new WaterMagicRepairPlan
        {
            SyncInfo = syncInfo,
            RepairedMenuConfig = repairedMenuConfig,
            RepairedRecords = repairedRecords,
            ItemListSector = itemListSector,
            StatusSector = statusSector,
            MappingSector = mappingSector,
            RemovedMenuOnlyItems = removedMenuOnlyItems,
        };
    }

    private static WaterMagicRepairPlan BuildRepairPlanFromMappingOnly(WaterCartScanResult scanResult)
    {
        MenuFlashStatusInfo status = CreateDefaultStatusInfo();
        Dictionary<int, DetectedNorGameInfo> headerByStartBucket = scanResult.Games
            .Where(game => !game.IsMenu)
            .GroupBy(game => game.StartBucket)
            .ToDictionary(group => group.Key, group => group.First());

        List<MenuItemConfigInfo> repairedItems = [];
        List<WlRomMapRecordInfo> repairedRecords = [];

        foreach (WlRomMapRecordInfo record in scanResult.WlMapping.Records.OrderBy(record => record.EntrySlot))
        {
            DetectedNorGameInfo? headerGame = null;
            if (record.PhysicalBuckets.Count > 0)
            {
                headerByStartBucket.TryGetValue(record.PhysicalBuckets[0], out headerGame);
            }

            repairedItems.Add(CreateRepairedMenuItem(null, null, headerGame, record));
            repairedRecords.Add(new WlRomMapRecordInfo
            {
                EntrySlot = repairedRecords.Count,
                LogicalBucketCount = record.LogicalBucketCount,
                PhysicalBuckets = record.PhysicalBuckets.ToList(),
            });
        }

        WaterMenuConfigInfo repairedMenuConfig = new()
        {
            IsDetected = true,
            Status = NormalizeWlStatus(status),
            Items = WaterMenuConfigCodec.ReindexItems(repairedItems, status.ItemListAddress),
        };

        byte[] itemListSector = WaterMenuConfigCodec.BuildItemListSector(
            repairedMenuConfig.Items,
            repairedMenuConfig.Status.ItemListAddress,
            repairedMenuConfig.Status.SectorSize);
        byte[] statusSector = WaterMenuConfigCodec.BuildStatusSector(repairedMenuConfig.Status);
        byte[] mappingSector = BuildMappingSector(repairedRecords);

        return new WaterMagicRepairPlan
        {
            SyncInfo = new WaterMagicMetadataSyncInfo
            {
                IsApplicable = true,
                MenuItemCount = 0,
                MappingRecordCount = scanResult.WlMapping.RecordCount,
                MappingOnlySlots = scanResult.WlMapping.Records
                    .Select(record => record.EntrySlot)
                    .OrderBy(slot => slot)
                    .ToList(),
            },
            RepairedMenuConfig = repairedMenuConfig,
            RepairedRecords = repairedRecords,
            ItemListSector = itemListSector,
            StatusSector = statusSector,
            MappingSector = mappingSector,
            RemovedMenuOnlyItems = [],
        };
    }

    public static byte[] BuildMappingSector(WaterMenuConfigInfo menuConfig)
    {
        if (!menuConfig.IsDetected || menuConfig.ItemCount <= 0)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "legacy_menu_empty", "Legacy menu config is empty; cannot build the WL mapping sector."));
        }

        List<WlRomMapRecordInfo> records = menuConfig.Items
            .OrderBy(item => item.Index)
            .Select(item => new WlRomMapRecordInfo
            {
                EntrySlot = item.Index,
                LogicalBucketCount = item.CoveredBucketCount,
                PhysicalBuckets = Enumerable.Range(item.StartBucket, item.CoveredBucketCount).ToList(),
            })
            .ToList();

        return BuildMappingSector(records);
    }

    public static byte[] BuildMappingSector(IReadOnlyList<WlRomMapRecordInfo> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        byte[] mappingSector = Enumerable.Repeat((byte)0xFF, WaterWlMappingCodec.SectorSize).ToArray();
        ushort[] bucketOwners = Enumerable.Repeat(WaterWlMappingCodec.BucketOwnerFree, WaterWlMappingCodec.BucketCount).ToArray();
        bucketOwners[0] = WaterWlMappingCodec.BucketOwnerSystem;

        int bucketOwnerOffset = WaterWlMappingCodec.HeaderSize;
        int romMapRecordOffset = bucketOwnerOffset + (WaterWlMappingCodec.BucketCount * BucketOwnerEntrySize);
        byte[] romMapRecords = Enumerable.Repeat((byte)0xFF, WaterWlMappingCodec.RomMapRecordCount * WaterWlMappingCodec.RomMapRecordSize).ToArray();

        foreach (WlRomMapRecordInfo record in records.OrderBy(item => item.EntrySlot))
        {
            if (record.EntrySlot < 0 || record.EntrySlot >= WaterWlMappingCodec.RomMapRecordCount)
            {
                throw new InvalidOperationException(
                    LocalizedText.Tf("error", "menu_item_out_of_range", "Menu item #{0} is out of WL record range.", record.EntrySlot + 1));
            }

            if (record.LogicalBucketCount <= 0 || record.LogicalBucketCount > WaterWlMappingCodec.MaxLogicalBucketCount)
            {
                throw new InvalidOperationException(
                    LocalizedText.Tf("error", "menu_item_bucket_count_invalid", "Menu item #{0} has an invalid bucket count: {1}.", record.EntrySlot + 1, record.LogicalBucketCount));
            }

            List<int> physicalBuckets = record.PhysicalBuckets
                .Distinct()
                .ToList();

            if (physicalBuckets.Count != record.LogicalBucketCount)
            {
                throw new InvalidOperationException(
                    LocalizedText.Tf("error", "menu_item_bucket_count_invalid", "Menu item #{0} has an invalid bucket count: {1}.", record.EntrySlot + 1, physicalBuckets.Count));
            }

            int recordOffset = record.EntrySlot * WaterWlMappingCodec.RomMapRecordSize;
            romMapRecords[recordOffset] = (byte)record.LogicalBucketCount;

            for (int logicalIndex = 0; logicalIndex < physicalBuckets.Count; logicalIndex++)
            {
                int physicalBucket = physicalBuckets[logicalIndex];
                if (physicalBucket <= 0 || physicalBucket > WaterWlMappingCodec.RomPhysicalBucketCount)
                {
                    throw new InvalidOperationException(
                        LocalizedText.Tf("error", "menu_item_start_bucket_invalid", "Menu item #{0} has an invalid start bucket: {1}.", record.EntrySlot + 1, physicalBucket));
                }

                if (bucketOwners[physicalBucket] != WaterWlMappingCodec.BucketOwnerFree)
                {
                    throw new InvalidOperationException(
                        LocalizedText.Tf(
                            "error",
                            "wl_bucket_owner_conflict",
                            "WL physical bucket {0:00} is already owned by entry #{1:000}.",
                            physicalBucket,
                            bucketOwners[physicalBucket] + 1));
                }

                romMapRecords[recordOffset + 1 + logicalIndex] = (byte)physicalBucket;
                bucketOwners[physicalBucket] = (ushort)record.EntrySlot;
            }
        }

        Span<byte> header = mappingSector.AsSpan(0, WaterWlMappingCodec.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x00..0x04], WaterWlMappingCodec.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x04..0x06], WaterWlMappingCodec.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x06..0x08], WaterWlMappingCodec.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x08..0x0C], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x0C..0x10], (uint)bucketOwnerOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x10..0x12], BucketOwnerEntrySize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x12..0x14], (ushort)WaterWlMappingCodec.BucketCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x14..0x18], (uint)romMapRecordOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x18..0x1A], (ushort)WaterWlMappingCodec.RomMapRecordSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1A..0x1C], (ushort)WaterWlMappingCodec.RomMapRecordCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x1C..0x20], 0);

        for (int i = 0; i < bucketOwners.Length; i++)
        {
            int ownerOffset = bucketOwnerOffset + (i * BucketOwnerEntrySize);
            BinaryPrimitives.WriteUInt16LittleEndian(mappingSector.AsSpan(ownerOffset, BucketOwnerEntrySize), bucketOwners[i]);
        }

        Buffer.BlockCopy(romMapRecords, 0, mappingSector, romMapRecordOffset, romMapRecords.Length);
        WaterWlMappingCodec.SealSector(mappingSector);
        return mappingSector;
    }

    private static WaterMenuConfigInfo BuildInstalledMenuConfig(
        WaterCartScanResult scanResult,
        out WaterCartEntry? droppedHeadGame,
        out List<WaterCartEntry> installedEntries)
    {
        if (!CanInstallMagicMenuFromHeaders(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        List<WaterCartEntry> headerEntries = scanResult.Games
            .Where(game => !game.IsMenu)
            .OrderBy(game => game.StartBucket)
            .ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .Select(game => new WaterCartEntry
            {
                HeaderGame = game,
            })
            .ToList();

        droppedHeadGame = headerEntries.FirstOrDefault(entry => entry.StartBucket == 0);
        installedEntries = headerEntries
            .Where(entry => entry.StartBucket > 0)
            .ToList();

        List<MenuItemConfigInfo> items = [];
        foreach (WaterCartEntry entry in installedEntries)
        {
            int startBucket = entry.StartBucket;
            int bucketCount = Math.Max(1, entry.EndBucket - entry.StartBucket + 1);

            if (startBucket <= 0 || entry.EndBucket > WaterWlMappingCodec.RomPhysicalBucketCount)
            {
                throw new InvalidOperationException(
                    LocalizedText.Tf(
                        "error",
                        "menu_item_bucket_range_invalid",
                        "Menu item #{0} exceeds the valid 4MiB bucket range: {1}.",
                        items.Count + 1,
                        entry.BucketRangeText));
            }

            string title = WaterMenuConfigCodec.NormalizeTitle(entry.Title);
            items.Add(new MenuItemConfigInfo
            {
                Font = 0,
                Title = title,
                TitleLength = (byte)Math.Min(title.Length, WaterMenuConfigCodec.TitleCharCapacity),
                RomOffsetBlocks = checked((ushort)(startBucket * 8)),
                RomSizeBlocks = checked((ushort)(bucketCount * 8)),
                SaveType = 0,
                SaveIndex = 0,
                HiddenKeysMask = 0,
            });
        }

        MenuFlashStatusInfo status = CreateDefaultStatusInfo();
        return new WaterMenuConfigInfo
        {
            IsDetected = true,
            Status = status,
            Items = WaterMenuConfigCodec.ReindexItems(items, status.ItemListAddress),
        };
    }

    private static MenuFlashStatusInfo CreateDefaultStatusInfo()
    {
        return new MenuFlashStatusInfo
        {
            StatusAddress = WlStatusOffset,
            SectorSize = WaterWlMappingCodec.SectorSize,
            Version = 0,
            BatteryPresent = false,
            LastBootMenuIndex = 0,
            LastBootSaveIndex = 0,
            LastBootSaveType = 0,
            SramBankType = 0,
        };
    }

    public static MenuFlashStatusInfo NormalizeWlStatus(MenuFlashStatusInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new MenuFlashStatusInfo
        {
            StatusAddress = WlStatusOffset,
            SectorSize = WaterWlMappingCodec.SectorSize,
            Version = source.Version,
            BatteryPresent = false,
            LastBootMenuIndex = source.LastBootMenuIndex,
            LastBootSaveIndex = 0xFF,
            LastBootSaveType = 0,
            SramBankType = source.SramBankType,
        };
    }

    private static byte[] BuildStatusSector(MenuFlashStatusInfo status)
    {
        if (status.SectorSize <= 0)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "legacy_itemlist_invalid", "Legacy itemlist address is invalid."));
        }

        byte[] sector = Enumerable.Repeat((byte)0xFF, status.SectorSize).ToArray();
        sector.AsSpan(0, 16).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0, 4), StatusMagic);
        sector[4] = status.Version;
        sector[5] = status.BatteryPresent ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(6, 2), status.LastBootMenuIndex);
        sector[8] = status.LastBootSaveIndex;
        sector[9] = status.LastBootSaveType;
        sector[10] = status.SramBankType;
        return sector;
    }

    private static List<WlRomMapRecordInfo> CreateMappingRecords(WaterMenuConfigInfo menuConfig)
    {
        return menuConfig.Items
            .OrderBy(item => item.Index)
            .Select(item => new WlRomMapRecordInfo
            {
                EntrySlot = item.Index,
                LogicalBucketCount = item.CoveredBucketCount,
                PhysicalBuckets = Enumerable.Range(item.StartBucket, item.CoveredBucketCount).ToList(),
            })
            .ToList();
    }

    public static byte[] BuildHeadAreaImage(
        byte[] menuRomBytes,
        WaterMenuConfigInfo menuConfig,
        byte[] itemListSector,
        byte[] statusSector,
        byte[] mappingSector)
    {
        if (menuRomBytes is null || menuRomBytes.Length <= 0)
        {
            throw new InvalidOperationException(LocalizedText.T("error", "magic_menu_empty", "Magic menu ROM resource is empty."));
        }

        if (menuRomBytes.Length > WlItemListOffset)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "magic_menu_too_large",
                    "Magic menu ROM is too large and exceeds itemlist start 0x{0:X6}.",
                    WlItemListOffset));
        }

        byte[] headArea = Enumerable.Repeat((byte)0xFF, WlHeadAreaSize).ToArray();
        Buffer.BlockCopy(menuRomBytes, 0, headArea, 0, menuRomBytes.Length);
        CopyHeadAreaSector(headArea, menuConfig.Status.ItemListAddress, itemListSector);
        CopyHeadAreaSector(headArea, menuConfig.Status.StatusAddress, statusSector);
        CopyHeadAreaSector(headArea, WaterWlMappingCodec.Offset, mappingSector);
        return headArea;
    }

    private static void CopyHeadAreaSector(byte[] headArea, int offset, byte[] sectorData)
    {
        ArgumentNullException.ThrowIfNull(headArea);
        ArgumentNullException.ThrowIfNull(sectorData);

        if (offset < 0 || offset + sectorData.Length > headArea.Length)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "magic_head_area_overflow",
                    "Magic metadata target range exceeds the reserved head area: {0}.",
                    $"0x{offset:X6}-0x{offset + sectorData.Length - 1:X6}"));
        }

        Buffer.BlockCopy(sectorData, 0, headArea, offset, sectorData.Length);
    }

    private static WaterMagicMetadataSyncInfo GetMagicMetadataSync(WaterCartScanResult scanResult)
    {
        if (scanResult.MagicMetadataSync.IsApplicable)
        {
            return scanResult.MagicMetadataSync;
        }

        return AnalyzeMagicMetadataSync(scanResult.MenuConfig, scanResult.WlMapping);
    }

    private static bool MenuItemMatchesRecord(MenuItemConfigInfo item, WlRomMapRecordInfo record)
    {
        if (item is null || record is null || record.PhysicalBuckets.Count <= 0)
        {
            return false;
        }

        return item.StartBucket == record.PhysicalBuckets[0] &&
               item.CoveredBucketCount == record.LogicalBucketCount;
    }

    private static MenuItemConfigInfo? FindBestMatchingMenuItem(
        IReadOnlyList<MenuItemConfigInfo> originalItems,
        ISet<int> consumedMenuIndexes,
        WlRomMapRecordInfo record)
    {
        ArgumentNullException.ThrowIfNull(originalItems);
        ArgumentNullException.ThrowIfNull(consumedMenuIndexes);
        ArgumentNullException.ThrowIfNull(record);

        MenuItemConfigInfo? exactSameSlot = originalItems.FirstOrDefault(item =>
            item.Index == record.EntrySlot &&
            !consumedMenuIndexes.Contains(item.Index) &&
            MenuItemMatchesRecord(item, record));

        if (exactSameSlot is not null)
        {
            return exactSameSlot;
        }

        MenuItemConfigInfo? exactAnySlot = originalItems
            .Where(item => !consumedMenuIndexes.Contains(item.Index) && MenuItemMatchesRecord(item, record))
            .OrderBy(item => Math.Abs(item.Index - record.EntrySlot))
            .ThenBy(item => item.Index)
            .FirstOrDefault();

        if (exactAnySlot is not null)
        {
            return exactAnySlot;
        }

        int startBucket = record.PhysicalBuckets.Count > 0
            ? record.PhysicalBuckets[0]
            : -1;

        if (startBucket < 0)
        {
            return null;
        }

        return originalItems
            .Where(item => !consumedMenuIndexes.Contains(item.Index) && item.StartBucket == startBucket)
            .OrderBy(item => Math.Abs(item.Index - record.EntrySlot))
            .ThenBy(item => item.Index)
            .FirstOrDefault();
    }

    private static MenuItemConfigInfo CreateRepairedMenuItem(
        MenuItemConfigInfo? matchedMenuItem,
        MenuItemConfigInfo? fallbackSlotItem,
        DetectedNorGameInfo? headerGame,
        WlRomMapRecordInfo record)
    {
        MenuItemConfigInfo seed = matchedMenuItem is not null
            ? WaterMenuConfigCodec.Clone(matchedMenuItem)
            : fallbackSlotItem is not null && headerGame is null
                ? WaterMenuConfigCodec.Clone(fallbackSlotItem)
                : new MenuItemConfigInfo
                {
                    Font = 0,
                    SaveType = 0,
                    SaveIndex = 0,
                    HiddenKeysMask = 0,
                };

        string title = !string.IsNullOrWhiteSpace(matchedMenuItem?.Title)
            ? matchedMenuItem.Title
            : !string.IsNullOrWhiteSpace(headerGame?.Title)
                ? headerGame.Title
                : !string.IsNullOrWhiteSpace(fallbackSlotItem?.Title)
                    ? fallbackSlotItem.Title
                    : LocalizedText.T("value", "untitled", "UNTITLED");

        seed.RomOffsetBlocks = checked((ushort)(record.PhysicalBuckets[0] * 8));
        seed.RomSizeBlocks = checked((ushort)(record.LogicalBucketCount * 8));
        seed.Title = WaterMenuConfigCodec.NormalizeTitle(title);
        seed.TitleLength = (byte)Math.Min(seed.Title.Length, WaterMenuConfigCodec.TitleCharCapacity);
        return seed;
    }

    private static IEnumerable<string> EnumerateMenuRomCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "wl_magic_menu.gba");
        yield return Path.Combine(AppContext.BaseDirectory, "wl_magic_menu.gba");
        yield return Path.Combine(AppContext.BaseDirectory, "lk_multimenu_wl.gba");

    }
}
