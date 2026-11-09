using ClientText = ChisFlashWaterClient.Services.LocalizedText;
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
        if (!result.MenuConfig.IsDetected)
            foreach (var entry in result.Entries.Where(entry => !entry.IsMenu && entry.StartBucket == 0))
                entry.AllocatedSizeBytesOverride = image.Length;
        return result;
    }

    private async Task BurnSingleGbaAsync()
    {
        if (activeBurnCancellation is not null) { RequestActiveBurnCancellation(); return; }
        if (isBusy || (gbaSingleRom is null && !gbaSingleNeedsRelocation)) return;
        if (!EnsureSelectedPortHasActiveScan(out string scanReason)) throw new InvalidOperationException(scanReason);
        if (!EnsureCurrentScanIsWritable(out string reason)) throw new InvalidOperationException(reason);
        var scan = currentScanResult!;
        if (scan.IsNorProbeOnly) throw new InvalidOperationException(ClientText.T("client", "single_analyze_first", "Analyze the cartridge contents before burning a single-game ROM."));
        long length = gbaSingleRom?.Length ?? (long)GetGbaSingleGame()!.LogicalBucketCount * WaterCartScanner.BucketSizeBytes;
        if (length <= 0 || length > ChisFlash10Image.CapacityBytes || length > scan.Geometry.DeviceSizeBytes) throw new InvalidDataException(ClientText.T("client", "game_length_invalid", "The game length is invalid or exceeds the detected NOR capacity."));
        byte[]? image = gbaSingleRom is null ? null : PrepareGbaRomWaitcnt(ChisFlash10Image.Build(gbaSingleRom), gbaSingleWaitcntPatch);
        if (image is not null) ValidateReplacementRomLength(image.Length, gbaSingleReplacementCapacityBytes, gbaSingleRomName);
        string patchText = gbaSingleRom is not null && gbaSingleWaitcntPatch ? ClientText.T("client", "single_waitcnt_notice", "Apply the WAITCNT patch selected in the list. ") : ClientText.T("client", "single_original_notice", "Write the original ROM. ");
        if (MessageBox.Show(this, (HasPendingGbaWorkspaceClear() ? GbaWorkspaceClearConfirmation() + "\n\n" : string.Empty) + ClientText.Tf("client", "single_burn_confirm", "Write {0} ({1}) from address 0?\n\nThis overwrites the existing game or multicart menu in that range. {2}\nNo multicart menu will be installed. The separate save chip is unchanged.", gbaSingleRom is null ? GetGbaSingleGame()!.Title : gbaSingleRomName, FormatMiBSize(length), patchText),
            ClientText.T("client", "single_burn_title", "Burn Single-Game ROM"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        string port = GetSelectedPortName()!;
        var kind = GetRequiredSelectedProgrammerKind();
        bool directLegacyErase = UseDirectGbaWorkspaceErase(scan, GetSelectedBurnProfileMode());
        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation; isBurnCancellationRequested = false;
        SetBusy(true, ClientText.T("client", "single_burning", "Burning single-game ROM…"));
        bool succeeded = false;
        try
        {
            if (image is null)
                image = await Task.Run(() => { using var protocol = new WaterDeviceProtocol(port); OpenProgrammerProtocol(protocol, kind); return ReadGbaSingleSource(protocol, kind, (int)length); });
            uint[] clearSectors = PendingGbaWorkspaceEraseSectors(scan, [new WaterNorWriteSegment { Data = image }]);
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, kind);
                if (kind == ProgrammerKind.BeggarLegacy)
                {
                    BeginMeasuredProgress(ClientText.T("client", "single_rom", "Single-game ROM"), image.Length + (long)clearSectors.Length * scan.Geometry.SectorSizeBytes, (image.Length + 4095) / 4096 + clearSectors.Length);
                    EraseGbaWorkspaceSectors(protocol, kind, scan, clearSectors, directLegacyErase, cancellation.Token);
                    int written = 0;
                    LegacyGbaRomWriter.Write(image,
                        (address, word) => { if (!protocol.TryWriteRom(address, BitConverter.GetBytes(word), 3000)) throw new IOException(ClientText.T("client", "single_command_ack", "No ACK received for the single-game ROM command.")); },
                        (address, length) => { byte[] data = new byte[length]; if (!protocol.TryReadRom(address, data, 3000)) throw new IOException(ClientText.T("client", "single_status_failed", "Failed to read the single-game ROM status.")); return data; },
                        (address, data, buffer) => { if (!protocol.TryProgramRom(address, data, buffer, 60000)) throw new IOException(ClientText.T("client", "single_data_ack", "No ACK received for the single-game ROM data.")); },
                        Thread.Sleep,
                        (current, _, phase) => { if (phase == "write") { AdvanceMeasuredProgress(current - written, 1, ClientText.T("client", "single_rom", "Single-game ROM")); written = current; } }, cancellation.Token);
                }
                else
                {
                    int sector = scan.Geometry.SectorSizeBytes;
                    if (sector <= 0) throw new InvalidDataException(ClientText.T("client", "nor_sector_unread", "No valid NOR sector size has been read."));
                    int size = checked((image.Length + sector - 1) / sector * sector);
                    byte[] aligned = Enumerable.Repeat((byte)255, size).ToArray(); image.CopyTo(aligned, 0);
                    var segment = new WaterNorWriteSegment { StartAddress = 0, Data = aligned, Label = ClientText.T("client", "single_rom", "Single-game ROM") };
                    BeginMeasuredProgress(ClientText.T("client", "single_rom", "Single-game ROM"), CountSegmentProgressWorkBytes(segment, scan.Geometry) + (long)clearSectors.Length * sector, CountSegmentProgressUnits(segment, scan.Geometry) + clearSectors.Length);
                    EraseGbaWorkspaceSectors(protocol, kind, scan, clearSectors, directLegacyErase, cancellation.Token);
                    ProgramNorSegment(protocol, kind, scan.Geometry, segment, 1, 1, cancellation.Token);
                }
                CompleteMeasuredProgress(ClientText.T("client", "single_rom", "Single-game ROM"));
            });
            var refreshed = GbaScanAfterWrite(image, scan);
            gbaSingleRom = null; gbaSingleRomSave = null; gbaSingleRomName = string.Empty;
            ApplyScanResult(refreshed, false);
            succeeded = true;
            AppendLog(ClientText.T("client", "single_write_log", "Single-game ROM write completed without additional ROM readback verification."));
        }
        finally
        {
            activeBurnCancellation = null; isBurnCancellationRequested = false;
            if (!succeeded) { AbortMeasuredProgress(); currentScanResult = null; lastScannedPortName = string.Empty; }
            SetBusy(false, succeeded ? ClientText.T("client", "single_write_done", "Single-game ROM write complete") : ClientText.T("client", "operation_reanalyze", "Operation ended. Analyze the cartridge again."));
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
        if (isBusy || !IsGbaConversionDraft()) return;
        if (!EnsureSelectedPortHasActiveScan(out string scanReason)) throw new InvalidOperationException(scanReason);
        if (!EnsureCurrentScanIsWritable(out string reason)) throw new InvalidOperationException(reason);
        var scan = currentScanResult!;
        var rows = workspaceRows.Where(row => row.PendingItem is not null).ToArray();
        if (scan.IsNorProbeOnly) throw new InvalidOperationException(ClientText.T("client", "multicart_analyze_first", "Analyze the cartridge contents before burning a multicart."));
        if (UsesChm1Mode(GetSelectedBurnProfileMode()) && !scan.IsChm1) throw new InvalidOperationException(ClientText.T("client", "chm1_hardware_required", "01G/02G fragment-management hardware has not been identified. The CHM1 multicart layout cannot be written."));
        if (!ConfirmGbaConversion(rows.Length)) return;
        bool keep = gbaKeepCurrentGame!.Checked && GetGbaSingleGame() is not null;
        bool mappedSource = GetGbaSingleGame() is { } source && (source.FoundInMenuConfig || source.FoundInWlMapping);
        int keepBytes = keep ? mappedSource ? GetGbaSingleGame()!.LogicalBucketCount * WaterCartScanner.BucketSizeBytes : GetCurrentSingleGameLength() : 0;
        if (keep && (keepBytes <= 0 || keepBytes > scan.Geometry.DeviceSizeBytes)) throw new InvalidDataException(ClientText.T("client", "current_length_required", "Specify a valid read length for the current game."));
        if (!keep && rows.Length == 0) return;
        if (rows.Any(row => row.PendingItem!.ImportedPlatform is not (null or RomPlatform.Gba))) throw new InvalidDataException(ClientText.T("client", "gba_games_only", "A GBA multicart list can only contain GBA games."));
        if (!TryValidateWorkspaceCapacity(rows, out reason)) throw new InvalidDataException(reason);
        var mode = GetSelectedBurnProfileMode();
        byte[] menu = LoadSelectedGbaMenuRom(UsesWlTargetPlanningMode(mode), out _);
        string port = GetSelectedPortName()!;
        var kind = GetRequiredSelectedProgrammerKind();
        string staging = Path.Combine(Path.GetTempPath(), "ChisFlash-GBA-conversion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation; isBurnCancellationRequested = false;
        SetBusy(true, ClientText.T("client", "multicart_preparing", "Preparing multicart image…"));
        bool writeStarted = false, succeeded = false;
        try
        {
            string menuPath = Path.Combine(staging, "menu.gba");
            File.WriteAllBytes(menuPath, menu);
            var games = rows.Select((row, index) => GbaConversionGame(PrepareGbaConversionWaitcnt(row.PendingItem!, staging, index), GetWorkspaceRowDisplayTitle(row),
                row.PendingItem!.SaveTypeInfo, row.PendingItem.ApplySramPatch, UsesBatterylessPatch(mode),
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
                if (result.SkippedTitles.Count > 0) throw new InvalidDataException(ClientText.T("client", "multicart_no_space", "There is not enough space for all games. The cartridge has not been erased or written."));
                return GbaCardWritePlan.CompleteMapping(File.ReadAllBytes(result.OutputPath), UsesWaterLinkTargetMode(mode),
                    scan.Geometry.DeviceSizeBytes, WaterWlMappingCodec.IsChimeraConfiguration(scan));
            });
            var refreshed = GbaScanAfterWrite(image, scan);
            for (int index = 0; index < rows.Length; index++)
                if (rows[index].PendingItem is { ReplacementCapacityBytes: > 0 } item)
                    EnsureReplacementRomFits(item, checked(refreshed.MenuConfig.Items[index + (keep ? 1 : 0)].RomSizeBlocks * WaterRomImportPlanner.BlockSizeBytes));
            var segments = GbaCardWritePlan.Multicart(image, scan.Geometry.DeviceSizeBytes, scan.Geometry.SectorSizeBytes);
            uint[] clearSectors = PendingGbaWorkspaceEraseSectors(scan, segments);
            bool directLegacyErase = UseDirectGbaWorkspaceErase(scan, mode);
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, kind);
                BeginMeasuredProgress(ClientText.T("client", "conversion_title", "Convert to Multicart"), segments.Sum(segment => CountSegmentProgressWorkBytes(segment, scan.Geometry)) + (long)clearSectors.Length * scan.Geometry.SectorSizeBytes, segments.Sum(segment => CountSegmentProgressUnits(segment, scan.Geometry)) + clearSectors.Length);
                writeStarted = true;
                EraseGbaWorkspaceSectors(protocol, kind, scan, clearSectors, directLegacyErase, cancellation.Token);
                for (int index = 0; index < segments.Count; index++)
                    ProgramNorSegment(protocol, kind, scan.Geometry, segments[index], index + 1, segments.Count, cancellation.Token);
                CompleteMeasuredProgress(ClientText.T("client", "conversion_title", "Convert to Multicart"));
            });
            pendingImportItems.Clear(); workspaceRows.Clear();
            gbaKeepCurrentGame.Checked = false;
            ApplyScanResult(refreshed, false);
            succeeded = true;
            AppendLog(ClientText.T("client", "multicart_write_log", "Multicart menu and games written without additional ROM readback verification."));
        }
        finally
        {
            activeBurnCancellation = null; isBurnCancellationRequested = false;
            if (!succeeded) { AbortMeasuredProgress(); if (writeStarted) { currentScanResult = null; lastScannedPortName = string.Empty; } }
            SetBusy(false, succeeded ? ClientText.T("client", "conversion_done", "Multicart conversion complete") : ClientText.T("client", "draft_preserved", "Operation ended. The draft has been preserved."));
            // 仅删除本次创建的独立暂存目录。
            try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private bool ConfirmGbaConversion(int pendingGames)
    {
        using var dialog = new Form { Text = ClientText.T("client", "conversion_title", "Convert to Multicart"), Font = Font, Width = LogicalToDeviceUnits(600), Height = LogicalToDeviceUnits(320), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var menuRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        menuRow.Controls.Add(new Label { Text = ClientText.T("client", "multicart_menu_prefix", "Multicart menu:") + " " + gbaDraftMenuName, AutoSize = true, Padding = new Padding(0, 5, 6, 0) });
        layout.Controls.Add(menuRow, 0, 0);
        gbaConversionOptions!.Visible = gbaKeepCurrentGame!.Checked && GetGbaSingleGame() is not null;
        gbaKeepCurrentGame.Enabled = false;
        layout.Controls.Add(gbaConversionOptions, 0, 1);
        var note = new Label { Dock = DockStyle.Fill, Text = HasPendingGbaWorkspaceClear() ? GbaWorkspaceClearConfirmation() : ClientText.Tf("client", "conversion_confirm", "Write {0} new game(s).\nThe multicart menu will overwrite the first 4 MiB. To keep the current game, select it and specify the read length.\nAfter preparation, games are written first and the menu directory last.", pendingGames), AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(note, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = ClientText.T("client", "start_burn", "Start Burn"), AutoSize = true };
        var cancel = new Button { Text = ClientText.T("client", "cancel", "Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) =>
        {
            bool mapped = GetGbaSingleGame() is { } game && (game.FoundInMenuConfig || game.FoundInWlMapping);
            if (gbaKeepCurrentGame!.Checked && GetGbaSingleGame() is not null && !mapped && GetCurrentSingleGameLength() <= 0) { note.Text = ClientText.T("client", "current_length_select", "Select the read length for the current game."); return; }
            if (pendingGames == 0 && (!gbaKeepCurrentGame.Checked || GetGbaSingleGame() is null)) { note.Text = ClientText.T("client", "add_games_first", "Add games to the list first."); return; }
            dialog.DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 3);
        dialog.Controls.Add(layout); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        try { return dialog.ShowDialog(this) == DialogResult.OK; }
        finally { layout.Controls.Remove(gbaConversionOptions); gbaConversionOptions.Visible = false; }
    }
}
