namespace GbaMultiMenuBuilder.Models;

public sealed record BuildGameEntry(
    bool Enabled,
    string Title,
    string RomPath,
    int? SaveSlot,
    ushort HiddenKeysMask,
    bool ApplySramPatch,
    bool SramReadyForBatteryless,
    bool ApplyBatterylessPatch,
    bool BatterylessAlreadyPatched,
    bool BatterylessRequiresExpansion,
    DetectedSaveTypeKind SaveTypeKind,
    bool GbcDedicatedSaveAllocation,
    bool Map256m,
    int TitleFont,
    string? CoverPath = null,
    bool CoverCleared = false);
