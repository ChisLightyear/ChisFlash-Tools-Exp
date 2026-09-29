using System;

namespace GbaMultiMenuBuilder.Models;

public sealed record BuildLayoutEstimate(
    int UsedBytes,
    int FreeBytes,
    int FlashSize,
    int IncludedGameCount,
    int SkippedGameCount,
    int ReservedSystemBytes,
    int UsedPayloadBytes,
    int PayloadCapacityBytes)
{
    public int OverflowBytes => Math.Max(0, UsedPayloadBytes - PayloadCapacityBytes);

    public bool ExceedsCapacity => OverflowBytes > 0;
}
