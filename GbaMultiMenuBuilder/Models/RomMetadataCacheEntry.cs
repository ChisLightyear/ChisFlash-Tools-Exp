namespace GbaMultiMenuBuilder.Models;

public sealed record RomMetadataCacheEntry(
    string RomPath,
    string FileName,
    long FileLength,
    long LastWriteUtcTicks,
    bool Map256m,
    DetectedSaveTypeKind SaveTypeKind,
    string SaveTypeIdentifier,
    string SramPatchTargetIdentifier,
    bool SramAlreadyPatched,
    bool BatterylessAlreadyPatched,
    bool BatterylessHasPayloadSpace,
    bool BatterylessCanExpand);
