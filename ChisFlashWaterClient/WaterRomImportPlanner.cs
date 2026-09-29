using System.Text;
using GbaMultiMenuBuilder.Services;
using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public sealed class WaterNorWriteSegment
{
    public uint StartAddress { get; init; }
    public byte[] Data { get; init; } = [];
    public string Label { get; init; } = string.Empty;
    public bool EraseOnly { get; init; }
}

public sealed class WaterRomImportPlan
{
    public string FilePath { get; init; } = string.Empty;
    public string DisplayTitle { get; init; } = string.Empty;
    public string HeaderTitle { get; init; } = string.Empty;
    public bool UsesWlMode { get; init; }
    public int InsertIndex { get; init; }
    public int FileSizeBytes { get; init; }
    public int AllocatedSizeBytes { get; init; }
    public int LogicalBucketCount { get; init; }
    public List<int> PhysicalBuckets { get; init; } = [];
    public MenuItemConfigInfo MenuItem { get; init; } = new();
    public WaterMenuConfigInfo UpdatedMenuConfig { get; init; } = new();
    public byte[] UpdatedItemListSector { get; init; } = [];
    public WaterWlMappingInfo? UpdatedWlMapping { get; init; }
    public byte[]? UpdatedWlMappingSector { get; init; }
    public List<WaterNorWriteSegment> RomSegments { get; init; } = [];
    public List<WaterNorWriteSegment> SaveSegments { get; init; } = [];
}

public static class WaterRomImportPlanner
{
    public const int BlockSizeBytes = 0x80000;
    private const int MinimumRomSizeBytes = WaterCartScanner.BucketSizeBytes;
    private const int LegacyMaximumRomSizeBytes = 0x2000000;

    private static readonly byte[] NintendoLogoGba =
    [
        0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21, 0x3D, 0x84, 0x82, 0x0A, 0x84, 0xE4, 0x09, 0xAD,
        0x11, 0x24, 0x8B, 0x98, 0xC0, 0x81, 0x7F, 0x21, 0xA3, 0x52, 0xBE, 0x19, 0x93, 0x09, 0xCE, 0x20,
        0x10, 0x46, 0x4A, 0x4A, 0xF8, 0x27, 0x31, 0xEC, 0x58, 0xC7, 0xE8, 0x33, 0x82, 0xE3, 0xCE, 0xBF,
        0x85, 0xF4, 0xDF, 0x94, 0xCE, 0x4B, 0x09, 0xC1, 0x94, 0x56, 0x8A, 0xC0, 0x13, 0x72, 0xA7, 0xFC,
        0x9F, 0x84, 0x4D, 0x73, 0xA3, 0xCA, 0x9A, 0x61, 0x58, 0x97, 0xA3, 0x27, 0xFC, 0x03, 0x98, 0x76,
        0x23, 0x1D, 0xC7, 0x61, 0x03, 0x04, 0xAE, 0x56, 0xBF, 0x38, 0x84, 0x00, 0x40, 0xA7, 0x0E, 0xFD,
        0xFF, 0x52, 0xFE, 0x03, 0x6F, 0x95, 0x30, 0xF1, 0x97, 0xFB, 0xC0, 0x85, 0x60, 0xD6, 0x80, 0x25,
        0xA9, 0x63, 0xBE, 0x03, 0x01, 0x4E, 0x38, 0xE2, 0xF9, 0xA2, 0x34, 0xFF, 0xBB, 0x3E, 0x03, 0x44,
        0x78, 0x00, 0x90, 0xCB, 0x88, 0x11, 0x3A, 0x94, 0x65, 0xC0, 0x7C, 0x63, 0x87, 0xF0, 0x3C, 0xAF,
        0xD6, 0x25, 0xE4, 0x8B, 0x38, 0x0A, 0xAC, 0x72, 0x21, 0xD4, 0xF8, 0x07
    ];

    public static WaterRomImportPlan Plan(
        WaterCartScanResult scanResult,
        string filePath,
        byte[] romBytes,
        string? displayTitleOverride = null,
        int? plannedRomLengthBytes = null,
        byte? legacyMenuSaveTypeOverride = null,
        WaterCartEntry? replacementTargetEntry = null)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(romBytes);

        if (romBytes.Length <= 0)
        {
            throw new InvalidOperationException(LocalizedText.T("error", "rom_file_empty", "The ROM file is empty."));
        }

        string displayTitle = WaterMenuConfigCodec.NormalizeTitle(displayTitleOverride ?? string.Empty);
        if (string.IsNullOrWhiteSpace(displayTitle))
        {
            displayTitle = WaterMenuConfigCodec.NormalizeTitle(Path.GetFileNameWithoutExtension(filePath));
        }

        if (string.IsNullOrWhiteSpace(displayTitle))
        {
            displayTitle = WaterMenuConfigCodec.NormalizeTitle(WaterCartScanner.DecodeGbaHeaderTitle(romBytes));
        }

        string headerTitle = TryGetHeaderTitle(romBytes);

