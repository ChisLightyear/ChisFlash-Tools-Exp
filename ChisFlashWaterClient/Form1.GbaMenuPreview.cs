using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private bool currentGbaPreviewUsesSelectedMenu;
    private bool currentPreviewShowsSingleGbaHint;

    private bool IsSingleGbaMenuPreviewDisabled() => IsGbaSingleCardMode() || IsChisFlash10Mode();

    private bool TryShowSingleGbaMenuPreviewHint()
    {
        if (!IsSingleGbaMenuPreviewDisabled()) return false;
        currentPreviewShowsSingleGbaHint = true;
        currentGbaPreviewUsesSelectedMenu = false;
        currentPreviewUsesGbcListMode = false;
        currentPreviewUsesGbaFirstScreenMode = false;
        labelMenuBackgroundPreviewCaption.Text = T("ui", "menu_preview", "Menu Preview");
        UpdateMenuBackgroundPreviewLayout();
        SetMenuBackgroundPreviewState(null, true);
        labelMenuBackgroundPreviewPlaceholder.Text = T("ui", "single_card_preview_hint", "This is a single-game cartridge");
        labelMenuBackgroundPreviewPlaceholder.Cursor = Cursors.Default;
        return true;
    }

    private void RefreshSingleGbaMenuPreviewState()
    {
        if (pictureBoxMenuBackgroundPreview is not null &&
            currentPreviewShowsSingleGbaHint != IsSingleGbaMenuPreviewDisabled())
            UpdateMenuBackgroundPreview(currentScanResult);
    }

    private WaterCartScanResult BuildSelectedGbaMenuPreview(WaterCartScanResult? source)
    {
        bool usesWlLayout = source is not null ? UsesWlMode(source) : UsesSelectedWaterLinkTargetMode();
        byte[] menu = LoadSelectedGbaMenuRom(usesWlLayout, out _);
        var preview = new WaterCartScanResult
        {
            MenuConfig = source?.MenuConfig ?? new(),
            Entries = source?.Entries ?? [],
            IsUniversalGbaMenuDetected = GbaMenuRomService.SupportsCovers(menu),
            IsSimplifiedNsGbaMenuDetected = GbaMenuRomService.HasSimplifiedNsMenuSignature(menu),
        };
        preview.GbaMenuFirstScreenPreview = GbaMenuFirstScreenPreviewCodec.Decode(menu, preview.MenuConfig.Items);
        if (TryResolveMenuBackgroundRegion(menu, usesWlLayout, out int offset))
        {
            preview.MenuBackgroundBitmapData = menu.AsSpan(offset, BackgroundImageEncoder.Width * BackgroundImageEncoder.Height).ToArray();
            preview.MenuBackgroundPaletteData = menu.AsSpan(offset + preview.MenuBackgroundBitmapData.Length, 0x200).ToArray();
        }
        return preview;
    }

    private bool TryPreviewSelectedGbaMenu(WaterCartScanResult? source)
    {
        currentGbaPreviewUsesSelectedMenu = source is null || !source.MenuConfig.IsDetected || HasGbaMenuSelectionChanges(source);
        if (!currentGbaPreviewUsesSelectedMenu) return false;
        currentPreviewUsesGbaFirstScreenMode = false;
        labelMenuBackgroundPreviewCaption.Text = string.Format(T("ui", "selected_gba_menu_preview", "Selected menu: {0} (not written)"), comboBoxGbaMenu.Text);
        UpdateMenuBackgroundPreviewLayout();
        try
        {
            WaterCartScanResult preview = BuildSelectedGbaMenuPreview(source);
            currentPreviewUsesGbaFirstScreenMode = preview.IsUniversalGbaMenuDetected;
            Bitmap? image = currentPreviewUsesGbaFirstScreenMode ? RenderGbaMenuFirstScreenPreview(preview)
                : preview.MenuBackgroundBitmapData.Length == BackgroundImageEncoder.Width * BackgroundImageEncoder.Height
                    ? DecodeMenuBackgroundPreview(preview.MenuBackgroundBitmapData, preview.MenuBackgroundPaletteData) : null;
            SetMenuBackgroundPreviewState(image, true);
        }
        catch
        {
            SetMenuBackgroundPreviewState(null, true);
            labelMenuBackgroundPreviewPlaceholder.Text = T("ui", "selected_gba_menu_preview_unavailable", "Selected menu resource unavailable");
        }
        return true;
    }
}
