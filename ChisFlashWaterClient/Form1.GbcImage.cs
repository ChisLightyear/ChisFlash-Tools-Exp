using System.IO;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private Panel? gbcImageWorkspace;
    private Label? gbcImageDetails;
    private byte[]? gbcImageData;
    private string gbcImageName = string.Empty;
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
        string note = T("gbc_profile", "raw_notice", "Full-image mode: import one ROM or prepared BIN. Bytes remain unchanged; no free-multicart menu or save directory is inserted.");
        gbcImageDetails.Text = GetBurnProfileModeDisplayName(GetSelectedBurnProfileMode()) + Environment.NewLine + Environment.NewLine + note + Environment.NewLine + Environment.NewLine +
            (gbcImageData is null ? T("gbc_profile", "add_image", "Import one ROM / BIN") : gbcImageName + " · " + FormatMiBSize(gbcImageData.Length)) + Environment.NewLine +
            T("ui", "capacity", "Capacity") + ": " + FormatMiBSize(SelectedGbcImageCapacity());
        if (gbcImageData is not null && gbcImageData.Length > SelectedGbcImageCapacity())
            gbcImageDetails.Text += Environment.NewLine + T("gbc_profile", "image_too_large", "Image exceeds the selected cartridge capacity.");
        groupBoxList.Text = GetBurnProfileModeDisplayName(GetSelectedBurnProfileMode()) + " · " + T("gbc_profile", "full_image", "Full ROM image");
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
        if (files.Length != 1) throw new InvalidDataException(T("gbc_profile", "one_image", "Select exactly one ROM or prepared BIN image."));
        long length = new FileInfo(files[0]).Length;
        if (length <= 0 || length > SelectedGbcImageCapacity()) throw new InvalidDataException(T("gbc_profile", "image_too_large", "Image is empty or exceeds the selected cartridge capacity."));
        byte[] data = await File.ReadAllBytesAsync(files[0]);
        if (data.Length == 0 || data.Length > SelectedGbcImageCapacity()) throw new InvalidDataException(T("gbc_profile", "image_too_large", "Image is empty or exceeds the selected cartridge capacity."));
        gbcImageData = data;
        gbcImageName = Path.GetFileName(files[0]);
        RefreshGbcImageDetails();
        UpdateActionButtons(isBusy);
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
        if (gbcImageData is null || gbcImageData.Length == 0 || gbcImageData.Length > capacity)
            throw new InvalidDataException(T("gbc_profile", "image_too_large", "Image is empty or exceeds the selected cartridge capacity."));
        byte[] image = new byte[capacity];
        Array.Fill(image, (byte)255);
        gbcImageData.CopyTo(image, 0);
        return image;
    }

    private async Task ExportGbcImageAsync()
    {
        if (isBusy || gbcImageData is null) return;
        using var dialog = new SaveFileDialog { Filter = "Complete ROM (*.bin)|*.bin", FileName = Path.GetFileNameWithoutExtension(gbcImageName) + ".bin" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await File.WriteAllBytesAsync(dialog.FileName, BuildGbcImage());
    }

    private bool CanWriteGbcImage() => IsGbcRawImageMode() && GetSelectedProgrammerKind() == ProgrammerKind.BeggarLegacy &&
        GetSelectedBeggarCartMode() == BeggarCartMode.Gb && HasActiveScanResult() &&
        currentScanResult is { IsBeggarMaxGbcMode: true, IsWriteProtected: false } scan && scan.Geometry.DeviceSizeBytes == SelectedGbcImageCapacity() &&
        scan.Geometry.PrimaryCommandSetId == 2 && scan.Geometry.RegionCount == 1 &&
        scan.Geometry.BufferWriteBytes is > 0 and <= ushort.MaxValue && scan.Geometry.SectorSizeBytes >= GbcNorImageWriter.BankBytes &&
        (long)scan.Geometry.SectorSizeBytes * scan.Geometry.SectorCount == scan.Geometry.DeviceSizeBytes;

    private async Task HandleGbcImagePrimaryActionAsync()
    {
        if (activeBurnCancellation is not null) { RequestActiveBurnCancellation(); return; }
        if (isBusy || gbcImageData is null) return;
        if (!CanWriteGbcImage()) { await ExportGbcImageAsync(); return; }
        if (MessageBox.Show(this, T("gbc_profile", "burn_confirm", "Erase the entire detected NOR, write the imported image (unused bytes filled FF), and verify it? Existing ROM content is replaced; SRAM is not erased. Back up first."),
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
        bool hasImage = raw && gbcImageData is { Length: > 0 } && gbcImageData.Length <= SelectedGbcImageCapacity();
        menuRom.Enabled = menuCartridge.Enabled = true;
        menuSave.Enabled = false;
        if (menuFcTools is not null) menuFcTools.Visible = false;
        buttonScan.Enabled = menuScan.Enabled = idle && !string.IsNullOrWhiteSpace(GetSelectedPortName()) && TryGetSelectedKnownProgrammerKind(out _, out _);
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
        foreach (ToolStripItem item in menuCartridge.DropDownItems) item.Enabled = false;
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
