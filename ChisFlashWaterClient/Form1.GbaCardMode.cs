using ClientText = ChisFlashWaterClient.Services.LocalizedText;
using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private FlowLayoutPanel? gbaConversionOptions;

    private CheckBox? gbaKeepCurrentGame;
    private ComboBox? gbaCurrentGameLength;

    private bool updatingGbaCardMode;
    private bool rebuildingGbaSingleGrid;
    private bool gbaSingleGridActive;
    private WaterCartScanResult? gbaSingleGridScan;
    private byte[]? gbaSingleGridRom;
    private byte[]? gbaSingleRom;
    private string gbaSingleRomName = string.Empty;
    private DetectedSaveTypeInfo? gbaSingleRomSave;

    private bool SupportsGbaCardMode() => GetSelectedBeggarCartMode() == BeggarCartMode.Gba &&
        GetSelectedBurnProfileMode() is not (BurnProfileMode.ChisFlash10 or BurnProfileMode.FcMbcx) &&
        !IsGbcRawImageMode() && !IsGbcProfileUnselected() && !IsMbc5FixedSlotMode() &&
        currentScanResult?.IsBeggarMaxGbcMode != true;

    private static bool IsSingleGameOnlyGbaScan(WaterCartScanResult? scan) => scan is { NoCartridgeDetected: false, IsBeggarMaxGbcMode: false, IsChm1: false } &&
        WaterCartScanner.IsSingleGameGbaNor(scan.NorId, scan.Geometry);
    private bool IsGbaSingleGameOnlyHardware() => GetSelectedBeggarCartMode() == BeggarCartMode.Gba && HasActiveScanResult() && IsSingleGameOnlyGbaScan(currentScanResult);

    private bool IsGbaSingleCardMode() => SupportsGbaCardMode() && (IsGbaMulticartUnavailable() || !HasGbaListMenu() && !IsGbaDirectoryDamaged());

    private bool IsGbaConversionDraft() => SupportsGbaCardMode() && !IsGbaMulticartUnavailable() && gbaDraftMenu is not null &&
        (gbaMenuRemoved || currentScanResult?.MenuConfig.IsDetected != true) && !IsGbaDirectoryDamaged();

    private string GbaCardText(string key, string chinese, string english) =>
        T("gba_card", key, language.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english);

    private void BuildGbaCardModeControls(FlowLayoutPanel toolbar)
    {
        gbaConversionOptions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(4), Visible = false };
        components.Add(gbaConversionOptions);
        gbaKeepCurrentGame = new CheckBox { AutoSize = true, Margin = new Padding(4, 7, 8, 4) };
        gbaCurrentGameLength = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
        gbaCurrentGameLength.Items.AddRange(new object[] { ClientText.T("client", "select_length", "Select length"), "4 MiB", "8 MiB", "16 MiB", "32 MiB" });
        gbaCurrentGameLength.SelectedIndex = 0;
        gbaKeepCurrentGame.CheckedChanged += (_, _) => UpdateActionButtons(isBusy);
        gbaCurrentGameLength.SelectedIndexChanged += (_, _) => UpdateActionButtons(isBusy);
        gbaConversionOptions.Controls.Add(gbaKeepCurrentGame);
        gbaConversionOptions.Controls.Add(gbaCurrentGameLength);
    }

    private void RefreshGbaCardModeUi()
    {
        if (updatingGbaCardMode || gbaConversionOptions is null || workspaceLayout is null) return;
        updatingGbaCardMode = true;
        try
        {
            bool single = IsGbaSingleCardMode();
            bool conversion = IsGbaConversionDraft();
            bool clearsHeadGame = conversion && currentScanResult?.MenuConfig.IsDetected != true &&
                gbaSingleRom is null && GetGbaSingleGame() is not null;
            // 两种模式共用原表格和工具栏，不移除或替换工作区控件。
            gbaCurrentGameLength!.Items[0] = ClientText.T("client", "select_length", "Select length");
            gbaKeepCurrentGame!.Text = GbaCardText("keep", "保留当前游戏", "Keep current game");
            gbaKeepCurrentGame.Enabled = conversion && !clearsHeadGame && !isBusy && gbaConversionOptions.Parent is null;
            gbaCurrentGameLength!.Visible = !clearsHeadGame && (GetGbaSingleGame() is not { } original || (!original.FoundInMenuConfig && !original.FoundInWlMapping));
            gbaCurrentGameLength.Enabled = conversion && !isBusy && gbaKeepCurrentGame.Checked;
            if (single)
            {
                RefreshSingleGbaGrid();
                groupBoxList.Text = GbaCardText("single_list", "ROM 工作区（单卡）", "ROM Workspace (Single)");
                if (currentScanResult is not null) labelCapacityValue.Text = FormatMiBSize(currentScanResult.Geometry.DeviceSizeBytes);
                UpdateGbaSingleButtons(isBusy);
            }
            else
            {
                gbaSingleGridActive = false;
                if (conversion)
                {
                    groupBoxList.Text = GbaCardText("multicart_list", "ROM 工作区（合卡 · 待烧录）", "ROM Workspace (Multicart draft)");
                    bool canBurn = !isBusy && !isAnalyzingQueueImports &&
                        (pendingImportItems.Count > 0 || gbaKeepCurrentGame!.Checked && GetGbaSingleGame() is not null || HasPendingGbaWorkspaceClear());
                    buttonQueueBurn.Enabled = menuStartBurn.Enabled = activeBurnCancellation is not null ? !isBurnCancellationRequested : canBurn;
                    buttonQueueBurn.Text = menuStartBurn.Text = activeBurnCancellation is not null ? T("ui", "cancel_burn", "Cancel") : T("ui", "queue_burn", "Start Burn");
                }
            }
            UpdateGbaMenuRowActions(isBusy);
            RefreshSingleGbaMenuPreviewState();
            RefreshWaitcntPatchUi();
        }
        finally { updatingGbaCardMode = false; }
    }

    private void RefreshSingleGbaGrid()
    {
        var head = GetGbaSingleGame();
        bool singleChip = IsGbaMulticartUnavailable();
        int count = singleChip ? 1 : 3;
        if (gbaSingleGridActive && ReferenceEquals(gbaSingleGridScan, currentScanResult) &&
            ReferenceEquals(gbaSingleGridRom, gbaSingleRom) && dataGridViewRoms.Rows.Count == count) return;
        rebuildingGbaSingleGrid = true;
        dataGridViewRoms.SuspendLayout();
        try
        {
            RestoreMbc5GridColumns();
            dataGridViewRoms.Columns["GbcDedicatedSave"]!.Visible = false;
            dataGridViewRoms.ReadOnly = true;
            dataGridViewRoms.Rows.Clear();
            if (!singleChip) AddGbaMenuGridRow();
            if (head is not null || gbaSingleRom is not null)
            {
                bool pending = gbaSingleRom is not null;
                string title = pending ? gbaSingleRomName : head!.Title;
                string header = pending ? WaterCartScanner.DecodeGbaHeaderTitle(gbaSingleRom!) : head!.HeaderTitle;
                string gameCode = pending ? WaterCartScanner.DecodeGbaHeaderGameCode(gbaSingleRom!) : head!.HeaderGame?.GameCode ?? string.Empty;
                int index = dataGridViewRoms.Rows.Add(ClientText.T("client", "game_one", "Game 1"),
                    pending ? T("value", "row_state_pending", "Pending") : T("value", "row_state_cart", "On Cart"),
                    pending ? T("value", "source_file", "File") : head!.SourceText,
                    "GBA", title, header,
                    pending ? FormatMiBSize(gbaSingleRom!.Length) : head!.FoundInMenuConfig || head.FoundInWlMapping
                        ? FormatMiBSize(head.LogicalBucketCount * WaterCartScanner.BucketSizeBytes) : GbaCardText("size_unconfirmed", "未确定", "Not determined"),
                    pending ? FormatDetectedSaveTypeDisplay(gbaSingleRomSave!) : GbaCardText("save_unanalyzed", "未分析", "Not analyzed"),
                    false, GbaCardText("original_rom", "原文件，不打补丁", "Original ROM; no patches"), "—", string.Empty);
                var row = dataGridViewRoms.Rows[index];
                row.ReadOnly = true;
                row.Tag = new WorkspaceListRow { CartEntry = head };
                foreach (DataGridViewCell cell in row.Cells)
                    cell.ToolTipText = $"{header}\n{gameCode}\n" + (gbaSingleNeedsRelocation ? ClientText.T("client", "single_relocation_hint", "Pending write to 0x000000; current data remains at its original mapped location") : "0x000000") +
                        (pending && head is not null ? "\n" + GbaCardText("on_card", "卡内游戏", "Current game") + ": " + head.Title : string.Empty);
                row.Selected = true;
                dataGridViewRoms.CurrentCell = row.Cells["Title"];
            }
            else
            {
                AddGbaEmptyGameRow(1);
                dataGridViewRoms.CurrentCell = dataGridViewRoms.Rows[singleChip ? 0 : 1].Cells["Title"];
            }
            if (!singleChip) AddGbaDisabledGameRow();
            gbaSingleGridScan = currentScanResult;
            gbaSingleGridRom = gbaSingleRom;
            gbaSingleGridActive = true;
        }
        finally { dataGridViewRoms.ResumeLayout(); rebuildingGbaSingleGrid = false; }
    }

    private void UpdateGbaSingleButtons(bool busy)
    {
        buttonQueueAdd.Text = T("gbc_profile", "add_image", "Import ROM / BIN");
        buttonQueueClear.Text = menuClearRomQueue.Text = T("ui", "clear_workspace", "Clear Workspace");
        bool idle = !busy && !isAnalyzingQueueImports;
        bool pending = gbaSingleRom is not null || gbaSingleNeedsRelocation || HasPendingGbaWorkspaceClear();
        bool canBurn = idle && pending;
        bool hasGame = HasActiveScanResult() && GetGbaSingleGame() is not null;
        menuRom.Enabled = menuCartridge.Enabled = true;
        menuSave.Enabled = false;
        menuFcScan.Enabled = idle;
        menuScan.Enabled = buttonScan.Enabled = idle && !string.IsNullOrWhiteSpace(GetSelectedPortName());
        buttonQueueAdd.Enabled = menuAddRom.Enabled = idle && gbaSingleRom is null && GetGbaSingleGame() is null;
        menuReplaceSelectedRom.Enabled = menuItemReplaceRom.Enabled = CanReplaceGbaWorkspaceSelection();
        buttonQueueRemove.Enabled = buttonQueueClear.Enabled = menuRemoveSelectedRom.Enabled = menuClearRomQueue.Enabled = idle && pending;
        menuExportSelectedRom.Enabled = menuItemExportRom.Enabled = idle && hasGame;
        buttonQueueBurn.Enabled = menuStartBurn.Enabled = activeBurnCancellation is not null ? !isBurnCancellationRequested : canBurn;
        buttonQueueBurn.Text = menuStartBurn.Text = activeBurnCancellation is not null ? T("ui", "cancel_burn", "Cancel") : T("ui", "queue_burn", "Start Burn");
        foreach (var item in new[] { menuUpdateMenu, menuReplaceMenuFile, menuRepairMenu, menuUpdateBackground, menuEraseConfig, menuPendingGbaCovers, menuDeleteSelectedMapping, menuFragmentTest,
            menuItemDeleteMapping, menuItemExportSave, menuItemImportSave, menuItemClearSave, menuItemExportChipSaveContext, menuItemImportChipSaveContext, menuItemClearChipSaveContext }) item.Enabled = false;
        menuEraseEntireNor.Enabled = idle && CanEraseEntireNorCurrentScan(out _);
        menuUnlockPpb.Enabled = idle && CanUnlockPpbCurrentScan(out _);
        comboBoxBurnProfileMode.Enabled = idle;
        dataGridViewRoms.Enabled = !busy;
        UpdateProgrammerModeSelectorUi(idle);
        UpdateGbaMenuRowActions(busy);
    }

    private async Task SelectSingleGbaFileAsync()
    {
        if (isBusy) return;
        if (gbaSingleRom is not null || GetGbaSingleGame() is not null)
            throw new InvalidOperationException(T("ui", "delete_game_before_add", "Delete the existing game before adding another."));
        using var dialog = new OpenFileDialog { Filter = "GBA ROM (*.gba)|*.gba", Multiselect = false };
        if (dialog.ShowDialog(this) == DialogResult.OK) await AddSingleGbaFilesAsync(dialog.FileNames);
    }

    private async Task AddSingleGbaFilesAsync(IEnumerable<string> paths)
    {
        if (isBusy) return;
        if (gbaSingleRom is not null || GetGbaSingleGame() is not null)
            throw new InvalidOperationException(T("ui", "delete_game_before_add", "Delete the existing game before adding another."));
        string[] files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length != 1) throw new InvalidDataException(GbaCardText("one_rom", "单卡每次只导入一个 GBA ROM。", "Choose one GBA ROM for single-card mode."));
        if (new FileInfo(files[0]).Length is < 192 or > ChisFlash10Image.CapacityBytes) throw new InvalidDataException(ClientText.T("client", "gba_rom_size", "The GBA ROM must be between 192 bytes and 32 MiB."));
        SetBusy(true, ClientText.T("client", "single_reading", "Reading single-game ROM…"));
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(files[0]);
            if (!WaterCartScanner.HasValidGbaHeader(bytes)) throw new InvalidDataException(ClientText.T("client", "gba_header_invalid", "No valid GBA header was found. Check the ROM file."));
            if (HasActiveScanResult() && bytes.Length > currentScanResult!.Geometry.DeviceSizeBytes) throw new InvalidDataException(ClientText.T("client", "game_exceeds_nor", "The game exceeds the detected NOR capacity."));
            var save = ChisFlash10Image.Analyze(bytes);
            void Apply() { gbaSingleReplacementCapacityBytes = 0; gbaSingleRom = bytes; gbaSingleRomPath = Path.GetFullPath(files[0]); gbaSingleRomName = Path.GetFileName(files[0]); gbaSingleRomSave = save; gbaSingleWaitcntPatch = defaultWaitcntPatchEnabled; gbaSingleGameRemoved = false; RefreshGbaCardModeUi(); }
            if (InvokeRequired) Invoke(new Action(Apply)); else Apply();
        }
        finally
        {
            void Complete() { SetBusy(false, ClientText.T("client", "single_ready", "Single-game ROM ready")); RefreshGbaCardModeUi(); }
            if (InvokeRequired) Invoke(new Action(Complete)); else Complete();
        }
    }

    private void ClearSingleGbaPending()
    {
        if (isBusy) return;
        gbaSingleReplacementCapacityBytes = 0;
        gbaSingleRom = null; gbaSingleRomName = string.Empty; gbaSingleRomSave = null;
        RefreshGbaCardModeUi();
    }

    private int GetCurrentSingleGameLength() => gbaCurrentGameLength?.SelectedIndex is > 0
        ? (1 << (gbaCurrentGameLength.SelectedIndex + 1)) * 1024 * 1024 : 0;

    private async Task ExportSingleGbaAsync()
    {
        if (isBusy || IsSelectedGbaMenuRow() || IsSelectedGbaDisabledGameRow() || !HasActiveScanResult() || GetGbaSingleGame() is not { } head) return;
        using var lengthDialog = new Form { Text = ClientText.T("client", "read_length_title", "Select ROM Read Length"), Width = 340, Height = 155, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var length = new ComboBox { Left = 18, Top = 18, Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
        length.Items.AddRange(new object[] { "4 MiB", "8 MiB", "16 MiB", "32 MiB" }); length.SelectedIndex = 2;
        var ok = new Button { Text = ClientText.T("client", "ok", "OK"), DialogResult = DialogResult.OK, Left = 210, Top = 58 };
        lengthDialog.Controls.AddRange([length, ok]); lengthDialog.AcceptButton = ok;
        bool mapped = head.FoundInMenuConfig || head.FoundInWlMapping;
        if (!mapped && lengthDialog.ShowDialog(this) != DialogResult.OK) return;
        int bytes = mapped ? checked(head.LogicalBucketCount * WaterCartScanner.BucketSizeBytes) : (4 << length.SelectedIndex) * 1024 * 1024;
        if (bytes > currentScanResult!.Geometry.DeviceSizeBytes) throw new InvalidDataException(ClientText.T("client", "read_exceeds_nor", "The read length exceeds the detected NOR capacity."));
        using var destination = new SaveFileDialog { Filter = "GBA ROM (*.gba)|*.gba", FileName = head.Title + ".gba" };
        if (destination.ShowDialog(this) != DialogResult.OK) return;
        string port = GetSelectedPortName()!;
        var kind = GetRequiredSelectedProgrammerKind();
        SetBusy(true, ClientText.T("client", "single_exporting", "Exporting single-game ROM…"));
        try
        {
            byte[] data = await Task.Run(() => { using var protocol = new WaterDeviceProtocol(port); OpenProgrammerProtocol(protocol, kind); return ReadGbaSingleSource(protocol, kind, bytes); });
            await File.WriteAllBytesAsync(destination.FileName, data);
        }
        finally { SetBusy(false, ClientText.T("client", "rom_export_ended", "ROM export ended")); }
    }
}
