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

    private async Task SelectGbaListSlotAsync()
    {
        if (isBusy || isAnalyzingQueueImports) return;
        if (IsSelectedGbaDisabledGameRow()) { ShowGbaDisabledGameHint(); return; }
        if (IsSelectedGbaMenuRow()) { await SelectGbaMenuAsync(); return; }
        if (IsGbaSingleCardMode()) { await SelectSingleGbaFileAsync(); return; }
        bool replace = TryGetSelectedWorkspaceRow(out _, out _);
        using var picker = new OpenFileDialog { Filter = "GBA游戏 (*.gba;*.zip)|*.gba;*.zip", Multiselect = !replace };
        if (picker.ShowDialog(this) == DialogResult.OK) await ImportGbaListFilesAsync(picker.FileNames);
    }

    private async Task ImportGbaListFilesAsync(IEnumerable<string> paths)
    {
        if (isBusy || isAnalyzingQueueImports) return;
        if (IsSelectedGbaDisabledGameRow()) { ShowGbaDisabledGameHint(); return; }
        if (IsGbaDirectoryDamaged()) throw new InvalidOperationException("目录异常，不能修改游戏列表。");
        if (IsSelectedGbaMenuRow()) { await ImportGbaMenuFilesAsync(paths); return; }
        if (IsGbaSingleCardMode()) { await AddSingleGbaFilesAsync(paths); return; }
        if (!TryGetSelectedWorkspaceRow(out var row, out int index) || row is null)
        {
            appendingGbaListGames = true;
            try { await AddPendingRomFilesAsync(paths); }
            finally { appendingGbaListGames = false; }
            return;
        }
        string[] files = paths.ToArray();
        if (files.Length != 1) throw new InvalidDataException("替换游戏槽每次只允许导入一个游戏；请在末尾空行添加多个游戏。");
        var imports = await Task.Run(() => CreatePendingRomImportItemsFromSource(files[0], false));
        if (imports.Items.Count != 1) throw new InvalidDataException("替换游戏槽需要一个游戏。");
        var pending = imports.Items[0];
        if (pending.ImportedPlatform is not (null or RomPlatform.Gba) || !WaterCartScanner.HasValidGbaHeader(File.ReadAllBytes(pending.FilePath)))
            throw new InvalidDataException("请选择有效的GBA游戏ROM。");
        var mode = GetSelectedBurnProfileMode();
        int length = GetEstimatedPreparedRomLengthBytes(pending, pending.FileSizeBytes, mode);
        if (!IsGbaConversionDraft() && row.CartEntry is { } cart && EstimateWorkspaceAllocatedSizeBytes(pending, length, mode) > GetCartEntryAllocatedSizeBytes(cart))
            throw new InvalidDataException("替换游戏超过当前映射的容量，请添加到空游戏行。");
        var previous = row.PendingItem;
        row.PendingItem = pending;
        if (!TryValidateWorkspaceCapacity(out string reason)) { row.PendingItem = previous; throw new InvalidDataException(reason); }
        if (previous is not null) pendingImportItems.Remove(previous);
        pendingImportItems.Add(pending);
        if (IsGbaConversionDraft() && row.CartEntry is not null) { gbaKeepCurrentGame!.Checked = false; row.CartEntry = null; }
        RefreshWorkspaceGrid(index);
    }

    private bool IsGbaDirectoryDamaged() => SupportsGbaCardMode() && !IsGbaMulticartUnavailable() &&
        currentScanResult is { MenuConfig.IsDetected: false, WlMapping.IsDetected: true };
    private bool HasGbaListMenu() => !IsGbaMulticartUnavailable() && (gbaDraftMenu is not null ||
        (!gbaMenuRemoved && currentScanResult?.MenuConfig.IsDetected == true));
    private bool IsSelectedGbaMenuRow() => SupportsGbaCardMode() &&
        dataGridViewRoms.SelectedRows.Count > 0 && ReferenceEquals(dataGridViewRoms.SelectedRows[0].Tag, GbaMenuRowTag);
    private bool IsGbaDisabledGameRow(int index) => SupportsGbaCardMode() && index >= 0 && index < dataGridViewRoms.Rows.Count &&
        ReferenceEquals(dataGridViewRoms.Rows[index].Tag, GbaDisabledGameRowTag);
    private bool IsSelectedGbaDisabledGameRow() => dataGridViewRoms.SelectedRows.Count > 0 && IsGbaDisabledGameRow(dataGridViewRoms.SelectedRows[0].Index);
    private string GetGbaDisabledGameReason() => IsGbaMulticartUnavailable()
        ? GbaCardText("single_profile_only", "当前为单卡类型，合卡功能不可用。", "Single-game profile selected. Multicart operations are unavailable.")
        : GbaCardText("add_menu_first", "游戏2及后续游戏不可用：添加菜单后开放。", "Games 2 and later are unavailable. Add a menu first.");
    private void ShowGbaDisabledGameHint() => labelStatus.Text = GetGbaDisabledGameReason();
    private void EnsureGbaMulticartSupported()
    {
        if (IsGbaMulticartUnavailable()) throw new InvalidOperationException(GetGbaDisabledGameReason());
    }
    private void UpdateGbaSingleDragEffect(DragEventArgs args)
    {
        Point location = dataGridViewRoms.PointToClient(new Point(args.X, args.Y));
        int index = dataGridViewRoms.HitTest(location.X, location.Y).RowIndex;
        args.Effect = !isBusy && !isAnalyzingQueueImports && index is 0 or 1 && !IsGbaDisabledGameRow(index) && args.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
    }
    private WaterCartEntry? GetGbaSingleGame() => gbaSingleGameRemoved ? null : gbaSingleCartSource ?? GetHeadAreaGame();
    private int WorkspaceIndexFromGrid(int index) => index >= 0 && index < dataGridViewRoms.Rows.Count &&
        dataGridViewRoms.Rows[index].Tag is WorkspaceListRow row ? workspaceRows.IndexOf(row) : -1;
    private int GridIndexFromWorkspace(int index) => index < 0 ? -1 : index + (SupportsGbaCardMode() ? 1 : 0);

    private void ResetGbaMenuDraft()
    {
        gbaDraftMenu = null; gbaDraftMenuName = string.Empty; gbaMenuRemoved = false;
        gbaSingleGameRemoved = false; gbaSingleCartSource = null; gbaSingleNeedsRelocation = false;
        gbaSingleGridActive = false;
    }

    private void AddGbaMenuGridRow()
    {
        bool damaged = IsGbaDirectoryDamaged();
        bool pending = gbaDraftMenu is not null;
        bool unread = !pending && currentScanResult?.IsNorProbeOnly == true;
        string state = unread ? "未分析" : damaged ? "目录异常" : pending ? "待写入" : HasGbaListMenu() ? "卡内" : "未添加";
        string title = pending ? gbaDraftMenuName : HasGbaListMenu()
            ? currentScanResult!.Games.FirstOrDefault(game => game.IsMenu)?.Title ?? "合卡菜单" : unread ? "菜单配置尚未分析" : "双击添加菜单";
        int index = dataGridViewRoms.Rows.Add("菜单", state, pending ? "文件" : HasGbaListMenu() ? "NOR" : "—", "菜单", title,
            pending ? WaterCartScanner.DecodeGbaHeaderTitle(gbaDraftMenu!) : string.Empty,
            pending ? FormatMiBSize(gbaDraftMenu!.Length) : "—", "—", false,
            unread ? "请使用“卡带 → 分析配置”读取目录" : damaged ? "已识别碎片表，但缺少有效目录；请先处理目录异常" : "菜单留空为单卡；添加菜单后开放其他游戏", "—", string.Empty);
        var row = dataGridViewRoms.Rows[index];
        row.Tag = GbaMenuRowTag; row.ReadOnly = true;
        row.DefaultCellStyle.BackColor = Color.FromArgb(230, 241, 252);
        foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = "菜单起点：0x000000；本地操作不会立即写入卡带。";
        if (IsGbaMulticartUnavailable())
        {
            row.Tag = GbaDisabledGameRowTag;
            row.Cells["State"].Value = "不可用";
            row.Cells["Title"].Value = "单卡芯片不支持合卡";
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
        int index = dataGridViewRoms.Rows.Add("游戏" + number, "可添加", "—", "GBA", "双击添加游戏", string.Empty,
            "—", "—", false, string.Empty, "—", string.Empty);
        dataGridViewRoms.Rows[index].Tag = GbaAddGameRowTag;
        dataGridViewRoms.Rows[index].ReadOnly = true;
    }

    private void AddGbaDisabledGameRow()
    {
        string reason = GetGbaDisabledGameReason();
        int index = dataGridViewRoms.Rows.Add(GbaCardText("game2", "游戏2", "Game 2"),
            GbaCardText("unavailable", "不可用", "Unavailable"), "—", "GBA",
            IsGbaMulticartUnavailable() ? "单卡芯片不支持合卡" : GbaCardText("menu_required", "添加菜单后开放", "Available after adding a menu"), string.Empty,
            "—", "—", false, reason, "—", string.Empty);
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
        if (IsGbaDirectoryDamaged()) throw new InvalidOperationException("已识别碎片表但目录异常，请先处理目录，不能按单卡新建合卡。");
        using var dialog = new Form { Text = "添加／替换菜单", Width = 420, Height = 180,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var menu = new ComboBox { Left = 18, Top = 18, Width = 365, DropDownStyle = ComboBoxStyle.DropDownList };
        menu.Items.AddRange(comboBoxGbaMenu.Items.Cast<object>().ToArray()); menu.SelectedItem = comboBoxGbaMenu.SelectedItem;
        var builtin = new Button { Text = "使用所选菜单", Left = 18, Top = 66, Width = 120 };
        var file = new Button { Text = "从文件导入…", Left = 145, Top = 66, Width = 120 };
        var cancel = new Button { Text = "取消", Left = 278, Top = 66, Width = 105, DialogResult = DialogResult.Cancel };
        string? customPath = null;
        builtin.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        file.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "GBA菜单 (*.gba)|*.gba", Multiselect = false };
            if (picker.ShowDialog(dialog) == DialogResult.OK) { customPath = picker.FileName; dialog.DialogResult = DialogResult.OK; }
        };
        dialog.Controls.AddRange([menu, builtin, file, cancel]); dialog.AcceptButton = builtin; dialog.CancelButton = cancel;
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
        if (files.Length != 1) throw new InvalidDataException("菜单行每次只允许导入一个菜单ROM。");
        if (new FileInfo(files[0]).Length > 0x400000) throw new InvalidDataException("菜单ROM不能超过4 MiB。");
        byte[] bytes = LoadMenuRomForUpdate(files[0], UsesWlTargetPlanningMode(GetSelectedBurnProfileMode()), out _);
        await StageGbaMenuAsync(bytes, Path.GetFileName(files[0]));
    }

    private Task StageGbaMenuAsync(byte[] bytes, string name)
    {
        EnsureGbaMulticartSupported();
        if (isBusy || isAnalyzingQueueImports) return Task.CompletedTask;
        if (IsGbaDirectoryDamaged()) throw new InvalidOperationException("目录异常，不能新建合卡草稿。");
        if (bytes.Length > 0x400000 || !WaterCartScanner.HasValidGbaHeader(bytes) ||
            (!GbaMenuRomService.SupportsCovers(bytes) && !TryResolveMenuBackgroundRegion(bytes, false, out _)))
            throw new InvalidDataException("请选择兼容的GBA合卡菜单ROM。");
        // 先准备游戏条目，校验失败时保留原单卡工作区。
        PendingRomImportItem? first = IsGbaSingleCardMode() && gbaSingleRom is not null
            ? CreatePendingRomImportItem(gbaSingleRomPath, isGbcWorkspaceModeOverride: false) : null;
        bool conversion = !HasGbaListMenu();
        updatingGbaMenuDraft = true;
        try
        {
            var selection = GbaMenuRomService.HasSimplifiedNsMenuSignature(bytes) ? GbaMenuSelection.Simplified
                : GbaMenuRomService.SupportsCovers(bytes) ? GbaMenuSelection.Universal : GbaMenuSelection.Legacy;
            comboBoxGbaMenu.SelectedItem = comboBoxGbaMenu.Items.Cast<GbaMenuSelectionOption>().First(option => option.Selection == selection);
            gbaDraftMenu = bytes.ToArray(); gbaDraftMenuName = name;
            if (conversion)
            {
                gbaKeepCurrentGame!.Checked = first is null && GetGbaSingleGame() is not null;
                if (first is not null)
                {
                    pendingImportItems.Clear(); pendingImportItems.Add(first);
                    gbaSingleRom = null; gbaSingleRomSave = null; gbaSingleRomName = string.Empty;
                }
                RebuildWorkspaceRows();
            }
            else RefreshWorkspaceGrid();
        }
        finally { updatingGbaMenuDraft = false; }
        UpdateMenuBackgroundPreview(currentScanResult);
        UpdateActionButtons(isBusy);
        return Task.CompletedTask;
    }

    private void RemoveGbaListSelection()
    {
        if (isBusy || isAnalyzingQueueImports) return;
        if (IsSelectedGbaDisabledGameRow()) { ShowGbaDisabledGameHint(); return; }
        if (IsGbaDirectoryDamaged()) throw new InvalidOperationException("目录异常，不能修改游戏列表。");
        if (IsSelectedGbaMenuRow())
        {
            var games = IsGbaSingleCardMode() ? new List<WorkspaceListRow>() : workspaceRows.Where(row => !row.IsLoadingPlaceholder).ToList();
            if (games.Count > 1) throw new InvalidOperationException("移除菜单前，请先移除游戏2及后面的游戏，只保留游戏1。");
            var first = games.FirstOrDefault();
            byte[]? single = first?.PendingItem is { } pending ? File.ReadAllBytes(pending.FilePath) : null;
            if (single is not null && !WaterCartScanner.HasValidGbaHeader(single)) throw new InvalidDataException("游戏1的GBA头部无效。");
            if (single?.Length > ChisFlash10Image.CapacityBytes || (first?.PendingItem is null && first?.CartEntry is { } cart &&
                (long)cart.LogicalBucketCount * WaterCartScanner.BucketSizeBytes > ChisFlash10Image.CapacityBytes))
                throw new InvalidOperationException("单卡游戏不能超过32 MiB，请先替换游戏1。");
            gbaDraftMenu = null; gbaDraftMenuName = string.Empty; gbaMenuRemoved = true;
            if (first is not null)
            {
                gbaSingleCartSource = first.CartEntry;
                gbaSingleNeedsRelocation = first.PendingItem is null && first.CartEntry is not null && currentScanResult?.MenuConfig.IsDetected == true;
                gbaSingleGameRemoved = false;
                if (single is not null)
                {
                    gbaSingleRom = single; gbaSingleRomPath = first.PendingItem!.FilePath;
                    gbaSingleRomName = first.PendingItem.FileName; gbaSingleRomSave = ChisFlash10Image.Analyze(single);
                }
            }
            else gbaSingleGameRemoved = true;
            pendingImportItems.Clear(); workspaceRows.Clear();
            if (gbaKeepCurrentGame is not null) gbaKeepCurrentGame.Checked = false;
            gbaSingleGridActive = false; RefreshGbaCardModeUi();
            return;
        }
        if (IsGbaSingleCardMode())
        {
            gbaSingleRom = null; gbaSingleRomSave = null; gbaSingleRomName = string.Empty;
            gbaSingleGameRemoved = true; gbaSingleNeedsRelocation = false;
            gbaSingleGridActive = false; RefreshGbaCardModeUi(); return;
        }
        if (!TryGetSelectedWorkspaceRow(out var row, out int index) || row is null) return;
        if (row.PendingItem is not null) pendingImportItems.Remove(row.PendingItem);
        if (IsGbaConversionDraft() && row.CartEntry is not null && row.PendingItem is null) gbaKeepCurrentGame!.Checked = false;
        workspaceRows.Remove(row);
        RefreshWorkspaceGrid(GetSafeSelectionIndex(index, workspaceRows.Count));
    }

    private void ClearGbaList()
    {
        if (isBusy || isAnalyzingQueueImports || IsGbaDirectoryDamaged()) return;
        pendingImportItems.Clear(); workspaceRows.Clear();
        gbaDraftMenu = null; gbaDraftMenuName = string.Empty; gbaMenuRemoved = true;
        gbaSingleRom = null; gbaSingleRomName = string.Empty; gbaSingleRomSave = null;
        gbaSingleGameRemoved = true; gbaSingleNeedsRelocation = false; gbaSingleGridActive = false;
        gbaKeepCurrentGame!.Checked = false;
        RefreshGbaCardModeUi();
    }

    private byte[] ReadGbaSingleSource(WaterDeviceProtocol protocol, ProgrammerKind kind, int length)
    {
        var source = GetGbaSingleGame() ?? throw new InvalidOperationException("工作区没有当前游戏。");
        return source.FoundInMenuConfig || source.FoundInWlMapping
            ? ReadMappedGbaRomFromDevice(protocol, kind, source)
            : ReadRomRangeFromDevice(protocol, kind, 0, length);
    }

    private void UpdateGbaMenuRowActions(bool busy)
    {
        if (!SupportsGbaCardMode()) return;
        bool idle = !busy && !isAnalyzingQueueImports && !IsGbaDirectoryDamaged();
        bool menu = IsSelectedGbaMenuRow();
        bool disabled = IsSelectedGbaDisabledGameRow();
        bool occupied = !disabled && (menu ? HasGbaListMenu() : IsGbaSingleCardMode() ? gbaSingleRom is not null || GetGbaSingleGame() is not null
            : TryGetSelectedWorkspaceRow(out _, out _));
        buttonQueueAdd.Enabled = menuAddRom.Enabled = idle && !disabled;
        buttonQueueRemove.Enabled = menuRemoveSelectedRom.Enabled = idle && occupied;
        buttonQueueClear.Enabled = menuClearRomQueue.Enabled = idle && (HasGbaListMenu() || gbaSingleRom is not null || GetGbaSingleGame() is not null || workspaceRows.Count > 0);
        menuReplaceSelectedRom.Enabled = menuItemReplaceRom.Enabled = idle && !disabled;
        menuReplaceMenuFile.Enabled = idle && !IsGbaMulticartUnavailable();
        if (IsGbaMulticartUnavailable()) comboBoxGbaMenu.Enabled = checkBoxWaitcntPatch.Enabled = false;
        if (disabled || menu || (dataGridViewRoms.SelectedRows.Count > 0 && ReferenceEquals(dataGridViewRoms.SelectedRows[0].Tag, GbaAddGameRowTag)))
        {
            menuExportSelectedRom.Enabled = menuItemExportRom.Enabled = false;
            foreach (var item in new[] { menuExportSelectedSave, menuImportSelectedSave, menuClearSelectedSave, menuItemExportSave, menuItemImportSave, menuItemClearSave, menuDeleteSelectedMapping, menuItemDeleteMapping }) item.Enabled = false;
        }
        if (IsGbaDirectoryDamaged())
        {
            buttonQueueBurn.Enabled = menuStartBurn.Enabled = false;
            groupBoxList.Text = "ROM 工作区（目录异常）";
        }
        dataGridViewRoms.Enabled = !busy;
        if (disabled && !busy) ShowGbaDisabledGameHint();
    }
}
