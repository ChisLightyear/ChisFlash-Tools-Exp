using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public sealed class WaterLegacyMenuInstallPlan
{
    public WaterMenuConfigInfo InstalledMenuConfig { get; init; } = new();
    public byte[] HeadAreaImage { get; init; } = [];
    public byte[] ItemListSector { get; init; } = [];
    public byte[] StatusSector { get; init; } = [];
    public List<WaterCartEntry> InstalledEntries { get; init; } = [];
    public WaterCartEntry? DroppedHeadGame { get; init; }
    public string LayoutSource { get; init; } = string.Empty;
}

public sealed class WaterLegacyMenuRepairPlan
{
    public WaterMenuConfigInfo RepairedMenuConfig { get; init; } = new();
    public byte[] ItemListSector { get; init; } = [];
    public byte[] StatusSector { get; init; } = [];
    public List<WaterCartEntry> RepairedEntries { get; init; } = [];
    public string LayoutSource { get; init; } = string.Empty;
}

public static class WaterLegacyMenuRepairBuilder
{
    private const int LayoutAlignmentBytes = 0x40000;
    private const int DefaultLegacyMenuLengthBytes = 672496;
    private const int LegacyHeadAreaSize = WaterCartScanner.BucketSizeBytes;
    private const byte LegacyBatterySaveType = 2; // SRAM_512K

    public static bool CanRepairLegacyMenu(WaterCartScanResult? scanResult, out string errorMessage)
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

        if (!scanResult.Games.Any(game => game.IsMenu && game.StartBucket == 0))
        {
            errorMessage = LocalizedText.T(
                "error",
                "legacy_menu_repair_requires_menu_header",
                "No legacy menu ROM header was detected in the head area.");
            return false;
        }

        if (!TryResolveLegacyMetadataLayout(scanResult.Geometry, out _, out _, out _, out errorMessage))
        {
            return false;
        }

