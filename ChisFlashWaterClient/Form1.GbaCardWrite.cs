using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;
using BuilderSaveKind = GbaMultiMenuBuilder.Models.DetectedSaveTypeKind;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private WaterCartScanResult GbaScanAfterWrite(byte[] image, WaterCartScanResult hardware)
    {
        var result = WaterCartScanner.ScanOfflineRomImage(image);
        result.Geometry = hardware.Geometry;
        result.NorId = hardware.NorId;
        result.RawNorIdHex = hardware.RawNorIdHex;
        result.ChipName = hardware.ChipName;
        result.IsChipRecognized = hardware.IsChipRecognized;
        result.IsChm1 = hardware.IsChm1;
        return result;
    }

    private async Task BurnSingleGbaAsync()
    {
        if (activeBurnCancellation is not null) { RequestActiveBurnCancellation(); return; }
        if (isBusy || (gbaSingleRom is null && !gbaSingleNeedsRelocation) || !HasActiveScanResult()) return;
        if (!EnsureCurrentScanIsWritable(out string reason)) throw new InvalidOperationException(reason);
        var scan = currentScanResult!;
        if (scan.IsNorProbeOnly) throw new InvalidOperationException("请先分析卡带内容，再烧录单卡。");
        long length = gbaSingleRom?.Length ?? (long)GetGbaSingleGame()!.LogicalBucketCount * WaterCartScanner.BucketSizeBytes;
        if (length <= 0 || length > ChisFlash10Image.CapacityBytes || length > scan.Geometry.DeviceSizeBytes) throw new InvalidDataException("游戏长度无效或超过实际NOR容量。");
        if (MessageBox.Show(this, $"从地址0写入 { (gbaSingleRom is null ? GetGbaSingleGame()!.Title : gbaSingleRomName)}（{FormatMiBSize(length)}）？\n\n这会覆盖该范围内的原游戏或合卡菜单。ROM按原文件写入，不安装合卡菜单，不自动打补丁，独立存档芯片不改写。",
            "烧录单卡", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        byte[]? image = gbaSingleRom is null ? null : ChisFlash10Image.Build(gbaSingleRom);
        string port = GetSelectedPortName()!;
        var kind = GetRequiredSelectedProgrammerKind();
        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation; isBurnCancellationRequested = false;
        SetBusy(true, "正在烧录单卡ROM…");
        bool succeeded = false;
        try
        {
            if (image is null)
                image = await Task.Run(() => { using var protocol = new WaterDeviceProtocol(port); OpenProgrammerProtocol(protocol, kind); return ReadGbaSingleSource(protocol, kind, (int)length); });
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, kind);
                if (kind == ProgrammerKind.BeggarLegacy)
                {
                    BeginMeasuredProgress("单卡ROM", image.Length, (image.Length + 4095) / 4096);
                    int written = 0;
                    LegacyGbaRomWriter.Write(image,
                        (address, word) => { if (!protocol.TryWriteRom(address, BitConverter.GetBytes(word), 3000)) throw new IOException("单卡ROM命令未收到ACK。"); },
                        (address, length) => { byte[] data = new byte[length]; if (!protocol.TryReadRom(address, data, 3000)) throw new IOException("单卡ROM状态读取失败。"); return data; },
                        (address, data, buffer) => { if (!protocol.TryProgramRom(address, data, buffer, 60000)) throw new IOException("单卡ROM数据未收到ACK。"); },
                        Thread.Sleep,
                        (current, _, phase) => { if (phase == "write") { AdvanceMeasuredProgress(current - written, 1, "单卡ROM"); written = current; } }, cancellation.Token);
                }
                else
                {
                    int sector = scan.Geometry.SectorSizeBytes;
                    if (sector <= 0) throw new InvalidDataException("未读取有效NOR扇区尺寸。");
                    int size = checked((image.Length + sector - 1) / sector * sector);
                    byte[] aligned = Enumerable.Repeat((byte)255, size).ToArray(); image.CopyTo(aligned, 0);
                    var segment = new WaterNorWriteSegment { StartAddress = 0, Data = aligned, Label = "单卡ROM" };
                    BeginMeasuredProgress("单卡ROM", CountSegmentProgressWorkBytes(segment, scan.Geometry), CountSegmentProgressUnits(segment, scan.Geometry));
                    ProgramNorSegment(protocol, kind, scan.Geometry, segment, 1, 1, cancellation.Token);
                }
                CompleteMeasuredProgress("单卡ROM");
            });
            var refreshed = GbaScanAfterWrite(image, scan);
            gbaSingleRom = null; gbaSingleRomSave = null; gbaSingleRomName = string.Empty;
            ApplyScanResult(refreshed, false);
            succeeded = true;
            AppendLog("单卡ROM写入完成，未追加ROM读回校验。");
        }
        finally
        {
            activeBurnCancellation = null; isBurnCancellationRequested = false;
            if (!succeeded) { AbortMeasuredProgress(); currentScanResult = null; lastScannedPortName = string.Empty; }
            SetBusy(false, succeeded ? "单卡ROM写入完成" : "操作已结束，请重新分析卡带。");
        }
    }

    private BuildRequest CreateGbaConversionRequest(WaterCartScanResult scan, BurnProfileMode mode,
        string menuPath, string outputPath, IReadOnlyList<BuildGameEntry> games)
    {
        bool battery = IsBatteryPresentForSelectedMode(mode);
        FlashCartMode flashMode = mode switch
        {
            BurnProfileMode.Chm1 => FlashCartMode.Chm1,
            BurnProfileMode.Chm1NoBattery => FlashCartMode.Chm1NoBattery,
            BurnProfileMode.WaterLinkSram or BurnProfileMode.WaterLinkMultiNor => FlashCartMode.WaterlineVirtualIron,
            _ => battery ? FlashCartMode.WithBattery : FlashCartMode.NoBattery,
        };
        var geometry = scan.Geometry;
        var knownCartridge = ResolveBatterylessCartridge(scan);
        var cartridge = knownCartridge is null
            ? new CartridgeDefinition(0, scan.ChipName, geometry.DeviceSizeBytes, geometry.SectorSizeBytes, 0x80000)
            : knownCartridge with { FlashSize = geometry.DeviceSizeBytes, SectorSize = geometry.SectorSizeBytes };
        var menuType = GetSelectedGbaMenuSelection() switch
        {
            GbaMenuSelection.Universal => GbaMenuType.NsMenu,
            GbaMenuSelection.Simplified => GbaMenuType.SimplifiedNsMenu,
            _ => GbaMenuType.Classic,
        };
        return new(menuPath, outputPath, null, null, cartridge, 0x400000, flashMode, menuType, battery, false, games);
    }

    private static BuildGameEntry GbaConversionGame(string path, string title, DetectedSaveTypeInfo save,
        bool patchSram, bool batteryless, bool alreadyPatched = false, bool needsExpansion = false, string? cover = null) =>
        new(true, title, path, null, 0, patchSram, save.Kind == DetectedSaveTypeKind.Sram || patchSram,
            batteryless && !alreadyPatched, alreadyPatched, needsExpansion, (BuilderSaveKind)(int)save.Kind,
            false, false, 1, cover);

    private async Task BurnGbaConversionAsync()
    {
        EnsureGbaMulticartSupported();
        if (activeBurnCancellation is not null) { RequestActiveBurnCancellation(); return; }
        if (isBusy || !IsGbaConversionDraft() || !HasActiveScanResult()) return;
        if (!EnsureCurrentScanIsWritable(out string reason)) throw new InvalidOperationException(reason);
        var scan = currentScanResult!;
        var rows = workspaceRows.Where(row => row.PendingItem is not null).ToArray();
        if (scan.IsNorProbeOnly) throw new InvalidOperationException("请先分析卡带内容，再烧录合卡。");
        if (UsesChm1Mode(GetSelectedBurnProfileMode()) && !scan.IsChm1) throw new InvalidOperationException("尚未识别为01G/02G碎片管理硬件，不能写入CHM1合卡布局。");
        if (!ConfirmGbaConversion(rows.Length)) return;
        bool keep = gbaKeepCurrentGame!.Checked && GetGbaSingleGame() is not null;
        bool mappedSource = GetGbaSingleGame() is { } source && (source.FoundInMenuConfig || source.FoundInWlMapping);
        int keepBytes = keep ? mappedSource ? GetGbaSingleGame()!.LogicalBucketCount * WaterCartScanner.BucketSizeBytes : GetCurrentSingleGameLength() : 0;
        if (keep && (keepBytes <= 0 || keepBytes > scan.Geometry.DeviceSizeBytes)) throw new InvalidDataException("请指定当前游戏的有效读取长度。");
        if (!keep && rows.Length == 0) return;
        if (rows.Any(row => row.PendingItem!.ImportedPlatform is not (null or RomPlatform.Gba))) throw new InvalidDataException("GBA合卡列表只能包含GBA游戏。");
        if (!TryValidateWorkspaceCapacity(rows, out reason)) throw new InvalidDataException(reason);
        var mode = GetSelectedBurnProfileMode();
        byte[] menu = LoadSelectedGbaMenuRom(UsesWlTargetPlanningMode(mode), out _);
        string port = GetSelectedPortName()!;
        var kind = GetRequiredSelectedProgrammerKind();
        string staging = Path.Combine(Path.GetTempPath(), "ChisFlash-GBA-conversion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation; isBurnCancellationRequested = false;
        SetBusy(true, "正在准备合卡镜像…");
        bool writeStarted = false, succeeded = false;
        try
        {
            string menuPath = Path.Combine(staging, "menu.gba");
            File.WriteAllBytes(menuPath, menu);
            var games = rows.Select(row => GbaConversionGame(row.PendingItem!.FilePath, GetWorkspaceRowDisplayTitle(row),
                row.PendingItem.SaveTypeInfo, row.PendingItem.ApplySramPatch, UsesBatterylessPatch(mode),
                row.PendingItem.BatterylessAlreadyPatched, row.PendingItem.BatterylessCanExpand, row.PendingItem.CoverPath)).ToList();
            if (keep)
            {
                byte[] original = await Task.Run(() => { using var protocol = new WaterDeviceProtocol(port); OpenProgrammerProtocol(protocol, kind); return ReadGbaSingleSource(protocol, kind, keepBytes); });
                string originalPath = Path.Combine(staging, "current-game.gba");
                File.WriteAllBytes(originalPath, original);
                var save = ChisFlash10Image.Analyze(original);
                if (kind == ProgrammerKind.BeggarLegacy && IsBatteryPresentForSelectedMode(mode))
                {
                    byte[] sram = await Task.Run(() => { using var protocol = new WaterDeviceProtocol(port); OpenProgrammerProtocol(protocol, kind); return ReadBeggarChipSave(protocol, 0x20000); });
                    File.WriteAllBytes(Path.ChangeExtension(originalPath, ".sav"), sram);
                }
                games.Insert(0, GbaConversionGame(originalPath, GetGbaSingleGame()!.Title, save,
                    save.Kind is not (DetectedSaveTypeKind.None or DetectedSaveTypeKind.Unknown or DetectedSaveTypeKind.Sram), UsesBatterylessPatch(mode)));
            }
            var request = CreateGbaConversionRequest(scan, mode, menuPath, Path.Combine(staging, "multicart.gba"), games);
            byte[] image = await Task.Run(() =>
            {
                var result = new CompilationBuilder().Build(request, null, cancellation.Token);
                if (result.SkippedTitles.Count > 0) throw new InvalidDataException("合卡空间不足，部分游戏未能放入；尚未擦写卡带。");
                return GbaCardWritePlan.CompleteMapping(File.ReadAllBytes(result.OutputPath), UsesWaterLinkTargetMode(mode));
            });
            var segments = GbaCardWritePlan.Multicart(image, scan.Geometry.DeviceSizeBytes, scan.Geometry.SectorSizeBytes);
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, kind);
                BeginMeasuredProgress("单卡转合卡", segments.Sum(segment => CountSegmentProgressWorkBytes(segment, scan.Geometry)), segments.Sum(segment => CountSegmentProgressUnits(segment, scan.Geometry)));
                writeStarted = true;
                for (int index = 0; index < segments.Count; index++)
                    ProgramNorSegment(protocol, kind, scan.Geometry, segments[index], index + 1, segments.Count, cancellation.Token);
                CompleteMeasuredProgress("单卡转合卡");
            });
            pendingImportItems.Clear(); workspaceRows.Clear();
            gbaKeepCurrentGame.Checked = false;
            ApplyScanResult(GbaScanAfterWrite(image, scan), false);
            succeeded = true;
            AppendLog("合卡菜单和游戏写入完成，未追加ROM读回校验。");
        }
        finally
        {
            activeBurnCancellation = null; isBurnCancellationRequested = false;
            if (!succeeded) { AbortMeasuredProgress(); if (writeStarted) { currentScanResult = null; lastScannedPortName = string.Empty; } }
            SetBusy(false, succeeded ? "单卡转合卡完成" : "操作结束，草稿已保留。");
            // 仅删除本次创建的独立暂存目录。
            try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private bool ConfirmGbaConversion(int pendingGames)
    {
        using var dialog = new Form { Text = "单卡转合卡", Width = 560, Height = 260, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var menuRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        menuRow.Controls.Add(new Label { Text = "合卡菜单：" + gbaDraftMenuName, AutoSize = true, Padding = new Padding(0, 5, 6, 0) });
        layout.Controls.Add(menuRow, 0, 0);
        gbaConversionOptions!.Visible = gbaKeepCurrentGame!.Checked && GetGbaSingleGame() is not null;
        gbaKeepCurrentGame.Enabled = false;
        layout.Controls.Add(gbaConversionOptions, 0, 1);
        var note = new Label { Dock = DockStyle.Fill, Text = $"将写入{pendingGames}个新游戏。\n合卡菜单会覆盖前4 MiB；需要保留当前游戏时，请勾选并指定读取长度。\n准备完成后先写游戏区，最后写菜单目录。", AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(note, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "开始烧录", AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) =>
        {
            bool mapped = GetGbaSingleGame() is { } game && (game.FoundInMenuConfig || game.FoundInWlMapping);
            if (gbaKeepCurrentGame!.Checked && GetGbaSingleGame() is not null && !mapped && GetCurrentSingleGameLength() <= 0) { note.Text = "请选择当前游戏的读取长度。"; return; }
            if (pendingGames == 0 && (!gbaKeepCurrentGame.Checked || GetGbaSingleGame() is null)) { note.Text = "请先在列表添加游戏。"; return; }
            dialog.DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 3);
        dialog.Controls.Add(layout); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        try { return dialog.ShowDialog(this) == DialogResult.OK; }
        finally { layout.Controls.Remove(gbaConversionOptions); gbaConversionOptions.Visible = false; }
    }
}
