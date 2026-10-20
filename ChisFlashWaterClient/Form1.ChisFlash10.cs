using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private byte[]? chisFlash10Rom;
    private string chisFlash10Name = string.Empty;
    private DetectedSaveTypeInfo? chisFlash10Save;
    private bool IsChisFlash10Mode() => GetSelectedBurnProfileMode() == BurnProfileMode.ChisFlash10;
    private string Chis10Text(string key, string chinese, string english) =>
        T("chisflash10", key, language.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english);

    private void ApplyChisFlash10Workspace()
    {
        if (!IsChisFlash10Mode() || dataGridViewRoms is null) return;
        HideFcWorkspace();
        RefreshChisFlash10Grid();
    }

    private void RefreshChisFlash10Grid()
    {
        RestoreMbc5GridColumns();
        dataGridViewRoms.ReadOnly = true;
        dataGridViewRoms.Rows.Clear();
        if (dataGridViewRoms.Columns["GbcDedicatedSave"] is { } column) column.Visible = false;
        if (chisFlash10Rom is not null && chisFlash10Save is not null)
        {
            int index = dataGridViewRoms.Rows.Add("1", T("value", "row_state_pending", "Pending"),
                T("value", "source_file", "File"), "GBA", chisFlash10Name,
                WaterCartScanner.DecodeGbaHeaderTitle(chisFlash10Rom), FormatMiBSize(chisFlash10Rom.Length),
                FormatDetectedSaveTypeDisplay(chisFlash10Save) + (chisFlash10Save.CodeEvidence is null && chisFlash10Save.Kind != DetectedSaveTypeKind.Unknown
                    ? Chis10Text("tag_only", "（标签，未确认代码）", " (tag only; code unconfirmed)") : string.Empty), false,
                Chis10Text("unchanged", "原文件，不打补丁", "Original ROM; no patches"), "0x000000", "");
            dataGridViewRoms.Rows[index].Cells[7].ToolTipText = chisFlash10Save.CodeEvidence is null
                ? Chis10Text("tag_evidence", "按 ROM 标签识别；未确认实际存档函数。", "Identified by ROM tag; save functions are not confirmed.")
                : Chis10Text("code_evidence", "已匹配存档代码：", "Matched save code: ") + chisFlash10Save.CodeEvidence;
        }
        groupBoxList.Text = Chis10Text("workspace", "ChisFlash 1.0V · 单游戏 32MB", "ChisFlash 1.0V · single game, 32MB");
        UpdateChisFlash10ActionButtons(isBusy);
    }

    private async Task SelectChisFlash10FileAsync()
    {
        if (isBusy) return;
        using var dialog = new OpenFileDialog { Filter = GetRomOpenFileDialogFilter(), Multiselect = false };
        if (dialog.ShowDialog(this) == DialogResult.OK) await AddChisFlash10FilesAsync(dialog.FileNames);
    }

    private async Task AddChisFlash10FilesAsync(IEnumerable<string> paths)
    {
        if (isBusy || !IsChisFlash10Mode()) return;
        string[] files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length != 1) throw new InvalidDataException(Chis10Text("one_rom", "1.0V 每次只导入一个 GBA ROM。", "1.0V accepts one GBA ROM at a time."));
        if (new FileInfo(files[0]).Length is < 192 or > ChisFlash10Image.CapacityBytes)
            throw new InvalidDataException(Chis10Text("size", "1.0V 游戏 ROM 不能超过 32MB。", "A 1.0V game ROM cannot exceed 32MB."));
        SetBusy(true, Chis10Text("analyzing", "正在分析 ROM 实际存档类型…", "Analyzing the ROM save implementation…"));
        try
        {
            var prepared = await Task.Run(() =>
            {
                byte[] data = File.ReadAllBytes(files[0]);
                return (Data: data, Save: ChisFlash10Image.Analyze(data));
            });
            chisFlash10Rom = prepared.Data;
            chisFlash10Save = prepared.Save;
            chisFlash10Name = Path.GetFileName(files[0]);
            AppendLog($"1.0V · {chisFlash10Name} · {FormatDetectedSaveTypeDisplay(prepared.Save)}");
            if (prepared.Save.CodeEvidence == "SRAM_LEGACY_BANKED_128K")
                AppendLog(Chis10Text("legacy_bank", "此 ROM 使用旧烧录卡的 SRAM 换页代码；识别为 SRAM 不代表其换页方式与 1.0V 固件一致。",
                    "This ROM uses a legacy cartridge SRAM bank switch. SRAM detection does not establish compatibility with the 1.0V firmware bank switch."));
            RefreshChisFlash10Grid();
        }
        finally { SetBusy(false, Chis10Text("ready", "1.0V：原 ROM 已就绪", "1.0V: original ROM ready")); }
    }

    private void ClearChisFlash10Image()
    {
        if (isBusy) return;
        chisFlash10Rom = null;
        chisFlash10Save = null;
        chisFlash10Name = string.Empty;
        RefreshChisFlash10Grid();
    }

    private bool CanWriteChisFlash10() => IsChisFlash10Mode() &&
        IsBeggarCompatibleSelectedProgrammer() && GetSelectedBeggarCartMode() == BeggarCartMode.Gba &&
        HasActiveScanResult() && currentScanResult is { NoCartridgeDetected: false, IsWriteProtected: false } scan &&
        !scan.IsBeggarMaxGbcMode && !scan.IsChm1 &&
        scan.Geometry.DeviceSizeBytes == ChisFlash10Image.CapacityBytes &&
        scan.Geometry.PrimaryCommandSetId == 2 && scan.Geometry.SectorSizeBytes > 0 &&
        scan.Geometry.BufferWriteBytes is >= 0 and <= ushort.MaxValue &&
        (long)scan.Geometry.SectorSizeBytes * scan.Geometry.SectorCount == ChisFlash10Image.CapacityBytes;

    private async Task ExportChisFlash10RomAsync()
    {
        if (isBusy || chisFlash10Rom is null) return;
        using var dialog = new SaveFileDialog { Filter = "GBA ROM (*.gba)|*.gba", FileName = chisFlash10Name };
        if (dialog.ShowDialog(this) == DialogResult.OK) await File.WriteAllBytesAsync(dialog.FileName, chisFlash10Rom);
    }

    private async Task HandleChisFlash10PrimaryActionAsync()
    {
        if (activeBurnCancellation is not null) { RequestActiveBurnCancellation(); return; }
        if (isBusy || chisFlash10Rom is null || chisFlash10Save is null) return;
        if (!CanWriteChisFlash10()) { await ExportChisFlash10RomAsync(); return; }
        string message = chisFlash10Name + Environment.NewLine + FormatDetectedSaveTypeDisplay(chisFlash10Save) + Environment.NewLine + Environment.NewLine +
            Chis10Text("confirm", "将按盖中盖原版流程擦写该 ROM 覆盖的范围。原文件不打补丁，不安装合卡菜单，也不改写独立存档芯片。当前固件是否支持该存档类型，需与卡带固件对应。继续烧录？",
                "Program this ROM using the original Beggar procedure and its file range? The ROM is not patched. No multicart menu is installed and the separate save chip is not written. Save compatibility depends on the cartridge firmware.");
        if (MessageBox.Show(this, message, "ChisFlash 1.0V", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        byte[] image = ChisFlash10Image.Build(chisFlash10Rom);
        string port = GetSelectedPortName()!;
        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation;
        isBurnCancellationRequested = false;
        SetBusy(true, Chis10Text("writing", "正在烧录 1.0V 原 ROM…", "Programming the original 1.0V ROM…"));
        try
        {
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
                BeginMeasuredProgress("ChisFlash 1.0V", image.Length, (image.Length + 4095) / 4096);
                int written = 0;
                LegacyGbaRomWriter.Write(image,
                    (address, value) =>
                    {
                        if (!protocol.TryWriteRom(address, BitConverter.GetBytes(value)))
                            throw new IOException("盖中盖 ROM 命令未收到 ACK。");
                    },
                    (address, length) =>
                    {
                        byte[] data = new byte[length];
                        if (!protocol.TryReadRom(address, data)) throw new IOException("盖中盖读取失败。");
                        return data;
                    },
                    (address, data, bufferBytes) =>
                    {
                        if (!protocol.TryProgramRom(address, data, bufferBytes, Timeout.Infinite))
                            throw new IOException("盖中盖 ROM 数据未收到 ACK。");
                    },
                    Thread.Sleep,
                    (current, total, phase) =>
                    {
                        if (phase == "write")
                        {
                            AdvanceMeasuredProgress(current - written, 1, "ChisFlash 1.0V");
                            written = current;
                        }
                        else AppendLog($"ChisFlash 1.0V · {phase} {current}/{total}");
                    }, cancellation.Token);
                CompleteMeasuredProgress("ChisFlash 1.0V");
            });
        }
        finally
        {
            activeBurnCancellation = null;
            isBurnCancellationRequested = false;
            currentScanResult = null;
            lastScannedPortName = string.Empty;
            SetBusy(false, Chis10Text("rescan", "操作结束，请重新分析卡带。", "Operation ended; analyze the cartridge again."));
        }
    }

    private void UpdateChisFlash10ActionButtons(bool busy)
    {
        bool idle = !busy && !isAnalyzingQueueImports;
        bool hasRom = chisFlash10Rom is not null;
        menuRom.Enabled = menuCartridge.Enabled = true;
        menuSave.Enabled = false;
        menuFcScan.Text = T("ui", "analyze_configuration", "分析配置");
        foreach (ToolStripItem item in menuCartridge.DropDownItems) item.Enabled = item == menuFcScan && idle;
        menuScan.Enabled = buttonScan.Enabled = idle && !string.IsNullOrWhiteSpace(GetSelectedPortName());
        menuFcScan.Enabled = idle;
        buttonRefreshPorts.Enabled = menuRefreshPorts.Enabled = idle;
        comboBoxPorts.Enabled = comboBoxLanguage.Enabled = comboBoxBurnProfileMode.Enabled = idle;
        UpdateProgrammerModeSelectorUi(idle);
        buttonQueueAdd.Enabled = menuAddRom.Enabled = menuReplaceSelectedRom.Enabled = idle;
        buttonQueueRemove.Enabled = menuRemoveSelectedRom.Enabled = buttonQueueClear.Enabled = menuClearRomQueue.Enabled = idle && hasRom;
        buttonQueueAdd.Text = Chis10Text("add", "导入／更换 ROM", "Import / replace ROM");
        buttonQueueClear.Text = Chis10Text("clear", "清除待烧录 ROM", "Clear pending ROM");
        buttonQueueBurn.Text = menuStartBurn.Text = activeBurnCancellation is not null ? T("ui", "cancel_burn", "Cancel") :
            CanWriteChisFlash10() ? Chis10Text("burn", "烧录原 ROM", "Program original ROM") : Chis10Text("export", "导出原 ROM", "Export original ROM");
        buttonQueueBurn.Enabled = menuStartBurn.Enabled = activeBurnCancellation is not null ? !isBurnCancellationRequested : idle && hasRom;
        menuExportSelectedRom.Enabled = idle && hasRom;
        menuPendingGbaCovers.Enabled = menuDeleteSelectedMapping.Enabled = menuFragmentTest.Enabled = false;
        menuItemReplaceRom.Enabled = menuItemExportRom.Enabled = false;
        menuItemDeleteMapping.Enabled = menuItemExportSave.Enabled = menuItemImportSave.Enabled = menuItemClearSave.Enabled = false;
        menuItemExportChipSaveContext.Enabled = menuItemImportChipSaveContext.Enabled = menuItemClearChipSaveContext.Enabled = false;
        if (menuFcTools is not null) menuFcTools.Visible = false;
        if (gbaMenuOptions is not null) gbaMenuOptions.Visible = false;
        if (checkBoxWaitcntPatch is not null) checkBoxWaitcntPatch.Enabled = false;
        if (gbcMenuOptions is not null) gbcMenuOptions.Visible = false;
    }
}