        return true;
    }

    public static bool CanInstallLegacyMenuFromHeaders(WaterCartScanResult? scanResult, out string errorMessage)
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

        if (!TryResolveLegacyMetadataLayout(scanResult.Geometry, out _, out _, out _, out errorMessage))
        {
            return false;
        }

        return true;
    }

    public static bool CanRepairLegacyBatterySaveMetadata(WaterCartScanResult? scanResult, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (scanResult is null)
        {
            errorMessage = LocalizedText.T("error", "scan_cart_first", "Please scan the cart first.");
            return false;
        }

        if (!scanResult.MenuConfig.IsDetected)
        {
            errorMessage = LocalizedText.T(
                "error",
                "export_chip_save_requires_menu_config",
                "A valid legacy menu config is required before exporting the chip save.");
            return false;
        }

        if (!scanResult.MenuConfig.Status.BatteryPresent)
        {
            errorMessage = LocalizedText.T(
                "error",
                "legacy_battery_save_repair_requires_battery",
                "The current legacy menu is not in battery/FRAM mode.");
            return false;
        }

        if (!scanResult.MenuConfig.Items.Any(item => item.SaveType == 0))
        {
            errorMessage = LocalizedText.T(
                "error",
                "legacy_battery_save_repair_not_needed",
                "All current legacy menu entries already have save slots assigned.");
            return false;
        }

        return true;
    }

    public static byte[] LoadLegacyMenuRom(out string sourcePath)
    {
        foreach (string candidate in EnumerateLegacyMenuRomCandidates())
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
                "legacy_menu_not_found",
                "Legacy menu ROM resource was not found. Make sure Assets\\lk_multimenu.gba exists."));
    }

    public static WaterLegacyMenuRepairPlan BuildRepairPlan(WaterCartScanResult scanResult, bool batteryPresent = false)
    {
        if (!CanRepairLegacyMenu(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        if (!TryResolveLegacyMetadataLayout(scanResult.Geometry, out int itemListAddress, out int statusAddress, out string layoutSource, out string layoutError))
        {
            throw new InvalidOperationException(layoutError);
        }

        List<WaterCartEntry> repairedEntries = BuildLegacyHeaderEntries(scanResult, out _, includeHeadAreaGame: false);
        MenuFlashStatusInfo status = CreateLegacyStatusInfo(scanResult.Geometry.SectorSizeBytes, statusAddress, batteryPresent);

        WaterMenuConfigInfo repairedMenuConfig = new()
        {
            IsDetected = true,
            Status = status,
            Items = WaterMenuConfigCodec.ReindexItems(BuildLegacyMenuItems(repairedEntries), itemListAddress),
        };

        if (batteryPresent)
        {
            repairedMenuConfig.Items = BuildLegacyBatterySaveMetadataItems(repairedMenuConfig.Items);
        }

        return new WaterLegacyMenuRepairPlan
        {
            RepairedMenuConfig = repairedMenuConfig,
            ItemListSector = WaterMenuConfigCodec.BuildItemListSector(
                repairedMenuConfig.Items,
                itemListAddress,
                status.SectorSize),
            StatusSector = WaterMenuConfigCodec.BuildStatusSector(status),
            RepairedEntries = repairedEntries,
            LayoutSource = layoutSource,
        };
    }

    public static WaterLegacyMenuInstallPlan BuildInstallPlan(byte[] menuRomBytes, WaterCartScanResult scanResult, bool batteryPresent = false)
    {
        if (!CanInstallLegacyMenuFromHeaders(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        if (menuRomBytes is null || menuRomBytes.Length <= 0)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "legacy_menu_empty_rom", "Legacy menu ROM resource is empty."));
        }

        if (!TryResolveLegacyMetadataLayout(scanResult.Geometry, out int itemListAddress, out int statusAddress, out string layoutSource, out string layoutError))
        {
            throw new InvalidOperationException(layoutError);
        }

        List<WaterCartEntry> installedEntries = BuildLegacyHeaderEntries(scanResult, out WaterCartEntry? droppedHeadGame, includeHeadAreaGame: false);
        MenuFlashStatusInfo status = CreateLegacyStatusInfo(scanResult.Geometry.SectorSizeBytes, statusAddress, batteryPresent);

        WaterMenuConfigInfo installedMenuConfig = new()
        {
            IsDetected = true,
            Status = status,
            Items = WaterMenuConfigCodec.ReindexItems(BuildLegacyMenuItems(installedEntries), itemListAddress),
        };

        if (batteryPresent)
        {
            installedMenuConfig.Items = BuildLegacyBatterySaveMetadataItems(installedMenuConfig.Items);
        }

        byte[] itemListSector = WaterMenuConfigCodec.BuildItemListSector(
            installedMenuConfig.Items,
            installedMenuConfig.Status.ItemListAddress,
            installedMenuConfig.Status.SectorSize);
        byte[] statusSector = WaterMenuConfigCodec.BuildStatusSector(installedMenuConfig.Status);
        byte[] headAreaImage = BuildHeadAreaImage(menuRomBytes, installedMenuConfig, itemListSector, statusSector);

        return new WaterLegacyMenuInstallPlan
        {
            InstalledMenuConfig = installedMenuConfig,
            HeadAreaImage = headAreaImage,
            ItemListSector = itemListSector,
            StatusSector = statusSector,
            InstalledEntries = installedEntries,
            DroppedHeadGame = droppedHeadGame,
            LayoutSource = layoutSource,
        };
    }

    public static WaterLegacyMenuRepairPlan BuildBatterySaveMetadataRepairPlan(WaterCartScanResult scanResult)
    {
        if (!CanRepairLegacyBatterySaveMetadata(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        List<MenuItemConfigInfo> repairedItems = BuildLegacyBatterySaveMetadataItems(scanResult.MenuConfig.Items);
        MenuFlashStatusInfo status = CloneLegacyStatus(scanResult.MenuConfig.Status);

        if (status.LastBootMenuIndex != 0xFFFF &&
            status.LastBootMenuIndex < repairedItems.Count &&
            repairedItems[status.LastBootMenuIndex].SaveType != 0)
        {
            status.LastBootSaveType = repairedItems[status.LastBootMenuIndex].SaveType;
            status.LastBootSaveIndex = repairedItems[status.LastBootMenuIndex].SaveIndex;
        }
        else
        {
            status.LastBootSaveType = 0;
            status.LastBootSaveIndex = 0xFF;
        }

        WaterMenuConfigInfo repairedMenuConfig = new()
        {
            IsDetected = true,
            Status = status,
            Items = WaterMenuConfigCodec.ReindexItems(repairedItems, status.ItemListAddress),
        };

        return new WaterLegacyMenuRepairPlan
        {
            RepairedMenuConfig = repairedMenuConfig,
            ItemListSector = WaterMenuConfigCodec.BuildItemListSector(
                repairedMenuConfig.Items,
                repairedMenuConfig.Status.ItemListAddress,
                repairedMenuConfig.Status.SectorSize),
            StatusSector = WaterMenuConfigCodec.BuildStatusSector(repairedMenuConfig.Status),
            RepairedEntries = scanResult.Entries.Where(entry => !entry.IsMenu).OrderBy(entry => entry.SortKey).ToList(),
            LayoutSource = "current-menu-config-save-slots",
        };
    }

    private static bool TryResolveLegacyMetadataLayout(
        NorGeometryInfo geometry,
        out int itemListAddress,
        out int statusAddress,
        out string layoutSource,
        out string errorMessage)
    {
        itemListAddress = 0;
        statusAddress = 0;
        layoutSource = string.Empty;
        errorMessage = string.Empty;

        int sectorSize = geometry.SectorSizeBytes;
        if (sectorSize <= 0 || sectorSize > WaterCartScanner.BucketSizeBytes)
        {
            errorMessage = LocalizedText.Tf(
                "error",
                "legacy_menu_repair_sector_invalid",
                "Current NOR sector size 0x{0:X} is invalid for legacy menu repair.",
                sectorSize);
            return false;
        }

        int menuLengthBytes = ResolveLegacyMenuLength(out layoutSource);
        int alignedMenuBytes = AlignPastBoundary(menuLengthBytes, LayoutAlignmentBytes);
        itemListAddress = DivCeil(alignedMenuBytes, sectorSize) * sectorSize;
        statusAddress = itemListAddress + sectorSize;

        if (itemListAddress <= 0 || statusAddress <= itemListAddress)
        {
            errorMessage = LocalizedText.T(
                "error",
                "legacy_menu_repair_layout_invalid",
                "Failed to resolve a valid legacy itemlist/status layout.");
            return false;
        }

        if (statusAddress + sectorSize > WaterCartScanner.BucketSizeBytes)
        {
            errorMessage = LocalizedText.Tf(
                "error",
                "legacy_menu_repair_layout_overflow",
                "The repaired legacy metadata layout would exceed the reserved head 4 MiB area: itemlist=0x{0:X6}, status=0x{1:X6}.",
                itemListAddress,
                statusAddress);
            return false;
        }

        return true;
    }

    private static int ResolveLegacyMenuLength(out string layoutSource)
    {
        foreach (string candidate in EnumerateLegacyMenuRomCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            layoutSource = candidate;
            return checked((int)new FileInfo(candidate).Length);
        }

        layoutSource = $"fallback:{DefaultLegacyMenuLengthBytes}";
        return DefaultLegacyMenuLengthBytes;
    }

    private static List<WaterCartEntry> BuildLegacyHeaderEntries(
        WaterCartScanResult scanResult,
        out WaterCartEntry? droppedHeadGame,
        bool includeHeadAreaGame)
    {
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
        if (includeHeadAreaGame)
        {
            return headerEntries;
        }

        return headerEntries
            .Where(entry => entry.StartBucket > 0)
            .ToList();
    }

    private static List<MenuItemConfigInfo> BuildLegacyMenuItems(IEnumerable<WaterCartEntry> entries)
    {
        return entries
            .Select(entry =>
            {
                int bucketCount = Math.Max(1, entry.EndBucket - entry.StartBucket + 1);
                string title = WaterMenuConfigCodec.NormalizeTitle(entry.Title);

                return new MenuItemConfigInfo
                {
                    Font = 0,
                    Title = title,
                    TitleLength = (byte)Math.Min(title.Length, WaterMenuConfigCodec.TitleCharCapacity),
                    RomOffsetBlocks = checked((ushort)(entry.StartBucket * 8)),
                    RomSizeBlocks = checked((ushort)(bucketCount * 8)),
                    SaveType = 0,
                    SaveIndex = 0,
                    HiddenKeysMask = 0,
                };
            })
            .ToList();
    }

    private static List<MenuItemConfigInfo> BuildLegacyBatterySaveMetadataItems(IReadOnlyList<MenuItemConfigInfo> items)
    {
        List<MenuItemConfigInfo> repairedItems = items
            .Select(WaterMenuConfigCodec.Clone)
            .ToList();

        foreach (MenuItemConfigInfo item in repairedItems)
        {
            if (item.SaveType != 0)
            {
                continue;
            }

            if (item.Index < 0 || item.Index > byte.MaxValue)
            {
                throw new InvalidOperationException(
                    LocalizedText.Tf(
                        "error",
                        "legacy_mode_save_slot_out_of_range",
                        "Legacy save slot index is out of range for {0}.",
                        item.Title));
            }

            item.SaveType = LegacyBatterySaveType;
            item.SaveIndex = checked((byte)item.Index);
        }

        return repairedItems;
    }

    private static MenuFlashStatusInfo CreateLegacyStatusInfo(int sectorSize, int statusAddress, bool batteryPresent)
    {
        return new MenuFlashStatusInfo
        {
            StatusAddress = statusAddress,
            SectorSize = sectorSize,
            Version = 0,
            BatteryPresent = batteryPresent,
            LastBootMenuIndex = 0xFFFF,
            LastBootSaveIndex = 0xFF,
            LastBootSaveType = 0,
            SramBankType = 0,
        };
    }

    private static MenuFlashStatusInfo CloneLegacyStatus(MenuFlashStatusInfo source)
    {
        return new MenuFlashStatusInfo
        {
            StatusAddress = source.StatusAddress,
            SectorSize = source.SectorSize,
            Version = source.Version,
            BatteryPresent = source.BatteryPresent,
            LastBootMenuIndex = source.LastBootMenuIndex,
            LastBootSaveIndex = source.LastBootSaveIndex,
            LastBootSaveType = source.LastBootSaveType,
            SramBankType = source.SramBankType,
        };
    }

    public static byte[] BuildHeadAreaImage(
        byte[] menuRomBytes,
        WaterMenuConfigInfo menuConfig,
        byte[] itemListSector,
        byte[] statusSector)
    {
        if (menuRomBytes.Length > menuConfig.Status.ItemListAddress)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "legacy_menu_too_large",
                    "Legacy menu ROM is too large and exceeds itemlist start 0x{0:X6}.",
                    menuConfig.Status.ItemListAddress));
        }

        byte[] headArea = Enumerable.Repeat((byte)0xFF, LegacyHeadAreaSize).ToArray();
        Buffer.BlockCopy(menuRomBytes, 0, headArea, 0, menuRomBytes.Length);
        CopyHeadAreaSector(headArea, menuConfig.Status.ItemListAddress, itemListSector);
        CopyHeadAreaSector(headArea, menuConfig.Status.StatusAddress, statusSector);
        return headArea;
    }

    private static void CopyHeadAreaSector(byte[] headArea, int offset, byte[] sectorData)
    {
        if (offset < 0 || offset + sectorData.Length > headArea.Length)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "legacy_head_area_overflow",
                    "Legacy metadata target range exceeds the reserved head area: {0}.",
                    $"0x{offset:X6}-0x{offset + sectorData.Length - 1:X6}"));
        }

        Buffer.BlockCopy(sectorData, 0, headArea, offset, sectorData.Length);
    }

    private static IEnumerable<string> EnumerateLegacyMenuRomCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "lk_multimenu.gba");
        yield return Path.Combine(AppContext.BaseDirectory, "lk_multimenu.gba");
        yield return Path.Combine(AppContext.BaseDirectory, "lk_multimenu_for_chisflash_01_02G.gba");

    }

    private static int AlignPastBoundary(int value, int alignment)
    {
        int remainder = value % alignment;
        int padding = remainder == 0
            ? alignment
            : alignment - remainder;
        return checked(value + padding);
    }

    private static int DivCeil(int value, int divisor) => (value / divisor) + ((value % divisor) != 0 ? 1 : 0);
}
