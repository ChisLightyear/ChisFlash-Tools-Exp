using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public readonly record struct WaterSysInfo(
    uint SysClockHz,
    uint PeripheralClockHz,
    uint PllClockHz,
    uint HseClockHz,
    uint HsiClockHz,
    uint ClockControl,
    uint PeripheralBusDivider,
    byte ClockSource,
    bool UsbPhyConnected,
    byte SclkDividerHigh,
    byte SclkDividerLow);

public sealed class DetectedNorGameInfo
{
    public int StartBucket { get; set; }
    public int EndBucket { get; set; }
    public uint StartAddress => (uint)(StartBucket * WaterCartScanner.BucketSizeBytes);
    public uint EndAddress => (uint)(((EndBucket + 1) * WaterCartScanner.BucketSizeBytes) - 1);
    public string Title { get; set; } = string.Empty;
    public string GameCode { get; set; } = string.Empty;
    public bool IsMenu { get; set; }
    public string KindText => IsMenu
        ? LocalizedText.T("value", "kind_menu", "Menu")
        : LocalizedText.T("value", "kind_game", "Game");
    public string SizeText => $"{EndBucket - StartBucket + 1} x 4 MiB";
    public string BucketRangeText => $"{StartBucket:00}-{EndBucket:00}";
}

public sealed class NorGeometryInfo
{
    public int DeviceSizeBytes { get; set; }
    public int SectorCount { get; set; }
    public int SectorSizeBytes { get; set; }
    public int BufferWriteBytes { get; set; }
    public int LargestSectorSizeBytes { get; set; }
    public int RegionCount { get; set; }
    public ushort PrimaryCommandSetId { get; set; }
    public string CommandSetName { get; set; } = "Unknown";
    // True only for S70GL02GS-style stacked 2 Gbit NOR that requires per-die command addressing.
    public bool IsS70Gl02GsDualDie { get; set; }
    public bool IsWaterLinkMultiNor { get; set; }
}

public sealed class MenuFlashStatusInfo
{
    public int ExplicitSaveAreaAddress { get; set; } = -1;
    public int SaveAreaAddress => ExplicitSaveAreaAddress >= 0 ? ExplicitSaveAreaAddress : StatusAddress + SectorSize;
    public int StatusAddress { get; set; }
    public int SectorSize { get; set; }
    public int ExplicitItemListAddress { get; set; } = -1;
    public byte Version { get; set; }
    public bool BatteryPresent { get; set; }
    public ushort LastBootMenuIndex { get; set; }
    public byte LastBootSaveIndex { get; set; }
    public byte LastBootSaveType { get; set; }
    public byte SramBankType { get; set; }
    public int ItemListAddress => ExplicitItemListAddress >= 0
        ? ExplicitItemListAddress
        : StatusAddress - SectorSize;
}

public sealed class MenuItemConfigInfo
{
    public int Index { get; set; }
    public int EntryAddress { get; set; }
    public byte Font { get; set; }
    public byte TitleLength { get; set; }
    public ushort RomOffsetBlocks { get; set; }
    public ushort RomSizeBlocks { get; set; }
    public byte SaveType { get; set; }
    public byte SaveIndex { get; set; }
    public ushort HiddenKeysMask { get; set; }
    public byte CoverIndex { get; set; } = 0xFF;
    public byte CoverFlags { get; set; }
    public string Title { get; set; } = string.Empty;
    public int StartBucket => RomOffsetBlocks >> 3;
    public int CoveredBucketCount => Math.Max(1, (RomSizeBlocks + 7) >> 3);
    public int EndBucket => StartBucket + CoveredBucketCount - 1;
    public string BucketRangeText => $"{StartBucket:00}-{EndBucket:00}";
    public string SaveTypeText => SaveType switch
    {
        1 => LocalizedText.T("value", "save_type_sram_256k", "SRAM 256K"),
        2 => LocalizedText.T("value", "save_type_sram_512k", "SRAM 512K"),
        3 => LocalizedText.T("value", "save_type_sram_1m", "SRAM 1M"),
        _ => LocalizedText.T("value", "save_type_none", "NONE"),
    };
}

public sealed class GbaMenuFirstScreenPreviewInfo
{
    public bool IsDetected { get; set; }
    public int PackOffset { get; set; }
    public int PackSize { get; set; }
    public int PaletteBase { get; set; }
    public byte[] PaletteData { get; set; } = [];
    public Dictionary<int, string> GameCodesByIndex { get; set; } = [];
    public Dictionary<int, byte[]> CoverPixelsByIndex { get; set; } = [];
}

