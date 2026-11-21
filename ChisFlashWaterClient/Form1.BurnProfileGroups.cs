namespace ChisFlashWaterClient;

public partial class Form1
{
    private static readonly BurnProfileMode[] GbaSingleProfileOrder =
    [
        BurnProfileMode.GbaSingle,
        BurnProfileMode.ChisFlashFram10,
        BurnProfileMode.ChisFlashFlash11,
        BurnProfileMode.ChisFlashFlash11Rtc,
        BurnProfileMode.ChisFlashFram12,
        BurnProfileMode.ChisFlash10,
        BurnProfileMode.ChisFlash11VRealClock,
        BurnProfileMode.ChisFlash11VVirtualClock,
        BurnProfileMode.ChisFlash12V,
    ];

    private static bool IsGbaSingleProfile(BurnProfileMode mode) => GbaSingleProfileOrder.Contains(mode);

    private bool IsGbaSingleProfileSelected() => GetSelectedBeggarCartMode() == BeggarCartMode.Gba && IsGbaSingleProfile(GetSelectedBurnProfileMode());
    private bool IsGbaMulticartUnavailable() => IsGbaSingleGameOnlyHardware() || IsGbaSingleProfileSelected();

    private IEnumerable<BurnProfileModeOption> GetGbaSingleProfileOptions()
    {
        foreach (var mode in GbaSingleProfileOrder)
            yield return new(GetBurnProfileModeDisplayName(mode), mode);
    }

    private void BindBurnProfileOptions(BurnProfileModeOption? analysisPrompt = null)
    {
        if (GetSelectedBeggarCartMode() != BeggarCartMode.Gba)
        {
            comboBoxBurnProfileMode.DataSource = burnProfileModeOptions.ToList();
            return;
        }
        var options = new List<BurnProfileModeOption>();
        if (analysisPrompt is not null) options.Add(analysisPrompt);
        var multicart = burnProfileModeOptions.Where(option => !IsGbaSingleProfile(option.Mode)).ToList();
        if (multicart.Count > 0)
        {
            options.Add(new(GbaCardText("multicart_group", "——合卡——", "—— Multicart ——"), (BurnProfileMode)(-1), true, false));
            options.AddRange(multicart);
        }
        var single = burnProfileModeOptions.Where(option => IsGbaSingleProfile(option.Mode)).ToList();
        if (single.Count > 0)
        {
            options.Add(new(GbaCardText("single_group", "——单卡——", "—— Single game ——"), (BurnProfileMode)(-1), true, false));
            options.AddRange(single);
        }
        comboBoxBurnProfileMode.DataSource = options;
    }
}
