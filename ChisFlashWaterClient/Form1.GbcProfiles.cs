namespace ChisFlashWaterClient;

public partial class Form1
{
    private int? gbcProfileCapacityBytes;
    private string gbcProfilePort = string.Empty;
    private bool gbcFreeProfileAutoMatched;

    private int? GetGbcProfileCapacity() => gbcProfilePort == (GetSelectedPortName() ?? string.Empty)
        ? gbcProfileCapacityBytes : null;

    private void ClearGbcProfileCapacity()
    {
        gbcProfileCapacityBytes = null;
        gbcProfilePort = string.Empty;
        gbcFreeProfileAutoMatched = false;
    }

    private void RememberGbcProfileCapacity(WaterCartScanResult result)
    {
        // 盖中盖口径：容量读不到 / 判定无卡都不再清空卡型候选，扫描结果照样可用。
        if (!result.IsBeggarMaxGbcMode)
        {
            ClearGbcProfileCapacity();
            return;
        }
        // 全 00／全 FF 的 CFI 不用于筛选卡型，烧录时按对应流程检查空间。
        gbcProfileCapacityBytes = result.Geometry is { IsCfiWindowBlank: false, DeviceSizeBytes: > 0 } geometry
            ? geometry.DeviceSizeBytes
            : null;
        gbcProfilePort = GetSelectedPortName() ?? string.Empty;
    }

    private static IReadOnlyList<BurnProfileMode> GetGbcProfileModes() =>
        [BurnProfileMode.GbcUnselected, BurnProfileMode.Mbc5EightMiB, BurnProfileMode.MbcxRtcEightMiB,
         BurnProfileMode.GbcSixteenInOne, BurnProfileMode.MbcxRtcThirtyTwoMiB, BurnProfileMode.BeggarMaxSram, BurnProfileMode.BeggarMaxLegacyFree, BurnProfileMode.FcMbcx];

    private static bool MatchesGbcFreeMulticart(WaterCartScanResult scan) => scan is
    {
        IsBeggarMaxGbcMode: true, IsChipRecognized: true, HasBeggarMaxDirectoryHeaders: true,
        NoCartridgeDetected: false, IsNorProbeOnly: false, IsFcMbcxContent: false, IsMdMbcxContent: false,
        Geometry: { IsCfiWindowBlank: false, DeviceSizeBytes: 0x2000000 },
    } && scan.Entries.Any(entry => !entry.IsMenu && entry.RomOffsetBank >= 0 && entry.RomBankCount > 0);

    private bool IsGbcRawImageMode() => GetSelectedBurnProfileMode() is BurnProfileMode.MbcxRtcEightMiB or BurnProfileMode.MbcxRtcThirtyTwoMiB;
    private bool IsGbcProfileUnselected() => GetSelectedBurnProfileMode() == BurnProfileMode.GbcUnselected;
    private int SelectedGbcImageCapacity() => GetSelectedBurnProfileMode() == BurnProfileMode.MbcxRtcEightMiB ? 0x800000 : 0x2000000;
}