public sealed class WaterMenuConfigInfo
{
    public bool IsDetected { get; set; }
    public MenuFlashStatusInfo Status { get; set; } = new();
    public List<MenuItemConfigInfo> Items { get; set; } = [];
    public int ItemCount => Items.Count;
    public IReadOnlyList<int> StartBuckets => Items
        .Select(item => item.StartBucket)
        .Distinct()
        .OrderBy(bucket => bucket)
        .ToArray();
}

public sealed class WlMappingHeaderInfo
{
    public uint Magic { get; set; }
    public ushort Version { get; set; }
    public ushort HeaderSize { get; set; }
    public uint Flags { get; set; }
    public int BucketOwnerOffset { get; set; }
    public ushort BucketOwnerEntrySize { get; set; }
    public ushort BucketOwnerCount { get; set; }
    public int RomMapRecordOffset { get; set; }
    public ushort RomMapRecordSize { get; set; }
    public ushort RomMapRecordCount { get; set; }
    public uint MappingChecksum { get; set; }
}

public sealed class WlRomMapRecordInfo
{
    public int EntrySlot { get; set; }
    public int LogicalBucketCount { get; set; }
    public List<int> PhysicalBuckets { get; set; } = [];
    public string PhysicalBucketText => WaterCartEntry.FormatBucketList(PhysicalBuckets);
}

public sealed class WaterWlMappingInfo
{
    public bool IsDetected { get; set; }
    public bool UsingBackupCopy { get; set; }
    public int SourceAddress { get; set; }
    public bool LegacyVersionDetected { get; set; }
    public string ValidationError { get; set; } = string.Empty;
    public bool HasChecksum { get; set; }
    public bool IsChecksumValid { get; set; }
    public uint ComputedChecksum { get; set; }
    public WlMappingHeaderInfo Header { get; set; } = new();
    public ushort[] BucketOwners { get; set; } = [];
    public List<WlRomMapRecordInfo> Records { get; set; } = [];
    public byte[] RawSector { get; set; } = [];
    public int RecordCount => Records.Count;
}

public sealed class WaterMagicMetadataSyncInfo
{
    public bool IsApplicable { get; set; }
    public int MenuItemCount { get; set; }
    public int MappingRecordCount { get; set; }
    public List<int> MenuOnlySlots { get; set; } = [];
    public List<int> MappingOnlySlots { get; set; } = [];
    public List<int> SlotRangeMismatchSlots { get; set; } = [];
    public List<int> NonSequentialRecordSlots { get; set; } = [];

    public bool NeedsRepair => IsApplicable && (
        MenuItemCount != MappingRecordCount ||
        MenuOnlySlots.Count > 0 ||
        MappingOnlySlots.Count > 0 ||
        SlotRangeMismatchSlots.Count > 0 ||
        NonSequentialRecordSlots.Count > 0);
}

public sealed class WlMappingDeletePlan
{
    public int EntrySlot { get; set; }
    public int LogicalBucketCount { get; set; }
    public List<int> ClearedBuckets { get; set; } = [];
    public List<int> OwnerConflictBuckets { get; set; } = [];
    public string ClearedBucketText => WaterCartEntry.FormatBucketList(ClearedBuckets);
    public string OwnerConflictText => WaterCartEntry.FormatBucketList(OwnerConflictBuckets);
}

public sealed class WaterSaveSlotSummaryInfo
{
    public const int SlotCount = 63;

    public bool IsAvailable { get; set; }
    public bool HasFullAbPreview { get; set; }
    public byte[] Entries { get; set; } = new byte[SlotCount];

    public byte GetEntryFlags(int gameSlot) =>
        gameSlot is >= 0 && gameSlot < Entries.Length
            ? Entries[gameSlot]
            : (byte)0;
}

public sealed class WaterRuntimeStatusInfo
{
    public bool IsAvailable { get; set; }
    public int BucketId { get; set; }
    public bool MapperLocked { get; set; }
    public bool GameModeActive { get; set; }
    public bool SupportsWaterLinkFixedLayout { get; set; }
    public int NorChipValidMask { get; set; }
    public int NorChipCount { get; set; }
    public int? CurrentGameSlot { get; set; }
    public uint SaveDelayRaw { get; set; }
}

