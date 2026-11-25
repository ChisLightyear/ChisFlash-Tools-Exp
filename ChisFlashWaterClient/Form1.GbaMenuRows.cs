using ClientText = ChisFlashWaterClient.Services.LocalizedText;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private static readonly object GbaMenuRowTag = new();
    private static readonly object GbaAddGameRowTag = new();
    private static readonly object GbaDisabledGameRowTag = new();
    private byte[]? gbaDraftMenu;
    private string gbaDraftMenuName = string.Empty;
    private bool gbaMenuRemoved;
    private bool gbaSingleGameRemoved;
    private WaterCartEntry? gbaSingleCartSource;
    private bool gbaSingleNeedsRelocation;
    private string gbaSingleRomPath = string.Empty;
    private bool updatingGbaMenuDraft;
    private bool appendingGbaListGames;
    private Button? gbaAddMenuArrow;
    private ToolStripMenuItem? gbaAddMenuCommand;

    private void BuildGbaAddMenuArrow(FlowLayoutPanel toolbar)
    {
        gbaAddMenuArrow = CreateHeaderButton("▾", 30);
        gbaAddMenuArrow.Margin = new Padding(0, 4, 4, 4);
        var dropdown = new ContextMenuStrip(components);
        gbaAddMenuCommand = new ToolStripMenuItem();
        gbaAddMenuCommand.Click += async (_, _) => await RunUiActionSafelyAsync(async () =>
        {
            if (IsMbc5MaxMode()) await SelectMbc5SlotFilesAsync(0);
            else if (IsGbcFreeWorkspace()) SelectGbcFreeMenu();
            else await SelectGbaMenuAsync();
        });
        dropdown.Items.Add(gbaAddMenuCommand);
        gbaAddMenuArrow.Click += (_, _) => dropdown.Show(gbaAddMenuArrow, 0, gbaAddMenuArrow.Height);
        toolbar.Controls.Add(gbaAddMenuArrow);
    }

    private async Task SelectGbaListSlotAsync()
    {
        if (isBusy || isAnalyzingQueueImports) return;
        if (IsSelectedGbaDisabledGameRow()) { ShowGbaDisabledGameHint(); return; }
        if (IsGbaSingleCardMode()) { await SelectSingleGbaFileAsync(); return; }
        using var picker = new OpenFileDialog { Filter = ClientText.T("client", "gba_file_filter", "GBA games (*.gba;*.zip)|*.gba;*.zip"), Multiselect = true };
        if (picker.ShowDialog(this) == DialogResult.OK) await ImportGbaListFilesAsync(picker.FileNames);
    }

    private async Task ImportGbaListFilesAsync(IEnumerable<string> paths)
    {
        if (isBusy || isAnalyzingQueueImports) return;
        if (IsSelectedGbaDisabledGameRow()) { ShowGbaDisabledGameHint(); return; }
        if (IsGbaSingleCardMode()) { await AddSingleGbaFilesAsync(paths); return; }
        appendingGbaListGames = true;
        try { await AddPendingRomFilesAsync(paths); }
        finally { appendingGbaListGames = false; }
    }

    private bool HasScannedGbaMulticart() => currentScanResult is { } scan &&
        (scan.MenuConfig.IsDetected || scan.WlMapping.IsDetected || scan.Games.Any(game => game.IsMenu && game.StartBucket == 0));
    private bool HasGbaListMenu() => !IsGbaMulticartUnavailable() && (gbaDraftMenu is not null ||
        (!gbaMenuRemoved && HasScannedGbaMulticart()));
    private bool IsSelectedGbaMenuRow() => SupportsGbaCardMode() &&
        dataGridViewRoms.SelectedRows.Count > 0 && ReferenceEquals(dataGridViewRoms.SelectedRows[0].Tag, GbaMenuRowTag);
    private bool IsGbaDisabledGameRow(int index) => SupportsGbaCardMode() && index >= 0 && index < dataGridViewRoms.Rows.Count &&
        ReferenceEquals(dataGridViewRoms.Rows[index].Tag, GbaDisabledGameRowTag);
    private bool IsSelectedGbaDisabledGameRow() => dataGridViewRoms.SelectedRows.Count > 0 && IsGbaDisabledGameRow(dataGridViewRoms.SelectedRows[0].Index);
    private string GetGbaDisabledGameReason() => IsGbaMulticartUnavailable()
        ? GbaCardText("single_profile_only", "当前为单卡类型，合卡功能不可用。", "Single-game profile selected. Multicart operations are unavailable.")
        : GbaCardText("add_menu_first", "游戏2及后续游戏不可用：添加菜单后开放。", "Games 2 and later are unavailable. Add a menu first.");
    private void ShowGbaDisabledGameHint() => labelStatus.Text = GetGbaDisabledGameReason();
    private bool CanReplaceGbaWorkspaceSelection()
    {
        if (isBusy || isAnalyzingQueueImports || dataGridViewRoms.SelectedRows.Count != 1) return false;
        if (IsChisFlash10Mode()) return chisFlash10Rom is not null || GetChisFlash10CartGame() is not null;
        if (!TryGetSelectedWorkspaceRow(out var row, out _) || row is null) return false;
        if (IsGbaSingleCardMode()) return gbaSingleRom is not null || GetGbaSingleGame() is not null;
        return row.PendingItem is { IsLoadingPlaceholder: false, ImportedPlatform: null or RomPlatform.Gba } ||
            row.CartEntry is { IsMenu: false };
    }
    private void EnsureGbaMulticartSupported()
    {
        if (IsGbaMulticartUnavailable()) throw new InvalidOperationException(GetGbaDisabledGameReason());
    }
    private void UpdateGbaSingleDragEffect(DragEventArgs args)
    {
        Point location = dataGridViewRoms.PointToClient(new Point(args.X, args.Y));
        int index = dataGridViewRoms.HitTest(location.X, location.Y).RowIndex;
        bool emptyGame = gbaSingleRom is null && GetGbaSingleGame() is null;
        bool menuRow = index >= 0 && index < dataGridViewRoms.Rows.Count && ReferenceEquals(dataGridViewRoms.Rows[index].Tag, GbaMenuRowTag);
        args.Effect = !isBusy && !isAnalyzingQueueImports && index is 0 or 1 && (menuRow || emptyGame) && !IsGbaDisabledGameRow(index) && args.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
    }
    private WaterCartEntry? GetGbaSingleGame() => gbaSingleGameRemoved ? null : gbaSingleCartSource ??= GetHeadAreaGame();
    private bool HasPendingGbaHeadRemoval() => gbaSingleGameRemoved &&
        currentScanResult?.Games.Any(game => !game.IsMenu && game.StartBucket == 0) == true;

    private int WorkspaceIndexFromGrid(int index) => index >= 0 && index < dataGridViewRoms.Rows.Count &&
        dataGridViewRoms.Rows[index].Tag is WorkspaceListRow row ? workspaceRows.IndexOf(row) : -1;
    private int GridIndexFromWorkspace(int index) => index < 0 ? -1 :
        index + (HasGbcFreeMenu() || SupportsGbaCardMode() && HasGbaListMenu() ? 1 : 0);

    private WaterCartScanResult? gbaAcknowledgedMenuOverwrite;

    private void ResetGbaMenuDraft()
    {
        gbaAcknowledgedMenuOverwrite = null;
        gbaWorkspaceClearSource = null;
        removedGbaGames.Clear();
        gbaIncludeCartGameInDraft = false;
        gbaDraftMenu = null; gbaDraftMenuName = string.Empty; gbaMenuRemoved = false;
        gbaSingleGameRemoved = false; gbaSingleCartSource = null; gbaSingleNeedsRelocation = false;
        if (gbaSingleRom is null) gbaSingleReplacementCapacityBytes = 0;
        if (chisFlash10Rom is null) chisFlash10ReplacementCapacityBytes = 0;
        gbaSingleGridActive = false;
    }

    private void AddGbaMenuGridRow()
    {
        bool pending = gbaDraftMenu is not null;
        bool unread = !pending && currentScanResult?.IsNorProbeOnly == true;
        string state = unread ? GbaCardText("config_unread", "待分析卡带", "Cart not analyzed")
            : pending ? GbaCardText("pending_menu", "待写入", "Pending") : HasGbaListMenu() ? T("value", "row_state_cart", "On Cart") : GbaCardText("menu_not_added", "未添加", "Not added");
        string title = pending ? gbaDraftMenuName : HasGbaListMenu()
            ? currentScanResult!.Games.FirstOrDefault(game => game.IsMenu)?.Title ?? GbaCardText("menu", "菜单", "Menu")
            : unread ? GbaCardText("read_config_first", "请先分析卡带", "Analyze the cart first") : GbaCardText("select_menu_hint", "双击选择菜单", "Double-click to select a menu");
        string hint = unread ? GbaCardText("read_config_hint", "请点击“分析卡带”读取目录", "Use Analyze Cart to read the directory")
            : GbaCardText("menu_row_hint", "双击选择菜单，点击“开始烧录”后写入。", "Double-click to select a menu. Start Burn writes it to the cartridge.");
        int index = dataGridViewRoms.Rows.Add(GbaCardText("menu", "菜单", "Menu"), state, pending ? T("value", "source_file", "File") : HasGbaListMenu() ? "NOR" : "—", GbaCardText("menu", "菜单", "Menu"), title,
            pending ? WaterCartScanner.DecodeGbaHeaderTitle(gbaDraftMenu!) : string.Empty,
            pending ? FormatMiBSize(gbaDraftMenu!.Length) : "—", "—", false,
            "—", "—", string.Empty);
        var row = dataGridViewRoms.Rows[index];
        row.Tag = GbaMenuRowTag; row.ReadOnly = true;
        if (!IsGbaMulticartUnavailable()) row.ContextMenuStrip = GetMenuRowContextMenu();
        row.DefaultCellStyle.BackColor = Color.FromArgb(230, 241, 252);
        foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = hint;
        if (IsGbaMulticartUnavailable())
        {
            row.Tag = GbaDisabledGameRowTag;
            row.Cells["State"].Value = ClientText.T("client", "unavailable", "Unavailable");
            row.Cells["Title"].Value = ClientText.T("client", "single_no_multicart", "This single-game chip does not support multicarts");
            row.Cells["PatchStatus"].Value = GetGbaDisabledGameReason();
            row.DefaultCellStyle.BackColor = Color.FromArgb(235, 235, 235);
            row.DefaultCellStyle.ForeColor = Color.Gray;
            row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(210, 210, 210);
            row.DefaultCellStyle.SelectionForeColor = Color.FromArgb(85, 85, 85);
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = GetGbaDisabledGameReason();
        }
    }

    private void AddGbaEmptyGameRow(int number)
    {
        int index = dataGridViewRoms.Rows.Add(GbaCardText("game", "游戏", "Game ") + number, GbaCardText("addable", "可添加", "Empty"), "—", "GBA", GbaCardText("add_game_hint", "点击添加游戏", "Click to add a game"), string.Empty,
            "—", "—", false, string.Empty, "—", string.Empty);
        dataGridViewRoms.Rows[index].Tag = GbaAddGameRowTag;
        dataGridViewRoms.Rows[index].ReadOnly = true;
    }

    private void AddGbaDisabledGameRow()
    {
        string reason = GetGbaDisabledGameReason();
        int index = dataGridViewRoms.Rows.Add(GbaCardText("game2", "游戏2", "Game 2"),
            GbaCardText("unavailable", "不可用", "Unavailable"), "—", "GBA",
            IsGbaMulticartUnavailable() ? ClientText.T("client", "single_no_multicart", "This single-game chip does not support multicarts") : GbaCardText("menu_required", "添加菜单后开放", "Available after adding a menu"), string.Empty,
            "—", "—", false, "—", "—", string.Empty);
        var row = dataGridViewRoms.Rows[index];
        row.Tag = GbaDisabledGameRowTag;
        row.ReadOnly = true;
        row.DefaultCellStyle.BackColor = Color.FromArgb(235, 235, 235);
        row.DefaultCellStyle.ForeColor = Color.Gray;
        row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(210, 210, 210);
        row.DefaultCellStyle.SelectionForeColor = Color.FromArgb(85, 85, 85);
        foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = reason;
    }

    private async Task SelectGbaMenuAsync()
    {
        EnsureGbaMulticartSupported();
        if (isBusy || isAnalyzingQueueImports) return;
        using var dialog = new Form { Text = GbaCardText("select_menu", "选择菜单", "Select Menu"), Font = Font,
            AutoScaleMode = AutoScaleMode.Dpi, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2,
            Padding = new Padding(LogicalToDeviceUnits(18)), MinimumSize = new Size(LogicalToDeviceUnits(400), 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var menu = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(14)) };
        menu.Items.AddRange(comboBoxGbaMenu.Items.Cast<object>().ToArray());
        menu.SelectedItem = HasGbaListMenu() ? comboBoxGbaMenu.SelectedItem : menu.Items[0];
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        var builtin = CreateHeaderButton(GbaCardText("use_menu", "使用所选菜单", "Use Selected Menu"), LogicalToDeviceUnits(120));
        var file = CreateHeaderButton(GbaCardText("custom_menu", "自定义菜单…", "Custom Menu…"), LogicalToDeviceUnits(120));
        var cancel = CreateHeaderButton(GbaCardText("cancel", "取消", "Cancel"), LogicalToDeviceUnits(90)); cancel.DialogResult = DialogResult.Cancel;
        foreach (var button in new[] { builtin, file, cancel })
        {
            button.Dock = DockStyle.Fill; button.MinimumSize = new Size(button.MinimumSize.Width, LogicalToDeviceUnits(38));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); actions.Controls.Add(button);
        }
        builtin.Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0);
        file.Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0); cancel.Margin = Padding.Empty;
        string? customPath = null;
        builtin.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        file.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = GbaCardText("custom_menu", "自定义菜单…", "Custom Menu…"), Filter = "GBA (*.gba)|*.gba", Multiselect = false };
            if (picker.ShowDialog(dialog) == DialogResult.OK) { customPath = picker.FileName; dialog.DialogResult = DialogResult.OK; }
        };
        layout.Controls.Add(menu, 0, 0); layout.Controls.Add(actions, 0, 1);
        dialog.Controls.Add(layout); dialog.AcceptButton = builtin; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var selection = ((GbaMenuSelectionOption)menu.SelectedItem!).Selection;
        byte[] bytes = customPath is null
            ? LoadBundledGbaMenuRom(UsesWlTargetPlanningMode(GetSelectedBurnProfileMode()), selection, out _)
            : LoadMenuRomForUpdate(customPath, UsesWlTargetPlanningMode(GetSelectedBurnProfileMode()), out _);
        await StageGbaMenuAsync(bytes, customPath is null ? menu.SelectedItem!.ToString()! : Path.GetFileName(customPath));
    }

    private async Task ImportGbaMenuFilesAsync(IEnumerable<string> paths)
    {
        EnsureGbaMulticartSupported();
        string[] files = paths.ToArray();
        if (files.Length != 1) throw new InvalidDataException(ClientText.T("client", "menu_one_file", "Import one menu ROM at a time into the menu row."));
        if (new FileInfo(files[0]).Length > 0x400000) throw new InvalidDataException(ClientText.T("client", "menu_size_limit", "The menu ROM must not exceed 4 MiB."));
        byte[] bytes = LoadMenuRomForUpdate(files[0], UsesWlTargetPlanningMode(GetSelectedBurnProfileMode()), out _);
        await StageGbaMenuAsync(bytes, Path.GetFileName(files[0]));
    }

    private Task StageGbaMenuAsync(byte[] bytes, string name) =>
        StageGbaMenuCoreAsync(bytes, name, ConfirmSingleGameExportAsync);

    private async Task<bool> ConfirmSingleGameExportAsync(WaterCartEntry head)
    {
        var choice = MessageBox.Show(this,
            ClientText.Tf("client", "export_single_before_menu", "Burning this menu will overwrite the single game {0} at address zero. Export it first?\n\nYes: select the ROM size and save a .gba file.\nNo: add the menu without exporting.\nCancel: keep the current workspace.", head.Title),
            ClientText.T("client", "export_single_before_menu_title", "Export existing game?"),
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
        return choice == DialogResult.No || choice == DialogResult.Yes && await ExportCartGbaGameAsync(head);
    }

    private WaterCartEntry? GetSingleGameAtRisk() =>
        HasActiveScanResult() && !HasScannedGbaMulticart() ? GetHeadAreaGame() : null;

    private async Task StageGbaMenuCoreAsync(byte[] bytes, string name, Func<WaterCartEntry, Task<bool>> confirmExport)
    {
        EnsureGbaMulticartSupported();
        if (isBusy || isAnalyzingQueueImports) return;
        if (bytes.Length > 0x400000 || !WaterCartScanner.HasValidGbaHeader(bytes))
            throw new InvalidDataException(ClientText.T("client", "menu_compatible_required", "Select a compatible GBA multicart menu ROM."));
        int selectedIndex = WorkspaceIndexFromGrid(dataGridViewRoms.CurrentCell?.RowIndex ?? -1);
        bool conversion = !HasGbaListMenu();
        var scan = currentScanResult;
        var head = GetSingleGameAtRisk();
        if (head is not null && !ReferenceEquals(scan, gbaAcknowledgedMenuOverwrite))
        {
            if (!await confirmExport(head) || !ReferenceEquals(scan, currentScanResult)) return;
            gbaAcknowledgedMenuOverwrite = scan;
        }
        updatingGbaMenuDraft = true;
        try
        {
            var selection = GbaMenuRomService.HasSimplifiedNsMenuSignature(bytes) ? GbaMenuSelection.Simplified
                : GbaMenuRomService.SupportsCovers(bytes) ? GbaMenuSelection.Universal : GbaMenuSelection.Legacy;
            comboBoxGbaMenu.SelectedItem = comboBoxGbaMenu.Items.Cast<GbaMenuSelectionOption>().First(option => option.Selection == selection);
            gbaDraftMenu = bytes.ToArray(); gbaDraftMenuName = name;
            if (conversion)
            {
                gbaIncludeCartGameInDraft = head is null && workspaceRows.Any(row =>
                    row.CartEntry is { } entry && (entry.FoundInMenuConfig || entry.FoundInWlMapping) && row.PendingItem is null);
                if (!gbaIncludeCartGameInDraft)
                {
                    foreach (var row in workspaceRows.Where(row => row.CartEntry?.StartBucket == 0).ToArray())
                    {
                        if (row.PendingItem is { } replacement)
                        {
                            if (!pendingImportItems.Contains(replacement)) pendingImportItems.Add(replacement);
                        }
                        else workspaceRows.Remove(row);
                    }
                }
            }
            RefreshWorkspaceGrid(selectedIndex);
        }
        finally { updatingGbaMenuDraft = false; }
        UpdateMenuBackgroundPreview(currentScanResult);
        UpdateActionButtons(isBusy);
    }

    private void RemoveGbaListSelection()
    {
        if (isBusy || isAnalyzingQueueImports) return;
        if (IsSelectedGbaDisabledGameRow()) { ShowGbaDisabledGameHint(); return; }
        if (IsSelectedGbaMenuRow())
        {
            if (gbaDraftMenu is not null)
            {
                bool restoreHead = gbaAcknowledgedMenuOverwrite is not null && !gbaSingleGameRemoved;
                gbaAcknowledgedMenuOverwrite = null;
                gbaDraftMenu = null;
                gbaDraftMenuName = string.Empty;
                if (restoreHead && GetSingleGameAtRisk() is { } head && !workspaceRows.Any(row => row.CartEntry?.StartBucket == 0))
                    workspaceRows.Insert(0, new WorkspaceListRow { CartEntry = head });
                RefreshWorkspaceGrid();
                UpdateMenuBackgroundPreview(currentScanResult);
                return;
            }
            var games = workspaceRows.Where(row => !row.IsLoadingPlaceholder).ToList();
            if (games.Count == 0 && HasGbaListMenu())
            {
                ClearGbaList();
                return;
            }

            if (games.Count > 1) throw new InvalidOperationException(ClientText.T("client", "remove_other_games_first", "Before removing the menu, remove games 2 and later, leaving only game 1."));
            var first = games.FirstOrDefault();
            if (first?.PendingItem is null && first?.CartEntry is { } cart &&
                (long)cart.LogicalBucketCount * WaterCartScanner.BucketSizeBytes > ChisFlash10Image.CapacityBytes)
                throw new InvalidOperationException(ClientText.T("client", "single_size_remove_first", "A single-game ROM cannot exceed 32 MiB."));
            gbaDraftMenu = null;
            gbaDraftMenuName = string.Empty;
            gbaMenuRemoved = true;
            gbaSingleCartSource = first?.CartEntry;
            gbaSingleNeedsRelocation = first?.CartEntry is { } source && first.PendingItem is null && (source.FoundInMenuConfig || source.FoundInWlMapping);
            gbaSingleGameRemoved = first?.CartEntry is null;
            gbaIncludeCartGameInDraft = false;
            RefreshWorkspaceGrid();
            return;
        }
        if (IsGbaSingleCardMode())
        {
            gbaSingleReplacementCapacityBytes = 0;
            gbaSingleRom = null; gbaSingleRomSave = null; gbaSingleRomName = string.Empty;
            gbaSingleGameRemoved = true; gbaSingleNeedsRelocation = false;
            gbaSingleGridActive = false; RefreshGbaCardModeUi(); return;
        }
        if (!TryGetSelectedWorkspaceRow(out var row, out int index) || row is null) return;
        if (row.PendingItem is not null) pendingImportItems.Remove(row.PendingItem);
        if (IsGbaConversionDraft() && row.CartEntry is not null && row.PendingItem is null) gbaIncludeCartGameInDraft = false;
        if (row.CartEntry is { } entry)
        {
            if (entry.FoundInMenuConfig || entry.FoundInWlMapping)
                removedGbaGames.Add(entry);
            if (!HasGbaListMenu())
            {
                gbaSingleGameRemoved = true;
                gbaSingleNeedsRelocation = false;
                if (entry.FoundInMenuConfig || entry.FoundInWlMapping) StageGbaWorkspaceClear();
            }
        }
        workspaceRows.Remove(row);
        RefreshWorkspaceGrid(GetSafeSelectionIndex(index, workspaceRows.Count));
    }

    private void ClearGbaList()
    {
        if (isBusy || isAnalyzingQueueImports) return;
        StageGbaWorkspaceClear();
        gbaAcknowledgedMenuOverwrite = null;
        gbaSingleReplacementCapacityBytes = 0;
        pendingImportItems.Clear(); workspaceRows.Clear();
        gbaDraftMenu = null; gbaDraftMenuName = string.Empty; gbaMenuRemoved = true;
        gbaSingleRom = null; gbaSingleRomName = string.Empty; gbaSingleRomSave = null;
        gbaSingleRomPath = string.Empty; gbaSingleCartSource = null;
        gbaSingleGameRemoved = true; gbaSingleNeedsRelocation = false; gbaSingleGridActive = false;
        gbaIncludeCartGameInDraft = false;
        RefreshWorkspaceGrid();
        labelStatus.Text = HasPendingGbaWorkspaceClear()
            ? GbaClearActionText() + ShellText("：待烧录", " (pending)")
            : GbaCardText("clear_local", "已取消预加入的菜单和游戏。", "Pending menu and games cancelled.");
    }

    private byte[] ReadGbaSingleSource(WaterDeviceProtocol protocol, ProgrammerKind kind, int length)
    {
        var source = GetGbaSingleGame() ?? throw new InvalidOperationException(ClientText.T("client", "no_current_game", "There is no current game in the workspace."));
        return source.FoundInMenuConfig || source.FoundInWlMapping
            ? ReadMappedGbaRomFromDevice(protocol, kind, source)
            : ReadRomRangeFromDevice(protocol, kind, 0, length);
    }

    private void UpdateGbaMenuRowActions(bool busy)
    {
        if (!SupportsGbaCardMode()) return;
        bool idle = !busy && !isAnalyzingQueueImports;
        bool menu = IsSelectedGbaMenuRow();
        buttonQueueAdd.Text = menuAddRom.Text = GbaCardText("add_game", "添加游戏", "Add Game");
        bool disabled = IsSelectedGbaDisabledGameRow();
        bool occupied = dataGridViewRoms.SelectedRows.Count > 0 && !disabled && (menu ? HasGbaListMenu() : IsGbaSingleCardMode() ? gbaSingleRom is not null || GetGbaSingleGame() is not null
            : TryGetSelectedWorkspaceRow(out _, out _));
        bool canRemoveMenu = !menu || gbaDraftMenu is not null || workspaceRows.Count(row => !row.IsLoadingPlaceholder) <= 1;
        buttonQueueAdd.Enabled = menuAddRom.Enabled = idle && !disabled &&
            (!IsGbaSingleCardMode() || menu || gbaSingleRom is null && GetGbaSingleGame() is null);
        buttonQueueRemove.Enabled = menuRemoveSelectedRom.Enabled = idle && occupied && canRemoveMenu;
        buttonQueueClear.Text = menuClearRomQueue.Text = T("ui", "clear_workspace", "Clear Workspace");
        if (!IsGbaSingleCardMode() && activeBurnCancellation is null)
        {
            // 移除后工作区已经隐藏了原条目，这里对照扫描结果判断是否仍有删除待办。
            bool removedFromCard = HasPendingGbaHeadRemoval()
                || gbaMenuRemoved && HasScannedGbaMulticart();
            SetStartBurnEnabled(idle && (removedFromCard || removedGbaGames.Count > 0 || gbaSingleNeedsRelocation ||
                pendingImportItems.Count > 0 || gbaDraftMenu is not null || HasWorkspaceChanges()));
        }
        menuClearRomQueue.ToolTipText = IsGbaMulticartUnavailable()
            ? T("ui", "clear_workspace_hint", "Remove the menu and games from the workspace; Start Burn applies on-card erasures.")
            : ShellText("清空工作区，开始烧录时清除合卡配置区。", "Clear the workspace. Start Burn clears the multicart configuration.");
        buttonQueueClear.Enabled = menuClearRomQueue.Enabled = idle && (HasGbaListMenu() || gbaSingleRom is not null || GetGbaSingleGame() is not null || workspaceRows.Count > 0);
        menuReplaceSelectedRom.Enabled = menuItemReplaceRom.Enabled = CanReplaceGbaWorkspaceSelection();
        if (IsGbaMulticartUnavailable()) comboBoxGbaMenu.Enabled = false;
        if (disabled || menu || (dataGridViewRoms.SelectedRows.Count > 0 && ReferenceEquals(dataGridViewRoms.SelectedRows[0].Tag, GbaAddGameRowTag)))
        {
            menuExportSelectedRom.Enabled = menuItemExportRom.Enabled = false;
            foreach (var item in new[] { menuExportSelectedSave, menuImportSelectedSave, menuClearSelectedSave, menuItemExportSave, menuItemImportSave, menuItemClearSave, menuDeleteSelectedMapping, menuItemDeleteMapping }) item.Enabled = false;
        }
        dataGridViewRoms.Enabled = !busy;
        if (disabled && !busy) ShowGbaDisabledGameHint();
    }
}
