using GbaMultiMenuBuilder.Fc;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private bool fcWorkspaceActive;
    private WaterCartScanResult? fcHardwareInfo;
    private TableLayoutPanel workspaceLayout = null!;
    private ToolStripMenuItem menuFcTools = null!;
    private readonly List<(ToolStripMenuItem Item, string Key, string Fallback)> fcMenuLabels = [];
    private ToolStripMenuItem menuFcUp = null!;
    private ToolStripMenuItem menuFcDown = null!;
    private ToolStripMenuItem menuFcPreview = null!;
    private ToolStripMenuItem menuFcEdit = null!;
    private ToolStripMenuItem menuFcRebuild = null!;
    private ToolStripMenuItem menuFcOpen = null!;
    private ToolStripMenuItem menuFcExport = null!;
    private bool IsFcWorkspaceActive => fcWorkspaceActive && fcWorkspace is { IsDisposed: false };
    private bool CanUseFcDevice => fcBackendOverride is not null ||
        GetSelectedProgrammerKind() == ProgrammerKind.BeggarLegacy &&
        GetSelectedBeggarCartMode() == BeggarCartMode.Gb &&
        !string.IsNullOrWhiteSpace(GetSelectedPortName());

    private void BuildFcMenus()
    {
        ToolStripMenuItem Add(string key, string fallback, Func<Task> action)
        {
            var item = new ToolStripMenuItem(T("fc", key, fallback));
            item.Click += async (_, _) => await RunUiActionSafelyAsync(action);
            fcMenuLabels.Add((item, key, fallback));
            menuFcTools.DropDownItems.Add(item);
            return item;
        }
        Task Local(Action action) { action(); return Task.CompletedTask; }
        menuFcTools = new ToolStripMenuItem("MIX · NES / MD / SNES 导入预览") { Visible = false };
        menuFcUp = Add("up", "Up", () => Local(() => fcWorkspace!.MoveRow(-1)));
        menuFcDown = Add("down", "Down", () => Local(() => fcWorkspace!.MoveRow(1)));
        menuFcPreview = Add("title_preview", "12px title preview", () => Local(() => fcWorkspace!.PreviewTitle()));
        menuFcTools.DropDownItems.Add(new ToolStripSeparator());
        menuFcEdit = Add("edit_config", "Edit NES configuration", () => Local(() => fcWorkspace!.EditConfiguration()));
        menuFcRebuild = Add("rebuild_config", "Check / rebuild NES configuration", () => Local(() => fcWorkspace!.RebuildConfigurations()));
        menuFcTools.DropDownItems.Add(new ToolStripSeparator());
        menuFcOpen = Add("import_image", "Open MIX .bin", () => fcWorkspace!.ImportImageAsync());
        menuFcExport = Add("export", "Build .bin", () => fcWorkspace!.ExportAsync());
        menuRom.DropDownItems.Insert(menuRom.DropDownItems.IndexOf(menuStartBurn), menuFcTools);
    }

    private async Task ActivateFcWorkspaceAsync()
    {
        if (isBusy || isAnalyzingQueueImports || IsFcWorkspaceActive) return;
        if (GetSelectedProgrammerKind() == ProgrammerKind.BeggarLegacy &&
            GetSelectedBeggarCartMode() != BeggarCartMode.Gb)
            radioButtonBeggarCartModeGb.Checked = true;
        burnProfileModeSelectionInitialized = false;
        if (!burnProfileModeOptions.Any(option => option.Mode == BurnProfileMode.FcMbcx))
        {
            burnProfileModeOptions.Add(new BurnProfileModeOption(T("fc", "mode", "MIX | NES + MD (MBCX)"), BurnProfileMode.FcMbcx));
            comboBoxBurnProfileMode.DataSource = burnProfileModeOptions.ToList();
        }
        comboBoxBurnProfileMode.SelectedItem = burnProfileModeOptions.First(option => option.Mode == BurnProfileMode.FcMbcx);
        burnProfileModeSelectionInitialized = true;
        if (fcWorkspace is null || fcWorkspace.IsDisposed)
        {
            fcWorkspace = new FcWorkspaceControl((key, fallback) => T("fc", key, fallback), CreateFcBackend, integrated: true);
            components.Add(fcWorkspace);
            fcWorkspace.BusyChanged += busy =>
            {
                if (!IsFcWorkspaceActive || Disposing || IsDisposed) return;
                SetBusy(busy, busy ? T("fc", "working", "MIX operation in progress") : fcWorkspace.StatusText);
                if (!busy) AppendLog(fcWorkspace.StatusText);
            };
            fcWorkspace.StateChanged += () =>
            {
                if (!IsFcWorkspaceActive || Disposing || IsDisposed || fcWorkspace.Disposing) return;
                UpdateFcCardInfo();
                UpdateActionButtons(isBusy);
            };
            fcWorkspace.StatusChanged += message =>
            {
                if (IsFcWorkspaceActive && !Disposing && !IsDisposed) labelStatus.Text = message;
            };
            fcWorkspace.ProgressChanged += progress =>
            {
                if (!IsFcWorkspaceActive || Disposing || IsDisposed) return;
                progressBarTotal.Style = ProgressBarStyle.Continuous;
                progressBarTotal.Value = Math.Clamp(progress.Percent * progressBarTotal.Maximum / 100, progressBarTotal.Minimum, progressBarTotal.Maximum);
            };
        }
        ResetCurrentCartAnalysisView();
        fcWorkspaceActive = true;
        dataGridViewRoms.Visible = false;
        workspaceLayout.Controls.Remove(dataGridViewRoms);
        workspaceLayout.Controls.Add(fcWorkspace, 0, 1);
        fcWorkspace.Visible = true;
        fcWorkspace.RefreshLanguage();
        RefreshGbcFontSelectorVisibility();
        UpdateFcCardInfo();
        UpdateActionButtons(false);
        var queued = pendingImportItems
            .Where(item => item.FcRom is not null || item.MdRom is not null || item.ImportedPlatform == GbaMultiMenuBuilder.Services.RomPlatform.Snes)
            .Select(item => item.MdRom?.SourcePath ?? (string.IsNullOrWhiteSpace(item.SourceImportKey) ? item.FilePath : item.SourceImportKey))
            .Where(path => !queuedFcSources.Contains(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (queued.Length > 0)
        {
            int previousCount = fcWorkspace.GameCount;
            await fcWorkspace.ImportAsync(queued);
            if (fcWorkspace.GameCount > previousCount)
                foreach (string path in queued) queuedFcSources.Add(path);
        }
    }

    private void HideFcWorkspace()
    {
        if (!IsFcWorkspaceActive || fcWorkspace!.IsBusy) return;
        fcWorkspaceActive = false;
        fcWorkspace.Visible = false;
        workspaceLayout.Controls.Remove(fcWorkspace);
        workspaceLayout.Controls.Add(dataGridViewRoms, 0, 1);
        dataGridViewRoms.Visible = true;
        fcWorkspace.InvalidateSnapshot();
        fcHardwareInfo = null;
        ResetCurrentCartAnalysisView();
        RebuildWorkspaceRows();
        RefreshGbcFontSelectorVisibility();
        UpdateActionButtons(isBusy);
    }

    private void InvalidateFcDevice()
    {
        fcHardwareInfo = null;
        fcWorkspace?.InvalidateSnapshot();
        if (IsFcWorkspaceActive && GetSelectedBurnProfileMode() != BurnProfileMode.FcMbcx)
            HideFcWorkspace();
        else if (IsFcWorkspaceActive)
            UpdateFcCardInfo();
    }

    private void UpdateFcHardwareInfo(WaterCartScanResult info)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<WaterCartScanResult>(UpdateFcHardwareInfo), info);
            return;
        }
        if (!IsFcWorkspaceActive) return;
        fcHardwareInfo = info;
        UpdateFcCardInfo();
    }

    private void UpdateFcCardInfo()
    {
        if (!IsFcWorkspaceActive) return;
        var snapshot = fcWorkspace!.Snapshot;
        labelNorIdValue.Text = fcHardwareInfo?.RawNorIdHex ?? T("ui", "not_read", "Not Read");
        labelChipNameValue.Text = fcHardwareInfo?.ChipName ?? T("ui", "not_read", "Not Read");
        labelCapacityValue.Text = snapshot is null ? T("ui", "not_read", "Not Read") : FormatMiBSize(snapshot.Capacity);
        labelSectorInfoValue.Text = fcHardwareInfo is not null ? FormatSectorSummary(fcHardwareInfo.Geometry) :
            snapshot is null ? T("ui", "not_read", "Not Read") : FormatKiBSize(snapshot.SectorBytes);
        labelRomCountValue.Text = fcWorkspace.GameCount.ToString();
        labelMenuConfigValue.Text = snapshot?.IsFc == true ? "MIX / MBCX" : T("ui", "not_detected", "Not Detected");
        labelMenuItemCountValue.Text = (snapshot?.Games.Count ?? 0).ToString();
        labelMenuTitleValue.Text = "NES / MD";
        labelScanMethodValue.Text = "MBCX";
        groupBoxList.Text = T("ui", "rom_workspace", "ROM Workspace") + " · NES / MD · SNES导入预览";
    }

    private void UpdateFcActionButtons(bool busy)
    {
        UpdateFcCardInfo();
        bool idle = !busy && !fcWorkspace!.IsBusy && !isAnalyzingQueueImports;
        bool selected = idle && fcWorkspace!.HasSelection;
        mainMenuStrip.Visible = true;
        groupBoxInfo.Visible = true;
        groupBoxList.Enabled = true;
        menuFcTools.Visible = true;
        menuFcTools.Enabled = idle;
        UpdateMixRebuildMenu(!idle);
        menuSave.Enabled = false;
        menuPendingGbaCovers.Enabled = false;
        menuReplaceSelectedRom.Enabled = false;
        menuDeleteSelectedMapping.Enabled = false;
        menuExportSelectedRom.Enabled = selected && fcWorkspace!.CanExportSelection;
        buttonQueueAdd.Enabled = menuAddRom.Enabled = idle;
        buttonQueueRemove.Enabled = menuRemoveSelectedRom.Enabled = selected;
        buttonQueueClear.Enabled = menuClearRomQueue.Enabled = idle && fcWorkspace!.HasGames;
        buttonScan.Enabled = menuScan.Enabled = idle && CanUseFcDevice;
        buttonRefreshPorts.Enabled = menuRefreshPorts.Enabled = idle;
        comboBoxPorts.Enabled = comboBoxLanguage.Enabled = idle;
        comboBoxBurnProfileMode.Enabled = idle && burnProfileModeOptions.Count > 0;
        UpdateProgrammerModeSelectorUi(idle);
        bool cancelling = fcWorkspace!.IsBusy;
        buttonQueueBurn.Text = menuStartBurn.Text = cancelling ? T("ui", "cancel_burn", "Cancel") : T("ui", "queue_burn", "Start Burn");
        buttonQueueBurn.Enabled = menuStartBurn.Enabled = cancelling || idle && CanUseFcDevice && fcWorkspace.CanBurn;
        menuExit.Enabled = idle;
        menuFcUp.Enabled = menuFcDown.Enabled = menuFcPreview.Enabled = selected;
        menuFcEdit.Enabled = idle && fcWorkspace.CanEditConfiguration;
        menuFcRebuild.Enabled = menuFcExport.Enabled = idle && fcWorkspace.HasGames;
        menuFcOpen.Enabled = idle;
        foreach (var label in fcMenuLabels) label.Item.Text = T("fc", label.Key, label.Fallback);
    }
}
