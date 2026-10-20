using System.IO;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private Panel? gbcImageWorkspace;
    private Label? gbcImageDetails;
    private byte[]? gbcImageData;
    private string gbcImageName = string.Empty;
    private string RtcText(string key, string chinese, string english) =>
        T("rtc", key, language.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english);

    private GbcRtcRomInspection InspectRtcGame(byte[]? data)
    {
        try { return GbcRtcRomValidator.Inspect(data ?? []); }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(exception.Message == "RTC_ROM_SIZE"
                ? RtcText("size_error", "RTC模式只允许一个32K～2M的GB/GBC游戏；文件大小和头部声明的寻址大小均不能超过2M。", "RTC mode accepts one 32 KiB–2 MiB GB/GBC game. Neither the file nor its declared addressable size may exceed 2 MiB.")
                : RtcText("header_error", "未找到有效GB/GBC头部，请检查ROM文件。", "The GB/GBC ROM header is invalid; check the ROM file."));
        }
    }

    private string RtcHeaderNotice(GbcRtcRomInspection inspection) => inspection.HasMbc3Rtc
        ? RtcText("detected", "头部已识别为MBC3 RTC游戏", "Header identifies an MBC3 RTC game") + $" (0x{inspection.CartridgeType:X2})"
        : RtcText("not_rtc", "提示：头部未标记当前卡型使用的MBC3 RTC（0x0F/0x10），请确认所选游戏；仍可继续导出或烧录。", "Warning: the header does not identify MBC3 RTC (0x0F/0x10) for this cartridge profile. Check the game; export/programming remain available.") + $" (0x{inspection.CartridgeType:X2})";
    private bool IsGbcImageWorkspaceActive => gbcImageWorkspace is not null && workspaceLayout.Controls.Contains(gbcImageWorkspace);

    private void ApplyGbcProfileWorkspace()
    {
        if (workspaceLayout is null || dataGridViewRoms is null) return;
        if (GetSelectedBurnProfileMode() != BurnProfileMode.FcMbcx) HideFcWorkspace();
        if (IsGbcRawImageMode())
        {
            bool initialized = beggarCartModeSelectionInitialized;
            beggarCartModeSelectionInitialized = false;
            try { radioButtonBeggarCartModeGb.Checked = true; }
            finally { beggarCartModeSelectionInitialized = initialized; }
            if (gbcImageWorkspace is null)
            {
                gbcImageWorkspace = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16), AllowDrop = true };
                gbcImageDetails = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, AllowDrop = true };
                gbcImageWorkspace.Controls.Add(gbcImageDetails);
                components.Add(gbcImageWorkspace);
                foreach (Control target in new Control[] { gbcImageWorkspace, gbcImageDetails })
                {
                    target.DragEnter += (_, args) => args.Effect = !isBusy && args.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
                    target.DragDrop += async (_, args) => { if (args.Data?.GetData(DataFormats.FileDrop) is string[] paths) await RunUiActionSafelyAsync(() => AddGbcImageFilesAsync(paths)); };
                }
            }
            workspaceLayout.Controls.Remove(dataGridViewRoms);
            dataGridViewRoms.Visible = false;
            workspaceLayout.Controls.Add(gbcImageWorkspace, 0, 1);
            gbcImageWorkspace.Visible = true;
            RefreshGbcImageDetails();
        }
        else if (IsGbcImageWorkspaceActive)
        {
            workspaceLayout.Controls.Remove(gbcImageWorkspace!);
            gbcImageWorkspace!.Visible = false;
            workspaceLayout.Controls.Add(dataGridViewRoms, 0, 1);
            dataGridViewRoms.Visible = true;
        }
    }

    private void RefreshGbcImageDetails()
    {
        if (gbcImageDetails is null) return;
        string note = RtcText("single_notice", "RTC单游戏：最多2M，检查GB/GBC头部及RTC类型；不添加合卡菜单。", "RTC single game: maximum 2 MiB; checks the GB/GBC header and RTC type. No multicart menu is added.");
        gbcImageDetails.Text = GetBurnProfileModeDisplayName(GetSelectedBurnProfileMode()) + Environment.NewLine + Environment.NewLine + note + Environment.NewLine + Environment.NewLine +
            (gbcImageData is null ? T("gbc_profile", "add_image", "Import one ROM / BIN") : gbcImageName + " · " + FormatMiBSize(gbcImageData.Length)) + Environment.NewLine +
            RtcText("physical_capacity", "NOR容量", "NOR capacity") + ": " + FormatMiBSize(SelectedGbcImageCapacity());
        gbcImageDetails.ForeColor = SystemColors.ControlText;
        if (gbcImageData is not null)
        {
            try
            {
                var inspection = InspectRtcGame(gbcImageData);
                gbcImageDetails.Text += Environment.NewLine + Environment.NewLine + RtcHeaderNotice(inspection);
                if (!inspection.HasMbc3Rtc) gbcImageDetails.ForeColor = Color.DarkOrange;
            }
            catch (InvalidDataException exception)
            {
                gbcImageDetails.Text += Environment.NewLine + exception.Message;
                gbcImageDetails.ForeColor = Color.Firebrick;
            }
        }
        groupBoxList.Text = GetBurnProfileModeDisplayName(GetSelectedBurnProfileMode()) + " · " + RtcText("single_title", "单游戏 ≤2M", "Single game ≤2 MiB");
    }

    private async Task SelectGbcImageFileAsync()
    {
        if (isBusy) return;
        using var dialog = new OpenFileDialog { Filter = "GB/GBC ROM / BIN (*.gb;*.gbc;*.bin)|*.gb;*.gbc;*.bin", Multiselect = false };
        if (dialog.ShowDialog(this) == DialogResult.OK) await AddGbcImageFilesAsync([dialog.FileName]);
    }

    private async Task AddGbcImageFilesAsync(IEnumerable<string> paths)
    {
        if (isBusy || !IsGbcRawImageMode()) return;
        string[] files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length != 1) throw new InvalidDataException(RtcText("one_game", "RTC模式每次只允许导入一个游戏ROM。", "RTC mode accepts exactly one game ROM."));
        long length = new FileInfo(files[0]).Length;
        if (length < 0x8000 || length > GbcRtcRomValidator.MaximumGameBytes) throw new InvalidDataException(RtcText("size_error", "RTC游戏必须为32K～2M，不能导入整卡合卡镜像。", "RTC games must be 32 KiB–2 MiB; complete multicart images cannot be imported."));
        SetBusy(true, RtcText("reading", "正在检查RTC游戏ROM…", "Checking RTC game ROM…"));
        try
        {
            byte[] data = await File.ReadAllBytesAsync(files[0]);
            void ApplyImportedRom()
            {
                GbcRtcRomInspection inspection = InspectRtcGame(data);
                gbcImageData = data;
                gbcImageName = Path.GetFileName(files[0]);
                AppendLog(RtcHeaderNotice(inspection));
                RefreshGbcImageDetails();
            }
            if (InvokeRequired) Invoke(new Action(ApplyImportedRom));
            else ApplyImportedRom();
        }
        finally
        {
            void CompleteImport() => SetBusy(false, RtcText("ready", "RTC单游戏：最大2M", "RTC single game: maximum 2 MiB"));
            if (InvokeRequired) Invoke(new Action(CompleteImport));
            else CompleteImport();
        }
    }

    private void ClearGbcImage()
    {
        if (isBusy) return;
        gbcImageData = null;
        gbcImageName = string.Empty;
        RefreshGbcImageDetails();
        UpdateActionButtons(isBusy);
    }

    private byte[] BuildGbcImage()
    {
        int capacity = SelectedGbcImageCapacity();
        InspectRtcGame(gbcImageData);
        byte[] image = new byte[capacity];
        Array.Fill(image, (byte)255);
        gbcImageData!.CopyTo(image, 0);
        return image;
    }

    private async Task ExportGbcImageAsync()
    {
        if (isBusy || gbcImageData is null) return;
        using var dialog = new SaveFileDialog { Filter = "Complete ROM (*.bin)|*.bin", FileName = Path.GetFileNameWithoutExtension(gbcImageName) + ".bin" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await File.WriteAllBytesAsync(dialog.FileName, BuildGbcImage());
    }

    private bool CanWriteGbcImage() => IsGbcRawImageMode() &&
        GetSelectedBeggarCartMode() == BeggarCartMode.Gb && HasActiveScanResult() &&
        currentScanResult is { IsBeggarMaxGbcMode: true, IsWriteProtected: false } scan && scan.Geometry.DeviceSizeBytes == SelectedGbcImageCapacity() &&
        scan.Geometry.PrimaryCommandSetId == 2 && scan.Geometry.RegionCount == 1 &&
        scan.Geometry.BufferWriteBytes is > 0 and <= ushort.MaxValue && scan.Geometry.SectorSizeBytes >= GbcNorImageWriter.BankBytes &&
        (long)scan.Geometry.SectorSizeBytes * scan.Geometry.SectorCount == scan.Geometry.DeviceSizeBytes;

    private async Task HandleGbcImagePrimaryActionAsync()
    {
        if (activeBurnCancellation is not null) { RequestActiveBurnCancellation(); return; }
        if (isBusy || gbcImageData is null) return;
        GbcRtcRomInspection inspection = InspectRtcGame(gbcImageData);
        if (!CanWriteGbcImage()) { await ExportGbcImageAsync(); return; }
        if (MessageBox.Show(this, RtcHeaderNotice(inspection) + Environment.NewLine + Environment.NewLine + T("gbc_profile", "burn_confirm", "Erase the entire detected NOR, write the imported image (unused bytes filled FF), and verify it? Existing ROM content is replaced; SRAM is not erased. Back up first."),
            GetBurnProfileModeDisplayName(GetSelectedBurnProfileMode()), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        byte[] image = BuildGbcImage();
        WaterCartScanResult scan = currentScanResult!;
        string port = GetSelectedPortName()!;
        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation;
        isBurnCancellationRequested = false;
        SetBusy(true, T("gbc_profile", "writing", "Erasing, writing and verifying the complete image..."));
        try { await Task.Run(() => WriteGbcImageOnDevice(port, scan, image, cancellation.Token)); }
        finally
        {
            activeBurnCancellation = null;
            isBurnCancellationRequested = false;
            currentScanResult = null;
            lastScannedPortName = string.Empty;
            SetBusy(false, T("gbc_profile", "rescan", "Operation ended; rescan the cartridge."));
        }
    }

    private void UpdateGbcProfileActionButtons(bool busy)
    {
        bool raw = IsGbcRawImageMode();
        bool idle = !busy && !isAnalyzingQueueImports;
        bool hasImage = raw && gbcImageData is not null && GbcRtcRomValidator.IsValid(gbcImageData);
        menuFcScan.Text = T("ui", "analyze_configuration", "分析配置");
        menuFcScan.Enabled = idle;
        menuRom.Enabled = menuCartridge.Enabled = true;
        menuSave.Enabled = false;
        if (menuFcTools is not null) menuFcTools.Visible = false;
        buttonScan.Enabled = menuScan.Enabled = idle && !string.IsNullOrWhiteSpace(GetSelectedPortName());
        buttonRefreshPorts.Enabled = menuRefreshPorts.Enabled = idle;
        comboBoxPorts.Enabled = comboBoxLanguage.Enabled = idle;
        comboBoxBurnProfileMode.Enabled = idle;
        UpdateProgrammerModeSelectorUi(idle);
        buttonQueueAdd.Enabled = menuAddRom.Enabled = menuReplaceSelectedRom.Enabled = idle && raw;
        buttonQueueRemove.Enabled = menuRemoveSelectedRom.Enabled = buttonQueueClear.Enabled = menuClearRomQueue.Enabled = idle && raw && gbcImageData is not null;
        buttonQueueAdd.Text = T("gbc_profile", "add_image", "Import ROM / BIN");
        buttonQueueClear.Text = T("gbc_profile", "clear_image", "Clear image");
        buttonQueueBurn.Text = menuStartBurn.Text = activeBurnCancellation is not null ? T("ui", "cancel_burn", "Cancel") : CanWriteGbcImage()
            ? T("gbc_profile", "burn_image", "Burn complete ROM") : T("gbc_profile", "export_image", "Export complete ROM");
        buttonQueueBurn.Enabled = menuStartBurn.Enabled = activeBurnCancellation is not null ? !isBurnCancellationRequested : idle && hasImage;
        menuExportSelectedRom.Enabled = idle && hasImage;
        foreach (ToolStripItem item in menuCartridge.DropDownItems) item.Enabled = item == menuFcScan && idle;
        menuEraseEntireNor.Enabled = idle && CanEraseEntireNorCurrentScan(out _);
        menuPendingGbaCovers.Enabled = menuDeleteSelectedMapping.Enabled = menuFragmentTest.Enabled = false;
        menuItemReplaceRom.Enabled = menuItemExportRom.Enabled = false;
        if (raw) RefreshGbcImageDetails();
        else
        {
            groupBoxList.Text = T("gbc_profile", "choose", "Choose cartridge type");
            if (idle) labelStatus.Text = T("gbc_profile", "choose", "Choose cartridge type");
        }
    }
}
