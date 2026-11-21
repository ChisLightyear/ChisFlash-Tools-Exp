using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private bool CanManageChm1() => currentScanResult?.IsChm1 == true &&
        !currentScanResult.NoCartridgeDetected &&
        EnsureSelectedPortHasActiveScan(out _) && EnsureCurrentScanIsWritable(out _);

    private bool NeedsChm1LayoutChange() => currentScanResult is not null &&
        Chm1MenuBuilder.RequiresConversion(currentScanResult, UsesChm1Mode(GetSelectedBurnProfileMode()), IsBatteryPresentForSelectedMode(GetSelectedBurnProfileMode()));

    private async Task InstallChm1MenuAsync(string? suppliedMenuPath = null, bool allowEmptyDirectory = false)
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
                const ProgrammerKind programmerKind = ProgrammerKind.BeggarLegacy;
                bool keepSaveAreaInPlace = Chm1MenuBuilder.CanKeepSaveAreaInPlace(scan, magic, battery);
                byte[] original = keepSaveAreaInPlace
                    ? new byte[Chm1Layout.BucketBytes]
                    : ReadRomRangeFromDevice(protocol, programmerKind, 0, Chm1Layout.BucketBytes);
                if (keepSaveAreaInPlace && !scan.WlMapping.IsDetected)
                {
                    // 普通布局只读两个标记，沿用原有的异常碎片目录识别。
                    int selectedBucket = -1;
                    byte[] signature = new byte[4];
                    foreach (int offset in new[] { 0xB0000, Chm1Layout.MappingOffset })
                    {
                        if (!ReadRomRangeWithSelectedBucket(protocol, programmerKind, (uint)offset, signature, ref selectedBucket))
                            throw new IOException(Tf("error", "read_save_range_failed",
                                "Failed to read save data at 0x{0:X6}.", offset));
                        signature.CopyTo(original, offset);
                    }
                }
                bool rebuildEmptyDirectory = allowEmptyDirectory && Chm1MenuBuilder.RequiresEmptyDirectoryRebuild(original, scan);
                byte[] head = Chm1MenuBuilder.BuildHead(original, menu, scan, magic, battery, rebuildEmptyDirectory, keepSaveAreaInPlace);
                byte[]? previousMenu = keepSaveAreaInPlace ? null : original;
                if (!rebuildEmptyDirectory && GbaMenuRomService.SupportsCovers(menu))
                    PreserveCurrentGbaMenuCovers(protocol, programmerKind, scan, head, previousMenu);
                else if (!rebuildEmptyDirectory && scan.MenuConfig.IsDetected && !scan.IsUniversalGbaMenuDetected)
                    PreserveCurrentMenuBackground(protocol, programmerKind, scan, head, magic, previousMenu);
                return (Head: head, RebuildEmptyDirectory: rebuildEmptyDirectory, KeepSaveAreaInPlace: keepSaveAreaInPlace);
            });
            string confirmation = prepared.RebuildEmptyDirectory ? EmptyMenuDirectoryConfirmation() : T("dialog", "chm1_menu_confirm",
                "Update the 01G/02G fragment management menu and layout? Game ROMs are preserved. Existing NOR saves are moved to their corresponding slots.");
            if (MessageBox.Show(this, confirmation,
                    T("ui", "update_menu", "Update Menu"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            const string label = "01G/02G fragment management menu";
            var segments = prepared.RebuildEmptyDirectory || prepared.KeepSaveAreaInPlace
                ? BuildWaterLinkInheritedMenuWriteSegments(prepared.Head, new WaterMenuConfigInfo
                {
                    Status = Chm1MenuBuilder.CreateStatus(new(), [], magic, battery),
                }, includeWlMapping: magic || prepared.RebuildEmptyDirectory)
                : [new WaterNorWriteSegment { StartAddress = 0, Data = prepared.Head, Label = label }];
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
                BeginMeasuredProgress(T("ui", "update_menu", "Update Menu"),
                    segments.Sum(segment => CountRewriteRangeProgressWorkBytes(segment.Data, scan.Geometry)),
                    segments.Sum(segment => CountRewriteRangeProgressUnits(segment.Data, scan.Geometry)));
                var sectors = segments.SelectMany(segment =>
                    BuildNorSectorWriteBatch(segment.StartAddress, segment.Data, Chm1Layout.SectorBytes, segment.Label)).ToList();
                RewriteNorSectorBatch(protocol, sectors, scan.Geometry, ProgrammerKind.BeggarLegacy);
                CompleteMeasuredProgress(T("ui", "update_menu", "Update Menu"));
            });
            ApplyScanResult(WaterCartScanner.RebuildCachedGbaMenuResult(scan, headImage: prepared.Head));
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
