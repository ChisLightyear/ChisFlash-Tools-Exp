namespace ChisFlashWaterClient;

public partial class Form1
{
    private static readonly object GbcFreeMenuRowTag = new();
    private GbcMenuLanguageMode? gbcFreeMenuDraft;
    private bool gbcFreeMenuHidden;

    private bool IsGbcFreeWorkspace() => IsBeggarMaxMode(GetSelectedBurnProfileMode());

    private bool HasGbcFreeMenu() => IsGbcFreeWorkspace() &&
        (gbcFreeMenuDraft.HasValue || !gbcFreeMenuHidden && currentScanResult?.HasBeggarMaxDirectoryHeaders == true);

    private bool IsSelectedGbcFreeMenuRow() => IsGbcFreeWorkspace() &&
        dataGridViewRoms.SelectedRows.Count > 0 && ReferenceEquals(dataGridViewRoms.SelectedRows[0].Tag, GbcFreeMenuRowTag);

    private bool ShouldEraseGbcFreeMenu(IReadOnlyList<WorkspaceListRow> workspaceSnapshot) =>
        IsGbcFreeWorkspace() && gbcFreeMenuHidden && !gbcFreeMenuDraft.HasValue &&
        currentScanResult?.HasBeggarMaxDirectoryHeaders == true && workspaceSnapshot.Count == 0;

    private bool CanRemoveGbcFreeMenu() => IsSelectedGbcFreeMenuRow() &&
        (workspaceRows.Count == 0 || gbcFreeMenuDraft.HasValue);

    private void ResetGbcFreeMenuDraft()
    {
        gbcFreeMenuDraft = null;
        gbcFreeMenuHidden = false;
    }

    private void SelectGbcFreeMenu()
    {
        if (isBusy || isAnalyzingQueueImports) return;
        string title = GbaCardText("select_menu", "选择菜单", "Select Menu");
        using var dialog = new Form
        {
            Text = title, Font = Font, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        var layout = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12),
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
        };
        var selection = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = LogicalToDeviceUnits(270) };
        selection.Items.AddRange(comboBoxGbcMenuLanguage.Items.Cast<object>().ToArray());
        selection.SelectedItem = selection.Items.Cast<GbcMenuLanguageOption>()
            .First(option => option.Mode == GetSelectedGbcMenuLanguageMode());
        layout.Controls.Add(new Label { Text = T("ui", "gbc_menu_language", "Menu language"), AutoSize = true, AccessibleName = title });
        layout.Controls.Add(selection);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        var ok = new Button { Text = T("dialog", "button_ok", "OK"), AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = T("dialog", "button_cancel", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([ok, cancel]);
        layout.Controls.Add(buttons);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) == DialogResult.OK && selection.SelectedItem is GbcMenuLanguageOption option)
            StageGbcFreeMenu(option.Mode);
    }

    private void StageGbcFreeMenu(GbcMenuLanguageMode mode)
    {
        int selectedIndex = WorkspaceIndexFromGrid(dataGridViewRoms.CurrentCell?.RowIndex ?? -1);
        gbcFreeMenuDraft = mode;
        RefreshWorkspaceGrid(selectedIndex);
        UpdateMenuBackgroundPreview(currentScanResult);
    }

    private void AddGbcFreeMenuRow()
    {
        bool pending = gbcFreeMenuDraft.HasValue;
        string menu = GbaCardText("menu", "菜单", "Menu");
        string title = "MAX/MBCX " + menu;
        if (gbcFreeMenuDraft is { } mode)
            title += " · " + (mode == GbcMenuLanguageMode.English
                ? T("ui", "gbc_menu_language_en", "English") : T("ui", "gbc_menu_language_zh", "Chinese"));
        var row = dataGridViewRoms.Rows[dataGridViewRoms.Rows.Add(menu,
            pending ? GbaCardText("pending_menu", "待写入", "Pending") : T("value", "row_state_cart", "On Cart"),
            pending ? T("value", "source_file", "File") : "NOR", menu, title)];
        row.Tag = GbcFreeMenuRowTag;
        row.ReadOnly = true;
        row.ContextMenuStrip = GetMenuRowContextMenu();
        row.DefaultCellStyle.BackColor = Color.FromArgb(230, 241, 252);
    }

    private void ClearGbcFreeWorkspace()
    {
        pendingImportItems.Clear();
        workspaceRows.Clear();
        gbcFreeMenuDraft = null;
        gbcFreeMenuHidden = true;
        RefreshWorkspaceGrid();
        UpdateMenuBackgroundPreview(currentScanResult);
        labelStatus.Text = currentScanResult?.HasBeggarMaxDirectoryHeaders == true
            ? ShellText("工作区已清空，开始烧录时擦除菜单和配置区。", "Workspace cleared. Start Burn erases the menu and configuration.")
            : ShellText("工作区已清空。", "Workspace cleared.");
    }

    private void RemoveGbcFreeSelection()
    {
        if (isBusy || isAnalyzingQueueImports) return;
        if (CanRemoveGbcFreeMenu())
        {
            if (workspaceRows.Count == 0)
            {
                ClearGbcFreeWorkspace();
                return;
            }

            gbcFreeMenuDraft = null;
        }
        else if (TryGetSelectedWorkspaceRow(out var row, out int index) && row is not null)
        {
            if (row.PendingItem is not null) pendingImportItems.Remove(row.PendingItem);
            workspaceRows.RemoveAt(index);
            RefreshWorkspaceGrid(GetSafeSelectionIndex(index, workspaceRows.Count));
            UpdateMenuBackgroundPreview(currentScanResult);
            return;
        }
        RefreshWorkspaceGrid();
        UpdateMenuBackgroundPreview(currentScanResult);
    }

    private bool CanReplaceGbcFreeSelection() => !isBusy && !isAnalyzingQueueImports &&
        TryGetSelectedWorkspaceRow(out var row, out _) && row is { IsLoadingPlaceholder: false } &&
        (row.PendingItem?.ImportedPlatform == GbaMultiMenuBuilder.Services.RomPlatform.Gbc ||
         row.CartEntry is { IsMenu: false, RomOffsetBank: >= 0 });
}