        if (replacementTargetEntry is not null)
        {
            var replacement = scanResult.WlMapping.IsDetected
                ? PlanWlReplacementImport(scanResult, filePath, romBytes, displayTitle, headerTitle, plannedRomLengthBytes, replacementTargetEntry)
                : PlanLegacyReplacementImport(scanResult, filePath, romBytes, displayTitle, headerTitle, plannedRomLengthBytes, legacyMenuSaveTypeOverride, replacementTargetEntry);
            return ApplyChm1SaveSlot(scanResult, replacement, legacyMenuSaveTypeOverride, replacementTargetEntry);
        }

        if (scanResult.WlMapping.IsDetected)
        {
            return ApplyChm1SaveSlot(scanResult, PlanWlImport(scanResult, filePath, romBytes, displayTitle, headerTitle, plannedRomLengthBytes), legacyMenuSaveTypeOverride, null);
        }

        return ApplyChm1SaveSlot(scanResult, PlanLegacyImport(scanResult, filePath, romBytes, displayTitle, headerTitle, plannedRomLengthBytes, legacyMenuSaveTypeOverride), legacyMenuSaveTypeOverride, null);
    }

    private static WaterRomImportPlan ApplyChm1SaveSlot(WaterCartScanResult scan, WaterRomImportPlan plan, byte? saveType, WaterCartEntry? replaced)
    {
        if (!scan.IsChm1 && !scan.UsesChm1Layout) return plan;
        if (plan.LogicalBucketCount > Chm1Layout.MaxGameBuckets)
            throw new InvalidOperationException("01G/02G fragment management games cannot exceed 32 MiB.");
        byte type = !scan.MenuConfig.Status.BatteryPresent ? (byte)0
            : plan.UsesWlMode ? saveType ?? replaced?.SaveType ?? 2 : plan.MenuItem.SaveType;
        bool needsSave = type != 0 && (replaced is null || replaced.SaveType is null or 0);
        byte index = plan.MenuItem.SaveIndex;
        if (plan.UsesWlMode || (needsSave && replaced is not null))
        {
            int saveLimit = plan.UsesWlMode ? Chm1Layout.MappingOffset : Chm1Layout.BucketBytes;
            int slotCount = (saveLimit - scan.MenuConfig.Status.SaveAreaAddress) / Chm1Layout.SectorBytes;
            index = type == 0 ? (byte)0 : !needsSave ? replaced!.SaveIndex!.Value
                : Chm1Layout.AllocateSaveSlot(scan.MenuConfig.Items.Where(item => item.SaveType != 0)
                    .Select(item => (int)item.SaveIndex), slotCount);
        }
        plan.MenuItem.SaveType = type;
        plan.MenuItem.SaveIndex = index;
        plan.UpdatedMenuConfig.Items[plan.InsertIndex].SaveType = type;
        plan.UpdatedMenuConfig.Items[plan.InsertIndex].SaveIndex = index;
        if (needsSave)
        {
            string savePath = Path.ChangeExtension(plan.FilePath, ".sav");
            var save = new WaterNorWriteSegment
            {
                StartAddress = (uint)(scan.MenuConfig.Status.SaveAreaAddress + index * Chm1Layout.SectorBytes),
                Data = Chm1Layout.PrepareSaveImage(File.Exists(savePath) ? File.ReadAllBytes(savePath) : null),
                Label = "01G/02G fragment management save " + (index + 1),
            };
            plan.SaveSegments.Add(save);
            plan.RomSegments.Add(save);
        }
        WaterMenuConfigCodec.BuildItemListSector(plan.UpdatedMenuConfig.Items,
            plan.UpdatedMenuConfig.Status.ItemListAddress, plan.UpdatedMenuConfig.Status.SectorSize)
            .CopyTo(plan.UpdatedItemListSector, 0);
        return plan;
    }

    public static int EstimateAllocatedSizeBytes(bool usesWlMode, long fileSizeBytes)
    {
        int normalizedSize = checked((int)Math.Min(int.MaxValue, Math.Max(1L, fileSizeBytes)));
        return usesWlMode
            ? Math.Max(MinimumRomSizeBytes, DivCeil(normalizedSize, WaterCartScanner.BucketSizeBytes) * WaterCartScanner.BucketSizeBytes)
            : NormalizeLegacyRomLength(normalizedSize);
    }

    public static List<WaterNorWriteSegment> BuildWriteSegments(WaterRomImportPlan plan, byte[] romBytes, int sectorSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(romBytes);

        if (plan.UsesWlMode)
        {
            return BuildWlRomSegments(romBytes, plan.PhysicalBuckets, sectorSizeBytes).Concat(plan.SaveSegments).ToList();
        }

        uint startAddress = plan.RomSegments.Count > 0
            ? plan.RomSegments[0].StartAddress
            : (uint)(plan.MenuItem.StartBucket * WaterCartScanner.BucketSizeBytes);
        string label = plan.RomSegments.Count > 0
            ? plan.RomSegments[0].Label
            : LocalizedText.Tf("value", "legacy_rom_segment_label", "Legacy ROM {0}", plan.DisplayTitle);

        return
        [
            new WaterNorWriteSegment
            {
                StartAddress = startAddress,
                Data = CreatePaddedRom(romBytes, plan.AllocatedSizeBytes),
                Label = label,
            },
            ..plan.SaveSegments,
        ];
    }

    private static WaterRomImportPlan PlanLegacyImport(
        WaterCartScanResult scanResult,
        string filePath,
        byte[] romBytes,
        string displayTitle,
        string headerTitle,
        int? plannedRomLengthBytes,
        byte? legacyMenuSaveTypeOverride)
    {
        if (!scanResult.MenuConfig.IsDetected)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "menu_config_required_for_import", "No menu config was detected, so ROM import is not available."));
        }

        int allocatedSizeBytes = NormalizeLegacyRomLength(plannedRomLengthBytes ?? romBytes.Length);
        if (allocatedSizeBytes > LegacyMaximumRomSizeBytes)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "legacy_mode_rom_too_large",
                    "Legacy mode only supports ROMs up to {0}.",
                    FormatMiB(LegacyMaximumRomSizeBytes)));
        }

        NorGeometryInfo geometry = scanResult.Geometry;
        if (geometry.SectorSizeBytes <= 0 || allocatedSizeBytes % geometry.SectorSizeBytes != 0)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "legacy_mode_alignment_invalid",
                    "Legacy ROM size 0x{0:X} is not aligned to NOR sector size 0x{1:X}.",
                    allocatedSizeBytes,
                    geometry.SectorSizeBytes));
        }

        int sectorCount = geometry.DeviceSizeBytes / geometry.SectorSizeBytes;
        bool[] usedSectors = new bool[sectorCount];

        MenuFlashStatusInfo status = scanResult.MenuConfig.Status;
        int statusSector = status.StatusAddress / geometry.SectorSizeBytes;
        int saveAreaStartSector = statusSector + 1;
        int systemReservedSectors = Math.Max(saveAreaStartSector, WaterCartScanner.BucketSizeBytes / geometry.SectorSizeBytes);

        // Legacy save slots live between the status sector and the first 4 MiB game bucket.
        // Reserve only the menu/head metadata area here; otherwise an empty-menu cart in
        // battery/FRAM mode incorrectly looks like it has no free save slot or ROM space.
        MarkRange(usedSectors, 0, saveAreaStartSector);

        int highestSaveSector = systemReservedSectors;
        foreach (MenuItemConfigInfo item in scanResult.MenuConfig.Items)
        {
            if (item.SaveType != 0)
            {
                int saveSector = saveAreaStartSector + item.SaveIndex;
                MarkRange(usedSectors, saveSector, 1);
                highestSaveSector = Math.Max(highestSaveSector, saveSector + 1);
            }

            int itemStartSector = BytesToSector(item.RomOffsetBlocks * BlockSizeBytes, geometry.SectorSizeBytes);
            int itemSectorCount = BytesToSector(item.RomSizeBlocks * BlockSizeBytes, geometry.SectorSizeBytes);
            MarkRange(usedSectors, itemStartSector, itemSectorCount);
        }

        foreach (DetectedNorGameInfo game in scanResult.Games.Where(game => !game.IsMenu))
        {
            bool presentInMenu = scanResult.MenuConfig.Items.Any(item => item.StartBucket == game.StartBucket);
            if (presentInMenu)
            {
                continue;
            }

            int startSector = BytesToSector(game.StartBucket * WaterCartScanner.BucketSizeBytes, geometry.SectorSizeBytes);
            int gameSectorCount = BytesToSector((game.EndBucket - game.StartBucket + 1) * WaterCartScanner.BucketSizeBytes, geometry.SectorSizeBytes);
            MarkRange(usedSectors, startSector, gameSectorCount);
        }

        byte saveType = 0;
        byte saveIndex = 0;
        if (status.BatteryPresent)
        {
            byte desiredSaveType = legacyMenuSaveTypeOverride ?? 2;
            if (desiredSaveType != 0)
            {
                int allocatedSaveSector = saveAreaStartSector;
                while (allocatedSaveSector < systemReservedSectors && usedSectors[allocatedSaveSector])
                {
                    allocatedSaveSector++;
                }

                if (allocatedSaveSector >= systemReservedSectors)
                {
                    throw new InvalidOperationException(
                        LocalizedText.Tf(
                            "error",
                            "legacy_mode_no_save_slot",
                            "No free legacy save slot remains for {0}.",
                            displayTitle));
                }

                int resolvedSaveIndex = allocatedSaveSector - saveAreaStartSector;
                if (resolvedSaveIndex < 0 || resolvedSaveIndex > byte.MaxValue)
                {
                    throw new InvalidOperationException(
                        LocalizedText.Tf(
                            "error",
                            "legacy_mode_save_slot_out_of_range",
                            "Legacy save slot index is out of range for {0}.",
                            displayTitle));
                }

                MarkRange(usedSectors, allocatedSaveSector, 1);
                saveType = desiredSaveType;
                saveIndex = checked((byte)resolvedSaveIndex);
            }
        }

        int requiredSectors = allocatedSizeBytes / geometry.SectorSizeBytes;
        int searchStartSector = Math.Max(systemReservedSectors, highestSaveSector);
        int alignmentSectors = Math.Max(1, WaterCartScanner.BucketSizeBytes / geometry.SectorSizeBytes);
        int startSectorOffset = FindAlignedFreeSectorRun(usedSectors, searchStartSector, requiredSectors, alignmentSectors);
        if (startSectorOffset < 0)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "legacy_mode_no_space",
                    "No contiguous free space large enough for {0} was found in legacy mode.",
                    displayTitle));
        }

        uint startAddress = (uint)(startSectorOffset * geometry.SectorSizeBytes);
        int romOffsetBlocks = (int)(startAddress / BlockSizeBytes);
        int romSizeBlocks = allocatedSizeBytes / BlockSizeBytes;

        MenuItemConfigInfo newItem = new()
        {
            Font = 0,
            RomOffsetBlocks = checked((ushort)romOffsetBlocks),
            RomSizeBlocks = checked((ushort)romSizeBlocks),
            SaveType = saveType,
            SaveIndex = saveIndex,
            HiddenKeysMask = 0,
            Title = displayTitle,
        };

        int insertIndex = GetDefaultInsertIndex(scanResult.MenuConfig.Items);
        List<MenuItemConfigInfo> updatedItems = scanResult.MenuConfig.Items
            .Select(WaterMenuConfigCodec.Clone)
            .ToList();
        updatedItems.Insert(insertIndex, newItem);

        List<MenuItemConfigInfo> indexedItems = WaterMenuConfigCodec.ReindexItems(updatedItems, status.ItemListAddress);
        byte[] updatedItemListSector = WaterMenuConfigCodec.BuildItemListSector(
            indexedItems,
            status.ItemListAddress,
            status.SectorSize);

        byte[] paddedRom = CreatePaddedRom(romBytes, allocatedSizeBytes);
        List<int> coveredBuckets = Enumerable
            .Range((int)(startAddress / WaterCartScanner.BucketSizeBytes), Math.Max(1, allocatedSizeBytes / WaterCartScanner.BucketSizeBytes))
            .ToList();

        return new WaterRomImportPlan
        {
            FilePath = filePath,
            DisplayTitle = displayTitle,
            HeaderTitle = headerTitle,
            UsesWlMode = false,
            InsertIndex = insertIndex,
            FileSizeBytes = romBytes.Length,
            AllocatedSizeBytes = allocatedSizeBytes,
            LogicalBucketCount = Math.Max(1, allocatedSizeBytes / WaterCartScanner.BucketSizeBytes),
            PhysicalBuckets = coveredBuckets,
            MenuItem = newItem,
            UpdatedMenuConfig = BuildUpdatedMenuConfig(status, indexedItems),
            UpdatedItemListSector = updatedItemListSector,
            RomSegments =
            [
                new WaterNorWriteSegment
                {
                    StartAddress = startAddress,
                    Data = paddedRom,
                    Label = LocalizedText.Tf("value", "legacy_rom_segment_label", "Legacy ROM {0}", displayTitle),
                }
            ],
        };
    }

    private static WaterRomImportPlan PlanLegacyReplacementImport(
        WaterCartScanResult scanResult,
        string filePath,
        byte[] romBytes,
        string displayTitle,
        string headerTitle,
        int? plannedRomLengthBytes,
        byte? legacyMenuSaveTypeOverride,
        WaterCartEntry replacementTargetEntry)
    {
        if (!scanResult.MenuConfig.IsDetected)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "menu_config_required_for_import", "No menu config was detected, so ROM import is not available."));
        }

        if (replacementTargetEntry.StartBucket <= 0)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "replace_rom_target_invalid", "The selected ROM mapping cannot be replaced because its target area is invalid."));
        }

        int replacementAllocatedSizeBytes = Math.Max(MinimumRomSizeBytes, replacementTargetEntry.LogicalBucketCount * WaterCartScanner.BucketSizeBytes);
        int requiredAllocatedSizeBytes = NormalizeLegacyRomLength(plannedRomLengthBytes ?? romBytes.Length);
        if (requiredAllocatedSizeBytes > replacementAllocatedSizeBytes)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "replace_rom_target_too_small",
                    "The replacement ROM needs {0}, but the target mapping only allows {1}.",
                    FormatMiB(requiredAllocatedSizeBytes),
                    FormatMiB(replacementAllocatedSizeBytes)));
        }

        NorGeometryInfo geometry = scanResult.Geometry;
        if (geometry.SectorSizeBytes <= 0 || replacementAllocatedSizeBytes % geometry.SectorSizeBytes != 0)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "legacy_mode_alignment_invalid",
                    "Legacy ROM size 0x{0:X} is not aligned to NOR sector size 0x{1:X}.",
                    replacementAllocatedSizeBytes,
                    geometry.SectorSizeBytes));
        }

        MenuFlashStatusInfo status = scanResult.MenuConfig.Status;
        byte saveType = status.BatteryPresent
            ? (byte)(legacyMenuSaveTypeOverride ?? replacementTargetEntry.SaveType ?? 2)
            : (byte)0;
        byte saveIndex = status.BatteryPresent && replacementTargetEntry.SaveIndex.HasValue
            ? replacementTargetEntry.SaveIndex.Value
            : (byte)0;

        uint startAddress = replacementTargetEntry.StartAddress;
        int romOffsetBlocks = (int)(startAddress / BlockSizeBytes);
        int romSizeBlocks = replacementAllocatedSizeBytes / BlockSizeBytes;
        MenuItemConfigInfo newItem = new()
        {
            Font = replacementTargetEntry.MenuItem?.Font ?? (byte)0,
            RomOffsetBlocks = checked((ushort)romOffsetBlocks),
            RomSizeBlocks = checked((ushort)romSizeBlocks),
            SaveType = saveType,
            SaveIndex = saveIndex,
            HiddenKeysMask = replacementTargetEntry.MenuItem?.HiddenKeysMask ?? (ushort)0,
            Title = displayTitle,
        };

        int insertIndex = replacementTargetEntry.EntrySlot >= 0
            ? Math.Clamp(replacementTargetEntry.EntrySlot, 0, scanResult.MenuConfig.ItemCount)
            : GetDefaultInsertIndex(scanResult.MenuConfig.Items);
        List<MenuItemConfigInfo> updatedItems = scanResult.MenuConfig.Items
            .Select(WaterMenuConfigCodec.Clone)
            .ToList();
        updatedItems.Insert(insertIndex, newItem);

        List<MenuItemConfigInfo> indexedItems = WaterMenuConfigCodec.ReindexItems(updatedItems, status.ItemListAddress);
        byte[] updatedItemListSector = WaterMenuConfigCodec.BuildItemListSector(
            indexedItems,
            status.ItemListAddress,
            status.SectorSize);

        byte[] paddedRom = CreatePaddedRom(romBytes, replacementAllocatedSizeBytes);
        List<int> coveredBuckets = replacementTargetEntry.PhysicalBuckets.ToList();
        if (coveredBuckets.Count == 0)
        {
            coveredBuckets.Add(replacementTargetEntry.StartBucket);
        }

        return new WaterRomImportPlan
        {
            FilePath = filePath,
            DisplayTitle = displayTitle,
            HeaderTitle = headerTitle,
            UsesWlMode = false,
            InsertIndex = insertIndex,
            FileSizeBytes = romBytes.Length,
            AllocatedSizeBytes = replacementAllocatedSizeBytes,
            LogicalBucketCount = Math.Max(1, replacementAllocatedSizeBytes / WaterCartScanner.BucketSizeBytes),
            PhysicalBuckets = coveredBuckets,
            MenuItem = newItem,
            UpdatedMenuConfig = BuildUpdatedMenuConfig(status, indexedItems),
            UpdatedItemListSector = updatedItemListSector,
            RomSegments =
            [
                new WaterNorWriteSegment
                {
                    StartAddress = startAddress,
                    Data = paddedRom,
                    Label = LocalizedText.Tf("value", "legacy_rom_segment_label", "Legacy ROM {0}", displayTitle),
                }
            ],
        };
    }

    private static WaterRomImportPlan PlanWlImport(
        WaterCartScanResult scanResult,
        string filePath,
        byte[] romBytes,
        string displayTitle,
        string headerTitle,
        int? plannedRomLengthBytes)
    {
        if (!scanResult.MenuConfig.IsDetected)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "menu_config_required_for_import", "No menu config was detected, so ROM import is not available."));
        }

        if (!scanResult.WlMapping.IsDetected || scanResult.WlMapping.RawSector.Length != WaterWlMappingCodec.SectorSize)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "wl_mapping_required_for_import", "No valid WL mapping sector is available, so Magic mode import is not available."));
        }

        int effectiveRomLengthBytes = plannedRomLengthBytes ?? romBytes.Length;
        int logicalBucketCount = Math.Max(1, DivCeil(effectiveRomLengthBytes, WaterCartScanner.BucketSizeBytes));
        if (logicalBucketCount > WaterWlMappingCodec.MaxLogicalBucketCount)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "wl_mode_rom_too_large",
                    "Magic mode only supports ROMs up to {0}.",
                    FormatMiB(WaterWlMappingCodec.MaxLogicalBucketCount * WaterCartScanner.BucketSizeBytes)));
        }

        byte[] mappingSector = scanResult.WlMapping.RawSector.ToArray();
        if (!scanResult.UsesChm1Layout && !WaterWlMappingCodec.TryExpandBucketOwners(
                scanResult.WlMapping.RawSector,
                out mappingSector,
                out string expansionError))
        {
            throw new InvalidOperationException(expansionError);
        }

        if (!WaterWlMappingCodec.TryParse(
                mappingSector,
                WaterWlMappingCodec.Offset,
                false,
                out WaterWlMappingInfo planningMapping,
                out string parseExpansionError))
        {
            throw new InvalidOperationException(parseExpansionError);
        }

        int maxPhysicalBucket = GetMaxPhysicalBucket(scanResult);
        List<int> freeBuckets = Enumerable.Range(1, maxPhysicalBucket)
            .Where(bucket => bucket < planningMapping.BucketOwners.Length &&
                              planningMapping.BucketOwners[bucket] == WaterWlMappingCodec.BucketOwnerFree)
            .ToList();

        if (freeBuckets.Count < logicalBucketCount)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "wl_mode_no_space",
                    "Not enough free 4MiB buckets remain for {0}.",
                    displayTitle));
        }

        List<int> physicalBuckets = freeBuckets.Take(logicalBucketCount).ToList();
        int insertIndex = GetDefaultInsertIndex(scanResult.MenuConfig.Items);

        if (insertIndex < scanResult.MenuConfig.ItemCount)
        {
            if (!WaterWlMappingCodec.TryInsertRecordSlot(mappingSector, insertIndex, out mappingSector, out string insertError))
            {
                throw new InvalidOperationException(insertError);
            }
        }

        if (!WaterWlMappingCodec.TryUpsertRecord(mappingSector, insertIndex, physicalBuckets, out byte[] updatedMappingSector, out string upsertError))
        {
            throw new InvalidOperationException(upsertError);
        }

        MenuItemConfigInfo newItem = new()
        {
            Font = 0,
            RomOffsetBlocks = checked((ushort)(physicalBuckets[0] * 8)),
            RomSizeBlocks = checked((ushort)(logicalBucketCount * 8)),
            SaveType = 0,
            SaveIndex = 0,
            HiddenKeysMask = 0,
            Title = displayTitle,
        };

        List<MenuItemConfigInfo> updatedItems = scanResult.MenuConfig.Items
            .Select(WaterMenuConfigCodec.Clone)
            .ToList();
        updatedItems.Insert(insertIndex, newItem);

        List<MenuItemConfigInfo> indexedItems = WaterMenuConfigCodec.ReindexItems(updatedItems, scanResult.MenuConfig.Status.ItemListAddress);
        byte[] updatedItemListSector = WaterMenuConfigCodec.BuildItemListSector(
            indexedItems,
            scanResult.MenuConfig.Status.ItemListAddress,
            scanResult.MenuConfig.Status.SectorSize);

        if (!WaterWlMappingCodec.TryParse(updatedMappingSector, WaterWlMappingCodec.Offset, false, out WaterWlMappingInfo updatedWlMapping, out string parseError))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(parseError)
                    ? LocalizedText.T("error", "wl_sector_format_invalid", "WL mapping sector format is invalid.")
                    : parseError);
        }

        return new WaterRomImportPlan
        {
            FilePath = filePath,
            DisplayTitle = displayTitle,
            HeaderTitle = headerTitle,
            UsesWlMode = true,
            InsertIndex = insertIndex,
            FileSizeBytes = romBytes.Length,
            AllocatedSizeBytes = logicalBucketCount * WaterCartScanner.BucketSizeBytes,
            LogicalBucketCount = logicalBucketCount,
            PhysicalBuckets = physicalBuckets,
            MenuItem = newItem,
            UpdatedMenuConfig = BuildUpdatedMenuConfig(scanResult.MenuConfig.Status, indexedItems),
            UpdatedItemListSector = updatedItemListSector,
            UpdatedWlMapping = updatedWlMapping,
            UpdatedWlMappingSector = updatedMappingSector,
            RomSegments = BuildWlRomSegments(romBytes, physicalBuckets, scanResult.Geometry.SectorSizeBytes),
        };
    }

    private static WaterRomImportPlan PlanWlReplacementImport(
        WaterCartScanResult scanResult,
        string filePath,
        byte[] romBytes,
        string displayTitle,
        string headerTitle,
        int? plannedRomLengthBytes,
        WaterCartEntry replacementTargetEntry)
    {
        if (!scanResult.MenuConfig.IsDetected)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "menu_config_required_for_import", "No menu config was detected, so ROM import is not available."));
        }

        if (!scanResult.WlMapping.IsDetected || scanResult.WlMapping.RawSector.Length != WaterWlMappingCodec.SectorSize)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "wl_mapping_required_for_import", "No valid WL mapping sector is available, so Magic mode import is not available."));
        }

        List<int> physicalBuckets = replacementTargetEntry.PhysicalBuckets.ToList();
        int logicalBucketCount = replacementTargetEntry.WlRecord?.LogicalBucketCount
            ?? Math.Max(1, physicalBuckets.Count);
        if (physicalBuckets.Count == 0 || logicalBucketCount <= 0)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "replace_rom_target_invalid", "The selected ROM mapping cannot be replaced because its target area is invalid."));
        }

        int effectiveRomLengthBytes = plannedRomLengthBytes ?? romBytes.Length;
        int requiredLogicalBucketCount = Math.Max(1, DivCeil(effectiveRomLengthBytes, WaterCartScanner.BucketSizeBytes));
        if (requiredLogicalBucketCount > logicalBucketCount)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf(
                    "error",
                    "replace_rom_target_too_small",
                    "The replacement ROM needs {0}, but the target mapping only allows {1}.",
                    FormatMiB(requiredLogicalBucketCount * WaterCartScanner.BucketSizeBytes),
                    FormatMiB(logicalBucketCount * WaterCartScanner.BucketSizeBytes)));
        }

        int insertIndex = replacementTargetEntry.EntrySlot >= 0
            ? Math.Clamp(replacementTargetEntry.EntrySlot, 0, scanResult.MenuConfig.ItemCount)
            : GetDefaultInsertIndex(scanResult.MenuConfig.Items);

        byte[] mappingSector = scanResult.WlMapping.RawSector.ToArray();
        if (insertIndex < scanResult.MenuConfig.ItemCount)
        {
            if (!WaterWlMappingCodec.TryInsertRecordSlot(mappingSector, insertIndex, out mappingSector, out string insertError))
            {
                throw new InvalidOperationException(insertError);
            }
        }

        if (!WaterWlMappingCodec.TryUpsertRecord(mappingSector, insertIndex, physicalBuckets, out byte[] updatedMappingSector, out string upsertError))
        {
            throw new InvalidOperationException(upsertError);
        }

        MenuItemConfigInfo newItem = new()
        {
            Font = replacementTargetEntry.MenuItem?.Font ?? (byte)0,
            RomOffsetBlocks = checked((ushort)(physicalBuckets[0] * 8)),
            RomSizeBlocks = checked((ushort)(logicalBucketCount * 8)),
            SaveType = 0,
            SaveIndex = 0,
            HiddenKeysMask = replacementTargetEntry.MenuItem?.HiddenKeysMask ?? (ushort)0,
            Title = displayTitle,
        };

        List<MenuItemConfigInfo> updatedItems = scanResult.MenuConfig.Items
            .Select(WaterMenuConfigCodec.Clone)
            .ToList();
        updatedItems.Insert(insertIndex, newItem);

        List<MenuItemConfigInfo> indexedItems = WaterMenuConfigCodec.ReindexItems(updatedItems, scanResult.MenuConfig.Status.ItemListAddress);
        byte[] updatedItemListSector = WaterMenuConfigCodec.BuildItemListSector(
            indexedItems,
            scanResult.MenuConfig.Status.ItemListAddress,
            scanResult.MenuConfig.Status.SectorSize);

        if (!WaterWlMappingCodec.TryParse(updatedMappingSector, WaterWlMappingCodec.Offset, false, out WaterWlMappingInfo updatedWlMapping, out string parseError))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(parseError)
                    ? LocalizedText.T("error", "wl_sector_format_invalid", "WL mapping sector format is invalid.")
                    : parseError);
        }

        return new WaterRomImportPlan
        {
            FilePath = filePath,
            DisplayTitle = displayTitle,
            HeaderTitle = headerTitle,
            UsesWlMode = true,
            InsertIndex = insertIndex,
            FileSizeBytes = romBytes.Length,
            AllocatedSizeBytes = logicalBucketCount * WaterCartScanner.BucketSizeBytes,
            LogicalBucketCount = logicalBucketCount,
            PhysicalBuckets = physicalBuckets,
            MenuItem = newItem,
            UpdatedMenuConfig = BuildUpdatedMenuConfig(scanResult.MenuConfig.Status, indexedItems),
            UpdatedItemListSector = updatedItemListSector,
            UpdatedWlMapping = updatedWlMapping,
            UpdatedWlMappingSector = updatedMappingSector,
            RomSegments = BuildWlRomSegments(romBytes, physicalBuckets, scanResult.Geometry.SectorSizeBytes),
        };
    }

    private static int NormalizeLegacyRomLength(int fileSizeBytes)
    {
        return Math.Max(
            MinimumRomSizeBytes,
            DivCeil(Math.Max(1, fileSizeBytes), MinimumRomSizeBytes) * MinimumRomSizeBytes);
    }

    private static int GetMaxPhysicalBucket(WaterCartScanResult scanResult)
    {
        int deviceBucketCount = scanResult.Geometry.DeviceSizeBytes / WaterCartScanner.BucketSizeBytes;
        int maxDeviceBucket = Math.Max(0, deviceBucketCount - 1);
        return Math.Min(WaterWlMappingCodec.RomPhysicalBucketCount, maxDeviceBucket);
    }

    private static byte[] CreatePaddedRom(byte[] romBytes, int outputLength)
    {
        byte[] output = Enumerable.Repeat((byte)0xFF, outputLength).ToArray();
        Buffer.BlockCopy(romBytes, 0, output, 0, Math.Min(romBytes.Length, output.Length));
        return output;
    }

    private static WaterMenuConfigInfo BuildUpdatedMenuConfig(MenuFlashStatusInfo status, IReadOnlyList<MenuItemConfigInfo> indexedItems)
    {
        return new WaterMenuConfigInfo
        {
            IsDetected = true,
            Status = new MenuFlashStatusInfo
            {
                StatusAddress = status.StatusAddress,
                ExplicitItemListAddress = status.ExplicitItemListAddress,
                ExplicitSaveAreaAddress = status.ExplicitSaveAreaAddress,
                SectorSize = status.SectorSize,
                Version = status.Version,
                BatteryPresent = status.BatteryPresent,
                LastBootMenuIndex = status.LastBootMenuIndex,
                LastBootSaveIndex = status.LastBootSaveIndex,
                LastBootSaveType = status.LastBootSaveType,
                SramBankType = status.SramBankType,
            },
            Items = indexedItems
                .Select(WaterMenuConfigCodec.Clone)
                .ToList(),
        };
    }

    private static List<WaterNorWriteSegment> BuildWlRomSegments(byte[] romBytes, IReadOnlyList<int> physicalBuckets, int sectorSizeBytes)
    {
        List<WaterNorWriteSegment> segments = [];
        if (physicalBuckets.Count == 0)
        {
            return segments;
        }

        int sectorSize = sectorSizeBytes > 0
            ? sectorSizeBytes
            : WaterWlMappingCodec.SectorSize;

        for (int logicalBucketStart = 0; logicalBucketStart < physicalBuckets.Count;)
        {
            int logicalBucketEnd = logicalBucketStart;
            while (logicalBucketEnd + 1 < physicalBuckets.Count &&
                   physicalBuckets[logicalBucketEnd + 1] == physicalBuckets[logicalBucketEnd] + 1)
            {
                logicalBucketEnd++;
            }

            int runBucketCount = logicalBucketEnd - logicalBucketStart + 1;
            int sourceOffset = logicalBucketStart * WaterCartScanner.BucketSizeBytes;
            int runCapacityBytes = runBucketCount * WaterCartScanner.BucketSizeBytes;
            int usedBytes = Math.Max(0, Math.Min(runCapacityBytes, romBytes.Length - sourceOffset));
            if (usedBytes <= 0)
            {
                logicalBucketStart = logicalBucketEnd + 1;
                continue;
            }

            int dataLength = DivCeil(usedBytes, sectorSize) * sectorSize;
            byte[] segmentData = Enumerable.Repeat((byte)0xFF, dataLength).ToArray();
            Buffer.BlockCopy(romBytes, sourceOffset, segmentData, 0, usedBytes);

            int startBucket = physicalBuckets[logicalBucketStart];
            List<int> runBuckets = physicalBuckets
                .Skip(logicalBucketStart)
                .Take(runBucketCount)
                .ToList();

            string label = runBucketCount == 1
                ? LocalizedText.Tf("value", "wl_rom_segment_label", "Magic ROM block {0:00}", startBucket)
                : LocalizedText.Tf(
                    "value",
                    "wl_rom_segment_range_label",
                    "Magic ROM blocks {0}",
                    WaterCartEntry.FormatBucketList(runBuckets));

            segments.Add(new WaterNorWriteSegment
            {
                StartAddress = (uint)(startBucket * WaterCartScanner.BucketSizeBytes),
                Data = segmentData,
                Label = label,
            });

            int eraseTailBytes = runCapacityBytes - dataLength;
            if (eraseTailBytes > 0)
            {
                int tailStartBucketIndex = logicalBucketStart + (dataLength / WaterCartScanner.BucketSizeBytes);
                List<int> tailBuckets = physicalBuckets
                    .Skip(tailStartBucketIndex)
                    .Take(logicalBucketEnd - tailStartBucketIndex + 1)
                    .ToList();

                segments.Add(new WaterNorWriteSegment
                {
                    StartAddress = (uint)(startBucket * WaterCartScanner.BucketSizeBytes + dataLength),
                    Data = new byte[eraseTailBytes],
                    Label = LocalizedText.Tf(
                        "value",
                        "wl_blank_tail_segment_label",
                        "Magic blank tail {0}",
                        WaterCartEntry.FormatBucketList(tailBuckets)),
                    EraseOnly = true,
                });
            }

            logicalBucketStart = logicalBucketEnd + 1;
        }

        return segments;
    }

    private static int GetDefaultInsertIndex(IReadOnlyList<MenuItemConfigInfo> items) =>
        items.TakeWhile(item => item.HiddenKeysMask == 0).Count();

    private static int FindAlignedFreeSectorRun(bool[] usedSectors, int searchStartSector, int requiredSectors, int alignmentSectors)
    {
        alignmentSectors = Math.Max(1, alignmentSectors);
        for (int sector = searchStartSector; sector + requiredSectors <= usedSectors.Length; sector++)
        {
            if (sector % alignmentSectors != 0)
            {
                continue;
            }

            bool isFree = true;
            for (int i = 0; i < requiredSectors; i++)
            {
                if (usedSectors[sector + i])
                {
                    isFree = false;
                    break;
                }
            }

            if (isFree)
            {
                return sector;
            }
        }

        return -1;
    }

    private static void MarkRange(bool[] usedSectors, int startSector, int sectorCount)
    {
        if (sectorCount <= 0)
        {
            return;
        }

        startSector = Math.Max(0, startSector);
        int endSector = Math.Min(usedSectors.Length, startSector + sectorCount);
        for (int sector = startSector; sector < endSector; sector++)
        {
            usedSectors[sector] = true;
        }
    }

    private static int BytesToSector(int bytes, int sectorSizeBytes) => DivCeil(bytes, sectorSizeBytes);

    private static int DivCeil(int value, int divisor) => (value / divisor) + ((value % divisor) != 0 ? 1 : 0);

    private static string TryGetHeaderTitle(byte[] romBytes)
    {
        if (romBytes.Length < 0xA0 || !romBytes.AsSpan(4, NintendoLogoGba.Length).SequenceEqual(NintendoLogoGba))
        {
            return string.Empty;
        }

        return Encoding.ASCII.GetString(romBytes, 0xA0, 12).TrimEnd('\0', ' ');
    }

    private static string FormatMiB(int bytes) => $"{bytes / 1024 / 1024} MiB";
}