public sealed class WaterCartEntry
{
    public DetectedNorGameInfo? HeaderGame { get; set; }
    public MenuItemConfigInfo? MenuItem { get; set; }
    public WlRomMapRecordInfo? WlRecord { get; set; }
    public int AllocatedSizeBytesOverride { get; set; }
    public int EntrySlotOverride { get; set; } = -1;
    public int SaveLengthBytesOverride { get; set; }
    public string SourceTextOverride { get; set; } = string.Empty;
    public string SaveTypeTextOverride { get; set; } = string.Empty;
    public int RomOffsetBankOverride { get; set; } = -1;
    public int RomBankCountOverride { get; set; }
    public byte[] SaveBankMapOverride { get; set; } = [];
    public bool UsesSharedSaveOverride { get; set; }
    public byte MbcTypeOverride { get; set; }
    public int RamBankCountOverride { get; set; }
    public bool HasBatteryOverride { get; set; }
    public string DirectoryTitleOverride { get; set; } = string.Empty;

    public bool FoundByHeaderScan => HeaderGame is not null;
    public bool FoundInMenuConfig => MenuItem is not null;
    public bool FoundInWlMapping => WlRecord is not null;
    public bool IsMenu => HeaderGame?.IsMenu ?? false;

    public int EntrySlot => EntrySlotOverride >= 0
        ? EntrySlotOverride
        : WlRecord?.EntrySlot ?? MenuItem?.Index ?? -1;
    public int SortKey => EntrySlot >= 0 ? EntrySlot : StartBucket;

    public int StartBucket => PhysicalBuckets.Count > 0
        ? PhysicalBuckets[0]
        : MenuItem?.StartBucket ?? HeaderGame?.StartBucket ?? 0;

    public int EndBucket => PhysicalBuckets.Count > 0
        ? PhysicalBuckets[^1]
        : MenuItem?.EndBucket ?? HeaderGame?.EndBucket ?? StartBucket;

    public IReadOnlyList<int> PhysicalBuckets
    {
        get
        {
            if (WlRecord is not null && WlRecord.PhysicalBuckets.Count > 0)
            {
                return WlRecord.PhysicalBuckets;
            }

            int startBucket = MenuItem?.StartBucket ?? HeaderGame?.StartBucket ?? 0;
            int endBucket = MenuItem?.EndBucket ?? HeaderGame?.EndBucket ?? startBucket;
            if (endBucket < startBucket)
            {
                endBucket = startBucket;
            }

            return Enumerable.Range(startBucket, endBucket - startBucket + 1).ToArray();
        }
    }

    public uint StartAddress => (uint)(StartBucket * WaterCartScanner.BucketSizeBytes);
    public uint EndAddress => (uint)(((EndBucket + 1) * WaterCartScanner.BucketSizeBytes) - 1);
    public int LogicalBucketCount => WlRecord?.LogicalBucketCount ?? Math.Max(1, PhysicalBuckets.Count);
    public bool CanDelete => EntrySlot >= 0 && !IsMenu && (FoundInMenuConfig || FoundInWlMapping || RomOffsetBankOverride >= 0);

    public string KindText => IsMenu
        ? LocalizedText.T("value", "kind_menu", "Menu")
        : LocalizedText.T("value", "kind_game", "Game");

