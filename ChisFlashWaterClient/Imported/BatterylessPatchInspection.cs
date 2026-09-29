namespace GbaMultiMenuBuilder.Models;

public sealed record BatterylessPatchInspection(
    bool AlreadyPatched,
    bool HasPayloadSpace,
    bool CanExpand)
{
    public bool CanInstallPayload => HasPayloadSpace || CanExpand;

    public bool RequiresExpansion => !HasPayloadSpace && CanExpand;
}
