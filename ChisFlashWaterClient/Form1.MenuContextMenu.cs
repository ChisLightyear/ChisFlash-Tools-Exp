namespace ChisFlashWaterClient;

public partial class Form1
{
    private ContextMenuStrip? menuRowContextMenu;

    private ContextMenuStrip GetMenuRowContextMenu()
    {
        if (menuRowContextMenu is not null) return menuRowContextMenu;
        menuRowContextMenu = new ContextMenuStrip(components);
        var changeMenu = new ToolStripMenuItem();
        menuRowContextMenu.Items.Add(changeMenu);
        menuRowContextMenu.Opening += (_, _) =>
        {
            changeMenu.Text = GbaCardText("change_menu", "换菜单", "Change Menu");
            changeMenu.Enabled = !isBusy && !isAnalyzingQueueImports &&
                (IsSelectedGbaMenuRow() || IsMbc5FixedSlotMode() && SelectedMbc5Slot() == 0);
        };
        changeMenu.Click += async (_, _) => await RunUiActionSafelyAsync(async () =>
        {
            if (isBusy || isAnalyzingQueueImports) return;
            if (IsSelectedGbaMenuRow()) await SelectGbaMenuAsync();
            else if (IsMbc5FixedSlotMode() && SelectedMbc5Slot() == 0) await SelectMbc5FilesAsync();
        });
        return menuRowContextMenu;
    }
}