    public string SourceText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SourceTextOverride))
            {
                return SourceTextOverride;
            }

            List<string> parts = [];
            if (FoundInWlMapping)
            {
                parts.Add(LocalizedText.T("value", "source_wl", "WL"));
            }

            if (FoundInMenuConfig)
            {
                parts.Add(LocalizedText.T("value", "source_menu", "Menu"));
            }

            if (FoundByHeaderScan)
            {
                parts.Add(LocalizedText.T("value", "source_nor", "NOR"));
            }

            return parts.Count > 0
                ? string.Join("+", parts)
                : LocalizedText.T("value", "source_unknown", "Unknown");
        }
    }

    public string Title => !string.IsNullOrWhiteSpace(DirectoryTitleOverride)
        ? DirectoryTitleOverride
        : !string.IsNullOrWhiteSpace(MenuItem?.Title)
        ? MenuItem!.Title
        : !string.IsNullOrWhiteSpace(HeaderGame?.Title)
            ? HeaderGame!.Title
            : EntrySlot >= 0
                ? LocalizedText.Tf("value", "untitled_entry_slot", "Untitled Entry #{0:000}", EntrySlot + 1)
                : LocalizedText.T("value", "untitled", "UNTITLED");

    public string HeaderTitle => HeaderGame?.Title ?? string.Empty;
    public string MenuTitle => MenuItem?.Title ?? string.Empty;
    public string BucketRangeText => FormatBucketList(PhysicalBuckets);
    public int? MenuIndex => EntrySlot >= 0 ? EntrySlot + 1 : null;
    public ushort? RomOffsetBlocks => MenuItem?.RomOffsetBlocks;
    public ushort? RomSizeBlocks => MenuItem?.RomSizeBlocks;
    public byte? SaveType => MenuItem?.SaveType;
    public int SaveLengthBytes => SaveLengthBytesOverride > 0
        ? SaveLengthBytesOverride
        : SaveType switch
        {
            1 => 0x8000,
            2 => 0x10000,
            3 => 0x20000,
            _ => 0,
        };
    public string SaveTypeText => !string.IsNullOrWhiteSpace(SaveTypeTextOverride)
        ? SaveTypeTextOverride
        : MenuItem?.SaveTypeText ?? string.Empty;
    public byte? SaveIndex => MenuItem?.SaveIndex;
    public int? InferredSpiSlot => FoundInWlMapping ? null : StartBucket is > 0 and <= WaterSaveSlotSummaryInfo.SlotCount ? StartBucket - 1 : null;
    public int RomOffsetBank => RomOffsetBankOverride;
    public int RomBankCount => RomBankCountOverride;
    public IReadOnlyList<byte> SaveBankMap => SaveBankMapOverride;
    public bool UsesSharedSave => UsesSharedSaveOverride;
    public byte MbcType => MbcTypeOverride;
    public int RamBankCount => RamBankCountOverride;
    public bool HasBattery => HasBatteryOverride;
    public string DirectoryTitle => DirectoryTitleOverride;

    public static string FormatBucketList(IEnumerable<int> buckets)
    {
        List<int> bucketList = buckets
            .Distinct()
            .OrderBy(bucket => bucket)
            .ToList();

        if (bucketList.Count == 0)
        {
            return string.Empty;
        }

        if (bucketList.Count == 1)
        {
            return bucketList[0].ToString("00");
        }

        bool contiguous = true;
        for (int i = 1; i < bucketList.Count; i++)
        {
            if (bucketList[i] != bucketList[i - 1] + 1)
            {
                contiguous = false;
                break;
            }
        }

        if (contiguous)
        {
            return $"{bucketList[0]:00}-{bucketList[^1]:00}";
        }

        return string.Join(", ", bucketList.Select(bucket => bucket.ToString("00")));
    }
}

public sealed class WaterCartScanResult
{
    public bool IsChm1 { get; set; }
    public bool UsesChm1Layout => WlMapping.IsDetected &&
        (WlMapping.Header.Flags & GbaMultiMenuBuilder.Services.Chm1Layout.LocalSaveFlag) != 0;
    public bool NoCartridgeDetected { get; set; }
    public bool IsBeggarMaxGbcMode { get; set; }
    public byte[]? Mbc5EightMiBRomImage { get; set; }
    public bool IsFcMbcxContent { get; set; }
    public bool IsMdMbcxContent { get; set; }
    public bool HasBeggarMaxDirectoryHeaders { get; set; }
    public bool IsChipRecognized { get; set; } = true;
    public bool IsWriteProtected { get; set; }
    public string WriteProtectionReason { get; set; } = string.Empty;
    public byte[] NorId { get; set; } = [];
    public string RawNorIdHex { get; set; } = string.Empty;
    public string ChipName { get; set; } = string.Empty;
    public string CapacitySummary { get; set; } = string.Empty;
    public string SectorSummary { get; set; } = string.Empty;
    public NorGeometryInfo Geometry { get; set; } = new();
    public List<DetectedNorGameInfo> Games { get; set; } = [];
    public WaterMenuConfigInfo MenuConfig { get; set; } = new();
    public WaterWlMappingInfo WlMapping { get; set; } = new();
    public WaterMagicMetadataSyncInfo MagicMetadataSync { get; set; } = new();
    public WaterSaveSlotSummaryInfo SaveSlotSummary { get; set; } = new();
    public WaterRuntimeStatusInfo RuntimeStatus { get; set; } = new();
    public Dictionary<int, bool> WaterLinkSaveDataByEntrySlot { get; set; } = [];
    public Dictionary<int, string> WaterLinkSaveStatusTextByEntrySlot { get; set; } = [];
    public Dictionary<int, bool> LegacySaveDataByEntrySlot { get; set; } = [];
    public Dictionary<int, string> LegacySaveStatusTextByEntrySlot { get; set; } = [];
    public Dictionary<int, bool> BatterylessSaveDataByEntrySlot { get; set; } = [];
    public Dictionary<int, string> BatterylessSaveStatusTextByEntrySlot { get; set; } = [];
    public byte[] MenuBackgroundBitmapData { get; set; } = [];
    public byte[] MenuBackgroundPaletteData { get; set; } = [];
    public bool IsUniversalGbaMenuDetected { get; set; }
    public bool IsSimplifiedNsGbaMenuDetected { get; set; }
    public GbaMenuFirstScreenPreviewInfo GbaMenuFirstScreenPreview { get; set; } = new();
    public List<WaterCartEntry> Entries { get; set; } = [];
}
