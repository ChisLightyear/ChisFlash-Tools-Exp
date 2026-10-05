using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private bool CanManageChm1() => currentScanResult?.IsChm1 == true &&
        !currentScanResult.NoCartridgeDetected &&
        EnsureSelectedPortHasActiveScan(out _) && EnsureCurrentScanIsWritable(out _);

    private bool NeedsChm1LayoutChange() => currentScanResult is not null &&
        Chm1MenuBuilder.RequiresConversion(currentScanResult, UsesChm1Mode(GetSelectedBurnProfileMode()), IsBatteryPresentForSelectedMode(GetSelectedBurnProfileMode()));

    private async Task InstallChm1MenuAsync(string? suppliedMenuPath = null)
    {
        if (!CanManageChm1()) throw new InvalidOperationException(T("error", "chm1_scan_required", "Read and identify the 01G/02G fragment management cartridge first."));
        var scan = currentScanResult!;
        string portName = GetSelectedPortName()!;
        bool magic = UsesChm1Mode(GetSelectedBurnProfileMode());
        bool battery = IsBatteryPresentForSelectedMode(GetSelectedBurnProfileMode());
        GbaMenuSelection selection = GetSelectedGbaMenuSelection();
        byte[] menu = suppliedMenuPath is null
            ? LoadSelectedGbaMenuRom(magic, selection, out _)
            : LoadMenuRomForUpdate(suppliedMenuPath, magic, out _);
        SetBusy(true, T("status", "chm1_preparing", "Preparing 01G/02G fragment management menu..."));
        try
        {
            var prepared = await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
                byte[] original = ReadRomRangeFromDevice(protocol, ProgrammerKind.BeggarLegacy, 0, Chm1Layout.BucketBytes);
                byte[] head = Chm1MenuBuilder.BuildHead(original, menu, scan, magic, battery);
                if (GbaMenuRomService.SupportsCovers(menu))
                    PreserveCurrentGbaMenuCovers(protocol, ProgrammerKind.BeggarLegacy, scan, head);
                else if (scan.MenuConfig.IsDetected && !scan.IsUniversalGbaMenuDetected)
                    PreserveCurrentMenuBackground(protocol, ProgrammerKind.BeggarLegacy, scan, head, magic);
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChisFlashWaterClient", "chm1-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "head.gba"), original);
                File.WriteAllBytes(Path.Combine(directory, "sram.sav"), ReadBeggarChipSave(protocol, 0x20000));
                return (Head: head, Backup: directory);
            });
            AppendLog(Tf("log", "chm1_backup", "01G/02G fragment management backup: {0}", prepared.Backup));
            if (MessageBox.Show(this, T("dialog", "chm1_menu_confirm",
                    "Update the 01G/02G fragment management menu and layout? Game ROMs are preserved. Existing NOR saves are moved to their corresponding slots; a head/SRAM backup has been saved."),
                    T("ui", "update_menu", "Update Menu"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
                BeginMeasuredProgress(T("ui", "update_menu", "Update Menu"),
                    CountRewriteRangeProgressWorkBytes(prepared.Head, scan.Geometry), CountRewriteRangeProgressUnits(prepared.Head, scan.Geometry));
                RewriteNorRange(protocol, 0, prepared.Head, scan.Geometry, "01G/02G fragment management menu", ProgrammerKind.BeggarLegacy);
                CompleteMeasuredProgress(T("ui", "update_menu", "Update Menu"));
            });
            var refreshed = await ReadScanResultAsync(portName);
            if (!TryApplyScanReadAttempt(refreshed, out string failure)) throw new IOException(failure);
            AppendLog(T("log", "chm1_menu_done", "01G/02G fragment management menu and save layout updated."));
        }
        finally
        {
            SetBusy(false, T("status", "chm1_ready", "Ready"));
        }
    }

    private void DiscardChm1PendingSave(WaterDeviceProtocol protocol, WaterCartScanResult scan, SelectedSaveTargetInfo target)
    {
        if (!scan.IsChm1 || target.Kind != SelectedSaveStorageKind.LegacyFlashSlot ||
            scan.MenuConfig.Status.LastBootSaveType == 0 || scan.MenuConfig.Status.LastBootSaveIndex != target.SaveSlotIndex) return;
        var status = ClearWorkspaceLastBootSelection(scan.MenuConfig.Status);
        byte[] sector = WaterMenuConfigCodec.BuildStatusSector(status);
        RewriteNorSector(protocol, (uint)status.StatusAddress, sector, scan.Geometry, "01G/02G fragment management save status", ProgrammerKind.BeggarLegacy);
        scan.MenuConfig.Status = status;
    }
}
