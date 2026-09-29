namespace GbaMultiMenuBuilder.Models;

public sealed class GameEntryModel
{
    public Md.MdRomCandidate? MdRom { get; set; }
    public Services.ArchiveRomSource? OtherRom { get; set; }
    public string MdStatus { get; set; } = string.Empty;
    public bool IsLoadingPlaceholder { get; set; }

    public bool IsLoadingActive { get; set; }

    public bool Enabled { get; set; } = true;

    public string Title { get; set; } = string.Empty;

    public string GameTitle { get; set; } = string.Empty;

    public string MbcTypeDisplay { get; set; } = string.Empty;

    public string SaveSupportDisplay { get; set; } = string.Empty;

    public string SaveAllocationDisplay { get; set; } = string.Empty;

    public bool GbcDedicatedSaveAllocation { get; set; }

    public bool? GbcDedicatedSaveAllocationOverride { get; set; }

    public bool GbcHasBatterySave { get; set; }

    public int GbcEffectiveRamBankCount { get; set; }

    public string RamSizeDisplay { get; set; } = string.Empty;

    public string RomSizeDisplay { get; set; } = string.Empty;

    public string RomPath { get; set; } = string.Empty;

    public string SaveSlot { get; set; } = string.Empty;

    public string HiddenKeys { get; set; } = string.Empty;

    public DetectedSaveTypeKind SaveTypeKind { get; set; } = DetectedSaveTypeKind.Unknown;

    public string SaveTypeIdentifier { get; set; } = string.Empty;

    public string SaveTypeDisplay { get; set; } = string.Empty;

    public string SramPatchTargetIdentifier { get; set; } = string.Empty;

    public string SramPatchRequirementDisplay { get; set; } = string.Empty;

    public bool SramAlreadyPatched { get; set; }

    public bool ApplySramPatch { get; set; }

    public bool ApplyBatterylessPatch { get; set; }

    public bool? ApplyBatterylessPatchOverride { get; set; }

    public bool BatterylessAlreadyPatched { get; set; }

    public bool BatterylessHasPayloadSpace { get; set; }

    public bool BatterylessCanExpand { get; set; }

    public string BatterylessStatusDisplay { get; set; } = string.Empty;

    public string FileSizeDisplay { get; set; } = string.Empty;

    public string OccupiedSizeDisplay { get; set; } = string.Empty;

    public bool Map256m { get; set; }

    public int TitleFont { get; set; } = 1;

    public string? CoverPath { get; set; }

    public bool CoverCleared { get; set; }
}
