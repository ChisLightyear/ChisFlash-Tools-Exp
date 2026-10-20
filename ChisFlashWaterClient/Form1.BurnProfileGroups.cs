namespace ChisFlashWaterClient;

public partial class Form1
{
    private static bool IsGbaSingleProfile(BurnProfileMode mode) => mode is BurnProfileMode.GbaSingle or BurnProfileMode.ChisFlash10 or
        BurnProfileMode.ChisFlashFram10 or BurnProfileMode.ChisFlashFlash11 or BurnProfileMode.ChisFlashFlash11Rtc or BurnProfileMode.ChisFlashFram12;

    private bool IsGbaSingleProfileSelected() => GetSelectedBeggarCartMode() == BeggarCartMode.Gba && IsGbaSingleProfile(GetSelectedBurnProfileMode());
    private bool IsGbaMulticartUnavailable() => IsGbaSingleGameOnlyHardware() || IsGbaSingleProfileSelected();

    private IEnumerable<BurnProfileModeOption> GetGbaSingleProfileOptions()
    {
        foreach (var mode in new[] { BurnProfileMode.GbaSingle, BurnProfileMode.ChisFlashFram10,
            BurnProfileMode.ChisFlashFlash11, BurnProfileMode.ChisFlashFlash11Rtc, BurnProfileMode.ChisFlashFram12, BurnProfileMode.ChisFlash10 })
            yield return new(GetBurnProfileModeDisplayName(mode), mode);
    }

    private void BindBurnProfileOptions()
    {
        if (GetSelectedBeggarCartMode() != BeggarCartMode.Gba)
        {
            comboBoxBurnProfileMode.DataSource = burnProfileModeOptions.ToList();
            return;
        }
        var options = new List<BurnProfileModeOption>();
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
            options.Add(new("ChisFlash 1.1V（待确认）", (BurnProfileMode)(-2), false, false));
        }
        comboBoxBurnProfileMode.DataSource = options;
    }
}
