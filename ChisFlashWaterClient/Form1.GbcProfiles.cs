namespace ChisFlashWaterClient;

public partial class Form1
{
    private int? gbcProfileCapacityBytes;
    private string gbcProfilePort = string.Empty;

    private int? GetGbcProfileCapacity() => gbcProfilePort == (GetSelectedPortName() ?? string.Empty)
        ? gbcProfileCapacityBytes : null;

    private void ClearGbcProfileCapacity()
    {
        gbcProfileCapacityBytes = null;
        gbcProfilePort = string.Empty;
    }

    private void RememberGbcProfileCapacity(WaterCartScanResult result)
    {
        if (result.NoCartridgeDetected || !result.IsBeggarMaxGbcMode || result.Geometry.DeviceSizeBytes <= 0)
        {
            ClearGbcProfileCapacity();
            return;
        }
        gbcProfileCapacityBytes = result.Geometry.DeviceSizeBytes;
        gbcProfilePort = GetSelectedPortName() ?? string.Empty;
    }

    private static IReadOnlyList<BurnProfileMode> GetGbcProfileModes(int? capacity) => capacity switch
    {
        0x800000 => [BurnProfileMode.GbcUnselected, BurnProfileMode.Mbc5EightMiB, BurnProfileMode.MbcxRtcEightMiB],
        0x2000000 => [BurnProfileMode.GbcUnselected, BurnProfileMode.GbcSixteenInOne, BurnProfileMode.MbcxRtcThirtyTwoMiB, BurnProfileMode.BeggarMaxSram, BurnProfileMode.FcMbcx],
        null => [BurnProfileMode.GbcUnselected, BurnProfileMode.Mbc5EightMiB, BurnProfileMode.MbcxRtcEightMiB, BurnProfileMode.GbcSixteenInOne, BurnProfileMode.MbcxRtcThirtyTwoMiB, BurnProfileMode.BeggarMaxSram, BurnProfileMode.FcMbcx],
        _ => [BurnProfileMode.GbcUnselected],
    };

    private bool IsGbcRawImageMode() => GetSelectedBurnProfileMode() is BurnProfileMode.MbcxRtcEightMiB or BurnProfileMode.MbcxRtcThirtyTwoMiB or BurnProfileMode.GbcSixteenInOne;
    private bool IsGbcProfileUnselected() => GetSelectedBurnProfileMode() == BurnProfileMode.GbcUnselected;
    private int SelectedGbcImageCapacity() => GetSelectedBurnProfileMode() == BurnProfileMode.MbcxRtcEightMiB ? 0x800000 : 0x2000000;
}
