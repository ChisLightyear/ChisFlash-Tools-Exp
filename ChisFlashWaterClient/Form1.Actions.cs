using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using ChisFlashWaterClient.Services;
using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private sealed class SelectedRomExportTargetInfo
    {
        public WaterCartEntry Entry { get; init; } = new();
        public uint StartAddress { get; init; }
        public int ReadLength { get; init; }
        public int ExportLength { get; init; }
        public bool IsGbc { get; init; }
        public bool IsTrimmedAfterRead { get; init; }
        public string SourceDescription { get; init; } = string.Empty;
        public string SuggestedExtension { get; init; } = "gba";
    }

    private string? replaceableLogKey;
    private int replaceableLogStart = -1;
    private int replaceableLogLength;
    private string replaceableLogTimestamp = string.Empty;
    private readonly Lock pendingProgressLogSync = new();
    private readonly Dictionary<string, string> pendingProgressLogs = [];
    private int pendingProgressLogFlushScheduled;

    private sealed class WaterLinkMenuUpdatePlan
    {
        public WaterMenuConfigInfo MenuConfig { get; init; } = new();
        public byte[] HeadAreaImage { get; init; } = [];
        public string MenuRomPath { get; init; } = string.Empty;
        public bool IsLegacyMigration { get; init; }
        public bool IsMetadataRepair { get; init; }
        public List<WaterCartEntry> DroppedLegacyEntries { get; init; } = [];
        public List<WaterCartEntry> AddedHeaderOnlyEntries { get; init; } = [];
        public int MappedBucketCount { get; init; }
    }

    private sealed class LegacyMenuUpdatePlan
    {
        public WaterMenuConfigInfo MenuConfig { get; init; } = new();
        public byte[] HeadAreaImage { get; init; } = [];
        public string MenuRomPath { get; init; } = string.Empty;
        public string LayoutSource { get; init; } = string.Empty;
        public bool SupportsCovers { get; init; }
    }

    private void DeleteSelectedMapping()
    {
        if (isBusy || isAnalyzingQueueImports)
        {
            return;
        }

        if (IsWorkspaceModificationLocked())
        {
            AppendLog(GetWorkspaceLockedMessage());
            return;
        }

        if (!TryGetSelectedWorkspaceRow(out WorkspaceListRow? selectedRow, out int rowIndex) ||
            selectedRow?.CartEntry is not WaterCartEntry entry ||
            !entry.CanDelete)
        {
            AppendLog(T("log", "delete_mapping_no_selection", "The current row does not contain a deletable game mapping."));
            return;
        }

        string bucketText = WaterCartEntry.FormatBucketList(entry.PhysicalBuckets);
        string message = Tf(
            "dialog",
            "delete_mapping_confirm_message",
            "This will remove the selected game from the current workspace mapping and release these 4MiB blocks: {0}\n\nThis does not erase NOR ROM data immediately and does not write to the cart right now. The menu metadata will be updated only after you click Start Burn.\n\nDo you want to continue?",
            bucketText);

        DialogResult confirm = MessageBox.Show(
            this,
            message,
            T("dialog", "delete_mapping_confirm_title", "Delete Mapping"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        workspaceRows.RemoveAt(rowIndex);
        RefreshWorkspaceGrid(GetSafeSelectionIndex(rowIndex, workspaceRows.Count));
        AppendLog(Tf(
            "log",
            "delete_mapping_queued",
            "Removed game mapping from the workspace: #{0:000} {1} | buckets {2}. This change has not been written to the cart yet; click Start Burn to apply it.",
            entry.EntrySlot + 1,
            entry.Title,
            bucketText));
    }

    private async Task ReplaceSelectedRomAsync()
    {
        if (IsMbc5EightMiBMode()) { await SelectMbc5FilesAsync(); return; }
        if (isBusy || isAnalyzingQueueImports)
        {
            return;
        }

        if (IsWorkspaceModificationLocked())
        {
            AppendLog(GetWorkspaceLockedMessage());
            return;
        }

        if (!TryGetSelectedWorkspaceRow(out WorkspaceListRow? selectedRow, out int rowIndex) ||
            selectedRow?.CartEntry is not WaterCartEntry entry ||
            entry.IsMenu ||
            (!entry.FoundInMenuConfig && !entry.FoundInWlMapping && entry.RomOffsetBank < 0))
        {
            AppendLog(T("log", "replace_rom_no_selection", "The current row does not contain a replaceable ROM mapping."));
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = T("dialog", "replace_rom_title", "Replace ROM"),
            Filter = GetRomOpenFileDialogFilter(),
            Multiselect = false,
            CheckFileExists = true,
            AddExtension = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        if (!IsSupportedRomFile(dialog.FileName))
        {
            AppendLog(Tf(
                "log",
                "replace_rom_unsupported",
                "The selected file is not a supported ROM: {0}",
                dialog.FileName));
            return;
        }

        PendingRomImportItem replacementItem;
        SetQueueAnalysisState(true, 1);
        try
        {
            bool isGbcWorkspaceMode = IsGbcWorkspaceMode();
            replacementItem = await Task.Run(() =>
            {
                var imported=CreatePendingRomImportItemsFromSource(dialog.FileName,isGbcWorkspaceMode);
                if(imported.Items.Count!=1)throw new InvalidDataException("替换需要一个游戏；含多个游戏的ZIP请拖入主列表。");
                return imported.Items[0];
            });
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "replace_rom_analyze_failed", "Failed to analyze the replacement ROM: {0}", ex.Message));
            return;
        }
        finally
        {
            SetQueueAnalysisState(false, 0);
        }

        BurnProfileMode burnProfileMode = GetSelectedBurnProfileMode();
        if (RequiresBatterylessPatchButUnavailable(replacementItem, burnProfileMode))
        {
            AppendLog(Tf(
                "log",
                "replace_rom_batteryless_unavailable",
                "Replacement cancelled: the current mode requires a batteryless patch for {0}, but this ROM does not have enough payload space.",
                replacementItem.FileName));
            return;
        }

        int plannedRomLengthBytes = GetEstimatedPreparedRomLengthBytes(replacementItem, replacementItem.FileSizeBytes, burnProfileMode);
        MatchPendingGbaCovers([replacementItem]);
        int requiredAllocatedSizeBytes = EstimateWorkspaceAllocatedSizeBytes(replacementItem, plannedRomLengthBytes, burnProfileMode);
        int targetAllocatedSizeBytes = GetCartEntryAllocatedSizeBytes(entry);
        if (currentScanResult?.IsBeggarMaxGbcMode != true &&
            requiredAllocatedSizeBytes > targetAllocatedSizeBytes)
        {
            AppendLog(Tf(
                "log",
                "replace_rom_size_mismatch",
                "Replacement cancelled: {0} needs {1} after patch preparation, but the target mapping only allows {2}.",
                replacementItem.FileName,
                FormatMiBSize(requiredAllocatedSizeBytes),
                FormatMiBSize(targetAllocatedSizeBytes)));
            return;
        }

        replacementItem.GbcDedicatedSaveAllocationOverride = selectedRow.GbcDedicatedSaveAllocationOverride;
        selectedRow.PendingItem = replacementItem;
        selectedRow.IsLoadingPlaceholder = false;
        selectedRow.DisplayTitleOverride = string.Empty;
        RefreshWorkspaceGrid(rowIndex);

        AppendLog(Tf(
            "log",
            "replace_rom_queued",
            "Queued ROM replacement: #{0:000} {1} -> {2} | target alloc {3} | patch {4}",
            entry.EntrySlot + 1,
            entry.Title,
            replacementItem.FileName,
            FormatMiBSize(targetAllocatedSizeBytes),
            FormatPendingPatchDisplay(replacementItem)));
    }

    private async Task HandlePrimaryQueueActionAsync()
    {
        if (IsMbc5EightMiBMode()) { await HandleMbc5PrimaryActionAsync(); return; }
        if (IsFcWorkspaceActive)
        {
            if (fcWorkspace!.IsBusy) fcWorkspace.Cancel();
            else if (CanUseFcDevice) await fcWorkspace.BurnAsync();
            return;
        }
        if (activeBurnCancellation is not null)
        {
            RequestActiveBurnCancellation();
            return;
        }

        if(CanWriteMdQueue){await ActivateFcWorkspaceAsync();return;}
        if(HasUnreadyPlatformImports(workspaceRows)){AppendLog("列表含其他平台或尚未开放的核心，请选择对应模式后烧录。");return;}

        MissingMenuActionMode missingMenuActionMode = GetMissingMenuActionMode();
        if (IsWorkspaceModificationLocked() && missingMenuActionMode != MissingMenuActionMode.None)
        {
            if (UsesSelectedWaterLinkTargetMode())
            {
                await UpdateWaterLinkMenuAsync();
            }
            else
            {
                await RepairMissingMenuAsync();
            }
            return;
        }

        if (currentScanResult?.IsBeggarMaxGbcMode == true &&
            GetSelectedGbcMenuLanguageMode() == GbcMenuLanguageMode.English &&
            !HasWorkspaceChanges())
        {
            await UpdateBeggarMaxMenuAsync();
            return;
        }

        await BurnQueuedRomFilesAsync();
    }

    private void RequestActiveBurnCancellation()
    {
        if (activeBurnCancellation is null || isBurnCancellationRequested)
        {
            return;
        }

        isBurnCancellationRequested = true;
        activeBurnCancellation.Cancel();
        AppendLog(T("log", "burn_cancel_requested", "Cancel requested. Waiting for the current flash operation to stop..."));
        labelStatus.Text = T("status", "burn_cancelling", "Cancelling burn...");
        UpdateActionButtons(isBusy);
    }

    private void ClearActiveBurnCancellation(CancellationTokenSource burnCancellation)
    {
        if (!ReferenceEquals(activeBurnCancellation, burnCancellation))
        {
            return;
        }

        activeBurnCancellation = null;
        isBurnCancellationRequested = false;
    }

    private async Task ExportSelectedEntryRomAsync()
    {
        if (IsMbc5EightMiBMode()) { await ExportMbc5SlotAsync(); return; }
        if (IsFcWorkspaceActive) { await fcWorkspace!.ExportSelectedRomAsync(); return; }
        if (!TryResolveSelectedRomExportTarget(out SelectedRomExportTargetInfo? target, out string reason) || target is null)
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        string title = string.IsNullOrWhiteSpace(target.Entry.Title)
            ? Tf("value", "untitled_entry_slot", "Untitled Entry #{0:000}", target.Entry.EntrySlot + 1)
            : target.Entry.Title;
        string safeFileName = SanitizeSaveFileName(title);
        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();

        using var dialog = new SaveFileDialog
        {
            Title = T("dialog", "export_rom_title", "Export ROM"),
            FileName = $"{safeFileName}.{target.SuggestedExtension}",
            Filter = T(
                "dialog",
                "export_rom_filter",
                "ROM File (*.gba;*.gbc;*.gb)|*.gba;*.gbc;*.gb|All files (*.*)|*.*"),
            AddExtension = true,
            DefaultExt = target.SuggestedExtension,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        SetBusy(true, T("status", "export_rom_busy", "Exporting ROM..."));
        AppendLog(Tf(
            "log",
            "export_selected_rom_start",
            "Start exporting ROM for {0}. Source={1}, read={2}, save={3}.",
            title,
            target.SourceDescription,
            FormatKiBSize(target.ReadLength),
            FormatKiBSize(target.ExportLength)));

        try
        {
            byte[] romData = await Task.Run(() => ExportSelectedRomFromDevice(portName, programmerKind, target));
            File.WriteAllBytes(dialog.FileName, romData);
            AppendLog(Tf("log", "export_selected_rom_done", "ROM exported successfully: {0}", dialog.FileName));
            SetBusy(false, T("status", "export_rom_done", "ROM exported"));
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "export_selected_rom_failed", "Exporting ROM failed: {0}", ex.Message));
            SetBusy(false, T("status", "export_rom_failed", "ROM export failed"));
        }
    }

    private async Task ExportChipSaveAsync()
    {
        if (!TryResolveChipSaveExportTarget(out WaterCartEntry? entry, out int saveSizeBytes, out string reason) ||
            entry is null)
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        string title = string.IsNullOrWhiteSpace(entry.Title)
            ? Tf("value", "untitled_entry_slot", "Untitled Entry #{0:000}", entry.EntrySlot + 1)
            : entry.Title;
        string fileName = $"{SanitizeSaveFileName(title)}.sav";

        using var dialog = new SaveFileDialog
        {
            Title = T("dialog", "export_chip_save_title", "Export Chip Save"),
            FileName = fileName,
            Filter = T("dialog", "export_chip_save_filter", "Save File (*.sav)|*.sav|All files (*.*)|*.*"),
            AddExtension = true,
            DefaultExt = "sav",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        SetBusy(true, T("status", "export_chip_save_busy", "Exporting chip save..."));
        AppendLog(Tf(
            "log",
            "export_chip_save_start",
            "Start exporting the current chip save for {0}. Size={1}.",
            title,
            FormatKiBSize(saveSizeBytes)));

        try
        {
            byte[] saveData = await Task.Run(() => ExportChipSaveFromDevice(portName, saveSizeBytes));
            File.WriteAllBytes(dialog.FileName, saveData);
            AppendLog(Tf(
                "log",
                "export_chip_save_done",
                "Chip save exported successfully: {0}",
                dialog.FileName));
            SetBusy(false, T("status", "export_chip_save_done", "Chip save exported"));
        }
        catch (Exception ex)
        {
            AppendLog(Tf(
                "log",
                "export_chip_save_failed",
                "Exporting the chip save failed: {0}",
                ex.Message));
            SetBusy(false, T("status", "export_chip_save_failed", "Chip save export failed"));
        }
    }

    private enum SelectedSaveStorageKind
    {
        WaterLinkLogicalSlot,
        LegacyFlashSlot,
        BeggarMaxRamSlot,
        BatterylessRomTail,
    }

    private sealed class SelectedSaveTargetInfo
    {
        public WaterCartEntry Entry { get; init; } = new();
        public SelectedSaveStorageKind Kind { get; init; }
        public int EntrySlot { get; init; } = -1;
        public uint SaveAddress { get; init; }
        public int SaveLength { get; init; }
        public int? SaveSlotIndex { get; init; }
        public string SourceDescription { get; init; } = string.Empty;
        public bool SupportsClear { get; init; }
    }

    private sealed class SaveExportSizeOption
    {
        public string DisplayText { get; init; } = string.Empty;
        public int? SizeBytes { get; init; }

        public override string ToString() => DisplayText;
    }

    private bool TryResolveSelectedRomExportTarget(out SelectedRomExportTargetInfo? target, out string reason)
    {
        target = null;
        reason = string.Empty;

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!TryGetSelectedEntry(out WaterCartEntry? entry) || entry is null || entry.IsMenu)
        {
            reason = T("error", "rom_action_requires_entry", "Please select a valid game entry first.");
            return false;
        }

        if (!EnsureCurrentScanCapacityValid(out reason))
        {
            return false;
        }

        if (!TryGetSelectedKnownProgrammerKind(out ProgrammerKind programmerKind, out reason))
        {
            return false;
        }

        WaterCartScanResult scanSnapshot = currentScanResult ?? throw new InvalidOperationException("No active scan result.");
        bool isGbc = scanSnapshot.IsBeggarMaxGbcMode;
        int readLength;
        int exportLength;
        uint startAddress;
        string sourceDescription;
        bool isTrimmedAfterRead = false;

        if (isGbc)
        {
            if (entry.RomOffsetBank >= 0 && entry.RomBankCount > 0)
            {
                readLength = checked(entry.RomBankCount * GameBoyRomHeaderService.RomBankSize);
                exportLength = readLength;
                startAddress = checked((uint)(entry.RomOffsetBank * GameBoyRomHeaderService.RomBankSize));
                sourceDescription = Tf("value", "beggar_max_rom_range", "MAX ROM banks {0}-{1}", entry.RomOffsetBank, entry.RomOffsetBank + entry.RomBankCount - 1);
            }
            else
            {
                readLength = Math.Max(GameBoyRomHeaderService.RomBankSize * 2, GetCartEntryAllocatedSizeBytes(entry));
                exportLength = readLength;
                startAddress = GetBeggarMaxFallbackRomStartAddress(entry);
                sourceDescription = Tf("value", "beggar_max_slot_source", "MAX slot #{0:000}", entry.EntrySlot + 1);
            }
        }
        else
        {
            readLength = GetCartEntryAllocatedSizeBytes(entry);
            if (readLength <= 0)
            {
                reason = T("error", "rom_export_size_invalid", "The selected ROM range is invalid.");
                return false;
            }

            exportLength = entry.WlRecord is not null
                ? Math.Max(WaterCartScanner.BucketSizeBytes, entry.LogicalBucketCount * WaterCartScanner.BucketSizeBytes)
                : readLength;
            startAddress = entry.StartAddress;
            sourceDescription = entry.WlRecord is not null
                ? Tf("value", "wl_rom_export_source", "WL mapping buckets {0}", entry.BucketRangeText)
                : Tf("value", "legacy_rom_export_source", "NOR range 0x{0:X6}-0x{1:X6}", entry.StartAddress, entry.StartAddress + (uint)readLength - 1);
            isTrimmedAfterRead = entry.WlRecord is not null || programmerKind == ProgrammerKind.BeggarLegacy;
        }

        target = new SelectedRomExportTargetInfo
        {
            Entry = entry,
            StartAddress = startAddress,
            ReadLength = readLength,
            ExportLength = exportLength,
            IsGbc = isGbc,
            IsTrimmedAfterRead = isTrimmedAfterRead,
            SourceDescription = sourceDescription,
            SuggestedExtension = isGbc ? "gbc" : "gba",
        };
        return true;
    }

    private static uint GetBeggarMaxFallbackRomStartAddress(WaterCartEntry entry)
    {
        if (entry.EntrySlot <= 0)
        {
            return 0x100000;
        }

        return entry.EntrySlot == 0
            ? 0x100000
            : checked((uint)(entry.EntrySlot * 0x200000));
    }

    private async Task ExportSelectedEntrySaveAsync()
    {
        if (!TryResolveSelectedSaveTarget(out SelectedSaveTargetInfo? target, out string reason) || target is null)
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        string title = string.IsNullOrWhiteSpace(target.Entry.Title)
            ? Tf("value", "untitled_entry_slot", "Untitled Entry #{0:000}", target.Entry.EntrySlot + 1)
            : target.Entry.Title;
        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
        WaterCartScanResult scanSnapshot = currentScanResult ?? throw new InvalidOperationException("No active scan result.");
        int exportLengthBytes = target.SaveLength;
        if (!TryPromptSelectedSaveExportLength(target, scanSnapshot, programmerKind, out exportLengthBytes))
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = T("dialog", "export_save_title", "Export Save"),
            FileName = $"{SanitizeSaveFileName(title)}.sav",
            Filter = T("dialog", "export_chip_save_filter", "Save File (*.sav)|*.sav|All files (*.*)|*.*"),
            AddExtension = true,
            DefaultExt = "sav",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        SetBusy(true, T("status", "export_chip_save_busy", "Exporting chip save..."));
        AppendLog(Tf(
            "log",
            "export_selected_save_start",
            "Start exporting save for {0}. Source={1}, size={2}.",
            title,
            target.SourceDescription,
            FormatKiBSize(exportLengthBytes)));

        try
        {
            byte[] saveData = await Task.Run(() => ExportSelectedSaveFromDevice(portName, programmerKind, target, exportLengthBytes));
            File.WriteAllBytes(dialog.FileName, saveData);
            AppendLog(Tf("log", "export_selected_save_done", "Save exported successfully: {0}", dialog.FileName));
            SetBusy(false, T("status", "export_chip_save_done", "Chip save exported"));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "export_selected_save_failed", "Exporting save failed: {0}", ex.Message));
            SetBusy(false, T("status", "export_chip_save_failed", "Chip save export failed"));
        }
    }

    private async Task ImportSelectedEntrySaveAsync()
    {
        if (!EnsureCurrentScanIsWritable(out string writeProtectReason))
        {
            AppendLog(writeProtectReason);
            return;
        }

        if (!TryResolveSelectedSaveTarget(out SelectedSaveTargetInfo? target, out string reason) || target is null)
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = T("dialog", "import_save_title", "Import Save"),
            Filter = T("dialog", "export_chip_save_filter", "Save File (*.sav)|*.sav|All files (*.*)|*.*"),
            Multiselect = false,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        byte[] sourceData;
        try
        {
            sourceData = File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "import_save_read_failed", "Failed to read the save file: {0}", ex.Message));
            return;
        }

        if (sourceData.Length <= 0)
        {
            AppendLog(T("log", "import_chip_save_empty", "The selected save file is empty."));
            return;
        }

        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
        WaterCartScanResult scanSnapshot = currentScanResult ?? throw new InvalidOperationException("No active scan result.");
        int importCapacityBytes = GetSelectedSaveImportCapacityBytes(target, scanSnapshot);
        int bytesToWrite = sourceData.Length;
        if (sourceData.Length > importCapacityBytes)
        {
            string capacityText = FormatKiBSize(importCapacityBytes);
            DialogResult confirm = MessageBox.Show(
                this,
                Tf(
                    "dialog",
                    "import_save_too_large_message",
                    "The selected save file exceeds the target save capacity ({0}).\n\nContinuing will import only the first {0} and discard the remaining data.\n\nDo you want to continue?",
                    capacityText),
                T("dialog", "import_save_title", "Import Save"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
            {
                return;
            }

            bytesToWrite = importCapacityBytes;
        }

        byte[] importData = sourceData.AsSpan(0, bytesToWrite).ToArray();
        string title = string.IsNullOrWhiteSpace(target.Entry.Title)
            ? Tf("value", "untitled_entry_slot", "Untitled Entry #{0:000}", target.Entry.EntrySlot + 1)
            : target.Entry.Title;

        SetBusy(true, T("status", "import_save_busy", "Importing save..."));
        AppendLog(Tf(
            "log",
            "import_save_start",
            "Start importing save for {0}. Source={1}, file={2}, write size={3}.",
            title,
            target.SourceDescription,
            dialog.FileName,
            FormatKiBSize(importData.Length)));

        try
        {
            await Task.Run(() => ImportSelectedSaveToDevice(portName, programmerKind, scanSnapshot, target, importData));
            UpdateLocalSaveStateAfterImport(target, importData);
            if (target.Kind == SelectedSaveStorageKind.WaterLinkLogicalSlot)
            {
                TryRefreshLocalWaterLinkSaveSummary(portName);
            }
            RefreshWorkspaceGrid();
            AppendLog(Tf("log", "import_save_done", "Imported save successfully: {0}", dialog.FileName));
            SetBusy(false, T("status", "import_save_done", "Save imported"));
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "import_save_failed", "Importing save failed: {0}", ex.Message));
            SetBusy(false, T("status", "import_save_failed", "Importing save failed"));
        }
    }

    private async Task ClearSelectedEntrySaveAsync()
    {
        if (!EnsureCurrentScanIsWritable(out string writeProtectReason))
        {
            AppendLog(writeProtectReason);
            return;
        }

        if (!TryResolveSelectedSaveTarget(out SelectedSaveTargetInfo? target, out string reason) || target is null)
        {
            AppendLog(reason);
            return;
        }

        if (!target.SupportsClear)
        {
            AppendLog(T(
                "log",
                "clear_save_unsupported",
                "Clear Save is not available for the current save storage type yet."));
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        string title = string.IsNullOrWhiteSpace(target.Entry.Title)
            ? Tf("value", "untitled_entry_slot", "Untitled Entry #{0:000}", target.Entry.EntrySlot + 1)
            : target.Entry.Title;
        string message = target.Kind switch
        {
            SelectedSaveStorageKind.WaterLinkLogicalSlot => Tf(
                "dialog",
                "clear_save_confirm_waterlink",
                "Clear the current WL SPI save slot for {0}?\n\nThis will erase only the currently selected bank0/bank1 64 KiB blocks in logical slot #{1:000}.\nROM data will not be touched.\n\nDo you want to continue?",
                title,
                target.EntrySlot + 1),
            SelectedSaveStorageKind.LegacyFlashSlot => Tf(
                "dialog",
                "clear_save_confirm_legacy",
                "Clear the legacy save slot for {0}?\n\nThis will erase save slot #{1:000} at 0x{2:X6}.\nGame ROM data will not be touched.\n\nDo you want to continue?",
                title,
                (target.SaveSlotIndex ?? 0) + 1,
                target.SaveAddress),
            SelectedSaveStorageKind.BeggarMaxRamSlot => Tf(
                "dialog",
                "clear_save_confirm_beggar_max",
                "Clear the MAX save slot for {0}?\n\nThis will blank MAX save slot #{1:000} ({2}).\nGame ROM data will not be touched.\n\nDo you want to continue?",
                title,
                target.EntrySlot + 1,
                FormatKiBSize(target.SaveLength)),
            SelectedSaveStorageKind.BatterylessRomTail => Tf(
                "dialog",
                "clear_save_confirm_batteryless",
                "Clear the batteryless save area for {0}?\n\nThis will blank 0x{1:X6}-0x{2:X6} and preserve the surrounding ROM data.\n\nDo you want to continue?",
                title,
                target.SaveAddress,
                target.SaveAddress + (uint)target.SaveLength - 1),
            _ => T("dialog", "clear_save_confirm", "Do you want to clear this save?"),
        };

        DialogResult confirm = MessageBox.Show(
            this,
            message,
            T("dialog", "clear_save_title", "Clear Save"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
        WaterCartScanResult scanSnapshot = currentScanResult ?? throw new InvalidOperationException("No active scan result.");

        SetBusy(true, T("status", "clear_save_busy", "Clearing save..."));
        try
        {
            await Task.Run(() => ClearSelectedSaveOnDevice(portName, programmerKind, scanSnapshot, target));
            UpdateLocalSaveStateAfterClear(target);
            if (target.Kind == SelectedSaveStorageKind.WaterLinkLogicalSlot)
            {
                TryRefreshLocalWaterLinkSaveSummary(portName);
            }
            RefreshWorkspaceGrid();
            AppendLog(Tf("log", "clear_save_done", "Cleared save for {0}.", title));
            SetBusy(false, T("status", "clear_save_done", "Save cleared"));
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "clear_save_failed", "Clearing save failed: {0}", ex.Message));
            SetBusy(false, T("status", "clear_save_failed", "Clearing save failed"));
        }
    }

    private byte[] ExportSelectedSaveFromDevice(
        string portName,
        ProgrammerKind programmerKind,
        SelectedSaveTargetInfo target,
        int exportLengthBytes)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        if (currentScanResult?.IsChm1 == true && target.Kind == SelectedSaveStorageKind.LegacyFlashSlot &&
            currentScanResult.MenuConfig.Status.LastBootSaveType != 0 &&
            currentScanResult.MenuConfig.Status.LastBootSaveIndex == target.SaveSlotIndex)
            return ReadBeggarChipSave(protocol, 0x20000).AsSpan(0, exportLengthBytes).ToArray();

        byte[] saveData = target.Kind switch
        {
            SelectedSaveStorageKind.WaterLinkLogicalSlot => ReadWaterLinkSelectedSave(protocol, target.EntrySlot),
            SelectedSaveStorageKind.LegacyFlashSlot => ReadRomRangeFromDevice(protocol, programmerKind, target.SaveAddress, exportLengthBytes),
            SelectedSaveStorageKind.BeggarMaxRamSlot => ReadBeggarMaxSave(protocol, target.Entry, exportLengthBytes),
            SelectedSaveStorageKind.BatterylessRomTail => ReadRomRangeFromDevice(protocol, programmerKind, target.SaveAddress, exportLengthBytes),
            _ => throw new InvalidOperationException("Unsupported save export target."),
        };

        if ((target.Kind == SelectedSaveStorageKind.WaterLinkLogicalSlot ||
             target.Kind == SelectedSaveStorageKind.BeggarMaxRamSlot) &&
            exportLengthBytes > 0 &&
            exportLengthBytes < saveData.Length)
        {
            return saveData.AsSpan(0, exportLengthBytes).ToArray();
        }

        return saveData;
    }

    private byte[] ExportSelectedRomFromDevice(
        string portName,
        ProgrammerKind programmerKind,
        SelectedRomExportTargetInfo target)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        byte[] romData;
        if (target.IsGbc)
        {
            romData = ReadBeggarMaxRomFromDevice(protocol, target.Entry, target.StartAddress, target.ReadLength);
            int trimmedLength = GetTrimmedExportRomLength(romData, isGbc: true);
            if (trimmedLength < romData.Length)
            {
                Array.Resize(ref romData, trimmedLength);
            }
        }
        else if (target.Entry.WlRecord is not null && target.Entry.PhysicalBuckets.Count > 0)
        {
            romData = ReadMappedGbaRomFromDevice(protocol, programmerKind, target.Entry);
            int trimmedLength = GetTrimmedExportRomLength(romData, isGbc: false);
            if (trimmedLength < romData.Length)
            {
                Array.Resize(ref romData, trimmedLength);
            }
        }
        else
        {
            romData = ReadLinearRomRangeFromDevice(protocol, programmerKind, target.StartAddress, target.ReadLength, target.SourceDescription);
            if (target.IsTrimmedAfterRead)
            {
                int trimmedLength = GetTrimmedExportRomLength(romData, isGbc: false);
                if (trimmedLength < romData.Length)
                {
                    Array.Resize(ref romData, trimmedLength);
                }
            }
        }

        return romData;
    }

    private void ClearSelectedSaveOnDevice(
        string portName,
        ProgrammerKind programmerKind,
        WaterCartScanResult scanSnapshot,
        SelectedSaveTargetInfo target)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        switch (target.Kind)
        {
            case SelectedSaveStorageKind.WaterLinkLogicalSlot:
                {
                    ClearWaterLinkSaveSlot(protocol, target.EntrySlot);
                    break;
                }
            case SelectedSaveStorageKind.BeggarMaxRamSlot:
                {
                    ClearBeggarMaxSave(protocol, target.Entry, target.SaveLength);
                    break;
                }
            case SelectedSaveStorageKind.LegacyFlashSlot:
                {
                    int sectorSize = scanSnapshot.MenuConfig.Status.SectorSize > 0
                        ? scanSnapshot.MenuConfig.Status.SectorSize
                        : scanSnapshot.Geometry.SectorSizeBytes;
                    if (sectorSize <= 0)
                    {
                        throw new InvalidOperationException(T("error", "legacy_itemlist_invalid", "Legacy itemlist address is invalid."));
                    }

                    byte[] blankSector = Enumerable.Repeat((byte)0xFF, sectorSize).ToArray();
                    BeginMeasuredProgress(
                        T("ui", "clear_save", "Clear Save"),
                        CountRewriteSectorProgressWorkBytes(blankSector, scanSnapshot.Geometry),
                        CountRewriteSectorProgressUnits(blankSector));
                    RewriteNorSector(protocol, target.SaveAddress, blankSector, scanSnapshot.Geometry, T("value", "save_slot_label", "save slot"), programmerKind);
                    DiscardChm1PendingSave(protocol, scanSnapshot, target);
                    CompleteMeasuredProgress(T("ui", "clear_save", "Clear Save"));
                    break;
                }
            case SelectedSaveStorageKind.BatterylessRomTail:
                {
                    ClearBatterylessSaveRegion(protocol, programmerKind, scanSnapshot.Geometry, target.SaveAddress, target.SaveLength);
                    break;
                }
            default:
                throw new InvalidOperationException(T(
                    "error",
                    "clear_save_unsupported",
                    "Clear Save is not available for the current save storage type yet."));
        }
    }

    private byte[] ReadWaterLinkSelectedSave(WaterDeviceProtocol protocol, int entrySlot)
    {
        if (entrySlot < 0 || entrySlot >= WaterSaveSlotSummaryInfo.SlotCount)
        {
            throw new InvalidOperationException(T("error", "save_slot_invalid", "The selected save slot is invalid."));
        }

        if (!TryReadWaterLinkSelectorState(protocol, out byte[] bank0CopySelect, out byte[] bank1CopySelect))
        {
            throw new InvalidOperationException(T(
                "error",
                "wl_selector_journal_direct_read_failed",
                "WL selector journal direct SPI read failed."));
        }

        if (!TryReadWaterLinkSelectedSaveImage(
                protocol,
                entrySlot,
                bank0CopySelect,
                bank1CopySelect,
                out byte[] saveData,
                out uint bank0Address,
                out uint bank1Address))
        {
            throw new InvalidOperationException(Tf(
                "error",
                "wl_save_distribution_direct_read_failed",
                "WL save distribution direct SPI read failed: slot #{0:000}, bank0=0x{1:X6}, bank1=0x{2:X6}.",
                entrySlot + 1,
                bank0Address,
                bank1Address));
        }

        return saveData;
    }

    private byte[] ReadBeggarMaxSave(WaterDeviceProtocol protocol, WaterCartEntry entry, int saveLengthBytes)
    {
        if (saveLengthBytes <= 0)
        {
            throw new InvalidOperationException(T("error", "save_slot_invalid", "The selected save slot is invalid."));
        }

        if (!TryConfigureBeggarMaxGameMapping(protocol, entry))
        {
            throw new InvalidOperationException(T("error", "save_slot_summary_unavailable", "The device did not respond to the save-slot request."));
        }

        if (!TrySetBeggarMaxRamEnabled(protocol, true))
        {
            throw new InvalidOperationException(T("error", "save_slot_summary_unavailable", "The device did not respond to the save-slot request."));
        }

        try
        {
            byte[] saveData = new byte[saveLengthBytes];
            int readOffset = 0;
            int currentBank = -1;
            int[] ramBankSequence = ResolveBeggarMaxSaveReadBanks(entry);
            if (ramBankSequence.Length == 0)
            {
                throw new InvalidOperationException(T("error", "save_slot_invalid", "The selected save slot is invalid."));
            }

            while (readOffset < saveLengthBytes)
            {
                int absoluteAddress = readOffset;
                int sequenceIndex = absoluteAddress >> 13;
                int bankOffset = absoluteAddress & 0x1FFF;
                int readLength = Math.Min(4096, saveLengthBytes - readOffset);
                readLength = Math.Min(readLength, 0x2000 - bankOffset);
                if (sequenceIndex >= ramBankSequence.Length)
                {
                    break;
                }

                int ramBank = ramBankSequence[sequenceIndex];

                if (ramBank != currentBank)
                {
                    currentBank = ramBank;
                    if (!TrySwitchBeggarMaxRamBank(protocol, ramBank))
                    {
                        throw new InvalidOperationException(T("error", "save_slot_summary_unavailable", "The device did not respond to the save-slot request."));
                    }
                }

                byte[] chunk = new byte[readLength];
                if (!protocol.TryReadGbc((uint)(0xA000 + bankOffset), chunk, 3000))
                {
                    throw new InvalidOperationException(T("error", "save_slot_summary_unavailable", "The device did not respond to the save-slot request."));
                }

                Buffer.BlockCopy(chunk, 0, saveData, readOffset, readLength);
                readOffset += readLength;
            }

            return saveData;
        }
        finally
        {
            TrySetBeggarMaxRamEnabled(protocol, false);
        }
    }

    private void WriteBeggarMaxSave(
        WaterDeviceProtocol protocol,
        WaterCartEntry entry,
        int saveLengthBytes,
        byte[] saveData,
        string operationTitle)
    {
        if (saveLengthBytes <= 0)
        {
            throw new InvalidOperationException(T("error", "save_slot_invalid", "The selected save slot is invalid."));
        }

        byte fillValue = 0xFF;
        byte[] normalizedData = Enumerable.Repeat(fillValue, saveLengthBytes).ToArray();
        if (saveData.Length > 0)
        {
            Buffer.BlockCopy(saveData, 0, normalizedData, 0, Math.Min(saveData.Length, normalizedData.Length));
        }

        if (!TryConfigureBeggarMaxGameMapping(protocol, entry))
        {
            throw new InvalidOperationException(T("error", "save_slot_summary_unavailable", "The device did not respond to the save-slot request."));
        }

        if (!TrySetBeggarMaxRamEnabled(protocol, true))
        {
            throw new InvalidOperationException(T("error", "save_slot_summary_unavailable", "The device did not respond to the save-slot request."));
        }

        try
        {
            int writeOffset = 0;
            int currentBank = -1;
            int totalUnits = Math.Max(1, (normalizedData.Length + 4095) / 4096);
            int[] ramBankSequence = ResolveBeggarMaxSaveWriteBanks(entry, normalizedData.Length);
            if (ramBankSequence.Length == 0)
            {
                throw new InvalidOperationException(T("error", "save_slot_invalid", "The selected save slot is invalid."));
            }
            BeginMeasuredProgress(operationTitle, normalizedData.Length, totalUnits);

            while (writeOffset < normalizedData.Length)
            {
                int absoluteAddress = writeOffset;
                int sequenceIndex = absoluteAddress >> 13;
                int bankOffset = absoluteAddress & 0x1FFF;
                int writeLength = Math.Min(4096, normalizedData.Length - writeOffset);
                writeLength = Math.Min(writeLength, 0x2000 - bankOffset);
                if (sequenceIndex >= ramBankSequence.Length)
                {
                    break;
                }

                int ramBank = ramBankSequence[sequenceIndex];

                if (ramBank != currentBank)
                {
                    currentBank = ramBank;
                    if (!TrySwitchBeggarMaxRamBank(protocol, ramBank))
                    {
                        throw new InvalidOperationException(T("error", "save_slot_summary_unavailable", "The device did not respond to the save-slot request."));
                    }
                }

                byte[] chunk = normalizedData.AsSpan(writeOffset, writeLength).ToArray();
                if (!protocol.TryWriteGbc((uint)(0xA000 + bankOffset), chunk, 3000))
                {
                    throw new InvalidOperationException(T("error", "save_slot_summary_unavailable", "The device did not respond to the save-slot request."));
                }

                writeOffset += writeLength;
                AdvanceMeasuredProgress(
                    writeLength,
                    1,
                    BuildProgressDetailText(
                        Tf("value", "beggar_max_save_slot", "MAX save slot #{0:000}", entry.EntrySlot + 1),
                        "progress_phase_write",
                        "Write"));
            }

            CompleteMeasuredProgress(operationTitle);
        }
        catch
        {
            AbortMeasuredProgress();
            throw;
        }
        finally
        {
            TrySetBeggarMaxRamEnabled(protocol, false);
        }

        byte[] readBackData = ReadBeggarMaxSave(protocol, entry, normalizedData.Length);
        if (!readBackData.AsSpan(0, normalizedData.Length).SequenceEqual(normalizedData))
        {
            throw new InvalidOperationException(T("error", "save_verify_failed", "Save verification failed after writing."));
        }
    }

    private void ClearBeggarMaxSave(WaterDeviceProtocol protocol, WaterCartEntry entry, int saveLengthBytes)
    {
        if (saveLengthBytes <= 0)
        {
            throw new InvalidOperationException(T("error", "save_slot_invalid", "The selected save slot is invalid."));
        }

        byte[] blankData = Enumerable.Repeat((byte)0xFF, saveLengthBytes).ToArray();
        WriteBeggarMaxSave(protocol, entry, saveLengthBytes, blankData, T("ui", "clear_save", "Clear Save"));
    }

    private static int[] ResolveBeggarMaxSaveReadBanks(WaterCartEntry entry)
    {
        if (entry.SaveLengthBytes <= 0 ||
            entry.RamBankCount <= 0 ||
            entry.RamBankCount > BeggarMaxGameSramBankLimit)
        {
            return [];
        }

        int bankCount = Math.Max(1, (entry.SaveLengthBytes + GameBoyRomHeaderService.RamBankSize - 1) / GameBoyRomHeaderService.RamBankSize);
        if (bankCount > entry.RamBankCount || bankCount > BeggarMaxGameSramBankLimit)
        {
            return [];
        }

        return Enumerable.Range(0, bankCount).ToArray();
    }

    private static int[] ResolveBeggarMaxSaveWriteBanks(WaterCartEntry entry, int writeLengthBytes)
    {
        if (writeLengthBytes <= 0)
        {
            return [];
        }

        return ResolveBeggarMaxSaveReadBanks(entry);
    }

    private static bool TryConfigureBeggarMaxGameMapping(WaterDeviceProtocol protocol, WaterCartEntry entry)
    {
        if (entry.RomOffsetBank < 0)
        {
            return false;
        }

        byte romBankLow = (byte)(entry.RomOffsetBank & 0xFF);
        byte romBankHigh = (byte)((entry.RomOffsetBank >> 8) & 0xFF);
        if (!protocol.TryWriteGbc(0x4000, [0x40], 3000) ||
            !protocol.TryWriteGbc(0xB000, [romBankLow], 3000) ||
            !protocol.TryWriteGbc(0xB000, [romBankHigh], 3000) ||
            !protocol.TryWriteGbc(0xB000, [entry.MbcType], 3000))
        {
            return false;
        }

        if (entry.RamBankCount <= 0 ||
            entry.RamBankCount > BeggarMaxGameSramBankLimit ||
            entry.SaveBankMap.Count < entry.RamBankCount)
        {
            return false;
        }

        byte[] ramMap = entry.SaveBankMap.Take(entry.RamBankCount).ToArray();
        int physicalBankLimit = BeggarMaxSharedSaveBankStart + BeggarMaxSharedSaveBankCount;
        if (ramMap.Any(bank => bank >= physicalBankLimit) || ramMap.Distinct().Count() != ramMap.Length)
        {
            return false;
        }

        if (!protocol.TryWriteGbc(0xB000, [0xFE], 3000))
        {
            return false;
        }

        foreach (byte bank in ramMap)
        {
            if (!protocol.TryWriteGbc(0xB000, [bank], 3000))
            {
                return false;
            }
        }

        return protocol.TryWriteGbc(0xB000, [0xFF], 3000);
    }

    private byte[] ReadLinearRomRangeFromDevice(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        uint startAddress,
        int byteCount,
        string label)
    {
        if (byteCount <= 0)
        {
            return [];
        }

        BeginMeasuredProgress(T("ui", "export_rom", "Export ROM"), byteCount, CountNorSectors(byteCount, currentScanResult?.Geometry ?? new NorGeometryInfo()));
        try
        {
            byte[] data = new byte[byteCount];
            const int chunkSize = 4096;
            int offset = 0;
            while (offset < data.Length)
            {
                int readLength = Math.Min(chunkSize, data.Length - offset);
                byte[] chunk = new byte[readLength];
                if (!TryReadRomRangeForProgrammer(protocol, programmerKind, startAddress + (uint)offset, chunk))
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "read_save_range_failed",
                        "Failed to read save data at 0x{0:X6}.",
                        startAddress + (uint)offset));
                }

                Buffer.BlockCopy(chunk, 0, data, offset, readLength);
                offset += readLength;
                AdvanceMeasuredProgress(readLength, 0, BuildProgressDetailText(label, "progress_phase_read", "Read"));
            }

            CompleteMeasuredProgress(T("ui", "export_rom", "Export ROM"));
            return data;
        }
        catch
        {
            AbortMeasuredProgress();
            throw;
        }
    }

    private byte[] ReadMappedGbaRomFromDevice(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        WaterCartEntry entry)
    {
        int logicalBucketCount = Math.Max(1, entry.LogicalBucketCount);
        byte[] romData = Enumerable.Repeat((byte)0xFF, logicalBucketCount * WaterCartScanner.BucketSizeBytes).ToArray();
        IReadOnlyList<int> physicalBuckets = entry.PhysicalBuckets;
        long totalBytes = checked((long)physicalBuckets.Count * WaterCartScanner.BucketSizeBytes);

        BeginMeasuredProgress(T("ui", "export_rom", "Export ROM"), totalBytes, physicalBuckets.Count);
        try
        {
            for (int index = 0; index < logicalBucketCount && index < physicalBuckets.Count; index++)
            {
                int bucket = physicalBuckets[index];
                uint startAddress = checked((uint)(bucket * WaterCartScanner.BucketSizeBytes));
                byte[] bucketData = ReadRomRangeFromDevice(protocol, programmerKind, startAddress, WaterCartScanner.BucketSizeBytes);
                Buffer.BlockCopy(bucketData, 0, romData, index * WaterCartScanner.BucketSizeBytes, bucketData.Length);
                AdvanceMeasuredProgress(bucketData.Length, 1, BuildProgressDetailText($"ROM {bucket:00}", "progress_phase_read", "Read"));
            }

            CompleteMeasuredProgress(T("ui", "export_rom", "Export ROM"));
            return romData;
        }
        catch
        {
            AbortMeasuredProgress();
            throw;
        }
    }

    private byte[] ReadBeggarMaxRomFromDevice(
        WaterDeviceProtocol protocol,
        WaterCartEntry entry,
        uint startAddress,
        int readLengthBytes)
    {
        if (readLengthBytes <= 0)
        {
            return [];
        }

        if (entry.RomOffsetBank >= 0 && !TryConfigureBeggarMaxGameMapping(protocol, entry))
        {
            throw new InvalidOperationException(T("error", "save_slot_summary_unavailable", "The device did not respond to the save-slot request."));
        }

        byte[] data = new byte[readLengthBytes];
        BeginMeasuredProgress(T("ui", "export_rom", "Export ROM"), readLengthBytes, Math.Max(1, (readLengthBytes + 4095) / 4096));
        try
        {
            int offset = 0;
            int currentBank = -1;
            while (offset < readLengthBytes)
            {
                int absoluteAddress = checked((int)startAddress + offset);
                int bank = absoluteAddress >> 14;
                int bankOffset = absoluteAddress & 0x3FFF;
                int chunkLength = Math.Min(4096, readLengthBytes - offset);
                chunkLength = Math.Min(chunkLength, 0x4000 - bankOffset);

                if (bank != currentBank)
                {
                    currentBank = bank;
                    if (!TrySwitchBeggarMaxRomBank(protocol, bank))
                    {
                        throw new InvalidOperationException(T("error", "beggar_max_read_sector_failed", "Failed to read the existing MAX flash sector image."));
                    }
                }

                byte[] chunk = new byte[chunkLength];
                int cartAddress = GetBeggarMaxBankBaseAddress(bank) + bankOffset;
                if (!protocol.TryReadGbc((uint)cartAddress, chunk, 3000))
                {
                    throw new InvalidOperationException(T("error", "beggar_max_read_sector_failed", "Failed to read the existing MAX flash sector image."));
                }

                Buffer.BlockCopy(chunk, 0, data, offset, chunkLength);
                offset += chunkLength;
                AdvanceMeasuredProgress(chunkLength, 1, BuildProgressDetailText($"ROM B{bank:X}", "progress_phase_read", "Read"));
            }

            CompleteMeasuredProgress(T("ui", "export_rom", "Export ROM"));
            return data;
        }
        catch
        {
            AbortMeasuredProgress();
            throw;
        }
    }

    private static int GetTrimmedExportRomLength(byte[] romData, bool isGbc)
    {
        if (romData.Length <= 0)
        {
            return 0;
        }

        if (isGbc && romData.Length >= 0x150)
        {
            try
            {
                GameBoyRomMetadata metadata = GameBoyRomHeaderService.Read("export", romData);
                int targetLength = checked((int)Math.Min(int.MaxValue, metadata.EffectiveRomSizeBytes));
                if (targetLength > 0 && targetLength <= romData.Length)
                {
                    return targetLength;
                }
            }
            catch
            {
            }
        }

        const int minimumLength = 0x200;
        int lastNonPaddingIndex = -1;
        for (int i = romData.Length - 1; i >= 0; i--)
        {
            if (romData[i] != 0xFF)
            {
                lastNonPaddingIndex = i;
                break;
            }
        }

        if (lastNonPaddingIndex < 0)
        {
            return Math.Min(romData.Length, minimumLength);
        }

        int trimmedLength = Math.Max(minimumLength, lastNonPaddingIndex + 1);
        int remainder = trimmedLength % 0x10;
        if (remainder != 0)
        {
            trimmedLength += 0x10 - remainder;
        }

        return Math.Min(trimmedLength, romData.Length);
    }

    private static uint GetWaterLinkSaveSlotBaseAddress(int entrySlot)
    {
        return ((uint)entrySlot + 1u) * 0x40000u;
    }

    private WaterSaveSlotSummaryInfo ReadWaterLinkSaveSlotSummary(WaterDeviceProtocol protocol)
    {
        if (!protocol.TryReadSaveSlotSummary(out byte[] summaryPayload))
        {
            throw new InvalidOperationException(T(
                "error",
                "save_slot_summary_unavailable",
                "The device did not respond to the save-slot summary request."));
        }

        return ParseSaveSlotSummary(summaryPayload);
    }

    private void ImportWaterLinkSelectedSave(
        WaterDeviceProtocol protocol,
        int entrySlot,
        byte[] saveData)
    {
        if (entrySlot < 0 || entrySlot >= WaterSaveSlotSummaryInfo.SlotCount)
        {
            throw new InvalidOperationException(T("error", "save_slot_invalid", "The selected save slot is invalid."));
        }

        if (!TryReadWaterLinkSelectorState(protocol, out byte[] bank0CopySelect, out byte[] bank1CopySelect))
        {
            throw new InvalidOperationException(T(
                "error",
                "wl_selector_journal_direct_read_failed",
                "WL selector journal direct SPI read failed."));
        }

        uint slotBaseAddress = GetWaterLinkSaveSlotBaseAddress(entrySlot);
        bool bank0SelectB = entrySlot < bank0CopySelect.Length && bank0CopySelect[entrySlot] != 0;
        bool bank1SelectB = entrySlot < bank1CopySelect.Length && bank1CopySelect[entrySlot] != 0;
        uint bank0ActiveAddress = slotBaseAddress + (bank0SelectB ? 0x10000u : 0x00000u);
        uint bank1ActiveAddress = slotBaseAddress + (bank1SelectB ? 0x30000u : 0x20000u);
        uint[] blockAddresses =
        [
            slotBaseAddress + 0x00000u,
            slotBaseAddress + 0x10000u,
            slotBaseAddress + 0x20000u,
            slotBaseAddress + 0x30000u,
        ];
        int bank0WriteLength = Math.Min(saveData.Length, 0x10000);
        int bank1WriteLength = Math.Min(Math.Max(0, saveData.Length - 0x10000), 0x10000);
        long totalBytes = blockAddresses.Length * 0x10000L + bank0WriteLength + bank1WriteLength;
        int totalUnits = blockAddresses.Length + (bank0WriteLength > 0 ? 1 : 0) + (bank1WriteLength > 0 ? 1 : 0);
        string label = T("value", "save_slot_label", "save slot");

        BeginMeasuredProgress(T("ui", "import_save", "Import Save"), totalBytes, totalUnits);
        foreach (uint blockAddress in blockAddresses)
        {
            EraseSpiFlashBlock(protocol, blockAddress, label);
        }

        if (bank0WriteLength > 0)
        {
            WriteSpiFlashRange(protocol, bank0ActiveAddress, saveData.AsSpan(0, bank0WriteLength).ToArray(), label);
        }

        if (bank1WriteLength > 0)
        {
            WriteSpiFlashRange(protocol, bank1ActiveAddress, saveData.AsSpan(0x10000, bank1WriteLength).ToArray(), label);
        }

        CompleteMeasuredProgress(T("ui", "import_save", "Import Save"));
    }

    private void ClearWaterLinkSaveSlot(WaterDeviceProtocol protocol, int entrySlot)
    {
        if (entrySlot < 0 || entrySlot >= WaterSaveSlotSummaryInfo.SlotCount)
        {
            throw new InvalidOperationException(T("error", "save_slot_invalid", "The selected save slot is invalid."));
        }

        uint slotBaseAddress = GetWaterLinkSaveSlotBaseAddress(entrySlot);
        uint[] blockAddresses =
        [
            slotBaseAddress + 0x00000u,
            slotBaseAddress + 0x10000u,
            slotBaseAddress + 0x20000u,
            slotBaseAddress + 0x30000u,
        ];
        string label = T("value", "save_slot_label", "save slot");

        BeginMeasuredProgress(T("ui", "clear_save", "Clear Save"), blockAddresses.Length * 0x10000L, blockAddresses.Length);
        foreach (uint blockAddress in blockAddresses)
        {
            EraseSpiFlashBlock(protocol, blockAddress, label);
        }
        CompleteMeasuredProgress(T("ui", "clear_save", "Clear Save"));
    }

    private void EraseSpiFlashBlock(WaterDeviceProtocol protocol, uint blockAddress, string label)
    {
        if (!protocol.TryEraseSpiFlashBlock(blockAddress, 60000))
        {
            throw new InvalidOperationException(Tf(
                "error",
                "spi_flash_erase_failed",
                "Failed to erase SPI flash block at 0x{0:X6}.",
                blockAddress));
        }

        AdvanceMeasuredProgress(
            0x10000,
            1,
            BuildProgressDetailText(label, "progress_phase_erase", "Erase"));
    }

    private void WriteSpiFlashRange(WaterDeviceProtocol protocol, uint startAddress, byte[] data, string label)
    {
        const int SpiFlashWriteChunkBytes = 4096;
        int offset = 0;

        while (offset < data.Length)
        {
            int writeLength = Math.Min(SpiFlashWriteChunkBytes, data.Length - offset);
            byte[] chunk = data.AsSpan(offset, writeLength).ToArray();
            uint address = startAddress + (uint)offset;

            if (!protocol.TryWriteSpiFlash(address, chunk, 60000))
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "spi_flash_write_failed",
                    "Failed to write SPI flash data at 0x{0:X6}.",
                    address));
            }

            offset += writeLength;
            AdvanceMeasuredProgress(
                writeLength,
                offset >= data.Length ? 1 : 0,
                BuildProgressDetailText(label, "progress_phase_write", "Write"));
        }
    }

    private byte[] ReadRomRangeFromDevice(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        uint startAddress,
        int byteCount)
    {
        byte[] data = new byte[byteCount];
        if (!TryReadRomRangeForProgrammer(protocol, programmerKind, startAddress, data))
        {
            throw new InvalidOperationException(Tf(
                "error",
                "read_save_range_failed",
                "Failed to read save data at 0x{0:X6}.",
                startAddress));
        }

        return data;
    }

    private void ImportSelectedSaveToDevice(
        string portName,
        ProgrammerKind programmerKind,
        WaterCartScanResult scanSnapshot,
        SelectedSaveTargetInfo target,
        byte[] saveData)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        switch (target.Kind)
        {
            case SelectedSaveStorageKind.WaterLinkLogicalSlot:
                {
                    ImportWaterLinkSelectedSave(protocol, target.EntrySlot, saveData);
                    break;
                }
            case SelectedSaveStorageKind.BeggarMaxRamSlot:
                {
                    WriteBeggarMaxSave(protocol, target.Entry, target.SaveLength, saveData, T("ui", "import_save", "Import Save"));
                    break;
                }
            case SelectedSaveStorageKind.LegacyFlashSlot:
                {
                    int sectorSize = scanSnapshot.MenuConfig.Status.SectorSize > 0
                        ? scanSnapshot.MenuConfig.Status.SectorSize
                        : scanSnapshot.Geometry.SectorSizeBytes;
                    if (sectorSize <= 0)
                    {
                        throw new InvalidOperationException(T("error", "legacy_itemlist_invalid", "Legacy itemlist address is invalid."));
                    }

                    byte[] sectorData = scanSnapshot.IsChm1
                        ? Chm1Layout.PrepareSaveImage(saveData)
                        : Enumerable.Repeat((byte)0xFF, sectorSize).ToArray();
                    if (!scanSnapshot.IsChm1)
                        Buffer.BlockCopy(saveData, 0, sectorData, 0, Math.Min(saveData.Length, sectorData.Length));

                    BeginMeasuredProgress(
                        T("ui", "import_save", "Import Save"),
                        CountRewriteSectorProgressWorkBytes(sectorData, scanSnapshot.Geometry),
                        CountRewriteSectorProgressUnits(sectorData));
                    RewriteNorSector(protocol, target.SaveAddress, sectorData, scanSnapshot.Geometry, T("value", "save_slot_label", "save slot"), programmerKind);
                    DiscardChm1PendingSave(protocol, scanSnapshot, target);
                    CompleteMeasuredProgress(T("ui", "import_save", "Import Save"));
                    break;
                }
            case SelectedSaveStorageKind.BatterylessRomTail:
                {
                    ImportBatterylessSaveRegion(protocol, programmerKind, scanSnapshot.Geometry, target.SaveAddress, target.SaveLength, saveData);
                    break;
                }
            default:
                throw new InvalidOperationException(T(
                    "error",
                    "import_save_unsupported",
                    "Import Save is not available for the current save storage type yet."));
        }
    }

    private void ImportBatterylessSaveRegion(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        NorGeometryInfo geometry,
        uint saveAddress,
        int saveLength,
        byte[] saveData)
    {
        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;
        uint startSectorAddress = saveAddress / (uint)sectorSize * (uint)sectorSize;
        uint endAddress = saveAddress + (uint)saveLength - 1;
        uint endSectorAddress = endAddress / (uint)sectorSize * (uint)sectorSize;
        List<(uint Address, byte[] Data)> sectors = [];

        for (uint sectorAddress = startSectorAddress; sectorAddress <= endSectorAddress; sectorAddress += (uint)sectorSize)
        {
            byte[] sectorData = ReadRomRangeFromDevice(protocol, programmerKind, sectorAddress, sectorSize);

            uint overlapStart = Math.Max(sectorAddress, saveAddress);
            uint overlapEnd = Math.Min(sectorAddress + (uint)sectorSize - 1, endAddress);
            if (overlapStart <= overlapEnd)
            {
                int regionOffset = (int)(overlapStart - saveAddress);
                int sectorOffset = (int)(overlapStart - sectorAddress);
                int clearLength = (int)(overlapEnd - overlapStart + 1);
                Array.Fill(sectorData, (byte)0xFF, sectorOffset, clearLength);
                int copyLength = Math.Max(0, Math.Min((int)(overlapEnd - overlapStart + 1), saveData.Length - regionOffset));
                if (copyLength > 0)
                {
                    Buffer.BlockCopy(saveData, regionOffset, sectorData, sectorOffset, copyLength);
                }
            }

            sectors.Add((sectorAddress, sectorData));
            if (sectorAddress == endSectorAddress)
            {
                break;
            }
        }

        long totalBytes = sectors.Sum(item => CountRewriteSectorProgressWorkBytes(item.Data, geometry));
        int totalUnits = sectors.Sum(item => CountRewriteSectorProgressUnits(item.Data));
        BeginMeasuredProgress(T("ui", "import_save", "Import Save"), totalBytes, totalUnits);
        RewriteNorSectorBatch(
            protocol,
            sectors.Select((item, index) => (
                item.Address,
                item.Data,
                $"{T("value", "batteryless_save_label", "batteryless save")} S{index:00}"))
            .ToList(),
            geometry,
            programmerKind);
        CompleteMeasuredProgress(T("ui", "import_save", "Import Save"));
    }

    private void ClearBatterylessSaveRegion(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        NorGeometryInfo geometry,
        uint saveAddress,
        int saveLength)
    {
        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;
        uint startSectorAddress = saveAddress / (uint)sectorSize * (uint)sectorSize;
        uint endAddress = saveAddress + (uint)saveLength - 1;
        uint endSectorAddress = endAddress / (uint)sectorSize * (uint)sectorSize;
        List<(uint Address, byte[] Data)> sectors = [];

        for (uint sectorAddress = startSectorAddress; sectorAddress <= endSectorAddress; sectorAddress += (uint)sectorSize)
        {
            byte[] sectorData = new byte[sectorSize];
            if (!TryReadRomRangeForProgrammer(protocol, programmerKind, sectorAddress, sectorData))
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "read_save_range_failed",
                    "Failed to read save data at 0x{0:X6}.",
                    sectorAddress));
            }

            uint overlapStart = Math.Max(sectorAddress, saveAddress);
            uint overlapEnd = Math.Min(sectorAddress + (uint)sectorSize - 1, endAddress);
            if (overlapStart <= overlapEnd)
            {
                int clearOffset = (int)(overlapStart - sectorAddress);
                int clearLength = (int)(overlapEnd - overlapStart + 1);
                Array.Fill(sectorData, (byte)0xFF, clearOffset, clearLength);
            }

            sectors.Add((sectorAddress, sectorData));
            if (sectorAddress == endSectorAddress)
            {
                break;
            }
        }

        long totalBytes = sectors.Sum(item => CountRewriteSectorProgressWorkBytes(item.Data, geometry));
        int totalUnits = sectors.Sum(item => CountRewriteSectorProgressUnits(item.Data));
        BeginMeasuredProgress(T("ui", "clear_save", "Clear Save"), totalBytes, totalUnits);
        RewriteNorSectorBatch(
            protocol,
            sectors.Select((item, index) => (
                item.Address,
                item.Data,
                $"{T("value", "batteryless_save_label", "batteryless save")} S{index:00}"))
            .ToList(),
            geometry,
            programmerKind);
        CompleteMeasuredProgress(T("ui", "clear_save", "Clear Save"));
    }

    private void UpdateLocalSaveStateAfterClear(SelectedSaveTargetInfo target)
    {
        if (currentScanResult is null || target.Entry.EntrySlot < 0)
        {
            return;
        }

        switch (target.Kind)
        {
            case SelectedSaveStorageKind.WaterLinkLogicalSlot:
                currentScanResult.WaterLinkSaveDataByEntrySlot[target.Entry.EntrySlot] = false;
                currentScanResult.WaterLinkSaveStatusTextByEntrySlot[target.Entry.EntrySlot] =
                    BuildLocalSaveOccupancyText(target.SaveLength);
                break;
            case SelectedSaveStorageKind.BeggarMaxRamSlot:
                currentScanResult.LegacySaveDataByEntrySlot[target.Entry.EntrySlot] = false;
                currentScanResult.LegacySaveStatusTextByEntrySlot[target.Entry.EntrySlot] =
                    BuildBeggarMaxSaveOccupancyText(target.Entry, target.SaveLength);
                break;
            case SelectedSaveStorageKind.LegacyFlashSlot:
                currentScanResult.LegacySaveDataByEntrySlot[target.Entry.EntrySlot] = false;
                currentScanResult.LegacySaveStatusTextByEntrySlot[target.Entry.EntrySlot] =
                    BuildLocalSaveOccupancyText(GetLegacySaveSlotStorageLength(currentScanResult));
                break;
            case SelectedSaveStorageKind.BatterylessRomTail:
                currentScanResult.BatterylessSaveDataByEntrySlot[target.Entry.EntrySlot] = false;
                currentScanResult.BatterylessSaveStatusTextByEntrySlot[target.Entry.EntrySlot] =
                    BuildLocalSaveOccupancyText(target.SaveLength);
                break;
        }
    }

    private void UpdateLocalSaveStateAfterImport(SelectedSaveTargetInfo target, byte[] saveData)
    {
        if (currentScanResult is null || target.Entry.EntrySlot < 0)
        {
            return;
        }

        switch (target.Kind)
        {
            case SelectedSaveStorageKind.WaterLinkLogicalSlot:
                {
                    string occupancyText = BuildLocalSaveOccupancyText(target.SaveLength, saveData, out bool hasData);
                    currentScanResult.WaterLinkSaveDataByEntrySlot[target.Entry.EntrySlot] = hasData;
                    currentScanResult.WaterLinkSaveStatusTextByEntrySlot[target.Entry.EntrySlot] = occupancyText;
                    break;
                }
            case SelectedSaveStorageKind.BeggarMaxRamSlot:
                {
                    string occupancyText = BuildBeggarMaxSaveOccupancyText(target.Entry, target.SaveLength, saveData, out bool hasData);
                    currentScanResult.LegacySaveDataByEntrySlot[target.Entry.EntrySlot] = hasData;
                    currentScanResult.LegacySaveStatusTextByEntrySlot[target.Entry.EntrySlot] = occupancyText;
                    break;
                }
            case SelectedSaveStorageKind.LegacyFlashSlot:
                {
                    string occupancyText = BuildLocalSaveOccupancyText(
                        GetLegacySaveSlotStorageLength(currentScanResult),
                        saveData,
                        out bool hasData);
                    currentScanResult.LegacySaveDataByEntrySlot[target.Entry.EntrySlot] = hasData;
                    currentScanResult.LegacySaveStatusTextByEntrySlot[target.Entry.EntrySlot] = occupancyText;
                    break;
                }
            case SelectedSaveStorageKind.BatterylessRomTail:
                {
                    string occupancyText = BuildLocalSaveOccupancyText(target.SaveLength, saveData, out bool hasData);
                    currentScanResult.BatterylessSaveDataByEntrySlot[target.Entry.EntrySlot] = hasData;
                    currentScanResult.BatterylessSaveStatusTextByEntrySlot[target.Entry.EntrySlot] = occupancyText;
                    break;
                }
        }
    }

    private bool TryRefreshLocalWaterLinkSaveSummary(string portName)
    {
        if (currentScanResult is null)
        {
            return false;
        }

        try
        {
            using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
            OpenProgrammerProtocol(protocol, ProgrammerKind.WaterLink);
            (currentScanResult.WaterLinkSaveDataByEntrySlot, currentScanResult.WaterLinkSaveStatusTextByEntrySlot) =
                ReadWaterLinkSavePresence(protocol, currentScanResult);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetLegacySaveSlotStorageLength(WaterCartScanResult scanSnapshot)
    {
        return scanSnapshot.MenuConfig.Status.SectorSize > 0
            ? scanSnapshot.MenuConfig.Status.SectorSize
            : scanSnapshot.Geometry.SectorSizeBytes;
    }

    private string BuildLocalSaveOccupancyText(int storageLengthBytes, byte[]? importedData = null) =>
        BuildLocalSaveOccupancyText(storageLengthBytes, importedData, out _);

    private string BuildLocalSaveOccupancyText(int storageLengthBytes, byte[]? importedData, out bool hasAnyData)
    {
        hasAnyData = false;
        if (storageLengthBytes <= 0)
        {
            return string.Empty;
        }

        byte[] storageData = Enumerable.Repeat((byte)0xFF, storageLengthBytes).ToArray();
        if (importedData is not null && importedData.Length > 0)
        {
            Buffer.BlockCopy(importedData, 0, storageData, 0, Math.Min(importedData.Length, storageData.Length));
        }

        int chunkCount = Math.Max(1, (storageData.Length + SaveStatusChunkBytes - 1) / SaveStatusChunkBytes);
        bool[] occupiedChunks = new bool[chunkCount];
        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            int offset = chunkIndex * SaveStatusChunkBytes;
            int chunkLength = Math.Min(SaveStatusChunkBytes, storageData.Length - offset);
            bool occupied = !IsSaveStatusChunkEmpty(storageData.AsSpan(offset, chunkLength));
            occupiedChunks[chunkIndex] = occupied;
            hasAnyData |= occupied;
        }

        return FormatSaveOccupancyText(occupiedChunks);
    }

    private string BuildBeggarMaxSaveOccupancyText(WaterCartEntry entry, int slotLengthBytes, byte[]? slotData = null) =>
        BuildBeggarMaxSaveOccupancyText(entry, slotLengthBytes, slotData, out _);

    private string BuildBeggarMaxSaveOccupancyText(WaterCartEntry entry, int slotLengthBytes, byte[]? slotData, out bool hasAnyData)
    {
        const int BeggarMaxDisplayBytes = 0x20000;   // 64 KiB + 64 KiB

        hasAnyData = false;
        if (slotLengthBytes <= 0)
        {
            return string.Empty;
        }

        int[] ramBanks = ResolveBeggarMaxSaveReadBanks(entry);
        if (ramBanks.Length == 0)
        {
            return string.Empty;
        }

        int effectiveSlotBytes = Math.Min(ramBanks.Length * GameBoyRomHeaderService.RamBankSize, Math.Max(slotLengthBytes, slotData?.Length ?? 0));
        byte[] storageData = Enumerable.Repeat((byte)0xFF, effectiveSlotBytes).ToArray();
        if (slotData is not null && slotData.Length > 0)
        {
            Buffer.BlockCopy(slotData, 0, storageData, 0, Math.Min(slotData.Length, storageData.Length));
        }

        bool[] occupiedChunks = new bool[BeggarMaxDisplayBytes / SaveStatusChunkBytes];
        int chunkCount = Math.Max(1, (storageData.Length + SaveStatusChunkBytes - 1) / SaveStatusChunkBytes);

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            int offset = chunkIndex * SaveStatusChunkBytes;
            int chunkLength = Math.Min(SaveStatusChunkBytes, storageData.Length - offset);
            bool occupied = !IsSaveStatusChunkEmpty(storageData.AsSpan(offset, chunkLength));
            if (chunkIndex < occupiedChunks.Length)
            {
                occupiedChunks[chunkIndex] = occupied;
            }
            hasAnyData |= occupied;
        }

        if (!hasAnyData)
        {
            return T("value", "save_status_empty_slot", "Empty Slot");
        }

        return FormatSaveOccupancyText(occupiedChunks);
    }

    private static int GetSelectedSaveImportCapacityBytes(SelectedSaveTargetInfo target, WaterCartScanResult scanSnapshot)
    {
        return target.Kind switch
        {
            SelectedSaveStorageKind.WaterLinkLogicalSlot => 0x20000,
            SelectedSaveStorageKind.LegacyFlashSlot => scanSnapshot.MenuConfig.Status.SectorSize > 0
                ? scanSnapshot.MenuConfig.Status.SectorSize
                : target.SaveLength,
            SelectedSaveStorageKind.BatterylessRomTail => target.SaveLength,
            _ => target.SaveLength,
        };
    }

    private bool TryPromptSelectedSaveExportLength(
        SelectedSaveTargetInfo target,
        WaterCartScanResult scanSnapshot,
        ProgrammerKind programmerKind,
        out int exportLengthBytes)
    {
        exportLengthBytes = target.SaveLength;
        bool shouldPrompt =
            programmerKind == ProgrammerKind.BeggarLegacy ||
            target.Kind == SelectedSaveStorageKind.WaterLinkLogicalSlot;
        if (!shouldPrompt)
        {
            return true;
        }

        int automaticLengthBytes = target.SaveLength;
        int maximumLengthBytes = GetSelectedSaveMaximumExportBytes(target, scanSnapshot);
        SaveExportSizeOption[] options =
        [
            new SaveExportSizeOption
            {
                DisplayText = Tf("dialog", "export_save_size_auto", "Auto ({0})", FormatKiBSize(automaticLengthBytes)),
                SizeBytes = null,
            },
            new SaveExportSizeOption { DisplayText = FormatKiBSize(0x2000), SizeBytes = 0x2000 },
            new SaveExportSizeOption { DisplayText = FormatKiBSize(0x8000), SizeBytes = 0x8000 },
            new SaveExportSizeOption { DisplayText = FormatKiBSize(0x10000), SizeBytes = 0x10000 },
            new SaveExportSizeOption { DisplayText = FormatKiBSize(0x20000), SizeBytes = 0x20000 },
        ];

        using Form dialog = new DpiAwareForm()
        {
            Text = T("dialog", "export_save_size_title", "Export Save Size"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new System.Drawing.Size(380, 138),
        };

        Label label = new()
        {
            AutoSize = true,
            Left = 12,
            Top = 15,
            Text = Tf("dialog", "export_save_size_label", "Export size for {0}:", target.SourceDescription),
        };

        ComboBox comboBox = new()
        {
            Left = 12,
            Top = 44,
            Width = 352,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        comboBox.Items.AddRange(options);
        comboBox.SelectedIndex = 0;

        Button okButton = new()
        {
            Text = T("dialog", "button_ok", "OK"),
            Left = 208,
            Top = 92,
            Width = 75,
            DialogResult = DialogResult.OK,
        };

        Button cancelButton = new()
        {
            Text = T("dialog", "button_cancel", "Cancel"),
            Left = 289,
            Top = 92,
            Width = 75,
            DialogResult = DialogResult.Cancel,
        };

        dialog.Controls.Add(label);
        dialog.Controls.Add(comboBox);
        dialog.Controls.Add(okButton);
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        while (true)
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return false;
            }

            if (comboBox.SelectedItem is not SaveExportSizeOption selectedOption)
            {
                return false;
            }

            int selectedLengthBytes = selectedOption.SizeBytes ?? automaticLengthBytes;
            if (selectedOption.SizeBytes.HasValue && selectedLengthBytes > maximumLengthBytes)
            {
                MessageBox.Show(
                    this,
                    Tf(
                        "dialog",
                        "export_save_size_too_large_message",
                        "The selected export size exceeds the readable save range ({0}).",
                        FormatKiBSize(maximumLengthBytes)),
                    T("dialog", "export_save_size_title", "Export Save Size"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                comboBox.Focus();
                continue;
            }

            exportLengthBytes = selectedLengthBytes;
            return true;
        }
    }

    private static int GetSelectedSaveMaximumExportBytes(SelectedSaveTargetInfo target, WaterCartScanResult scanSnapshot)
    {
        return target.Kind switch
        {
            SelectedSaveStorageKind.LegacyFlashSlot => scanSnapshot.MenuConfig.Status.SectorSize > 0
                ? scanSnapshot.MenuConfig.Status.SectorSize
                : target.SaveLength,
            SelectedSaveStorageKind.BatterylessRomTail => target.SaveLength,
            _ => target.SaveLength,
        };
    }

    private bool TryResolveSelectedSaveTarget(out SelectedSaveTargetInfo? target, out string reason)
    {
        target = null;
        reason = string.Empty;

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanCapacityValid(out reason))
        {
            return false;
        }

        if (!TryGetSelectedEntry(out WaterCartEntry? entry) || entry is null || entry.IsMenu)
        {
            reason = T("error", "save_action_requires_entry", "Please select a valid game entry first.");
            return false;
        }

        if (!TryGetSelectedKnownProgrammerKind(out ProgrammerKind programmerKind, out reason))
        {
            return false;
        }

        WaterCartScanResult scanSnapshot = currentScanResult!;
        bool isWaterLinkSaveTarget = programmerKind == ProgrammerKind.WaterLink &&
            entry.EntrySlot is >= 0 and < WaterSaveSlotSummaryInfo.SlotCount;
        if (!isWaterLinkSaveTarget && !HasEntryAccessibleSave(entry, scanSnapshot))
        {
            reason = T("error", "save_not_available_for_entry", "The selected game does not currently have an accessible save slot.");
            return false;
        }

        if (programmerKind == ProgrammerKind.WaterLink)
        {
            if (entry.EntrySlot < 0)
            {
                reason = T("error", "save_slot_invalid", "The selected save slot is invalid.");
                return false;
            }

            target = new SelectedSaveTargetInfo
            {
                Entry = entry,
                Kind = SelectedSaveStorageKind.WaterLinkLogicalSlot,
                EntrySlot = entry.EntrySlot,
                SaveAddress = GetWaterLinkSaveSlotBaseAddress(entry.EntrySlot),
                SaveLength = 0x20000,
                SourceDescription = Tf("value", "waterlink_logical_slot", "WaterLink logical slot #{0:000}", entry.EntrySlot + 1),
                SupportsClear = true,
            };
            return true;
        }

        if (scanSnapshot.IsBeggarMaxGbcMode)
        {
            if (entry.EntrySlot < 0)
            {
                reason = T("error", "save_not_available_for_entry", "The selected game does not currently have an accessible save slot.");
                return false;
            }

            target = new SelectedSaveTargetInfo
            {
                Entry = entry,
                Kind = SelectedSaveStorageKind.BeggarMaxRamSlot,
                EntrySlot = entry.EntrySlot,
                SaveLength = entry.SaveLengthBytes,
                SourceDescription = Tf("value", "beggar_max_save_slot", "MAX save slot #{0:000}", entry.EntrySlot + 1),
                SupportsClear = true,
            };
            return true;
        }

        if (scanSnapshot.MenuConfig.IsDetected && scanSnapshot.MenuConfig.Status.BatteryPresent)
        {
            if (entry.SaveType is not > 0 || !entry.SaveIndex.HasValue)
            {
                reason = T("error", "save_not_available_for_entry", "The selected game does not currently have a menu-managed save slot.");
                return false;
            }

            int sectorSize = scanSnapshot.MenuConfig.Status.SectorSize > 0
                ? scanSnapshot.MenuConfig.Status.SectorSize
                : scanSnapshot.Geometry.SectorSizeBytes;
            uint saveAreaBase = (uint)scanSnapshot.MenuConfig.Status.SaveAreaAddress;
            int saveLength = ResolveLegacySaveLengthBytes(entry);
            target = new SelectedSaveTargetInfo
            {
                Entry = entry,
                Kind = SelectedSaveStorageKind.LegacyFlashSlot,
                EntrySlot = entry.EntrySlot,
                SaveAddress = saveAreaBase + (uint)(entry.SaveIndex.Value * sectorSize),
                SaveLength = saveLength,
                SaveSlotIndex = entry.SaveIndex.Value,
                SourceDescription = Tf("value", "legacy_save_slot", "Legacy save slot #{0:000}", entry.SaveIndex.Value + 1),
                SupportsClear = true,
            };
            return true;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            reason = T("log", "select_port_first", "Please select a compatible programmer port first.");
            return false;
        }

        try
        {
            using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
            OpenProgrammerProtocol(protocol, programmerKind);
            if (!TryLocateBatterylessSaveRegion(protocol, programmerKind, entry, scanSnapshot.Geometry.DeviceSizeBytes, out uint saveAddress, out int saveLength))
            {
                reason = T("error", "batteryless_save_not_found", "No batteryless save area was found for the selected game.");
                return false;
            }

            target = new SelectedSaveTargetInfo
            {
                Entry = entry,
                Kind = SelectedSaveStorageKind.BatterylessRomTail,
                EntrySlot = entry.EntrySlot,
                SaveAddress = saveAddress,
                SaveLength = saveLength,
                SourceDescription = T("value", "batteryless_save_area", "Batteryless ROM-tail save area"),
                SupportsClear = true,
            };
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static int ResolveLegacySaveLengthBytes(WaterCartEntry entry)
    {
        return entry.SaveType switch
        {
            1 => 0x8000,
            2 => 0x10000,
            3 => 0x20000,
            _ => 0x10000,
        };
    }

    private static bool HasEntryAccessibleSave(WaterCartEntry entry, WaterCartScanResult scanSnapshot)
    {
        if (scanSnapshot.IsBeggarMaxGbcMode)
        {
            return entry.EntrySlot >= 0 &&
                entry.HasBattery &&
                entry.SaveLengthBytes > 0;
        }

        return !scanSnapshot.MenuConfig.Status.BatteryPresent || !IsEntrySaveTypeNone(entry);
    }

    private static bool IsEntrySaveTypeNone(WaterCartEntry entry)
    {
        if (entry.SaveType is byte saveType)
        {
            return saveType == 0;
        }

        return !string.IsNullOrWhiteSpace(entry.SaveTypeText) &&
            string.Equals(entry.SaveTypeText.Trim(), "NONE", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ImportChipSaveAsync()
    {
        if (!EnsureCurrentScanIsWritable(out string writeProtectReason))
        {
            AppendLog(writeProtectReason);
            return;
        }

        if (!TryResolveChipSaveExportTarget(out WaterCartEntry? entry, out _, out string reason) ||
            entry is null)
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = T("dialog", "import_chip_save_title", "Import Chip Save"),
            Filter = T("dialog", "export_chip_save_filter", "Save File (*.sav)|*.sav|All files (*.*)|*.*"),
            Multiselect = false,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        byte[] sourceData;
        try
        {
            sourceData = File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "import_chip_save_read_failed", "Failed to read the save file: {0}", ex.Message));
            return;
        }

        if (sourceData.Length <= 0)
        {
            AppendLog(T("log", "import_chip_save_empty", "The selected save file is empty."));
            return;
        }

        const int MaximumChipSaveImportBytes = 0x20000;
        int bytesToWrite = sourceData.Length;
        if (sourceData.Length > MaximumChipSaveImportBytes)
        {
            DialogResult confirm = MessageBox.Show(
                this,
                Tf(
                    "dialog",
                    "import_chip_save_too_large_message",
                    "The selected save file is larger than 128 KiB.\n\nContinuing will import only the first 128 KiB and discard the remaining data.\n\nDo you want to continue?"),
                T("dialog", "import_chip_save_title", "Import Chip Save"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
            {
                return;
            }

            bytesToWrite = MaximumChipSaveImportBytes;
        }

        byte[] importData = sourceData.AsSpan(0, bytesToWrite).ToArray();
        string title = string.IsNullOrWhiteSpace(entry.Title)
            ? Tf("value", "untitled_entry_slot", "Untitled Entry #{0:000}", entry.EntrySlot + 1)
            : entry.Title;

        SetBusy(true, T("status", "import_chip_save_busy", "Importing chip save..."));
        AppendLog(Tf(
            "log",
            "import_chip_save_start",
            "Start importing chip save for {0}. File={1}, write size={2}.",
            title,
            dialog.FileName,
            FormatKiBSize(importData.Length)));

        try
        {
            await Task.Run(() => ImportChipSaveToDevice(portName, importData));

            if (currentScanResult is not null && entry.EntrySlot >= 0)
            {
                bool hasData = importData.Any(value => value != 0xFF);
                currentScanResult.LegacySaveDataByEntrySlot[entry.EntrySlot] = hasData;
                currentScanResult.LegacySaveStatusTextByEntrySlot.Remove(entry.EntrySlot);
            }

            RefreshWorkspaceGrid();
            AppendLog(Tf("log", "import_chip_save_done", "Chip save imported successfully: {0}", dialog.FileName));
            SetBusy(false, T("status", "import_chip_save_done", "Chip save imported"));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "import_chip_save_failed", "Importing chip save failed: {0}", ex.Message));
            SetBusy(false, T("status", "import_chip_save_failed", "Chip save import failed"));
        }
    }

    private async Task ClearChipSaveAsync()
    {
        if (!EnsureCurrentScanIsWritable(out string writeProtectReason))
        {
            AppendLog(writeProtectReason);
            return;
        }

        if (!TryResolveChipSaveExportTarget(out WaterCartEntry? entry, out int saveSizeBytes, out string reason) ||
            entry is null)
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        string title = string.IsNullOrWhiteSpace(entry.Title)
            ? Tf("value", "untitled_entry_slot", "Untitled Entry #{0:000}", entry.EntrySlot + 1)
            : entry.Title;

        DialogResult confirm = MessageBox.Show(
            this,
            Tf(
                "dialog",
                "clear_chip_save_confirm_message",
                "Clear the current chip save for {0}?\n\nThis will write 0x00 across the current SRAM/FRAM chip area ({1}).\nMenu save slots and ROM data will not be touched.\n\nDo you want to continue?",
                title,
                FormatKiBSize(saveSizeBytes)),
            T("dialog", "clear_chip_save_title", "Clear Chip Save"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, T("status", "clear_chip_save_busy", "Clearing chip save..."));
        AppendLog(Tf(
            "log",
            "clear_chip_save_start",
            "Start clearing the current chip save for {0}. Size={1}.",
            title,
            FormatKiBSize(saveSizeBytes)));

        try
        {
            await Task.Run(() => ClearChipSaveOnDevice(portName, saveSizeBytes));
            AppendLog(T("log", "clear_chip_save_done", "Chip save cleared successfully."));
            SetBusy(false, T("status", "clear_chip_save_done", "Chip save cleared"));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "clear_chip_save_failed", "Clearing chip save failed: {0}", ex.Message));
            SetBusy(false, T("status", "clear_chip_save_failed", "Clearing chip save failed"));
        }
    }

    private byte[] ExportChipSaveFromDevice(string portName, int saveSizeBytes)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
        return ReadBeggarChipSave(protocol, saveSizeBytes);
    }

    private void ImportChipSaveToDevice(string portName, byte[] saveData)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
        WriteBeggarChipSave(protocol, saveData);
    }

    private void ClearChipSaveOnDevice(string portName, int saveSizeBytes)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
        WriteBeggarChipSave(protocol, new byte[saveSizeBytes]);
    }

    private byte[] ReadBeggarChipSave(WaterDeviceProtocol protocol, int saveSizeBytes)
    {
        byte[] data = new byte[saveSizeBytes];
        int bankSizeBytes = 0x10000;
        int totalBanks = saveSizeBytes > bankSizeBytes ? 2 : 1;
        int offset = 0;

        try
        {
            for (int bank = 0; bank < totalBanks; bank++)
            {
                if (!TrySwitchBeggarSramBank(protocol, bank))
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "export_chip_save_bank_switch_failed",
                        "Failed to switch to SRAM bank {0}.",
                        bank));
                }

                int currentBankSize = Math.Min(bankSizeBytes, saveSizeBytes - offset);
                byte[] bankData = new byte[currentBankSize];
                if (!protocol.TryReadRamRange(0, bankData, 4096, 3000))
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "export_chip_save_ram_read_failed",
                        "Failed to read SRAM data from bank {0}.",
                        bank));
                }

                Buffer.BlockCopy(bankData, 0, data, offset, currentBankSize);
                offset += currentBankSize;
            }
        }
        finally
        {
            TrySwitchBeggarSramBank(protocol, 0);
        }

        return data;
    }

    private void WriteBeggarChipSave(WaterDeviceProtocol protocol, byte[] saveData)
    {
        const int BankSizeBytes = 0x10000;
        const int ChunkSizeBytes = 4096;
        int totalBanks = saveData.Length > BankSizeBytes ? 2 : 1;

        try
        {
            for (int bank = 0; bank < totalBanks; bank++)
            {
                if (!TrySwitchBeggarSramBank(protocol, bank))
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "export_chip_save_bank_switch_failed",
                        "Failed to switch to SRAM bank {0}.",
                        bank));
                }

                int bankOffset = bank * BankSizeBytes;
                int currentBankSize = Math.Min(BankSizeBytes, saveData.Length - bankOffset);
                for (int offset = 0; offset < currentBankSize; offset += ChunkSizeBytes)
                {
                    int writeLength = Math.Min(ChunkSizeBytes, currentBankSize - offset);
                    byte[] chunk = saveData.AsSpan(bankOffset + offset, writeLength).ToArray();
                    if (!protocol.TryWriteRam((uint)offset, chunk, 3000))
                    {
                        throw new InvalidOperationException(Tf(
                            "error",
                            "import_chip_save_ram_write_failed",
                            "Failed to write SRAM data to bank {0} at offset 0x{1:X}.",
                            bank,
                            offset));
                    }
                }
            }
        }
        finally
        {
            TrySwitchBeggarSramBank(protocol, 0);
        }
    }

    private static bool TrySwitchBeggarSramBank(WaterDeviceProtocol protocol, int bank, int timeoutMs = 3000)
    {
        if (!protocol.TryWriteRom(0x800000, BitConverter.GetBytes((ushort)(bank == 0 ? 0 : 1)), timeoutMs))
        {
            return false;
        }

        Thread.Sleep(100);
        return true;
    }

    private static string SanitizeSaveFileName(string title)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        char[] sanitized = title
            .Select(character => invalidChars.Contains(character) ? '_' : character)
            .ToArray();
        string result = new string(sanitized).Trim();
        return string.IsNullOrWhiteSpace(result) ? "chip_save" : result;
    }

    private async Task RepairMissingMenuAsync()
    {
        if (currentScanResult?.IsChm1 == true) { await InstallChm1MenuAsync(); return; }
        MissingMenuActionMode missingMenuActionMode = GetMissingMenuActionMode();
        if (missingMenuActionMode == MissingMenuActionMode.None && !CanRepairMenuCurrentScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = currentScanResult!;
        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
        if (missingMenuActionMode == MissingMenuActionMode.RepairWaterLinkMagic)
        {
            await RepairMagicMetadataAsync();
            return;
        }

        if (missingMenuActionMode == MissingMenuActionMode.UpgradeWaterLinkFromLegacyMenu)
        {
            await RepairWaterLinkFromLegacyMenuAsync();
            return;
        }

        if (missingMenuActionMode == MissingMenuActionMode.RepairLegacyBatterySaveMetadata)
        {
            WaterLegacyMenuRepairPlan repairPlan;

            try
            {
                repairPlan = WaterLegacyMenuRepairBuilder.BuildBatterySaveMetadataRepairPlan(scanSnapshot);
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "repair_menu_prepare_failed", "Unable to prepare menu repair/install: {0}", ex.Message));
                return;
            }

            string message = Tf(
                "dialog",
                "repair_legacy_save_slots_confirm_message",
                "The current cart is in battery/FRAM mode, but one or more legacy menu entries still use save=NONE.\n\nContinuing will:\n1. Rewrite the itemlist sector at 0x{0:X6}-0x{1:X6}\n2. Rewrite the status sector at 0x{2:X6}-0x{3:X6}\n3. Assign legacy SRAM save slots to the current {4} menu entrie(s)\n\nROM data will not be erased.\n\nDo you want to continue?",
                repairPlan.RepairedMenuConfig.Status.ItemListAddress,
                repairPlan.RepairedMenuConfig.Status.ItemListAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1,
                repairPlan.RepairedMenuConfig.Status.StatusAddress,
                repairPlan.RepairedMenuConfig.Status.StatusAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1,
                repairPlan.RepairedMenuConfig.ItemCount);

            DialogResult confirm = MessageBox.Show(
                this,
                message,
                T("dialog", "repair_menu_confirm_title", "Repair Menu"),
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.OK)
            {
                return;
            }

            SetBusy(true, T("status", "repair_menu_busy", "Repairing menu..."));
            AppendLog(Tf(
                "log",
                "repair_legacy_save_slots_start",
                "Battery/FRAM mode is enabled, but some legacy menu entries still use save=NONE. Start assigning save slots. Entries={0}, itemlist=0x{1:X6}, status=0x{2:X6}",
                repairPlan.RepairedMenuConfig.ItemCount,
                repairPlan.RepairedMenuConfig.Status.ItemListAddress,
                repairPlan.RepairedMenuConfig.Status.StatusAddress));

            try
            {
                await Task.Run(() => RepairLegacyMenuOnDevice(portName, programmerKind, scanSnapshot, repairPlan));
                AppendLog(T("log", "repair_legacy_save_slots_done", "Legacy menu save slots were assigned successfully."));

                try
                {
                    ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                    if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                    {
                        AppendLog(Tf("log", "repair_menu_rescan_failed", "Repair finished, but rescan failed: {0}", failureMessage));
                        SetBusy(false, T("status", "repair_menu_rescan_failed", "Repair complete, rescan failed"));
                        return;
                    }

                    SetBusy(false, T("status", "repair_menu_done", "Repair complete"));
                }
                catch (Exception ex)
                {
                    AppendLog(Tf("log", "repair_menu_rescan_failed", "Repair finished, but rescan failed: {0}", ex.Message));
                    SetBusy(false, T("status", "repair_menu_rescan_failed", "Repair complete, rescan failed"));
                }
            }
            catch (Exception ex)
            {
                AbortMeasuredProgress();
                AppendLog(Tf("log", "repair_menu_failed", "Repair/install menu failed: {0}", ex.Message));
                SetBusy(false, T("status", "repair_menu_failed", "Repair failed"));
            }

            return;
        }

        if (missingMenuActionMode == MissingMenuActionMode.InstallLegacyMenu)
        {
            byte[] menuRomBytes;
            string menuRomPath;
            WaterLegacyMenuInstallPlan installPlan;

            try
            {
                menuRomBytes = LoadSelectedGbaMenuRom(usesWlLayout: false, out menuRomPath);
                installPlan = WaterLegacyMenuRepairBuilder.BuildInstallPlan(
                    menuRomBytes,
                    scanSnapshot,
                    IsBatteryPresentForSelectedMode(GetSelectedBurnProfileMode()));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "repair_menu_prepare_failed", "Unable to prepare menu repair/install: {0}", ex.Message));
                return;
            }

            int legacySaveAreaStart = installPlan.InstalledMenuConfig.Status.StatusAddress + installPlan.InstalledMenuConfig.Status.SectorSize;
            string message = installPlan.DroppedHeadGame is not null
                ? Tf(
                    "dialog",
                    "repair_menu_legacy_head_confirm_message",
                    installPlan.InstalledMenuConfig.Status.BatteryPresent
                        ? "The first 4 MiB head area is currently occupied by:\n{0}\n\nContinuing will:\n1. Rewrite the legacy menu area 0x000000-0x{2:X6}\n2. Preserve the save area 0x{3:X6}-0x3FFFFF\n3. Rewrite itemlist/status\n4. Rebuild {1} visible menu entries from current NOR headers\n5. Generate a Chis legacy multicart\n\nThis will destroy the head-area game above, but keep the battery save area intact.\n\nDo you want to continue?"
                        : "The first 4 MiB head area is currently occupied by:\n{0}\n\nContinuing will:\n1. Erase 0x000000-0x3FFFFF\n2. Flash the legacy menu\n3. Rewrite itemlist/status\n4. Rebuild {1} visible menu entries from current NOR headers\n5. Generate a Chis legacy multicart\n\nThis will destroy the head-area game above.\n\nDo you want to continue?",
                    installPlan.DroppedHeadGame.Title,
                    installPlan.InstalledMenuConfig.ItemCount,
                    legacySaveAreaStart - 1,
                    legacySaveAreaStart)
                : Tf(
                    "dialog",
                    "repair_menu_legacy_confirm_message",
                    installPlan.InstalledMenuConfig.Status.BatteryPresent
                        ? "No menu config was detected on this cart.\n\nContinuing will:\n1. Rewrite the legacy menu area 0x000000-0x{1:X6}\n2. Preserve the save area 0x{2:X6}-0x3FFFFF\n3. Rewrite itemlist/status\n4. Rebuild {0} visible menu entries from current NOR headers\n5. Generate a Chis legacy multicart\n\nDo you want to continue?"
                        : "No menu config was detected on this cart.\n\nContinuing will:\n1. Erase 0x000000-0x3FFFFF\n2. Flash the legacy menu\n3. Rewrite itemlist/status\n4. Rebuild {0} visible menu entries from current NOR headers\n5. Generate a Chis legacy multicart\n\nDo you want to continue?",
                    installPlan.InstalledMenuConfig.ItemCount,
                    legacySaveAreaStart - 1,
                    legacySaveAreaStart);

            DialogResult confirm = MessageBox.Show(
                this,
                message,
                T("dialog", "repair_menu_confirm_title", "Repair Menu"),
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.OK)
            {
                return;
            }

            SetBusy(true, T("status", "repair_menu_busy", "Repairing menu..."));
            AppendLog(Tf(
                "log",
                "repair_menu_start_legacy",
                "No menu config was detected. Start repairing/installing the legacy menu from NOR headers. Entries={0}, itemlist=0x{1:X6}, status=0x{2:X6}",
                installPlan.InstalledMenuConfig.ItemCount,
                installPlan.InstalledMenuConfig.Status.ItemListAddress,
                installPlan.InstalledMenuConfig.Status.StatusAddress));
            AppendLog(Tf(
                "log",
                "repair_menu_layout",
                "Menu repair layout source: {0}",
                installPlan.LayoutSource));

            if (installPlan.DroppedHeadGame is not null)
            {
                AppendLog(Tf(
                    "log",
                    "head_game_overwrite_warn",
                    "The head 4 MiB area is currently occupied by {0}. Repairing/installing the menu will overwrite that game.",
                    installPlan.DroppedHeadGame.Title));
            }

            try
            {
                await Task.Run(() => InstallLegacyMenuOnDevice(portName, programmerKind, scanSnapshot, installPlan, menuRomPath));
                AppendLog(T("log", "repair_menu_done_legacy", "Legacy menu head area, itemlist, and status were written successfully."));

                try
                {
                    ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                    if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                    {
                        AppendLog(Tf("log", "repair_menu_rescan_failed", "Repair finished, but rescan failed: {0}", failureMessage));
                        SetBusy(false, T("status", "repair_menu_rescan_failed", "Repair complete, rescan failed"));
                        return;
                    }

                    SetBusy(false, T("status", "repair_menu_done", "Repair complete"));
                }
                catch (Exception ex)
                {
                    AppendLog(Tf("log", "repair_menu_rescan_failed", "Repair finished, but rescan failed: {0}", ex.Message));
                    SetBusy(false, T("status", "repair_menu_rescan_failed", "Repair complete, rescan failed"));
                }
            }
            catch (Exception ex)
            {
                AbortMeasuredProgress();
                AppendLog(Tf("log", "repair_menu_failed", "Repair/install menu failed: {0}", ex.Message));
                SetBusy(false, T("status", "repair_menu_failed", "Repair failed"));
            }

            return;
        }

        byte[] wlMenuRomBytes;
        string wlMenuRomPath;
        WaterMagicInstallPlan wlInstallPlan;

        try
        {
            wlMenuRomBytes = LoadSelectedGbaMenuRom(usesWlLayout: true, out wlMenuRomPath);
            wlInstallPlan = WaterMagicUpgradeBuilder.BuildInstallPlan(wlMenuRomBytes, scanSnapshot);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "repair_menu_prepare_failed", "Unable to prepare menu repair/install: {0}", ex.Message));
            return;
        }

        int mappedBucketCount = wlInstallPlan.InstalledMenuConfig.Items.Sum(item => item.CoveredBucketCount);
        string wlMessage = wlInstallPlan.DroppedHeadGame is not null
            ? Tf(
                "dialog",
                "repair_menu_wl_head_confirm_message",
                "The first 4 MiB head area is currently occupied by:\n{0}\n\nContinuing will:\n1. Erase 0x000000-0x3FFFFF\n2. Flash the new WL menu\n3. Rewrite itemlist/status/WL mapping\n4. Rebuild {1} visible menu entries from current NOR headers\n5. Generate a WaterLink multicart\n\nThis will destroy the head-area game above.\n\nDo you want to continue?",
                wlInstallPlan.DroppedHeadGame.Title,
                wlInstallPlan.InstalledMenuConfig.ItemCount)
            : Tf(
                "dialog",
                "repair_menu_wl_confirm_message",
                "No menu config was detected on this cart.\n\nContinuing will:\n1. Erase 0x000000-0x3FFFFF\n2. Flash the new WL menu\n3. Rewrite itemlist/status/WL mapping\n4. Rebuild {0} visible menu entries from current NOR headers\n5. Generate a WaterLink multicart\n\nMapped 4MiB buckets this time: {1}\n\nDo you want to continue?",
                wlInstallPlan.InstalledMenuConfig.ItemCount,
                mappedBucketCount);

        DialogResult wlConfirm = MessageBox.Show(
            this,
            wlMessage,
            T("dialog", "repair_menu_confirm_title", "Repair Menu"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (wlConfirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, T("status", "repair_menu_busy", "Repairing menu..."));
        AppendLog(Tf(
            "log",
            "repair_menu_start_wl",
            "No menu config was detected. Start repairing/installing the WL menu from NOR headers. Entries={0}, mapped 4MiB buckets={1}",
            wlInstallPlan.InstalledMenuConfig.ItemCount,
            mappedBucketCount));

        if (wlInstallPlan.DroppedHeadGame is not null)
        {
            AppendLog(Tf(
                "log",
                "head_game_overwrite_warn",
                "The head 4 MiB area is currently occupied by {0}. Repairing/installing the menu will overwrite that game.",
                wlInstallPlan.DroppedHeadGame.Title));
        }

        try
        {
            await Task.Run(() => InstallMagicMenuOnDevice(portName, programmerKind, scanSnapshot, wlInstallPlan, wlMenuRomPath));
            AppendLog(T("log", "repair_menu_done_wl", "WL menu head area, itemlist, status, and WL mapping were written successfully."));

            try
            {
                ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                {
                    AppendLog(Tf("log", "repair_menu_rescan_failed", "Repair finished, but rescan failed: {0}", failureMessage));
                    SetBusy(false, T("status", "repair_menu_rescan_failed", "Repair complete, rescan failed"));
                    return;
                }

                SetBusy(false, T("status", "repair_menu_done", "Repair complete"));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "repair_menu_rescan_failed", "Repair finished, but rescan failed: {0}", ex.Message));
                SetBusy(false, T("status", "repair_menu_rescan_failed", "Repair complete, rescan failed"));
            }
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "repair_menu_failed", "Repair/install menu failed: {0}", ex.Message));
            SetBusy(false, T("status", "repair_menu_failed", "Repair failed"));
        }
    }

    private async Task RepairLegacyMenuAsync()
    {
        if (!CanRepairLegacyCurrentScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = currentScanResult!;
        WaterLegacyMenuRepairPlan repairPlan;

        try
        {
            repairPlan = WaterLegacyMenuRepairBuilder.BuildRepairPlan(
                scanSnapshot,
                IsBatteryPresentForSelectedMode(GetSelectedBurnProfileMode()));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "repair_legacy_menu_prepare_failed", "Unable to prepare legacy menu repair: {0}", ex.Message));
            return;
        }

        string message = Tf(
            "dialog",
            "repair_legacy_menu_confirm_message",
            "A legacy menu ROM header was found, but itemlist/status were not recognized.\n\nContinuing will:\n1. Rewrite the itemlist sector at 0x{0:X6}-0x{1:X6}\n2. Rewrite the status sector at 0x{2:X6}-0x{3:X6}\n3. Rebuild {4} legacy menu entrie(s) from current NOR headers\n\nThe menu ROM itself will not be erased, and game ROM data will not be touched.\n\nDo you want to continue?",
            repairPlan.RepairedMenuConfig.Status.ItemListAddress,
            repairPlan.RepairedMenuConfig.Status.ItemListAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1,
            repairPlan.RepairedMenuConfig.Status.StatusAddress,
            repairPlan.RepairedMenuConfig.Status.StatusAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1,
            repairPlan.RepairedMenuConfig.ItemCount);

        DialogResult confirm = MessageBox.Show(
            this,
            message,
            T("dialog", "repair_legacy_menu_confirm_title", "Repair Legacy Menu"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, T("status", "repair_legacy_menu_busy", "Repairing legacy menu..."));
        AppendLog(Tf(
            "log",
            "repair_legacy_menu_start",
            "A legacy menu header was found, but itemlist/status are missing. Start rebuilding the legacy menu config from NOR headers. Entries={0}, itemlist=0x{1:X6}, status=0x{2:X6}",
            repairPlan.RepairedMenuConfig.ItemCount,
            repairPlan.RepairedMenuConfig.Status.ItemListAddress,
            repairPlan.RepairedMenuConfig.Status.StatusAddress));
        AppendLog(Tf(
            "log",
            "repair_legacy_menu_layout",
            "Legacy menu layout source: {0}",
            repairPlan.LayoutSource));

        try
        {
            ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
            await Task.Run(() => RepairLegacyMenuOnDevice(portName, programmerKind, scanSnapshot, repairPlan));
            AppendLog(T("log", "repair_legacy_menu_done", "Legacy menu itemlist/status were written successfully."));

            try
            {
                ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                {
                    AppendLog(Tf("log", "repair_legacy_menu_rescan_failed", "Legacy menu repair finished, but rescan failed: {0}", failureMessage));
                    SetBusy(false, T("status", "repair_legacy_menu_rescan_failed", "Repair complete, rescan failed"));
                    return;
                }

                SetBusy(false, T("status", "repair_legacy_menu_done", "Repair complete"));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "repair_legacy_menu_rescan_failed", "Legacy menu repair finished, but rescan failed: {0}", ex.Message));
                SetBusy(false, T("status", "repair_legacy_menu_rescan_failed", "Repair complete, rescan failed"));
            }
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "repair_legacy_menu_failed", "Legacy menu repair failed: {0}", ex.Message));
            SetBusy(false, T("status", "repair_legacy_menu_failed", "Repair failed"));
        }
    }

    private async Task EraseHeadAreaAsync()
    {
        if (!EnsureSelectedPortHasActiveScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = currentScanResult!;
        bool isBeggarMaxGbcMode = scanSnapshot.IsBeggarMaxGbcMode;
        WaterCartEntry? headAreaGame = isBeggarMaxGbcMode ? null : GetHeadAreaGame();
        string message = isBeggarMaxGbcMode
            ? T(
                "dialog",
                "erase_config_confirm_message_gbc",
                "This will erase 0x000000-0x0FFFFF directly.\n\nThis will destroy the current MBCX menu/config/fixed directory data in the head 1 MiB area. Game ROM data after 1 MiB will be preserved.\n\nDo you want to continue?")
            : headAreaGame is not null
                ? Tf(
                    "dialog",
                    "erase_config_confirm_message_with_game",
                    "The first 4 MiB head area is currently occupied by:\n{0}\n\nContinuing will erase 0x000000-0x3FFFFF directly.\n\nThis will destroy the current menu/config data and the head-area game above.\n\nDo you want to continue?",
                    headAreaGame.Title)
                : T(
                    "dialog",
                    "erase_config_confirm_message",
                    "This will erase 0x000000-0x3FFFFF directly.\n\nThis will destroy the current menu/config data in the head 4 MiB area.\n\nDo you want to continue?");

        DialogResult confirm = MessageBox.Show(
            this,
            message,
            T("dialog", "erase_config_confirm_title", "Erase Config"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();

        SetBusy(true, T("status", "erase_config_busy", "Erasing head area..."));
        AppendLog(isBeggarMaxGbcMode
            ? T("log", "erase_config_start_gbc", "Start erasing MBCX head 1 MiB: 0x000000-0x0FFFFF")
            : T("log", "erase_config_start", "Start erasing head 4 MiB: 0x000000-0x3FFFFF"));

        try
        {
            await Task.Run(() => EraseHeadAreaOnDevice(portName, programmerKind, scanSnapshot));
            AppendLog(isBeggarMaxGbcMode
                ? T("log", "erase_config_done_gbc", "MBCX head 1 MiB erase completed successfully.")
                : T("log", "erase_config_done", "Head 4 MiB erase completed successfully."));

            try
            {
                ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                {
                    AppendLog(Tf("log", "erase_config_rescan_failed", "Erase finished, but rescan failed: {0}", failureMessage));
                    SetBusy(false, T("status", "erase_config_rescan_failed", "Erase complete, rescan failed"));
                    return;
                }

                SetBusy(false, T("status", "erase_config_done", "Head area erased"));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "erase_config_rescan_failed", "Erase finished, but rescan failed: {0}", ex.Message));
                SetBusy(false, T("status", "erase_config_rescan_failed", "Erase complete, rescan failed"));
            }
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "erase_config_failed", "Erase head area failed: {0}", ex.Message));
            SetBusy(false, T("status", "erase_config_failed", "Erase failed"));
        }
    }

    private async Task EraseEntireNorAsync()
    {
        if (!CanEraseEntireNorCurrentScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = GetEntireNorEraseScanSnapshot()!;
        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
        bool useGbcErase = scanSnapshot.IsBeggarMaxGbcMode;
        int targetMask = useGbcErase ? 0x01 : ResolveGbaPpbChipMask(scanSnapshot, programmerKind);
        if (!useGbcErase && programmerKind == ProgrammerKind.BeggarLegacy && scanSnapshot.Geometry.IsS70Gl02GsDualDie)
        {
            targetMask = 0x03;
        }
        int targetCount = CountPpbTargets(targetMask);
        int contiguousMask = (1 << targetCount) - 1;
        if (targetCount <= 0 || targetMask != contiguousMask)
        {
            AppendLog(Tf(
                "error",
                "erase_entire_nor_target_mask_invalid",
                "The detected NOR target mask 0x{0:X} is not a contiguous layout, so full erase was blocked.",
                targetMask));
            return;
        }

        const int logicalTargetSizeBytes = 128 * 1024 * 1024;
        int deviceSizeBytes = scanSnapshot.Geometry.DeviceSizeBytes;
        long targetCoverageBytes = (long)targetCount * logicalTargetSizeBytes;
        bool targetCoverageMatches = targetCount == 1
            ? deviceSizeBytes <= logicalTargetSizeBytes
            : deviceSizeBytes == targetCoverageBytes;
        if (!useGbcErase && !targetCoverageMatches)
        {
            AppendLog(Tf(
                "error",
                "erase_entire_nor_target_capacity_mismatch",
                "The detected capacity ({0} MiB) does not match {1} NOR erase target(s), so full erase was blocked.",
                deviceSizeBytes / (1024 * 1024),
                targetCount));
            return;
        }

        string detectedDescription = BuildPpbDetectedTargetDescription(scanSnapshot, targetCount);
        int capacityMiB = deviceSizeBytes / (1024 * 1024);
        string confirmMessage = useGbcErase
            ? Tf("dialog", "erase_entire_nor_gbc_confirm_message",
                "Detected: {0}\nCapacity: {1} MiB\n\nThis will permanently erase the entire NOR, including all menus, directories and game ROMs. The cartridge's separate SRAM is not erased. No valid multicart directory is required.\n\nDo you want to continue?",
                detectedDescription, capacityMiB)
            : Tf(
            "dialog",
            "erase_entire_nor_confirm_message",
            "Detected: {0}\nCapacity: {1} MiB\nErase targets: {2} (mask 0x{3:X})\n\nThis will permanently erase the entire NOR, including the menu, all game ROMs, batteryless saves, and any save data stored in NOR. SPI/FRAM save chips are not erased.\n\nFor stacked 2 Gbit/02G NOR and multi-NOR carts, every detected 128 MiB target will be erased separately.\n\nDo you want to continue?",
            detectedDescription,
            capacityMiB,
            targetCount,
            targetMask);
        DialogResult confirm = MessageBox.Show(
            this,
            confirmMessage,
            T("dialog", "erase_entire_nor_confirm_title", "Erase Entire NOR"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        DialogResult finalConfirm = MessageBox.Show(
            this,
            T(
                "dialog",
                "erase_entire_nor_final_confirm_message",
                "Final confirmation: all NOR contents will be irreversibly erased. Continue?"),
            T("dialog", "erase_entire_nor_final_confirm_title", "Confirm Full Erase"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Stop,
            MessageBoxDefaultButton.Button2);
        if (finalConfirm != DialogResult.Yes)
        {
            return;
        }

        SetBusy(true, T("status", "erase_entire_nor_busy", "Erasing entire NOR..."));
        lastScannedPortName = string.Empty;
        fcWorkspace?.InvalidateSnapshot();
        AppendLog(Tf(
            "log",
            "erase_entire_nor_start",
            "Start full NOR erase. Capacity={0} MiB, targets={1}, mask=0x{2:X}.",
            capacityMiB,
            targetCount,
            targetMask));

        try
        {
            await Task.Run(() =>
            {
                if (useGbcErase)
                {
                    EraseEntireGbcNorOnDevice(portName, programmerKind, scanSnapshot);
                    return;
                }

                if (programmerKind == ProgrammerKind.BeggarLegacy)
                {
                    EraseEntireLegacyNorOnDevice(portName, scanSnapshot, targetCount);
                    return;
                }

                int sectorSizeBytes = ResolveGbaPpbSectorSizeBytes(scanSnapshot);
                int sectorsPerTarget = ResolveGbaPpbSectorsPerTarget(
                    scanSnapshot,
                    programmerKind,
                    targetCount,
                    sectorSizeBytes);
                // 功耗分离实验开关：跳过 PPB 解锁，单独量「纯擦除」的电流。
                // 注意：跳过解锁后如果 PPB 仍是锁死状态，擦除会失败——这只用于
                // 已经解锁过、且不需要重新解锁的卡。测完请把开关改回 false。
                if (SkipPpbUnlockForPowerMeasurement)
                {
                    AppendLog(T("log", "skip_ppb_unlock_power_test",
                        "PPB unlock skipped for power measurement; erasing directly."));
                }
                else
                {
                    UnlockGbaPpbOnDevice(
                        portName,
                        programmerKind,
                        targetMask,
                        sectorSizeBytes,
                        sectorsPerTarget);
                }
                EraseEntireNorOnDevice(portName, programmerKind, scanSnapshot, targetMask);
            });

            AppendLog(T("log", "erase_entire_nor_done", "Entire NOR erase and verification completed successfully."));

            if (IsFcWorkspaceActive)
            {
                string rescanMessage = T("status", "erase_entire_nor_rescan_required", "Entire NOR erased. Rescan the cartridge before continuing.");
                AppendLog(rescanMessage);
                SetBusy(false, rescanMessage);
                return;
            }

            try
            {
                ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                {
                    AppendLog(Tf("log", "erase_entire_nor_rescan_failed", "Full erase finished, but rescan failed: {0}", failureMessage));
                    SetBusy(false, T("status", "erase_entire_nor_rescan_failed", "Full erase complete, rescan failed"));
                    return;
                }

                SetBusy(false, T("status", "erase_entire_nor_done", "Entire NOR erased"));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "erase_entire_nor_rescan_failed", "Full erase finished, but rescan failed: {0}", ex.Message));
                SetBusy(false, T("status", "erase_entire_nor_rescan_failed", "Full erase complete, rescan failed"));
            }
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "erase_entire_nor_failed", "Full NOR erase failed: {0}", ex.Message));
            SetBusy(false, T("status", "erase_entire_nor_failed", "Full NOR erase failed"));
        }
    }

    private async Task UnlockPpbAsync()
    {
        if (!CanUnlockPpbCurrentScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = currentScanResult!;
        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
        bool useGbcPpb = scanSnapshot.IsBeggarMaxGbcMode;
        int chipMask = useGbcPpb ? 0x01 : ResolveGbaPpbChipMask(scanSnapshot, programmerKind);
        int chipCount = CountPpbTargets(chipMask);
        string detectedTargetDescription = BuildPpbDetectedTargetDescription(scanSnapshot, chipCount);
        string confirmMessage = useGbcPpb
            ? Tf(
                "dialog",
                "unlock_ppb_confirm_message_gbc",
                "Detected: {0}\n\nThis will clear all persistent sector protection bits (PPBs) on this GBC flash cart. ROM and save data will not be erased, but all flash sectors will become writable.\n\nDo you want to continue?",
                detectedTargetDescription)
            : Tf(
                "dialog",
                "unlock_ppb_confirm_message",
                "Detected: {0}\n\nThis will clear all persistent sector protection bits (PPBs) on this GBA flash cart. ROM and save data will not be erased, but all NOR sectors will become writable.\n\nDo you want to continue?",
                detectedTargetDescription);
        DialogResult confirm = MessageBox.Show(
            this,
            confirmMessage,
            T("dialog", "unlock_ppb_confirm_title", "Unlock PPB"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, T("status", "unlock_ppb_busy", "Unlocking PPB..."));
        AppendLog(useGbcPpb
            ? T("log", "unlock_ppb_start_gbc", "Start clearing all PPBs through the GBC/MBC5 command path.")
            : Tf(
                "log",
                "unlock_ppb_start",
                "Start clearing all GBA NOR PPBs on {0} target(s). Target mask=0x{1:X}.",
                chipCount,
                chipMask));

        try
        {
            await Task.Run(() =>
            {
                if (useGbcPpb)
                {
                    UnlockGbcPpbOnDevice(portName, programmerKind);
                }
                else
                {
                    int ppbSectorSizeBytes = ResolveGbaPpbSectorSizeBytes(scanSnapshot);
                    int ppbSectorsPerTarget = ResolveGbaPpbSectorsPerTarget(
                        scanSnapshot,
                        programmerKind,
                        chipCount,
                        ppbSectorSizeBytes);
                    UnlockGbaPpbOnDevice(
                        portName,
                        programmerKind,
                        chipMask,
                        ppbSectorSizeBytes,
                        ppbSectorsPerTarget);
                }
            });
            AppendLog(T("log", "unlock_ppb_done", "The cartridge passed PPB unlock verification."));

            try
            {
                ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                {
                    AppendLog(Tf("log", "unlock_ppb_rescan_failed", "PPB unlock finished, but rescan failed: {0}", failureMessage));
                    SetBusy(false, T("status", "unlock_ppb_rescan_failed", "PPB unlocked, rescan failed"));
                    return;
                }

                SetBusy(false, T("status", "unlock_ppb_done", "PPB unlocked"));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "unlock_ppb_rescan_failed", "PPB unlock finished, but rescan failed: {0}", ex.Message));
                SetBusy(false, T("status", "unlock_ppb_rescan_failed", "PPB unlocked, rescan failed"));
            }
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "unlock_ppb_failed", "PPB unlock failed: {0}", ex.Message));
            SetBusy(false, T("status", "unlock_ppb_failed", "PPB unlock failed"));
        }
    }

    private async Task UpdateMenuAsync()
    {
        if(HasActiveMixWorkspace){await fcWorkspace!.RebuildCardConfigurationAsync();return;}
        if (currentScanResult?.IsChm1 == true) { await InstallChm1MenuAsync(); return; }
        MenuUpdateActionMode actionMode = GetMenuUpdateActionMode();
        if (actionMode == MenuUpdateActionMode.None)
        {
            string reason = currentScanResult?.IsBeggarMaxGbcMode == true
                ? CanUpdateBeggarMaxMenuCurrentScan(out string maxReason) ? string.Empty : maxReason
                : UsesSelectedWaterLinkTargetMode()
                    ? CanUpdateWaterLinkMenuCurrentScan(out string wlReason) ? string.Empty : wlReason
                    : CanUpdateLegacyMenuCurrentScan(out string legacyReason) ? string.Empty : legacyReason;
            AppendLog(reason);
            return;
        }

        if (actionMode == MenuUpdateActionMode.UpdateBeggarMaxMenu)
        {
            await UpdateBeggarMaxMenuAsync();
            return;
        }

        if (actionMode == MenuUpdateActionMode.UpdateLegacyMenu)
        {
            await UpdateLegacyMenuAsync();
            return;
        }

        await UpdateWaterLinkMenuAsync();
    }

    private async Task ReplaceMenuFromFileAsync()
    {
        MenuUpdateActionMode actionMode = GetMenuUpdateActionMode();
        if (actionMode is not (MenuUpdateActionMode.UpdateWaterLinkMenu or MenuUpdateActionMode.UpdateLegacyMenu))
        {
            AppendLog(T("error", "replace_menu_file_not_available", "Replacing the menu from a file is not available for the current cart mode."));
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = T("dialog", "replace_menu_file_title", "Select Menu ROM"),
            Filter = T("dialog", "replace_menu_file_filter", "GBA ROM (*.gba)|*.gba|All files (*.*)|*.*"),
            Multiselect = false,
            CheckFileExists = true,
            AddExtension = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        bool usesWlLayout = actionMode == MenuUpdateActionMode.UpdateWaterLinkMenu;
        if (currentScanResult?.IsChm1 == true) { await InstallChm1MenuAsync(dialog.FileName); return; }
        try
        {
            _ = LoadMenuRomForUpdate(dialog.FileName, usesWlLayout, out _);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "update_menu_prepare_failed", "Unable to prepare menu update: {0}", ex.Message));
            return;
        }

        if (usesWlLayout)
        {
            await UpdateWaterLinkMenuAsync(dialog.FileName);
            return;
        }

        await UpdateLegacyMenuAsync(dialog.FileName);
    }

    private async Task UpdateBeggarMaxMenuAsync()
    {
        if (!CanUpdateBeggarMaxMenuCurrentScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = currentScanResult!;
        IReadOnlyList<WorkspaceListRow> workspaceSnapshot = CreateWorkspaceCartSnapshot(CreateWorkspaceSnapshot());
        int detectedEntryCount = workspaceSnapshot.Count;
        bool hasQueuedChanges = pendingImportItems.Count > 0;
        string queuedHint = hasQueuedChanges
            ? T(
                "dialog",
                "beggar_max_update_menu_queue_ignored",
                "\n\n注意：这个操作不会应用当前排队的 ROM 导入/替换，只会根据卡上已经存在的内容重建菜单。")
            : string.Empty;
        string languageHint = GetSelectedGbcMenuLanguageMode() == GbcMenuLanguageMode.English
            ? T(
                "dialog",
                "beggar_max_update_menu_english_language_hint",
                "\n\n当前会使用英文 MAX/MBCX 菜单；本次不会使用字库渲染。")
            : string.Empty;
        string updateHint = queuedHint + languageHint;
        string message = scanSnapshot.HasBeggarMaxDirectoryHeaders
            ? Tf(
                "dialog",
                "beggar_max_update_menu_clear_config_prompt",
                "是否清除配置区？\n\n是：清空当前 MBCX 目录，完整擦除并重写头部 1 MiB，刷入全新的空 MAX/MBCX 菜单。\n否：继承当前目录，更新菜单，并根据当前识别到的 {0} 个槽位重建索引。\n取消：不操作。\n\n1 MiB 之后的游戏 ROM 数据不会被擦除。清空后，在再次导入至少一个游戏前，当前老菜单会显示 NO MENU LAYERS。{1}",
                detectedEntryCount,
                updateHint)
            : Tf(
                "dialog",
                "beggar_max_update_menu_clear_config_nonfree_prompt",
                "当前还没有识别到有效的 MBCX 目录，但 NOR 芯片和 MAX 槽位头部已经识别成功。\n\n是：清空配置区，完整擦除并重写头部 1 MiB，刷入全新的空 MAX/MBCX 菜单。\n否：根据当前识别到的 {0} 个槽位建立目录并更新菜单。\n取消：不操作。\n\n如果当前固件不是兼容的自由合卡/MAX 固件，更新后卡可能无法正常启动。1 MiB 之后的游戏 ROM 数据不会被擦除。清空后，在再次导入至少一个游戏前，当前老菜单会显示 NO MENU LAYERS。{1}",
                detectedEntryCount,
                updateHint);

        DialogResult confirm = MessageBox.Show(
            this,
            message,
            T("dialog", "update_menu_confirm_title", "更新菜单"),
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirm == DialogResult.Cancel)
        {
            return;
        }

        bool clearConfigArea = confirm == DialogResult.Yes;
        if (!clearConfigArea && !TryValidateWorkspaceCapacity(workspaceSnapshot, out string capacityErrorMessage))
        {
            AppendLog(Tf(
                "log",
                "workspace_capacity_exceeded_detail",
                "Current workspace exceeds cart capacity: {0}",
                capacityErrorMessage));
            return;
        }

        SetBusy(true, T("status", "update_menu_busy", "Updating menu..."));
        AppendLog(Tf(
            "log",
            clearConfigArea ? "beggar_max_update_menu_start_clear" : "beggar_max_update_menu_start_inherit",
            clearConfigArea
                ? "开始清空 MBCX 配置区并完整擦写头部 1 MiB，生成空菜单。原目录条目数={0}。"
                : "开始继承 MBCX 配置区，重写菜单并根据当前识别到的 {0} 个槽位重建索引。",
            detectedEntryCount));

        try
        {
            BurnProfileMode burnProfileMode = GetSelectedBurnProfileMode();
            GbcMenuLanguageMode gbcMenuLanguageSnapshot = GetSelectedGbcMenuLanguageMode();
            string? gbcFontPathSnapshot = ResolveSelectedGbcFontPath();
            string? gbcBackgroundPathSnapshot = ResolveSelectedGbcBackgroundPath();
            WaterCartScanResult refreshed = await Task.Run(() => clearConfigArea || detectedEntryCount == 0
                ? RewriteEmptyBeggarMaxMenuOnDevice(
                    portName,
                    scanSnapshot,
                    burnProfileMode,
                    gbcFontPathSnapshot,
                    gbcBackgroundPathSnapshot,
                    gbcMenuLanguageSnapshot,
                    clearConfigArea)
                : ImportRomFilesOnDevice(
                    portName,
                    ProgrammerKind.BeggarLegacy,
                    scanSnapshot,
                    workspaceSnapshot,
                    burnProfileMode,
                    false,
                    gbcFontPathSnapshot,
                    gbcBackgroundPathSnapshot,
                    gbcMenuLanguageSnapshot));
            ApplyScanResult(refreshed);
            AppendLog(T(
                "log",
                clearConfigArea ? "beggar_max_update_menu_done_clear" : "beggar_max_update_menu_done_inherit",
                clearConfigArea
                    ? "MBCX 配置区已清空，空菜单已完成写入。"
                    : "MBCX 菜单已在继承当前目录后完成更新。"));
            SetBusy(false, T("status", "update_menu_done", "Menu update complete"));
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "update_menu_failed", "Menu update failed: {0}", ex.Message));
            SetBusy(false, T("status", "update_menu_failed", "Menu update failed"));
        }
    }

    private async Task<bool> UpdateMenuBackgroundAsync(IWin32Window? owner = null)
    {
        if (!CanUpdateMenuBackgroundCurrentScan(out string reason))
        {
            AppendLog(reason);
            return false;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return false;
        }

        using var dialog = new OpenFileDialog
        {
            Title = T("dialog", "update_menu_background_title", "更新菜单背景图"),
            Filter = T("dialog", "update_menu_background_filter", "PNG Image (*.png)|*.png|All files (*.*)|*.*"),
            Multiselect = false,
            CheckFileExists = true,
            AddExtension = false,
        };

        if (dialog.ShowDialog(owner ?? this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return false;
        }

        (byte[] bitmapData, byte[] paletteData) encodedBackground;
        try
        {
            encodedBackground = BackgroundImageEncoder.Encode(dialog.FileName);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("240x160", StringComparison.Ordinal))
        {
            AppendLog(T("error", "menu_background_invalid_size", "Background image must be exactly 240x160."));
            return false;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("240 colors", StringComparison.Ordinal))
        {
            AppendLog(T("error", "menu_background_too_many_colors", "Background image uses more than 240 colors."));
            return false;
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "update_menu_background_prepare_failed", "Unable to prepare the menu background image: {0}", ex.Message));
            return false;
        }

        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
        WaterCartScanResult scanSnapshot = currentScanResult ?? throw new InvalidOperationException("No active scan result.");

        SetBusy(true, T("status", "update_menu_background_busy", "Updating menu background..."));
        AppendLog(Tf(
            "log",
            "update_menu_background_start",
            "Start updating the current menu background from image: {0}",
            dialog.FileName));

        try
        {
            await Task.Run(() => UpdateMenuBackgroundOnDevice(portName, programmerKind, scanSnapshot, encodedBackground.bitmapData, encodedBackground.paletteData));
            AppendLog(Tf("log", "update_menu_background_done", "Menu background updated successfully: {0}", dialog.FileName));

            try
            {
                ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                {
                    AppendLog(Tf("log", "update_menu_background_rescan_failed", "Background update finished, but rescan failed: {0}", failureMessage));
                    SetBusy(false, T("status", "update_menu_background_rescan_failed", "Menu background updated, rescan failed"));
                    return false;
                }

                SetBusy(false, T("status", "update_menu_background_done", "Menu background updated"));
                return true;
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "update_menu_background_rescan_failed", "Background update finished, but rescan failed: {0}", ex.Message));
                SetBusy(false, T("status", "update_menu_background_rescan_failed", "Menu background updated, rescan failed"));
                return false;
            }
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "update_menu_background_failed", "Updating the menu background failed: {0}", ex.Message));
            SetBusy(false, T("status", "update_menu_background_failed", "Menu background update failed"));
            return false;
        }
    }

    private void ExportMenuBackgroundImage(Image sourceImage, IWin32Window owner)
    {
        using var dialog = new SaveFileDialog
        {
            Title = T("dialog", "export_menu_background_title", "Export Menu Background"),
            FileName = "menu_background.png",
            Filter = T("dialog", "export_menu_background_filter", "PNG Image (*.png)|*.png|All files (*.*)|*.*"),
            AddExtension = true,
            DefaultExt = "png",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(owner) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        try
        {
            using Image exportImage = (Image)sourceImage.Clone();
            exportImage.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
            AppendLog(Tf("log", "export_menu_background_done", "Menu background exported successfully: {0}", dialog.FileName));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "export_menu_background_failed", "Exporting menu background failed: {0}", ex.Message));
        }
    }

    private async Task UpdateWaterLinkMenuAsync(string? customMenuRomPath = null)
    {
        if (!CanUpdateWaterLinkMenuCurrentScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = currentScanResult!;
        WaterCartEntry? headAreaGame = GetHeadAreaGame();
        string customMenuHint = BuildCustomMenuSelectionHint(customMenuRomPath, usesWlLayout: true);
        string clearConfigMessage = headAreaGame is null
            ? T(
                "dialog",
                "force_rebuild_menu_clear_config_prompt",
                "Clear the config area?\n\nYes: clear current itemlist/WL mapping, fully erase and rewrite the head 4 MiB, then flash a fresh empty WL Magic menu.\nNo: inherit the current config area, preserve the current NS menu covers, update the menu with its bundled background, and rewrite the inherited itemlist/status/WL mapping.\nCancel: do nothing.") + customMenuHint
            : Tf(
                "dialog",
                "force_rebuild_menu_clear_config_head_prompt",
                "Clear the config area?\n\nThe first 4 MiB currently contains:\n{0}\n\nYes: clear current itemlist/WL mapping, fully erase and rewrite the head 4 MiB, then flash a fresh empty WL Magic menu.\nNo: inherit the current config area, preserve the current NS menu covers, update the menu with its bundled background, and rewrite the inherited itemlist/status/WL mapping.\nCancel: do nothing.\n\nWriting the menu will overwrite the head-area game above.",
                headAreaGame.Title) + customMenuHint;

        DialogResult clearConfigConfirm = MessageBox.Show(
            this,
            clearConfigMessage,
            T("dialog", "force_rebuild_menu_confirm_title", "Rebuild Menu"),
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (clearConfigConfirm == DialogResult.Cancel)
        {
            return;
        }

        bool clearConfigArea = clearConfigConfirm == DialogResult.Yes;
        WaterLinkMenuUpdatePlan updatePlan;

        try
        {
            updatePlan = BuildWaterLinkMenuUpdatePlan(scanSnapshot, clearConfigArea, customMenuRomPath);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "update_menu_prepare_failed", "Unable to prepare menu update: {0}", ex.Message));
            return;
        }

        SetBusy(true, T("status", "update_menu_busy", "Updating menu..."));
        AppendCustomMenuSourceLog(customMenuRomPath, usesWlLayout: true);
        AppendLog(Tf(
            "log",
            clearConfigArea ? "force_rebuild_menu_start_wl_clear" : "force_rebuild_menu_start_wl_inherit",
            clearConfigArea
                ? "Start rebuilding the WL Magic menu with a cleared config area. Entries={0}, mapped 4MiB buckets={1}"
                : "Start rebuilding the WL Magic menu while inheriting the current config area. Entries={0}, mapped 4MiB buckets={1}",
            updatePlan.MenuConfig.ItemCount,
            updatePlan.MappedBucketCount));

        if (updatePlan.IsLegacyMigration && updatePlan.DroppedLegacyEntries.Count > 0)
        {
            string droppedText = string.Join(" | ", updatePlan.DroppedLegacyEntries.Select(entry => TrimLogTitle(entry.Title, 24)));
            AppendLog(Tf("log", "upgrade_magic_drop_legacy_only", "Drop stale legacy-only entries: {0}", droppedText));
        }

        if (updatePlan.IsLegacyMigration && updatePlan.AddedHeaderOnlyEntries.Count > 0)
        {
            string addedText = string.Join(" | ", updatePlan.AddedHeaderOnlyEntries.Select(entry => TrimLogTitle(entry.Title, 24)));
            AppendLog(Tf("log", "upgrade_magic_add_header_only", "Add header-only ROMs missing from the old menu: {0}", addedText));
        }

        try
        {
            ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
            await Task.Run(() => UpdateWaterLinkMenuOnDevice(
                portName,
                programmerKind,
                scanSnapshot,
                updatePlan,
                T("ui", "force_rebuild_menu", "重建菜单"),
                preserveCurrentBackground: false,
                preserveCurrentCovers: !clearConfigArea,
                rewriteFullHeadArea: clearConfigArea));
            AppendLog(T(
                "log",
                clearConfigArea ? "force_rebuild_menu_done_wl_clear" : "force_rebuild_menu_done_wl_inherit",
                clearConfigArea
                    ? "The WL Magic menu was rebuilt after clearing the config area."
                    : "The WL Magic menu was rebuilt while inheriting the current config area."));

            try
            {
                ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                {
                    AppendLog(Tf("log", "update_menu_rescan_failed", "Menu update finished, but rescan failed: {0}", failureMessage));
                    SetBusy(false, T("status", "update_menu_rescan_failed", "Menu update complete, rescan failed"));
                    return;
                }

                SetBusy(false, T("status", "update_menu_done", "Menu update complete"));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "update_menu_rescan_failed", "Menu update finished, but rescan failed: {0}", ex.Message));
                SetBusy(false, T("status", "update_menu_rescan_failed", "Menu update complete, rescan failed"));
            }
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "update_menu_failed", "Menu update failed: {0}", ex.Message));
            SetBusy(false, T("status", "update_menu_failed", "Menu update failed"));
        }
    }

    private async Task RepairWaterLinkFromLegacyMenuAsync()
    {
        if (!CanUpgradeCurrentScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = currentScanResult!;
        WaterLinkMenuUpdatePlan updatePlan;

        try
        {
            updatePlan = BuildWaterLinkMenuUpdatePlan(scanSnapshot);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "repair_menu_prepare_failed", "Unable to prepare menu repair/install: {0}", ex.Message));
            return;
        }

        string message = Tf(
            "dialog",
            "repair_menu_wl_from_legacy_confirm_message",
            "The WaterLink flash-cart target is selected, but the scan found legacy menu config on this cart.\n\nContinuing will:\n1. Flash the bundled WL menu\n2. Rebuild {0} WL menu entries, status, and WL mapping from the current legacy itemlist/status\n3. Preserve current game ROM data\n\nMapped 4MiB buckets this time: {1}\n\nDo you want to continue?",
            updatePlan.MenuConfig.ItemCount,
            updatePlan.MappedBucketCount);

        DialogResult confirm = MessageBox.Show(
            this,
            message,
            T("dialog", "repair_menu_confirm_title", "Repair Menu"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, T("status", "repair_menu_busy", "Repairing menu..."));
        AppendLog(Tf(
            "log",
            "repair_menu_start_wl_from_legacy",
            "WaterLink flash-cart target is selected and legacy menu config was detected. Start rebuilding the WL menu and mapping from the current menu config. Entries={0}, mapped 4MiB buckets={1}",
            updatePlan.MenuConfig.ItemCount,
            updatePlan.MappedBucketCount));

        if (updatePlan.IsLegacyMigration && updatePlan.DroppedLegacyEntries.Count > 0)
        {
            string droppedText = string.Join(" | ", updatePlan.DroppedLegacyEntries.Select(entry => TrimLogTitle(entry.Title, 24)));
            AppendLog(Tf("log", "upgrade_magic_drop_legacy_only", "Drop stale legacy-only entries: {0}", droppedText));
        }

        if (updatePlan.IsLegacyMigration && updatePlan.AddedHeaderOnlyEntries.Count > 0)
        {
            string addedText = string.Join(" | ", updatePlan.AddedHeaderOnlyEntries.Select(entry => TrimLogTitle(entry.Title, 24)));
            AppendLog(Tf("log", "upgrade_magic_add_header_only", "Add header-only ROMs missing from the old menu: {0}", addedText));
        }

        try
        {
            ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
            await Task.Run(() => UpdateWaterLinkMenuOnDevice(
                portName,
                programmerKind,
                scanSnapshot,
                updatePlan,
                T("ui", "repair_menu", "Repair Menu"),
                preserveCurrentBackground: false,
                preserveCurrentCovers: true));
            AppendLog(T("log", "repair_menu_done_wl_from_legacy", "WL menu, itemlist, status, and WL mapping were rebuilt from the current legacy menu config."));

            try
            {
                ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                {
                    AppendLog(Tf("log", "repair_menu_rescan_failed", "Repair finished, but rescan failed: {0}", failureMessage));
                    SetBusy(false, T("status", "repair_menu_rescan_failed", "Repair complete, rescan failed"));
                    return;
                }

                SetBusy(false, T("status", "repair_menu_done", "Repair complete"));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "repair_menu_rescan_failed", "Repair finished, but rescan failed: {0}", ex.Message));
                SetBusy(false, T("status", "repair_menu_rescan_failed", "Repair complete, rescan failed"));
            }
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "repair_menu_failed", "Repair/install menu failed: {0}", ex.Message));
            SetBusy(false, T("status", "repair_menu_failed", "Repair failed"));
        }
    }

    private async Task UpdateLegacyMenuAsync(string? customMenuRomPath = null)
    {
        if (!CanUpdateLegacyMenuCurrentScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = currentScanResult!;
        LegacyMenuUpdatePlan updatePlan;

        try
        {
            updatePlan = BuildLegacyMenuUpdatePlan(scanSnapshot, customMenuRomPath);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "update_menu_prepare_failed", "Unable to prepare menu update: {0}", ex.Message));
            return;
        }

        int updateLegacySaveAreaStart = updatePlan.MenuConfig.Status.StatusAddress + updatePlan.MenuConfig.Status.SectorSize;
        bool batteryPresent = updatePlan.MenuConfig.Status.BatteryPresent;
        string message = Tf(
            "dialog",
            batteryPresent ? "update_gba_menu_battery_confirm_message" : "update_gba_menu_confirm_message",
            batteryPresent
                ? "Update this cart to the selected GBA menu?\n\nThis will:\n1. Rewrite the GBA menu area 0x000000-0x{1:X6}\n2. Preserve the save area 0x{2:X6}-0x3FFFFF\n3. Rewrite itemlist and status\n4. Preserve the current visible menu order and rewrite {0} menu entries\n5. Preserve the current cover pack when both the current and selected menus support covers\n\nROM data will not be erased. Existing save data will not be touched.\n\nDo you want to continue?"
                : "Update this cart to the selected GBA menu?\n\nThis will:\n1. Erase 0x000000-0x3FFFFF\n2. Flash the selected GBA menu\n3. Rewrite itemlist and status\n4. Preserve the current visible menu order and rewrite {0} menu entries\n5. Preserve the current cover pack when both the current and selected menus support covers\n\nROM data will not be erased. Existing save data will not be touched.\n\nDo you want to continue?",
            updatePlan.MenuConfig.ItemCount,
            updateLegacySaveAreaStart - 1,
            updateLegacySaveAreaStart) + BuildCustomMenuSelectionHint(customMenuRomPath, usesWlLayout: false);

        DialogResult confirm = MessageBox.Show(
            this,
            message,
            T("dialog", "update_menu_confirm_title", "更新菜单"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, T("status", "update_menu_busy", "Updating menu..."));
        AppendCustomMenuSourceLog(customMenuRomPath, usesWlLayout: false);
        AppendLog(Tf(
            "log",
            "update_menu_start_gba",
            "Start updating the cart to the selected GBA menu. Entries={0}, itemlist=0x{1:X6}, status=0x{2:X6}",
            updatePlan.MenuConfig.ItemCount,
            updatePlan.MenuConfig.Status.ItemListAddress,
            updatePlan.MenuConfig.Status.StatusAddress));
        AppendLog(Tf(
            "log",
            "repair_menu_layout",
            "Menu repair layout source: {0}",
            updatePlan.LayoutSource));

        try
        {
            ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
            await Task.Run(() => UpdateLegacyMenuOnDevice(portName, programmerKind, scanSnapshot, updatePlan));
            AppendLog(T("log", "update_menu_done_gba", "The selected GBA menu, itemlist, and status were written successfully."));

            try
            {
                ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                {
                    AppendLog(Tf("log", "update_menu_rescan_failed", "Menu update finished, but rescan failed: {0}", failureMessage));
                    SetBusy(false, T("status", "update_menu_rescan_failed", "Menu update complete, rescan failed"));
                    return;
                }

                SetBusy(false, T("status", "update_menu_done", "Menu update complete"));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "update_menu_rescan_failed", "Menu update finished, but rescan failed: {0}", ex.Message));
                SetBusy(false, T("status", "update_menu_rescan_failed", "Menu update complete, rescan failed"));
            }
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "update_menu_failed", "Menu update failed: {0}", ex.Message));
            SetBusy(false, T("status", "update_menu_failed", "Menu update failed"));
        }
    }

    private WaterLinkMenuUpdatePlan BuildWaterLinkMenuUpdatePlan(
        WaterCartScanResult scanSnapshot,
        bool clearConfigArea = false,
        string? customMenuRomPath = null)
    {
        byte[] menuRomBytes = LoadMenuRomForUpdate(customMenuRomPath, usesWlLayout: true, out string menuRomPath);

        if (clearConfigArea)
        {
            return BuildWaterLinkMenuUpdatePlanFromConfig(
                menuRomBytes,
                menuRomPath,
                BuildEmptyWaterLinkMenuConfig(),
                []);
        }

        if (CanRepairCurrentScan(out _))
        {
            WaterMagicRepairPlan repairPlan = WaterMagicUpgradeBuilder.BuildRepairPlan(scanSnapshot);
            byte[] headAreaImage = WaterMagicUpgradeBuilder.BuildHeadAreaImage(
                menuRomBytes,
                repairPlan.RepairedMenuConfig,
                repairPlan.ItemListSector,
                repairPlan.StatusSector,
                repairPlan.MappingSector);

            return new WaterLinkMenuUpdatePlan
            {
                MenuConfig = repairPlan.RepairedMenuConfig,
                HeadAreaImage = headAreaImage,
                MenuRomPath = menuRomPath,
                IsMetadataRepair = true,
                MappedBucketCount = repairPlan.RepairedRecords.Sum(record => record.LogicalBucketCount),
            };
        }

        if (CanUpgradeCurrentScan(out _))
        {
            WaterMenuConfigInfo migratedMenuConfig = WaterMagicUpgradeBuilder.BuildMigratedMenuConfig(
                scanSnapshot,
                out List<WaterCartEntry> droppedLegacyEntries,
                out List<WaterCartEntry> addedHeaderOnlyEntries);
            byte[] itemListSector = WaterMenuConfigCodec.BuildItemListSector(
                migratedMenuConfig.Items,
                migratedMenuConfig.Status.ItemListAddress,
                migratedMenuConfig.Status.SectorSize);
            byte[] statusSector = WaterMenuConfigCodec.BuildStatusSector(migratedMenuConfig.Status);
            List<WlRomMapRecordInfo> migratedRecords = BuildWlMappingRecordsFromMenuConfig(migratedMenuConfig);
            byte[] mappingSector = WaterMagicUpgradeBuilder.BuildMappingSector(migratedRecords);
            byte[] headAreaImage = WaterMagicUpgradeBuilder.BuildHeadAreaImage(
                menuRomBytes,
                migratedMenuConfig,
                itemListSector,
                statusSector,
                mappingSector);

            return new WaterLinkMenuUpdatePlan
            {
                MenuConfig = migratedMenuConfig,
                HeadAreaImage = headAreaImage,
                MenuRomPath = menuRomPath,
                IsLegacyMigration = true,
                DroppedLegacyEntries = droppedLegacyEntries,
                AddedHeaderOnlyEntries = addedHeaderOnlyEntries,
                MappedBucketCount = migratedRecords.Sum(record => record.LogicalBucketCount),
            };
        }

        if (scanSnapshot.MenuConfig.IsDetected)
        {
            List<MenuItemConfigInfo> clonedItems = scanSnapshot.MenuConfig.Items
                .OrderBy(item => item.Index)
                .Select(WaterMenuConfigCodec.Clone)
                .ToList();
            WaterMenuConfigInfo currentMenuConfig = new()
            {
                IsDetected = true,
                Status = WaterMagicUpgradeBuilder.NormalizeWlStatus(scanSnapshot.MenuConfig.Status),
            };
            currentMenuConfig.Items = WaterMenuConfigCodec.ReindexItems(
                clonedItems,
                currentMenuConfig.Status.ItemListAddress);

            List<WlRomMapRecordInfo> mappingRecords = scanSnapshot.WlMapping.IsDetected
                ? CloneWlMappingRecords(scanSnapshot.WlMapping.Records.OrderBy(record => record.EntrySlot))
                : BuildWlMappingRecordsFromMenuConfig(currentMenuConfig);

            return BuildWaterLinkMenuUpdatePlanFromConfig(
                menuRomBytes,
                menuRomPath,
                currentMenuConfig,
                mappingRecords);
        }

        if (scanSnapshot.WlMapping.IsDetected)
        {
            return BuildWaterLinkMenuUpdatePlanFromConfig(
                menuRomBytes,
                menuRomPath,
                BuildEmptyWaterLinkMenuConfig(),
                CloneWlMappingRecords(scanSnapshot.WlMapping.Records.OrderBy(record => record.EntrySlot)));
        }

        WaterMagicInstallPlan installPlan = WaterMagicUpgradeBuilder.BuildInstallPlan(menuRomBytes, scanSnapshot);
        return new WaterLinkMenuUpdatePlan
        {
            MenuConfig = installPlan.InstalledMenuConfig,
            HeadAreaImage = installPlan.HeadAreaImage,
            MenuRomPath = menuRomPath,
            MappedBucketCount = installPlan.InstalledMenuConfig.Items.Sum(item => item.CoveredBucketCount),
        };
    }

    private static WaterLinkMenuUpdatePlan BuildWaterLinkMenuUpdatePlanFromConfig(
        byte[] menuRomBytes,
        string menuRomPath,
        WaterMenuConfigInfo menuConfig,
        IReadOnlyList<WlRomMapRecordInfo> mappingRecords)
    {
        byte[] currentItemListSector = WaterMenuConfigCodec.BuildItemListSector(
            menuConfig.Items,
            menuConfig.Status.ItemListAddress,
            menuConfig.Status.SectorSize);
        byte[] currentStatusSector = WaterMenuConfigCodec.BuildStatusSector(menuConfig.Status);
        byte[] currentMappingSector = WaterMagicUpgradeBuilder.BuildMappingSector(mappingRecords);
        byte[] currentHeadAreaImage = WaterMagicUpgradeBuilder.BuildHeadAreaImage(
            menuRomBytes,
            menuConfig,
            currentItemListSector,
            currentStatusSector,
            currentMappingSector);

        return new WaterLinkMenuUpdatePlan
        {
            MenuConfig = menuConfig,
            HeadAreaImage = currentHeadAreaImage,
            MenuRomPath = menuRomPath,
            MappedBucketCount = mappingRecords.Sum(record => record.LogicalBucketCount),
        };
    }

    private static WaterMenuConfigInfo BuildEmptyWaterLinkMenuConfig()
    {
        MenuFlashStatusInfo status = new()
        {
            StatusAddress = WaterMagicUpgradeBuilder.WlStatusOffset,
            SectorSize = WaterWlMappingCodec.SectorSize,
            Version = 0,
            BatteryPresent = false,
            LastBootMenuIndex = 0,
            LastBootSaveIndex = 0xFF,
            LastBootSaveType = 0,
            SramBankType = 0,
        };

        return new WaterMenuConfigInfo
        {
            IsDetected = true,
            Status = status,
            Items = [],
        };
    }

    private static List<WlRomMapRecordInfo> BuildWlMappingRecordsFromMenuConfig(WaterMenuConfigInfo menuConfig)
    {
        return menuConfig.Items
            .OrderBy(item => item.Index)
            .Select(item => new WlRomMapRecordInfo
            {
                EntrySlot = item.Index,
                LogicalBucketCount = item.CoveredBucketCount,
                PhysicalBuckets = Enumerable.Range(item.StartBucket, item.CoveredBucketCount).ToList(),
            })
            .ToList();
    }

    private static List<WlRomMapRecordInfo> CloneWlMappingRecords(IEnumerable<WlRomMapRecordInfo> records)
    {
        return records
            .Select(record => new WlRomMapRecordInfo
            {
                EntrySlot = record.EntrySlot,
                LogicalBucketCount = record.LogicalBucketCount,
                PhysicalBuckets = record.PhysicalBuckets.ToList(),
            })
            .ToList();
    }

    private LegacyMenuUpdatePlan BuildLegacyMenuUpdatePlan(WaterCartScanResult scanSnapshot, string? customMenuRomPath = null)
    {
        byte[] menuRomBytes = LoadMenuRomForUpdate(customMenuRomPath, usesWlLayout: false, out string menuRomPath);

        if (!scanSnapshot.MenuConfig.IsDetected)
        {
            throw new InvalidOperationException(T(
                "error",
                "update_menu_requires_existing_menu",
                "No menu config was detected. Use 'Repair Menu' first."));
        }

        List<MenuItemConfigInfo> clonedItems = scanSnapshot.MenuConfig.Items
            .OrderBy(item => item.Index)
            .Select(WaterMenuConfigCodec.Clone)
            .ToList();
        MenuFlashStatusInfo status = CreateWorkspaceStatusForBurn(scanSnapshot.MenuConfig.Status, GetSelectedBurnProfileMode());
        WaterMenuConfigInfo currentMenuConfig = new()
        {
            IsDetected = true,
            Status = status,
            Items = WaterMenuConfigCodec.ReindexItems(clonedItems, status.ItemListAddress),
        };
        byte[] itemListSector = WaterMenuConfigCodec.BuildItemListSector(
            currentMenuConfig.Items,
            currentMenuConfig.Status.ItemListAddress,
            currentMenuConfig.Status.SectorSize);
        byte[] statusSector = WaterMenuConfigCodec.BuildStatusSector(currentMenuConfig.Status);
        byte[] headAreaImage = WaterLegacyMenuRepairBuilder.BuildHeadAreaImage(
            menuRomBytes,
            currentMenuConfig,
            itemListSector,
            statusSector);

        return new LegacyMenuUpdatePlan
        {
            MenuConfig = currentMenuConfig,
            HeadAreaImage = headAreaImage,
            MenuRomPath = menuRomPath,
            LayoutSource = "current-menu-config",
            SupportsCovers = GbaMenuRomService.SupportsCovers(menuRomBytes),
        };
    }

    private byte[] LoadMenuRomForUpdate(string? customMenuRomPath, bool usesWlLayout, out string menuRomPath)
    {
        if (string.IsNullOrWhiteSpace(customMenuRomPath))
        {
            return LoadSelectedGbaMenuRom(usesWlLayout, out menuRomPath);
        }

        menuRomPath = Path.GetFullPath(customMenuRomPath);
        byte[] menuRomBytes = File.ReadAllBytes(menuRomPath);
        if (!WaterCartScanner.HasValidGbaHeader(menuRomBytes))
        {
            throw new InvalidOperationException(T(
                "error",
                "replace_menu_file_invalid_gba",
                "The selected file does not have a valid GBA ROM header."));
        }

        bool supportsCovers = GbaMenuRomService.SupportsCovers(menuRomBytes);
        if (supportsCovers)
        {
            menuRomBytes = GbaMenuRomService.PrepareCoverMenuRom(menuRomBytes);
        }
        else if (!TryResolveMenuBackgroundRegion(menuRomBytes, usesWlLayout, out int backgroundOffset) ||
                 menuRomBytes
                     .AsSpan(backgroundOffset, BackgroundImageEncoder.Width * BackgroundImageEncoder.Height)
                     .ContainsAnyInRange((byte)BackgroundImageEncoder.MaxBackgroundColors, byte.MaxValue))
        {
            throw new InvalidOperationException(T(
                "error",
                "replace_menu_file_invalid_layout",
                "The selected file is not a compatible menu ROM for the current layout."));
        }

        return menuRomBytes;
    }

    private string BuildCustomMenuSelectionHint(string? customMenuRomPath, bool usesWlLayout)
    {
        if (string.IsNullOrWhiteSpace(customMenuRomPath))
        {
            return string.Empty;
        }

        return Tf(
            "dialog",
            "replace_menu_file_selected_hint",
            "\n\nSelected menu file:\n{0}\n\nThis file replaces the bundled menu in the operation above. Continue only if it was built for the current {1} layout.",
            Path.GetFullPath(customMenuRomPath),
            usesWlLayout ? "WL" : T("value", "legacy_menu_layout_name", "legacy"));
    }

    private void AppendCustomMenuSourceLog(string? customMenuRomPath, bool usesWlLayout)
    {
        if (string.IsNullOrWhiteSpace(customMenuRomPath))
        {
            return;
        }

        AppendLog(Tf(
            "log",
            "replace_menu_file_source",
            "Use selected {0} menu ROM: {1}",
            usesWlLayout ? "WL" : T("value", "legacy_menu_layout_name", "legacy"),
            Path.GetFullPath(customMenuRomPath)));
    }

    private async Task RepairMagicMetadataAsync()
    {
        if (currentScanResult?.IsChm1 == true) { await InstallChm1MenuAsync(); return; }
        if (!CanRepairCurrentScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = currentScanResult!;
        WaterMagicRepairPlan repairPlan;

        try
        {
            repairPlan = WaterMagicUpgradeBuilder.BuildRepairPlan(scanSnapshot);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "repair_magic_prepare_failed", "Unable to prepare Magic repair: {0}", ex.Message));
            return;
        }

        WaterMagicMetadataSyncInfo syncInfo = repairPlan.SyncInfo;
        int itemListAddress = repairPlan.RepairedMenuConfig.Status.ItemListAddress;
        bool rebuildingMappingFromItemlist =
            scanSnapshot.WlMapping.Header.Magic == WaterWlMappingCodec.Magic &&
            !scanSnapshot.WlMapping.IsDetected;

        string message = rebuildingMappingFromItemlist
            ? Tf(
                "dialog",
                "repair_magic_confirm_message_legacy",
                "Detected a WL mapping sector that needs to be rebuilt.\n\nThis will:\n1. Rewrite itemlist sector at 0x{0:X6}-0x{1:X6}\n2. Rewrite status sector at 0x{2:X6}-0x{3:X6}\n3. Rewrite WL mapping sector at 0x{4:X6}-0x{5:X6}\n4. Rebuild {6} visible entry/entries from the current itemlist\n5. Upgrade the cart to the new checksummed WL mapping format\n\nROM data and save data will not be erased.\n\nDo you want to continue?",
                itemListAddress,
                itemListAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1,
                repairPlan.RepairedMenuConfig.Status.StatusAddress,
                repairPlan.RepairedMenuConfig.Status.StatusAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1,
                WaterWlMappingCodec.Offset,
                WaterWlMappingCodec.Offset + WaterWlMappingCodec.SectorSize - 1,
                repairPlan.RepairedMenuConfig.ItemCount)
            : Tf(
                "dialog",
                "repair_magic_confirm_message",
                "Repair the current Magic metadata?\n\nThis will:\n1. Rewrite itemlist sector at 0x{0:X6}-0x{1:X6}\n2. Rewrite status sector at 0x{2:X6}-0x{3:X6}\n3. Rewrite WL mapping sector at 0x{4:X6}-0x{5:X6}\n4. Rebuild {6} visible entry/entries from current WL mapping records\n5. Remove {7} stale itemlist-only entry/entries\n\nDetected desync:\n- itemlist-only slots: {8}\n- mapping-only slots: {9}\n- mismatched slots: {10}\n- non-sequential mapping slots: {11}\n\nROM data and save data will not be erased.\n\nDo you want to continue?",
                itemListAddress,
                itemListAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1,
                repairPlan.RepairedMenuConfig.Status.StatusAddress,
                repairPlan.RepairedMenuConfig.Status.StatusAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1,
                WaterWlMappingCodec.Offset,
                WaterWlMappingCodec.Offset + WaterWlMappingCodec.SectorSize - 1,
                repairPlan.RepairedMenuConfig.ItemCount,
                repairPlan.RemovedMenuOnlyItems.Count,
                FormatSlotListForLog(syncInfo.MenuOnlySlots),
                FormatSlotListForLog(syncInfo.MappingOnlySlots),
                FormatSlotListForLog(syncInfo.SlotRangeMismatchSlots),
                FormatSlotListForLog(syncInfo.NonSequentialRecordSlots));

        DialogResult confirm = MessageBox.Show(
            this,
            message,
            T("dialog", "repair_magic_confirm_title", "Repair Magic Metadata"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, T("status", "repair_magic_busy", "Repairing Magic metadata..."));
        AppendLog(rebuildingMappingFromItemlist
            ? Tf(
                "log",
                "repair_magic_start_legacy",
                "Detected a WL mapping sector that needs rebuild. Rebuilding the new checksummed mapping sector from the current itemlist. itemlist={0}, entries={1}",
                itemListAddress,
                repairPlan.RepairedMenuConfig.ItemCount)
            : Tf(
                "log",
                "repair_magic_start",
                "Start repairing Magic metadata. itemlist={0}, mapping={1}, itemlist-only={2}, mapping-only={3}, mismatched={4}, non-sequential={5}",
                syncInfo.MenuItemCount,
                syncInfo.MappingRecordCount,
                FormatSlotListForLog(syncInfo.MenuOnlySlots),
                FormatSlotListForLog(syncInfo.MappingOnlySlots),
                FormatSlotListForLog(syncInfo.SlotRangeMismatchSlots),
                FormatSlotListForLog(syncInfo.NonSequentialRecordSlots)));

        if (repairPlan.RemovedMenuOnlyItems.Count > 0)
        {
            string removedText = string.Join(" | ", repairPlan.RemovedMenuOnlyItems.Select(item => TrimLogTitle(item.Title, 24)));
            AppendLog(Tf("log", "repair_magic_remove_stale_items", "Remove stale itemlist-only entries: {0}", removedText));
        }

        try
        {
            ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
            await Task.Run(() => RepairMagicMetadataOnDevice(portName, programmerKind, scanSnapshot));
            AppendLog(T("log", "repair_magic_done", "Magic itemlist and WL mapping sector were repaired successfully."));

            try
            {
                ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
                if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
                {
                    AppendLog(Tf("log", "repair_magic_rescan_failed", "Repair finished, but rescan failed: {0}", failureMessage));
                    SetBusy(false, T("status", "repair_magic_rescan_failed", "Repair complete, rescan failed"));
                    return;
                }

                SetBusy(false, T("status", "repair_magic_done", "Repair complete"));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "repair_magic_rescan_failed", "Repair finished, but rescan failed: {0}", ex.Message));
                SetBusy(false, T("status", "repair_magic_rescan_failed", "Repair complete, rescan failed"));
            }
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "repair_magic_failed", "Magic metadata repair failed: {0}", ex.Message));
            SetBusy(false, T("status", "repair_magic_failed", "Repair failed"));
        }
    }

    private void UpgradeMagicOnDevice(string portName, ProgrammerKind programmerKind, WaterCartScanResult scanResult)
    {
        if (!WaterMagicUpgradeBuilder.CanUpgradeToMagic(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        byte[] menuRomBytes = WaterMagicUpgradeBuilder.LoadMagicMenuRom(out string menuRomPath);
        WaterMagicUpgradePlan upgradePlan = WaterMagicUpgradeBuilder.BuildUpgradePlan(menuRomBytes, scanResult);
        long totalBytes = CountRewriteRangeProgressWorkBytes(upgradePlan.MenuAreaImage, scanResult.Geometry)
            + CountRewriteSectorProgressWorkBytes(upgradePlan.ItemListSector, scanResult.Geometry)
            + CountRewriteSectorProgressWorkBytes(upgradePlan.StatusSector, scanResult.Geometry)
            + CountRewriteSectorProgressWorkBytes(upgradePlan.MappingSector, scanResult.Geometry);
        int totalSectors = CountRewriteRangeProgressUnits(upgradePlan.MenuAreaImage, scanResult.Geometry)
            + CountRewriteSectorProgressUnits(upgradePlan.ItemListSector)
            + CountRewriteSectorProgressUnits(upgradePlan.StatusSector)
            + CountRewriteSectorProgressUnits(upgradePlan.MappingSector);

        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        string magicMenuLabel = T("value", "magic_menu_label", "Magic menu");
        string wlMappingLabel = T("value", "wl_mapping_label", "WL mapping");
        string itemListLabel = T("value", "itemlist_label", "itemlist");
        string statusLabel = T("value", "status_label", "status");

        BeginMeasuredProgress(T("ui", "upgrade_magic", "Upgrade Magic"), totalBytes, totalSectors);
        AppendLog(Tf("log", "use_magic_menu_rom", "Use magic menu ROM: {0}", menuRomPath));
        AppendLog(Tf(
            "log",
            "write_menu_area",
            "Write menu area: 0x000000-0x{0:X6} | {1} sector(s)",
            upgradePlan.MenuAreaImage.Length - 1,
            upgradePlan.MenuAreaImage.Length / scanResult.Geometry.SectorSizeBytes));
        AppendLog(Tf(
            "log",
            "write_itemlist_sector",
            "Write itemlist sector: 0x{0:X6}-0x{1:X6}",
            upgradePlan.MigratedMenuConfig.Status.ItemListAddress,
            upgradePlan.MigratedMenuConfig.Status.ItemListAddress + upgradePlan.MigratedMenuConfig.Status.SectorSize - 1));

        AppendLog(Tf(
            "log",
            "write_wl_mapping_sector",
            "Write WL mapping sector: 0x{0:X6}-0x{1:X6}",
            WaterWlMappingCodec.Offset,
            WaterWlMappingCodec.Offset + WaterWlMappingCodec.SectorSize - 1));
        AppendLog(Tf(
            "log",
            "write_status_sector",
            "Write status sector: 0x{0:X6}-0x{1:X6}",
            upgradePlan.MigratedMenuConfig.Status.StatusAddress,
            upgradePlan.MigratedMenuConfig.Status.StatusAddress + upgradePlan.MigratedMenuConfig.Status.SectorSize - 1));

        int sectorSize = scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;
        List<(uint Address, byte[] Data, string Label)> sectorBatch = [];
        sectorBatch.AddRange(BuildNorSectorWriteBatch(0, upgradePlan.MenuAreaImage, sectorSize, magicMenuLabel));
        sectorBatch.Add(((uint)upgradePlan.MigratedMenuConfig.Status.ItemListAddress, upgradePlan.ItemListSector, itemListLabel));
        sectorBatch.Add(((uint)upgradePlan.MigratedMenuConfig.Status.StatusAddress, upgradePlan.StatusSector, statusLabel));
        sectorBatch.Add(((uint)WaterWlMappingCodec.Offset, upgradePlan.MappingSector, wlMappingLabel));
        RewriteNorSectorBatch(protocol, sectorBatch, scanResult.Geometry, programmerKind);
        CompleteMeasuredProgress(T("ui", "upgrade_magic", "Upgrade Magic"));
    }

    private void UpdateWaterLinkMenuOnDevice(
        string portName,
        ProgrammerKind programmerKind,
        WaterCartScanResult scanResult,
        WaterLinkMenuUpdatePlan updatePlan,
        string? operationTitle = null,
        bool preserveCurrentBackground = false,
        bool preserveCurrentCovers = false,
        bool rewriteFullHeadArea = true)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        byte[] headAreaImage = updatePlan.HeadAreaImage.ToArray();
        if (preserveCurrentBackground && scanResult.MenuConfig.IsDetected)
        {
            PreserveCurrentMenuBackground(protocol, programmerKind, scanResult, headAreaImage, targetUsesWlLayout: true);
        }

        if (preserveCurrentCovers)
        {
            PreserveCurrentGbaMenuCovers(protocol, programmerKind, scanResult, headAreaImage);
        }

        string progressTitle = operationTitle ?? T("ui", "update_menu", "更新菜单");
        string magicHeadAreaLabel = T("value", "magic_head_area_label", "Magic head 4MiB");
        List<WaterNorWriteSegment> segments = rewriteFullHeadArea
            ? BuildMenuHeadWriteSegments(headAreaImage, updatePlan.MenuConfig.Status, true, magicHeadAreaLabel)
            : BuildWaterLinkInheritedMenuWriteSegments(headAreaImage, updatePlan.MenuConfig);

        BeginMeasuredProgress(
            progressTitle,
            segments.Sum(segment => CountSegmentProgressWorkBytes(segment, scanResult.Geometry)),
            segments.Sum(segment => CountSegmentProgressUnits(segment, scanResult.Geometry)));
        AppendLog(Tf("log", "use_magic_menu_rom", "Use magic menu ROM: {0}", updatePlan.MenuRomPath));

        foreach (WaterNorWriteSegment segment in segments)
        {
            AppendLog(Tf(
                "log",
                "rewrite_head_segment",
                "Rewrite range: 0x{0:X6}-0x{1:X6} | {2}",
                segment.StartAddress,
                segment.StartAddress + (uint)segment.Data.Length - 1,
                segment.Label));
        }

        int sectorSize = scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;
        List<(uint Address, byte[] Data, string Label)> sectorBatch = [];
        foreach (WaterNorWriteSegment segment in segments)
        {
            sectorBatch.AddRange(BuildNorSectorWriteBatch(segment.StartAddress, segment.Data, sectorSize, segment.Label));
        }

        RewriteNorSectorBatch(protocol, sectorBatch, scanResult.Geometry, programmerKind);
        CompleteMeasuredProgress(progressTitle);
    }

    private void PreserveCurrentGbaMenuCovers(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        WaterCartScanResult scanResult,
        byte[] targetHeadAreaImage)
    {
        if (!scanResult.IsUniversalGbaMenuDetected ||
            !scanResult.GbaMenuFirstScreenPreview.IsDetected ||
            !GbaMenuRomService.SupportsCovers(targetHeadAreaImage))
        {
            return;
        }

        int menuAreaLength = scanResult.MenuConfig.Status.ItemListAddress;
        if (menuAreaLength <= 0 || menuAreaLength > WaterCartScanner.BucketSizeBytes)
        {
            throw new InvalidOperationException(T(
                "error",
                "preserve_menu_covers_source_range_invalid",
                "The current NS menu area length is invalid. The menu update was canceled to avoid losing covers."));
        }

        byte[] currentMenuArea = ReadRomRangeFromDevice(protocol, programmerKind, 0, menuAreaLength);
        if (!GbaMenuFirstScreenPreviewCodec.TryCopyCoverPack(
                currentMenuArea,
                targetHeadAreaImage,
                out int coverCount,
                out int copiedBytes))
        {
            throw new InvalidOperationException(T(
                "error",
                "preserve_menu_covers_failed",
                "The current NS menu cover pack could not be preserved or does not fit in the updated menu. The update was canceled before erasing the menu."));
        }

        AppendLog(Tf(
            "log",
            "preserve_menu_covers_done",
            "Preserved the current NS menu cover pack: {0} cover(s), {1} bytes.",
            coverCount,
            copiedBytes));
    }

    private List<WaterNorWriteSegment> BuildWaterLinkInheritedMenuWriteSegments(
        byte[] headAreaImage,
        WaterMenuConfigInfo menuConfig)
    {
        int sectorSize = menuConfig.Status.SectorSize > 0
            ? menuConfig.Status.SectorSize
            : WaterWlMappingCodec.SectorSize;
        int itemListAddress = menuConfig.Status.ItemListAddress;
        int statusAddress = menuConfig.Status.StatusAddress;

        if (itemListAddress <= 0 ||
            itemListAddress % sectorSize != 0 ||
            itemListAddress + sectorSize > headAreaImage.Length ||
            statusAddress <= 0 ||
            statusAddress % sectorSize != 0 ||
            statusAddress + sectorSize > headAreaImage.Length ||
            WaterWlMappingCodec.Offset + sectorSize > headAreaImage.Length)
        {
            throw new InvalidOperationException(T(
                "error",
                "magic_metadata_range_invalid",
                "Magic metadata target range is invalid."));
        }

        return
        [
            new WaterNorWriteSegment
            {
                StartAddress = 0,
                Data = headAreaImage.AsSpan(0, itemListAddress).ToArray(),
                Label = T("value", "magic_menu_area_label", "Magic menu area"),
            },
            new WaterNorWriteSegment
            {
                StartAddress = (uint)itemListAddress,
                Data = headAreaImage.AsSpan(itemListAddress, sectorSize).ToArray(),
                Label = T("value", "itemlist_label", "itemlist"),
            },
            new WaterNorWriteSegment
            {
                StartAddress = (uint)statusAddress,
                Data = headAreaImage.AsSpan(statusAddress, sectorSize).ToArray(),
                Label = T("value", "status_label", "status"),
            },
            new WaterNorWriteSegment
            {
                StartAddress = (uint)WaterWlMappingCodec.Offset,
                Data = headAreaImage.AsSpan(WaterWlMappingCodec.Offset, sectorSize).ToArray(),
                Label = T("value", "wl_mapping_label", "WL mapping"),
            },
        ];
    }

    private void UpdateLegacyMenuOnDevice(string portName, ProgrammerKind programmerKind, WaterCartScanResult scanResult, LegacyMenuUpdatePlan updatePlan)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        byte[] headAreaImage = updatePlan.HeadAreaImage.ToArray();
        if (updatePlan.SupportsCovers)
        {
            PreserveCurrentGbaMenuCovers(protocol, programmerKind, scanResult, headAreaImage);
        }
        else if (!scanResult.IsUniversalGbaMenuDetected)
        {
            PreserveCurrentMenuBackground(protocol, programmerKind, scanResult, headAreaImage, targetUsesWlLayout: false);
        }

        string legacyHeadAreaLabel = T("value", "gba_menu_head_area_label", "GBA menu head 4MiB");
        BeginMeasuredProgress(
            T("ui", "update_menu", "更新菜单"),
            CountMenuHeadWriteWorkBytes(headAreaImage, scanResult.Geometry, updatePlan.MenuConfig.Status, false, legacyHeadAreaLabel),
            CountMenuHeadWriteUnits(headAreaImage, scanResult.Geometry, updatePlan.MenuConfig.Status, false, legacyHeadAreaLabel));
        AppendLog(Tf("log", "use_gba_menu_rom", "Use GBA menu ROM: {0}", updatePlan.MenuRomPath));
        RewriteMenuHeadArea(protocol, programmerKind, scanResult.Geometry, headAreaImage, updatePlan.MenuConfig.Status, false, legacyHeadAreaLabel);
        CompleteMeasuredProgress(T("ui", "update_menu", "更新菜单"));
    }

    private void UpdateMenuBackgroundOnDevice(
        string portName,
        ProgrammerKind programmerKind,
        WaterCartScanResult scanResult,
        byte[] bitmapData,
        byte[] paletteData)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        int menuAreaLength = scanResult.MenuConfig.Status.ItemListAddress;
        if (menuAreaLength <= 0 || menuAreaLength > WaterCartScanner.BucketSizeBytes)
        {
            throw new InvalidOperationException(T(
                "error",
                "update_menu_background_source_range_invalid",
                "当前菜单区长度无效，无法在“更新菜单”时保留现有背景图。"));
        }

        byte[] currentMenuArea = ReadRomRangeFromDevice(protocol, programmerKind, 0, menuAreaLength);
        if (!TryResolveMenuBackgroundRegion(currentMenuArea, UsesWlMode(scanResult), out int backgroundOffset))
        {
            throw new InvalidOperationException(T(
                "error",
                "update_menu_current_background_not_found",
                "无法在当前卡带上定位现有菜单背景图区域。为避免覆盖背景图，已取消本次“更新菜单”。"));
        }

        int paletteOffset = backgroundOffset + bitmapData.Length;
        if (paletteOffset + paletteData.Length > currentMenuArea.Length)
        {
            throw new InvalidOperationException(T(
                "error",
                "update_menu_bundled_background_not_found",
                "无法定位自带菜单 ROM 中的背景图区域。为避免覆盖当前背景图，已取消本次“更新菜单”。"));
        }

        Buffer.BlockCopy(bitmapData, 0, currentMenuArea, backgroundOffset, bitmapData.Length);
        Buffer.BlockCopy(paletteData, 0, currentMenuArea, paletteOffset, paletteData.Length);

        int sectorSize = scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;
        int rewriteStart = (backgroundOffset / sectorSize) * sectorSize;
        int rewriteEnd = AlignUpToBoundary(backgroundOffset + MenuBackgroundRegionLengthBytes, sectorSize);
        int rewriteLength = rewriteEnd - rewriteStart;
        byte[] rewriteRange = currentMenuArea.AsSpan(rewriteStart, rewriteLength).ToArray();

        AppendLog(Tf(
            "log",
            "update_menu_background_write_range",
            "Rewrite menu background range: 0x{0:X6}-0x{1:X6}",
            rewriteStart,
            rewriteStart + rewriteLength - 1));

        string menuBackgroundLabel = T("value", "menu_background_label", "menu background");
        BeginMeasuredProgress(
            T("ui", "update_menu_background", "Update Background"),
            CountRewriteRangeProgressWorkBytes(rewriteRange, scanResult.Geometry),
            CountRewriteRangeProgressUnits(rewriteRange, scanResult.Geometry));
        RewriteNorRange(protocol, (uint)rewriteStart, rewriteRange, scanResult.Geometry, menuBackgroundLabel, programmerKind);
        CompleteMeasuredProgress(T("ui", "update_menu_background", "Update Background"));
    }

    private async Task<bool> UpdateGbaMenuCoversAsync(
        WaterCartScanResult scanResult,
        IReadOnlyList<MenuItemConfigInfo> items,
        IReadOnlyList<Bitmap?> covers,
        IReadOnlyDictionary<int, string> gameCodesByBucket)
    {
        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return false;
        }

        if (scanResult.GbaMenuFirstScreenPreview.PackOffset <= 0 ||
            scanResult.GbaMenuFirstScreenPreview.PackSize <= 0)
        {
            AppendLog(T("error", "cover_pack_layout_invalid", "The current cover pack layout is invalid."));
            return false;
        }

        GbaMenuCoverPackBuildResult build = await Task.Run(() =>
            BuildStagedGbaCoverPack(items, covers, gameCodesByBucket));
        AssignPackedCoverIndexes(items, covers);

        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
        SetBusy(true, T("status", "update_menu_covers_busy", "Saving menu covers..."));
        try
        {
            await Task.Run(() => UpdateGbaMenuCoversOnDevice(
                portName,
                programmerKind,
                scanResult,
                items,
                build.PackData));

            ScanReadAttempt refreshed = await ReadScanResultAsync(portName);
            if (!TryApplyScanReadAttempt(refreshed, out string failureMessage))
            {
                AppendLog(Tf("log", "update_menu_covers_rescan_failed", "Covers saved, but rescan failed: {0}", failureMessage));
                SetBusy(false, T("status", "update_menu_covers_rescan_failed", "Covers saved, rescan failed"));
                return true;
            }

            AppendLog(T("log", "update_menu_covers_done", "Menu covers saved successfully."));
            SetBusy(false, T("status", "update_menu_covers_done", "Menu covers saved"));
            return true;
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf("log", "update_menu_covers_failed", "Saving menu covers failed: {0}", ex.Message));
            SetBusy(false, T("status", "update_menu_covers_failed", "Saving menu covers failed"));
            return false;
        }
    }

    private void UpdateGbaMenuCoversOnDevice(
        string portName,
        ProgrammerKind programmerKind,
        WaterCartScanResult scanResult,
        IReadOnlyList<MenuItemConfigInfo> items,
        byte[] packData)
    {
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        int menuAreaLength = scanResult.MenuConfig.Status.ItemListAddress;
        int packOffset = scanResult.GbaMenuFirstScreenPreview.PackOffset;
        int allocatedPackSize = scanResult.GbaMenuFirstScreenPreview.PackSize;
        const int trailerSize = 16;
        const int trailerOffset = 0x0AFFF0;
        if (menuAreaLength <= trailerOffset + trailerSize ||
            packOffset < 0 || allocatedPackSize != trailerOffset - packOffset ||
            allocatedPackSize <= 0)
        {
            throw new InvalidOperationException(T("error", "cover_pack_layout_invalid", "The current cover pack layout is invalid."));
        }

        byte[] currentMenuArea = ReadRomRangeFromDevice(protocol, programmerKind, 0, menuAreaLength);
        int newPackOffset = ResolveCoverPackOffset(
            currentMenuArea,
            packOffset,
            packData.Length,
            trailerOffset);
        int newAllocatedPackSize = trailerOffset - newPackOffset;
        int clearStart = Math.Min(packOffset, newPackOffset);
        int clearLength = trailerOffset - clearStart;
        Array.Fill(currentMenuArea, (byte)0, clearStart, clearLength);
        Buffer.BlockCopy(packData, 0, currentMenuArea, newPackOffset, packData.Length);
        "GCVT"u8.CopyTo(currentMenuArea.AsSpan(trailerOffset));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(currentMenuArea.AsSpan(trailerOffset + 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(currentMenuArea.AsSpan(trailerOffset + 6), trailerSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(currentMenuArea.AsSpan(trailerOffset + 8), (uint)newPackOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(currentMenuArea.AsSpan(trailerOffset + 12), (uint)newAllocatedPackSize);

        List<MenuItemConfigInfo> updatedItems = items
            .Select(WaterMenuConfigCodec.Clone)
            .ToList();
        if (currentMenuArea.Length >= WaterCartScanner.UniversalImageManifestOffset + WaterCartScanner.UniversalImageManifestSize &&
            WaterCartScanner.TrySynchronizeUniversalImageManifestEntryCount(
                currentMenuArea.AsSpan(
                    WaterCartScanner.UniversalImageManifestOffset,
                    WaterCartScanner.UniversalImageManifestSize),
                updatedItems.Count,
                out int previousManifestEntryCount) &&
            previousManifestEntryCount != updatedItems.Count)
        {
            AppendLog(Tf(
                "log",
                "universal_image_entry_count_synced",
                "Synchronized universal menu manifest entry count: {0} -> {1}.",
                previousManifestEntryCount,
                updatedItems.Count));
        }

        int sectorSize = scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;
        int rewriteStart = (clearStart / sectorSize) * sectorSize;
        int rewriteEnd = AlignUpToBoundary(trailerOffset + trailerSize, sectorSize);
        byte[] coverRange = currentMenuArea.AsSpan(rewriteStart, rewriteEnd - rewriteStart).ToArray();
        byte[] itemListSector = WaterMenuConfigCodec.BuildItemListSector(
            updatedItems,
            scanResult.MenuConfig.Status.ItemListAddress,
            scanResult.MenuConfig.Status.SectorSize);
        List<(uint Address, byte[] Data, string Label)> sectors = BuildNorSectorWriteBatch(
            (uint)rewriteStart,
            coverRange,
            sectorSize,
            T("value", "menu_cover_pack_label", "menu cover pack"));
        sectors.Add(((uint)scanResult.MenuConfig.Status.ItemListAddress, itemListSector, T("value", "itemlist_label", "itemlist")));

        BeginMeasuredProgress(
            T("ui", "save_cover_changes", "Save Covers"),
            sectors.Sum(sector => CountRewriteSectorProgressWorkBytes(sector.Data, scanResult.Geometry)),
            sectors.Sum(sector => CountRewriteSectorProgressUnits(sector.Data)));
        RewriteNorSectorBatch(protocol, sectors, scanResult.Geometry, programmerKind);
        CompleteMeasuredProgress(T("ui", "save_cover_changes", "Save Covers"));
    }

    private int ResolveCoverPackOffset(
        byte[] currentMenuArea,
        int currentPackOffset,
        int packLength,
        int trailerOffset)
    {
        int candidate = AlignDown(trailerOffset - packLength, 4);
        if (candidate < 0 || candidate + packLength > trailerOffset)
        {
            throw new InvalidOperationException(Tf(
                "error",
                "cover_pack_too_large",
                "The rebuilt cover pack is too large: {0} bytes, available {1} bytes.",
                packLength,
                Math.Max(0, trailerOffset - currentPackOffset)));
        }

        // A pack may grow into the erased gap before the old pack. Never overwrite
        // menu code or data if that gap is not actually erased on the device.
        if (candidate < currentPackOffset &&
            !IsErasedCoverGap(currentMenuArea.AsSpan(candidate, currentPackOffset - candidate)))
        {
            int available = Math.Max(0, trailerOffset - currentPackOffset);
            throw new InvalidOperationException(Tf(
                "error",
                "cover_pack_too_large",
                "The rebuilt cover pack is too large: {0} bytes, available {1} bytes.",
                packLength,
                available));
        }

        return candidate;
    }

    private static bool IsErasedCoverGap(ReadOnlySpan<byte> gap)
    {
        foreach (byte value in gap)
        {
            // The original prepared image uses 0xFF; previous cover updates
            // clear the old slot to 0x00. Both are safe filler values here.
            if (value != 0xFF && value != 0x00)
            {
                return false;
            }
        }

        return true;
    }

    private static int AlignDown(int value, int alignment) => value / alignment * alignment;

    private void PreserveCurrentMenuBackground(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        WaterCartScanResult scanResult,
        byte[] targetHeadAreaImage,
        bool targetUsesWlLayout)
    {
        int currentMenuAreaLength = scanResult.MenuConfig.Status.ItemListAddress;
        if (currentMenuAreaLength <= 0 || currentMenuAreaLength > WaterCartScanner.BucketSizeBytes)
        {
            throw new InvalidOperationException(T(
                "error",
                "update_menu_background_source_range_invalid",
                "当前菜单区长度无效，无法在“更新菜单”时保留现有背景图。"));
        }

        byte[] currentMenuArea = ReadRomRangeFromDevice(protocol, programmerKind, 0, currentMenuAreaLength);
        if (!TryResolveMenuBackgroundRegion(currentMenuArea, UsesWlMode(scanResult), out int sourceBackgroundOffset))
        {
            throw new InvalidOperationException(T(
                "error",
                "update_menu_current_background_not_found",
                "无法在当前卡带上定位现有菜单背景图区域。为避免覆盖背景图，已取消本次“更新菜单”。"));
        }

        if (!TryResolveMenuBackgroundRegion(targetHeadAreaImage, targetUsesWlLayout, out int targetBackgroundOffset))
        {
            throw new InvalidOperationException(T(
                "error",
                "update_menu_bundled_background_not_found",
                "无法定位自带菜单 ROM 中的背景图区域。为避免覆盖当前背景图，已取消本次“更新菜单”。"));
        }

        Buffer.BlockCopy(
            currentMenuArea,
            sourceBackgroundOffset,
            targetHeadAreaImage,
            targetBackgroundOffset,
            MenuBackgroundRegionLengthBytes);

        AppendLog(Tf(
            "log",
            "update_menu_preserve_background",
            "Preserve the current menu background: source 0x{0:X6}-0x{1:X6} -> target 0x{2:X6}-0x{3:X6}.",
            sourceBackgroundOffset,
            sourceBackgroundOffset + MenuBackgroundRegionLengthBytes - 1,
            targetBackgroundOffset,
            targetBackgroundOffset + MenuBackgroundRegionLengthBytes - 1));
    }

    private static int AlignUpToBoundary(int value, int alignment)
    {
        if (alignment <= 0)
        {
            return value;
        }

        int remainder = value % alignment;
        return remainder == 0
            ? value
            : value + (alignment - remainder);
    }

    private void InstallMagicMenuOnDevice(string portName, ProgrammerKind programmerKind, WaterCartScanResult scanResult, WaterMagicInstallPlan installPlan, string menuRomPath)
    {
        if (!WaterMagicUpgradeBuilder.CanInstallMagicMenuFromHeaders(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        string magicHeadAreaLabel = T("value", "magic_head_area_label", "Magic head 4MiB");
        BeginMeasuredProgress(
            T("ui", "repair_menu", "Repair Menu"),
            CountMenuHeadWriteWorkBytes(installPlan.HeadAreaImage, scanResult.Geometry, installPlan.InstalledMenuConfig.Status, true, magicHeadAreaLabel),
            CountMenuHeadWriteUnits(installPlan.HeadAreaImage, scanResult.Geometry, installPlan.InstalledMenuConfig.Status, true, magicHeadAreaLabel));
        AppendLog(Tf("log", "use_magic_menu_rom", "Use magic menu ROM: {0}", menuRomPath));
        RewriteMenuHeadArea(protocol, programmerKind, scanResult.Geometry, installPlan.HeadAreaImage, installPlan.InstalledMenuConfig.Status, true, magicHeadAreaLabel);
        CompleteMeasuredProgress(T("ui", "repair_menu", "Repair Menu"));
    }

    private void InstallLegacyMenuOnDevice(string portName, ProgrammerKind programmerKind, WaterCartScanResult scanResult, WaterLegacyMenuInstallPlan installPlan, string menuRomPath)
    {
        if (!WaterLegacyMenuRepairBuilder.CanInstallLegacyMenuFromHeaders(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        string legacyHeadAreaLabel = T("value", "legacy_head_area_label", "Legacy head 4MiB");
        BeginMeasuredProgress(
            T("ui", "repair_menu", "Repair Menu"),
            CountMenuHeadWriteWorkBytes(installPlan.HeadAreaImage, scanResult.Geometry, installPlan.InstalledMenuConfig.Status, false, legacyHeadAreaLabel),
            CountMenuHeadWriteUnits(installPlan.HeadAreaImage, scanResult.Geometry, installPlan.InstalledMenuConfig.Status, false, legacyHeadAreaLabel));
        AppendLog(Tf("log", "use_legacy_menu_rom", "Use legacy menu ROM: {0}", menuRomPath));
        RewriteMenuHeadArea(protocol, programmerKind, scanResult.Geometry, installPlan.HeadAreaImage, installPlan.InstalledMenuConfig.Status, false, legacyHeadAreaLabel);
        CompleteMeasuredProgress(T("ui", "repair_menu", "Repair Menu"));
    }

    private void RepairMagicMetadataOnDevice(string portName, ProgrammerKind programmerKind, WaterCartScanResult scanResult)
    {
        if (!WaterMagicUpgradeBuilder.CanRepairMagicMetadata(scanResult, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        WaterMagicRepairPlan repairPlan = WaterMagicUpgradeBuilder.BuildRepairPlan(scanResult);
        long totalBytes = CountRewriteSectorProgressWorkBytes(repairPlan.ItemListSector, scanResult.Geometry)
            + CountRewriteSectorProgressWorkBytes(repairPlan.StatusSector, scanResult.Geometry)
            + CountRewriteSectorProgressWorkBytes(repairPlan.MappingSector, scanResult.Geometry);
        int totalSectors = CountRewriteSectorProgressUnits(repairPlan.ItemListSector)
            + CountRewriteSectorProgressUnits(repairPlan.StatusSector)
            + CountRewriteSectorProgressUnits(repairPlan.MappingSector);

        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        string wlMappingLabel = T("value", "wl_mapping_label", "WL mapping");
        string itemListLabel = T("value", "itemlist_label", "itemlist");
        string statusLabel = T("value", "status_label", "status");

        BeginMeasuredProgress(T("ui", "repair_magic", "Repair Magic"), totalBytes, totalSectors);
        AppendLog(Tf(
            "log",
            "write_itemlist_sector",
            "Write itemlist sector: 0x{0:X6}-0x{1:X6}",
            repairPlan.RepairedMenuConfig.Status.ItemListAddress,
            repairPlan.RepairedMenuConfig.Status.ItemListAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1));
        AppendLog(Tf(
            "log",
            "write_wl_mapping_sector",
            "Write WL mapping sector: 0x{0:X6}-0x{1:X6}",
            WaterWlMappingCodec.Offset,
            WaterWlMappingCodec.Offset + WaterWlMappingCodec.SectorSize - 1));
        AppendLog(Tf(
            "log",
            "write_status_sector",
            "Write status sector: 0x{0:X6}-0x{1:X6}",
            repairPlan.RepairedMenuConfig.Status.StatusAddress,
            repairPlan.RepairedMenuConfig.Status.StatusAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1));
        RewriteNorSectorBatch(
            protocol,
            [
                ((uint)repairPlan.RepairedMenuConfig.Status.ItemListAddress, repairPlan.ItemListSector, itemListLabel),
                ((uint)repairPlan.RepairedMenuConfig.Status.StatusAddress, repairPlan.StatusSector, statusLabel),
                ((uint)WaterWlMappingCodec.Offset, repairPlan.MappingSector, wlMappingLabel),
            ],
            scanResult.Geometry,
            programmerKind);
        CompleteMeasuredProgress(T("ui", "repair_magic", "Repair Magic"));
    }

    private void RepairLegacyMenuOnDevice(string portName, ProgrammerKind programmerKind, WaterCartScanResult scanResult, WaterLegacyMenuRepairPlan repairPlan)
    {
        long totalBytes = CountRewriteSectorProgressWorkBytes(repairPlan.ItemListSector, scanResult.Geometry)
            + CountRewriteSectorProgressWorkBytes(repairPlan.StatusSector, scanResult.Geometry);
        int totalSectors = CountRewriteSectorProgressUnits(repairPlan.ItemListSector)
            + CountRewriteSectorProgressUnits(repairPlan.StatusSector);

        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        string itemListLabel = T("value", "itemlist_label", "itemlist");
        string statusLabel = T("value", "status_label", "status");

        BeginMeasuredProgress(T("ui", "repair_legacy_menu", "Repair Legacy Menu"), totalBytes, totalSectors);
        AppendLog(Tf(
            "log",
            "write_itemlist_sector",
            "Write itemlist sector: 0x{0:X6}-0x{1:X6}",
            repairPlan.RepairedMenuConfig.Status.ItemListAddress,
            repairPlan.RepairedMenuConfig.Status.ItemListAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1));
        AppendLog(Tf(
            "log",
            "write_status_sector",
            "Write status sector: 0x{0:X6}-0x{1:X6}",
            repairPlan.RepairedMenuConfig.Status.StatusAddress,
            repairPlan.RepairedMenuConfig.Status.StatusAddress + repairPlan.RepairedMenuConfig.Status.SectorSize - 1));
        RewriteNorSectorBatch(
            protocol,
            [
                ((uint)repairPlan.RepairedMenuConfig.Status.ItemListAddress, repairPlan.ItemListSector, itemListLabel),
                ((uint)repairPlan.RepairedMenuConfig.Status.StatusAddress, repairPlan.StatusSector, statusLabel),
            ],
            scanResult.Geometry,
            programmerKind);
        CompleteMeasuredProgress(T("ui", "repair_legacy_menu", "Repair Legacy Menu"));
    }

    private void EraseHeadAreaOnDevice(string portName, ProgrammerKind programmerKind, WaterCartScanResult scanResult)
    {
        int sectorSize = scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;
        int sectorCount = WaterCartScanner.BucketSizeBytes / sectorSize;

        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        if (scanResult.IsBeggarMaxGbcMode)
        {
            EraseBeggarMaxHeadAreaOnDevice(protocol, scanResult);
            return;
        }

        WaterNorWriteSegment segment = new()
        {
            StartAddress = 0,
            Data = Enumerable.Repeat((byte)0xFF, WaterCartScanner.BucketSizeBytes).ToArray(),
            Label = T("value", "head_area_label", "head 4MiB"),
            EraseOnly = true,
        };

        BeginMeasuredProgress(
            T("ui", "erase_config", "Erase Config"),
            (long)sectorSize * sectorCount,
            sectorCount);
        ProgramNorSegment(protocol, programmerKind, scanResult.Geometry, segment, 1, 1);
        CompleteMeasuredProgress(T("ui", "erase_config", "Erase Config"));
    }

    private void EraseEntireNorOnDevice(
        string portName,
        ProgrammerKind programmerKind,
        WaterCartScanResult scanResult,
        int targetMask)
    {
        const int LogicalTargetSizeBytes = 128 * 1024 * 1024;

        int deviceSizeBytes = scanResult.Geometry.DeviceSizeBytes;
        int targetCount = CountPpbTargets(targetMask);
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);

        BeginMeasuredProgress(
            T("ui", "erase_entire_nor", "Erase Entire NOR"),
            deviceSizeBytes,
            targetCount);

        try
        {
            for (int targetIndex = 0; targetIndex < 4; targetIndex++)
            {
                if ((targetMask & (1 << targetIndex)) == 0)
                {
                    continue;
                }

                int targetStartBytes = targetIndex * LogicalTargetSizeBytes;
                int targetLengthBytes = targetCount == 1
                    ? deviceSizeBytes
                    : Math.Min(LogicalTargetSizeBytes, deviceSizeBytes - targetStartBytes);
                if (targetLengthBytes <= 0)
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "erase_entire_nor_target_range_invalid",
                        "NOR target {0} is outside the detected capacity.",
                        targetIndex));
                }

                AppendLog(Tf(
                    "log",
                    "erase_entire_nor_target_start",
                    "Erase NOR target {0}: 0x{1:X8}-0x{2:X8}.",
                    targetIndex,
                    targetStartBytes,
                    targetStartBytes + targetLengthBytes - 1));

                if (!TryEraseWaterLinkChipTarget(protocol, targetIndex, 20 * 60 * 1000))
                {
                    throw new IOException(Tf(
                        "error",
                        "erase_entire_nor_target_failed",
                        "NOR target {0} chip erase failed or timed out: {1}",
                        targetIndex,
                        protocol.DescribeLastAckFailure()));
                }

                VerifyErasedNorTarget(protocol, programmerKind, targetIndex, targetStartBytes, targetLengthBytes);
                AdvanceMeasuredProgress(
                    targetLengthBytes,
                    1,
                    Tf("value", "erase_entire_nor_target_progress", "Verified target {0}/{1}", targetIndex + 1, targetCount));
                AppendLog(Tf(
                    "log",
                    "erase_entire_nor_target_done",
                    "NOR target {0} erase completed and passed bucket sampling verification.",
                    targetIndex));
            }

            CompleteMeasuredProgress(T("ui", "erase_entire_nor", "Erase Entire NOR"));
        }
        finally
        {
            for (int targetIndex = 0; targetIndex < 4; targetIndex++)
            {
                if ((targetMask & (1 << targetIndex)) != 0)
                {
                    uint targetWordBase = (uint)targetIndex * (LogicalTargetSizeBytes / 2u);
                    TryWriteWaterLinkCommandWord(protocol, targetWordBase, 0xF0, 3000);
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // USB 直插擦除的功耗调优开关
    //
    // 背景：USB 直插路径由上位机逐字下发 NOR 命令，并在整个擦除窗口内每
    // ErasePollDelayMs 毫秒回读一次 0xFFFF。每次回读都要占用一整条
    // USB -> MCU -> CPLD(90MHz) -> NOR 数据总线，是擦除电流主要来源。
    // 盖中盖路径（0xF1 单条命令 + STM32 本地时序）没有这部分开销。
    //
    // 想复现原始行为做对照实验，把 ErasePollDelayMs 改回 20 即可。
    // ---------------------------------------------------------------------
    private const int ErasePollDelayMs = 200;
    private const int ErasePollFirstProbeDelayMs = 30;
    private const int ErasePollReadTimeoutMs = 3000;

    /// <summary>
    /// 0xAD 擦除序列命令的能力探测超时。
    /// 不支持该命令的旧固件会静默丢弃、不回 ACK，所以这里必须用一个
    /// 明显短于擦除超时的值，否则每次擦除都要先干等一轮才回退。
    /// </summary>
    private const int EraseSequenceProbeTimeoutMs = 2000;

    /// <summary>
    /// 功耗分离实验开关。置 true 时「全片擦除」会跳过擦除前的 PPB 解锁，
    /// 用来把 PPB 解锁阶段的开销从总电流里剥出来单独测量。
    ///
    /// ⚠️ 只在已经解锁过、不需要重新解锁的卡上使用；否则擦除会失败。
    /// 测量结束后请改回 false。
    /// </summary>
    private const bool SkipPpbUnlockForPowerMeasurement = false;

    private bool TryEraseWaterLinkChipTarget(
        WaterDeviceProtocol protocol,
        int targetIndex,
        int timeoutMs)
    {
        const uint LogicalTargetWords = 128u * 1024u * 1024u / 2u;

        uint targetWordBase = (uint)targetIndex * LogicalTargetWords;
        uint targetByteBase = targetWordBase << 1;
        int effectiveTimeoutMs = Math.Max(timeoutMs, 5000);

        try
        {
            // 单次 CS 周期内把整条 chip erase 命令序列打完（固件侧 0xAD）。
            // 旧实现是 7 条命令各自一次 USB 往返 + 各自一次独立 CS 周期。
            //
            // 注意：不支持该命令的旧固件会在 default 分支静默丢弃，
            // 不返回任何 ACK —— 所以这里用较短的探测超时，避免卡满整个擦除超时。
            if (!TryWriteWaterLinkEraseSequence(
                    protocol,
                    targetWordBase,
                    0x10,
                    EraseSequenceProbeTimeoutMs))
            {
                // 固件不支持 0xAD 时退回逐字下发，保持对旧固件的兼容。
                AppendLog(T("log", "erase_seq_fallback",
                    "Firmware erase-sequence command is unavailable; falling back to word-by-word command writes."));
                if (!SendWaterLinkChipEraseCommandsWordByWord(protocol, targetWordBase, effectiveTimeoutMs))
                {
                    return false;
                }
            }

            Thread.Sleep(100);
            return WaitForWaterLinkSectorErase(protocol, targetByteBase, effectiveTimeoutMs);
        }
        finally
        {
            TryWriteWaterLinkCommandWord(protocol, targetWordBase, 0xF0, 3000);
        }
    }

    /// <summary>
    /// 请求固件在单次片选周期内完成一整条 chip/sector erase 命令序列。
    /// payload = [0x555 AA][0x2AA 55][0x555 80][0x555 AA][0x2AA 55][0x555 final]
    /// final 为 0x10 表示 chip erase，0x30 表示 sector erase。
    /// </summary>
    private static bool TryWriteWaterLinkEraseSequence(
        WaterDeviceProtocol protocol,
        uint targetWordBase,
        ushort finalCommand,
        int timeoutMs)
    {
        byte[] payload = new byte[24];
        WriteCommandPair(payload, 0, 0x555, 0xAA);
        WriteCommandPair(payload, 4, 0x2AA, 0x55);
        WriteCommandPair(payload, 8, 0x555, 0x80);
        WriteCommandPair(payload, 12, 0x555, 0xAA);
        WriteCommandPair(payload, 16, 0x2AA, 0x55);
        WriteCommandPair(payload, 20, 0x555, finalCommand);
        return protocol.TryWriteEraseSequence(targetWordBase, payload, timeoutMs);
    }

    private static void WriteCommandPair(byte[] payload, int offset, ushort wordOffset, ushort value)
    {
        payload[offset] = (byte)(wordOffset & 0xFF);
        payload[offset + 1] = (byte)(wordOffset >> 8);
        payload[offset + 2] = (byte)(value & 0xFF);
        payload[offset + 3] = (byte)(value >> 8);
    }

    /// <summary>
    /// 旧固件兼容路径：逐条下发命令字。保持既有行为，只是抽成独立方法。
    /// </summary>
    private static bool SendWaterLinkChipEraseCommandsWordByWord(
        WaterDeviceProtocol protocol,
        uint targetWordBase,
        int timeoutMs)
    {
        return TryWriteWaterLinkCommandWord(protocol, targetWordBase, 0xF0, timeoutMs) &&
               TryWriteWaterLinkCommandWord(protocol, targetWordBase + 0x555, 0xAA, timeoutMs) &&
               TryWriteWaterLinkCommandWord(protocol, targetWordBase + 0x2AA, 0x55, timeoutMs) &&
               TryWriteWaterLinkCommandWord(protocol, targetWordBase + 0x555, 0x80, timeoutMs) &&
               TryWriteWaterLinkCommandWord(protocol, targetWordBase + 0x555, 0xAA, timeoutMs) &&
               TryWriteWaterLinkCommandWord(protocol, targetWordBase + 0x2AA, 0x55, timeoutMs) &&
               TryWriteWaterLinkCommandWord(protocol, targetWordBase + 0x555, 0x10, timeoutMs);
    }

    private void VerifyErasedNorTarget(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        int targetIndex,
        int targetStartBytes,
        int targetLengthBytes)
    {
        const int SampleLength = 32;
        int targetEndExclusive = checked(targetStartBytes + targetLengthBytes);
        int firstBucket = targetStartBytes / WaterCartScanner.BucketSizeBytes;
        int lastBucket = (targetEndExclusive - 1) / WaterCartScanner.BucketSizeBytes;
        byte[] sample = new byte[SampleLength];

        for (int bucket = firstBucket; bucket <= lastBucket; bucket++)
        {
            int bucketStart = bucket * WaterCartScanner.BucketSizeBytes;
            int rangeStart = Math.Max(targetStartBytes, bucketStart);
            int rangeEndExclusive = Math.Min(targetEndExclusive, bucketStart + WaterCartScanner.BucketSizeBytes);
            int lastSampleStart = Math.Max(rangeStart, rangeEndExclusive - SampleLength);
            int middleSampleStart = Math.Clamp(
                rangeStart + ((rangeEndExclusive - rangeStart - SampleLength) / 2),
                rangeStart,
                lastSampleStart);
            int[] sampleAddresses = [rangeStart, middleSampleStart, lastSampleStart];

            foreach (int sampleAddress in sampleAddresses.Distinct())
            {
                Array.Clear(sample);
                if (!TryReadRomRangeForProgrammer(protocol, programmerKind, (uint)sampleAddress, sample))
                {
                    throw new IOException(Tf(
                        "error",
                        "erase_entire_nor_verify_read_failed",
                        "Failed to verify NOR target {0} at 0x{1:X8}.",
                        targetIndex,
                        sampleAddress));
                }

                int nonErasedOffset = Array.FindIndex(sample, value => value != 0xFF);
                if (nonErasedOffset >= 0)
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "erase_entire_nor_verify_failed",
                        "NOR target {0} verification found non-erased data at 0x{1:X8}.",
                        targetIndex,
                        sampleAddress + nonErasedOffset));
                }
            }
        }
    }

    private void UnlockGbaPpbOnDevice(
        string portName,
        ProgrammerKind programmerKind,
        int chipMask,
        int sectorSizeBytes,
        int sectorsPerTarget)
    {
        const uint NorChipWordCount = 128u * 1024u * 1024u / 2u;

        using var protocol = new WaterDeviceProtocol(portName);
        OpenProgrammerProtocol(protocol, programmerKind);

        try
        {
            if (programmerKind == ProgrammerKind.BeggarLegacy &&
                !TrySwitchLegacyBeggarBucketLocal(protocol, 0, 3000))
            {
                throw new IOException(Tf(
                    "error",
                    "unlock_ppb_gba_bank_switch_failed",
                    "Failed to switch the GBA cartridge to bank 0 before PPB unlock: {0}",
                    protocol.DescribeLastAckFailure()));
            }

            for (int chipIndex = 0; chipIndex < 4; chipIndex++)
            {
                if ((chipMask & (1 << chipIndex)) == 0)
                {
                    continue;
                }

                uint chipWordBase = programmerKind == ProgrammerKind.BeggarLegacy
                    ? 0u
                    : (uint)chipIndex * NorChipWordCount;
                uint chipByteBase = chipWordBase << 1;
                ushort lockStatus = ReadGbaPpbAsoWord(
                    protocol,
                    chipIndex,
                    chipWordBase,
                    chipByteBase,
                    0x50,
                    "lock status");
                ushort ppbBefore = ReadGbaPpbAsoWord(
                    protocol,
                    chipIndex,
                    chipWordBase,
                    chipByteBase,
                    0xC0,
                    "sector 0 status");

                AppendLog(Tf(
                    "log",
                    "unlock_ppb_chip_status",
                    "NOR target {0}: base=0x{1:X8}, PPB Lock=0x{2:X4}, sector 0 PPB before=0x{3:X4}.",
                    chipIndex,
                    chipByteBase,
                    lockStatus,
                    ppbBefore));

                if (lockStatus != 0x0001)
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "unlock_ppb_lock_frozen",
                        "NOR target {0} PPB Lock status is 0x{1:X4}, so its PPB array cannot be changed. Fully power-cycle the cartridge and retry.",
                        chipIndex,
                        lockStatus));
                }

                try
                {
                    ResetGbaPpbMode(protocol, chipIndex, chipWordBase, throwOnFailure: true);
                    WriteGbaPpbCommandOrThrow(protocol, chipIndex, chipWordBase + 0x555, 0xAA, "enter PPB ASO");
                    WriteGbaPpbCommandOrThrow(protocol, chipIndex, chipWordBase + 0x2AA, 0x55, "enter PPB ASO");
                    WriteGbaPpbCommandOrThrow(protocol, chipIndex, chipWordBase + 0x555, 0xC0, "enter PPB ASO");
                    WriteGbaPpbCommandOrThrow(protocol, chipIndex, chipWordBase, 0x80, "All PPB Erase setup");
                    WriteGbaPpbCommandOrThrow(protocol, chipIndex, chipWordBase, 0x30, "All PPB Erase confirm");
                    WaitForGbaPpbEraseCompletion(protocol, chipIndex, chipByteBase);
                    VerifyGbaPpbTarget(
                        protocol,
                        chipIndex,
                        chipByteBase,
                        sectorSizeBytes,
                        sectorsPerTarget);
                }
                finally
                {
                    ResetGbaPpbMode(protocol, chipIndex, chipWordBase, throwOnFailure: false);
                }

                AppendLog(Tf(
                    "log",
                    "unlock_ppb_chip_done",
                    "NOR target {0}: All PPB Erase completed; verified {1} sector PPBs as unprotected.",
                    chipIndex,
                    sectorsPerTarget));
            }
        }
        finally
        {
            if (programmerKind == ProgrammerKind.BeggarLegacy)
            {
                ResetGbaPpbMode(protocol, 0, 0, throwOnFailure: false);
                TrySwitchLegacyBeggarBucketLocal(protocol, 0, 3000);
            }
        }
    }

    private static int ResolveGbaPpbChipMask(WaterCartScanResult scanResult, ProgrammerKind programmerKind)
    {
        if (programmerKind == ProgrammerKind.BeggarLegacy)
        {
            return 0x01;
        }

        if (!scanResult.Geometry.IsWaterLinkMultiNor)
        {
            return scanResult.Geometry.IsS70Gl02GsDualDie ? 0x03 : 0x01;
        }

        int chipMask = scanResult.RuntimeStatus.NorChipValidMask & 0x0F;
        if (chipMask != 0)
        {
            return chipMask;
        }

        const int NorChipSizeBytes = 128 * 1024 * 1024;
        int chipCount = scanResult.RuntimeStatus.NorChipCount;
        if (chipCount <= 0)
        {
            chipCount = scanResult.Geometry.DeviceSizeBytes / NorChipSizeBytes;
        }

        chipCount = Math.Clamp(chipCount, 1, 4);
        return (1 << chipCount) - 1;
    }

    private static int ResolveGbaPpbSectorSizeBytes(WaterCartScanResult scanResult) =>
        scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;

    private static int ResolveGbaPpbSectorsPerTarget(
        WaterCartScanResult scanResult,
        ProgrammerKind programmerKind,
        int targetCount,
        int sectorSizeBytes)
    {
        const int WaterLinkTargetBytes = 128 * 1024 * 1024;

        int targetBytes = scanResult.Geometry.IsWaterLinkMultiNor ||
                          scanResult.Geometry.IsS70Gl02GsDualDie
            ? WaterLinkTargetBytes
            : scanResult.Geometry.DeviceSizeBytes;

        if (programmerKind == ProgrammerKind.BeggarLegacy && targetCount > 1)
        {
            targetBytes = Math.Max(1, scanResult.Geometry.DeviceSizeBytes / targetCount);
        }

        targetBytes = Math.Max(sectorSizeBytes, targetBytes);
        return Math.Max(1, (targetBytes + sectorSizeBytes - 1) / sectorSizeBytes);
    }

    private string BuildPpbDetectedTargetDescription(WaterCartScanResult scanResult, int commandTargetCount)
    {
        string chipName = WaterCartScanner.GetChipName(
                scanResult.NorId,
                scanResult.Geometry.DeviceSizeBytes,
                scanResult.Geometry.BufferWriteBytes)
            ?? scanResult.ChipName;
        if (string.IsNullOrWhiteSpace(chipName))
        {
            chipName = T("value", "chip_unknown", "Unknown / Needs confirmation");
        }

        string capacity = string.IsNullOrWhiteSpace(scanResult.CapacitySummary)
            ? T("ui", "not_read", "Not Read")
            : scanResult.CapacitySummary;

        if (scanResult.IsBeggarMaxGbcMode)
        {
            return Tf(
                "dialog",
                "unlock_ppb_detected_gbc",
                "{0}, {1}, one flash chip",
                chipName,
                capacity);
        }

        if (scanResult.Geometry.IsS70Gl02GsDualDie &&
            !scanResult.Geometry.IsWaterLinkMultiNor)
        {
            return Tf(
                "dialog",
                "unlock_ppb_detected_s70gl02",
                "{0}, {1}, one physical NOR chip with two dies",
                chipName,
                capacity);
        }

        if (scanResult.Geometry.IsWaterLinkMultiNor)
        {
            int logicalTargetsPerPhysicalChip = scanResult.Geometry.IsS70Gl02GsDualDie ? 2 : 1;
            int physicalChipCount = Math.Max(1, (commandTargetCount + logicalTargetsPerPhysicalChip - 1) / logicalTargetsPerPhysicalChip);
            return Tf(
                "dialog",
                "unlock_ppb_detected_multi_nor",
                "WaterLink multi-NOR, {0} physical NOR chip(s), {1}",
                physicalChipCount,
                capacity);
        }

        return Tf(
            "dialog",
            "unlock_ppb_detected_single_nor",
            "{0}, {1}, one physical NOR chip",
            chipName,
            capacity);
    }

    private static int CountPpbTargets(int chipMask) =>
        Enumerable.Range(0, 4).Count(index => (chipMask & (1 << index)) != 0);

    private ushort ReadGbaPpbAsoWord(
        WaterDeviceProtocol protocol,
        int chipIndex,
        uint chipWordBase,
        uint byteAddress,
        ushort asoCommand,
        string phase)
    {
        try
        {
            ResetGbaPpbMode(protocol, chipIndex, chipWordBase, throwOnFailure: true);
            WriteGbaPpbCommandOrThrow(protocol, chipIndex, chipWordBase + 0x555, 0xAA, phase);
            WriteGbaPpbCommandOrThrow(protocol, chipIndex, chipWordBase + 0x2AA, 0x55, phase);
            WriteGbaPpbCommandOrThrow(protocol, chipIndex, chipWordBase + 0x555, asoCommand, phase);

            byte[] word = new byte[2];
            if (!protocol.TryReadRom(byteAddress, word, 3000))
            {
                throw new IOException(Tf(
                    "error",
                    "unlock_ppb_read_failed",
                    "NOR chip {0} PPB {1} read did not return a valid payload within 3000 ms.",
                    chipIndex,
                    phase));
            }

            return BitConverter.ToUInt16(word, 0);
        }
        finally
        {
            ResetGbaPpbMode(protocol, chipIndex, chipWordBase, throwOnFailure: false);
        }
    }

    /// <summary>
    /// 等待 All PPB Erase 完成。
    ///
    /// 功耗要点：旧实现在 <paramref name="eraseTimeoutMs"/> 整个窗口内用
    /// <c>Thread.Sleep(10)</c> 密集轮询，而每次判定要读两次 DQ6 toggle，
    /// 每次读又是 6 次 USB 写 + 1 次 USB 读（含两次 ASO 模式切换）。
    /// PPB 阵列擦除本身只需要几毫秒到几十毫秒，因此这里改为
    /// 「先给一段静默窗口再开始探测」，并把探测间隔拉到
    /// <see cref="ErasePollDelayMs"/>，总线占用可降一个数量级。
    /// </summary>
    private void WaitForGbaPpbEraseCompletion(
        WaterDeviceProtocol protocol,
        int chipIndex,
        uint chipByteBase)
    {
        const int eraseTimeoutMs = 60000;
        const ushort Dq5Timeout = 0x0020;
        const ushort Dq6Toggle = 0x0040;
        Stopwatch stopwatch = Stopwatch.StartNew();

        // All PPB Erase 的典型完成时间远小于首次探测间隔，先静默一段时间，
        // 避免在芯片刚进入擦除状态时就开始反复切换 ASO 模式。
        Thread.Sleep(ErasePollDelayMs);

        while (stopwatch.ElapsedMilliseconds < eraseTimeoutMs)
        {
            ushort first = ReadGbaPpbRawWord(protocol, chipIndex, chipByteBase, "erase status");
            ushort second = ReadGbaPpbRawWord(protocol, chipIndex, chipByteBase, "erase status");
            if (((first ^ second) & Dq6Toggle) == 0)
            {
                return;
            }

            if ((second & Dq5Timeout) != 0)
            {
                ushort third = ReadGbaPpbRawWord(protocol, chipIndex, chipByteBase, "erase timeout check");
                if (((second ^ third) & Dq6Toggle) != 0)
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "unlock_ppb_erase_failed",
                        "NOR target {0} reported a timeout while erasing its PPB array.",
                        chipIndex));
                }

                return;
            }

            Thread.Sleep(ErasePollDelayMs);
        }

        throw new TimeoutException(Tf(
            "error",
            "unlock_ppb_erase_timeout",
            "NOR target {0} did not finish erasing its PPB array within {1} seconds.",
            chipIndex,
            eraseTimeoutMs / 1000));
    }

    private void VerifyGbaPpbTarget(
        WaterDeviceProtocol protocol,
        int chipIndex,
        uint chipByteBase,
        int sectorSizeBytes,
        int sectorCount)
    {
        for (int sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
        {
            uint byteAddress = checked(chipByteBase + ((uint)sectorIndex * (uint)sectorSizeBytes));
            ushort ppb = ReadGbaPpbRawWord(protocol, chipIndex, byteAddress, "sector verification");
            if ((ppb & 0x0001) == 0)
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "unlock_ppb_verify_failed",
                    "NOR target {0} PPB verification failed at sector {1} (0x{2:X8}): returned 0x{3:X4}, expected bit 0 to be 1.",
                    chipIndex,
                    sectorIndex,
                    byteAddress,
                    ppb));
            }
        }
    }

    private ushort ReadGbaPpbRawWord(
        WaterDeviceProtocol protocol,
        int chipIndex,
        uint byteAddress,
        string phase)
    {
        byte[] word = new byte[2];
        if (!protocol.TryReadRom(byteAddress, word, 3000))
        {
            throw new IOException(Tf(
                "error",
                "unlock_ppb_read_failed",
                "NOR chip {0} PPB {1} read at 0x{2:X8} did not return a valid payload within 3000 ms.",
                chipIndex,
                phase,
                byteAddress));
        }

        return BitConverter.ToUInt16(word, 0);
    }

    private void ResetGbaPpbMode(
        WaterDeviceProtocol protocol,
        int chipIndex,
        uint chipWordBase,
        bool throwOnFailure)
    {
        string? failureDetail = null;
        if (!TryWriteWaterLinkCommandWord(protocol, chipWordBase, 0x90, 3000))
        {
            failureDetail = protocol.DescribeLastAckFailure();
        }
        if (!TryWriteWaterLinkCommandWord(protocol, chipWordBase, 0x00, 3000))
        {
            failureDetail ??= protocol.DescribeLastAckFailure();
        }
        if (!TryWriteWaterLinkCommandWord(protocol, chipWordBase, 0xF0, 3000))
        {
            failureDetail ??= protocol.DescribeLastAckFailure();
        }

        if (failureDetail is not null && throwOnFailure)
        {
            throw new IOException(Tf(
                "error",
                "unlock_ppb_command_failed",
                "NOR chip {0} PPB {1} command failed: {2}",
                chipIndex,
                "reset/ASO exit",
                failureDetail));
        }
    }

    private void WriteGbaPpbCommandOrThrow(
        WaterDeviceProtocol protocol,
        int chipIndex,
        uint wordAddress,
        ushort value,
        string phase)
    {
        if (!TryWriteWaterLinkCommandWord(protocol, wordAddress, value, 5000))
        {
            throw new IOException(Tf(
                "error",
                "unlock_ppb_command_failed",
                "NOR chip {0} PPB {1} command failed: {2}",
                chipIndex,
                phase,
                protocol.DescribeLastAckFailure()));
        }
    }

    private void UnlockGbcPpbOnDevice(string portName, ProgrammerKind programmerKind)
    {
        using var protocol = new WaterDeviceProtocol(portName);
        OpenProgrammerProtocol(protocol, programmerKind);

        try
        {
            if (!TrySwitchBeggarMaxRomBank(protocol, 0))
            {
                throw new IOException(Tf(
                    "error",
                    "unlock_ppb_gbc_bank_switch_failed",
                    "Failed to switch the GBC cartridge to MBC5 ROM bank 0 before PPB unlock: {0}",
                    protocol.DescribeLastAckFailure()));
            }

            byte lockStatus = ReadGbcPpbAsoByte(protocol, 0x50, 0x0000, "lock status");
            byte ppbBefore = ReadGbcPpbAsoByte(protocol, 0xC0, 0x4000, "sector 0 status");
            AppendLog(Tf(
                "log",
                "unlock_ppb_gbc_status",
                "GBC/MBC5 PPB: Lock=0x{0:X2}, sector 0 before=0x{1:X2}.",
                lockStatus,
                ppbBefore));

            if (lockStatus != 0x01)
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "unlock_ppb_gbc_lock_frozen",
                    "GBC PPB Lock status is 0x{0:X2}, so its PPB array cannot be changed. Fully power-cycle the cartridge and retry.",
                    lockStatus));
            }

            try
            {
                ResetGbcPpbMode(protocol, throwOnFailure: true);
                WriteGbcPpbCommandOrThrow(protocol, 0x0AAA, 0xAA, "enter PPB ASO");
                WriteGbcPpbCommandOrThrow(protocol, 0x0555, 0x55, "enter PPB ASO");
                WriteGbcPpbCommandOrThrow(protocol, 0x0AAA, 0xC0, "enter PPB ASO");
                WriteGbcPpbCommandOrThrow(protocol, 0x0000, 0x80, "All PPB Erase setup");
                WriteGbcPpbCommandOrThrow(protocol, 0x0000, 0x30, "All PPB Erase confirm");
                Thread.Sleep(2000);
            }
            finally
            {
                ResetGbcPpbMode(protocol, throwOnFailure: false);
            }

            if (!TrySwitchBeggarMaxRomBank(protocol, 0))
            {
                throw new IOException(Tf(
                    "error",
                    "unlock_ppb_gbc_bank_switch_failed",
                    "Failed to switch the GBC cartridge to MBC5 ROM bank 0 before PPB verification: {0}",
                    protocol.DescribeLastAckFailure()));
            }

            byte ppbAfter = ReadGbcPpbAsoByte(protocol, 0xC0, 0x4000, "sector 0 verification");
            if (ppbAfter != 0x01)
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "unlock_ppb_gbc_verify_failed",
                    "GBC PPB verification failed: sector 0 returned 0x{0:X2}, expected 0x01.",
                    ppbAfter));
            }

            AppendLog(Tf(
                "log",
                "unlock_ppb_gbc_done",
                "GBC/MBC5 All PPB Erase completed; sector 0 PPB=0x{0:X2} (unprotected).",
                ppbAfter));
        }
        finally
        {
            ResetGbcPpbMode(protocol, throwOnFailure: false);
            TrySwitchBeggarMaxRomBank(protocol, 0);
        }
    }

    private byte ReadGbcPpbAsoByte(
        WaterDeviceProtocol protocol,
        byte asoCommand,
        uint readAddress,
        string phase)
    {
        try
        {
            ResetGbcPpbMode(protocol, throwOnFailure: true);
            WriteGbcPpbCommandOrThrow(protocol, 0x0AAA, 0xAA, phase);
            WriteGbcPpbCommandOrThrow(protocol, 0x0555, 0x55, phase);
            WriteGbcPpbCommandOrThrow(protocol, 0x0AAA, asoCommand, phase);

            byte[] value = new byte[1];
            if (!protocol.TryReadGbc(readAddress, value, 3000))
            {
                throw new IOException(Tf(
                    "error",
                    "unlock_ppb_gbc_read_failed",
                    "GBC PPB {0} read at 0x{1:X4} did not return a valid payload within 3000 ms.",
                    phase,
                    readAddress));
            }

            return value[0];
        }
        finally
        {
            ResetGbcPpbMode(protocol, throwOnFailure: false);
        }
    }

    private void ResetGbcPpbMode(WaterDeviceProtocol protocol, bool throwOnFailure)
    {
        string? failureDetail = null;
        if (!protocol.TryWriteGbc(0x0000, [0x90], 3000))
        {
            failureDetail = protocol.DescribeLastAckFailure();
        }
        if (!protocol.TryWriteGbc(0x0000, [0x00], 3000))
        {
            failureDetail ??= protocol.DescribeLastAckFailure();
        }
        if (!protocol.TryWriteGbc(0x0000, [0xF0], 3000))
        {
            failureDetail ??= protocol.DescribeLastAckFailure();
        }

        if (failureDetail is not null && throwOnFailure)
        {
            throw new IOException(Tf(
                "error",
                "unlock_ppb_gbc_command_failed",
                "GBC PPB {0} command failed: {1}",
                "reset/ASO exit",
                failureDetail));
        }
    }

    private void WriteGbcPpbCommandOrThrow(
        WaterDeviceProtocol protocol,
        uint byteAddress,
        byte value,
        string phase)
    {
        if (!protocol.TryWriteGbc(byteAddress, [value], 5000))
        {
            throw new IOException(Tf(
                "error",
                "unlock_ppb_gbc_command_failed",
                "GBC PPB {0} command failed: {1}",
                phase,
                protocol.DescribeLastAckFailure()));
        }
    }

    private void EraseBeggarMaxHeadAreaOnDevice(WaterDeviceProtocol protocol, WaterCartScanResult scanResult)
    {
        int sectorSize = scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : 0x20000;
        int sectorCount = BeggarMaxFixedMapReservedBytes / sectorSize;
        byte[] blankSector = Enumerable.Repeat((byte)0xFF, sectorSize).ToArray();

        BeginMeasuredProgress(
            T("ui", "erase_config", "Erase Config"),
            (long)sectorSize * sectorCount,
            sectorCount);

        try
        {
            for (int sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
            {
                int sectorAddress = sectorIndex * sectorSize;
                RewriteBeggarMaxSector(
                    protocol,
                    sectorAddress,
                    blankSector,
                    scanResult.Geometry,
                    T("value", "gbc_head_area_label", "MBCX head 1MiB"));
            }

            CompleteMeasuredProgress(T("ui", "erase_config", "Erase Config"));
        }
        catch
        {
            AbortMeasuredProgress();
            throw;
        }
    }

    private static List<(uint Address, byte[] Data, string Label)> BuildNorSectorWriteBatch(
        uint startAddress,
        byte[] rangeData,
        int sectorSize,
        string label)
    {
        List<(uint Address, byte[] Data, string Label)> sectors = [];
        int sectorCount = rangeData.Length / sectorSize;
        for (int offset = 0; offset < rangeData.Length; offset += sectorSize)
        {
            int sectorIndex = offset / sectorSize;
            string sectorLabel = sectorCount == 1
                ? label
                : $"{label} S{sectorIndex:00}";
            sectors.Add((
                startAddress + (uint)offset,
                rangeData.AsSpan(offset, sectorSize).ToArray(),
                sectorLabel));
        }

        return sectors;
    }

    private void AddUniversalManifestSyncSector(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        NorGeometryInfo geometry,
        int entryCount,
        List<(uint Address, byte[] Data, string Label)> sectors)
    {
        if (entryCount <= 0)
        {
            return;
        }

        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;
        uint manifestSectorAddress = (uint)(
            WaterCartScanner.UniversalImageManifestOffset / sectorSize * sectorSize);
        int manifestOffsetInSector = WaterCartScanner.UniversalImageManifestOffset - (int)manifestSectorAddress;
        if (manifestOffsetInSector < 0 ||
            manifestOffsetInSector + WaterCartScanner.UniversalImageManifestSize > sectorSize)
        {
            return;
        }

        int existingIndex = sectors.FindIndex(sector => sector.Address == manifestSectorAddress);
        byte[] manifestSector = existingIndex >= 0
            ? sectors[existingIndex].Data.ToArray()
            : ReadRomRangeFromDevice(protocol, programmerKind, manifestSectorAddress, sectorSize);
        if (!WaterCartScanner.TrySynchronizeUniversalImageManifestEntryCount(
                manifestSector.AsSpan(
                    manifestOffsetInSector,
                    WaterCartScanner.UniversalImageManifestSize),
                entryCount,
                out int previousEntryCount) ||
            previousEntryCount == entryCount)
        {
            return;
        }

        string label = T("value", "universal_manifest_label", "universal menu manifest");
        if (existingIndex >= 0)
        {
            sectors[existingIndex] = (manifestSectorAddress, manifestSector, sectors[existingIndex].Label);
        }
        else
        {
            sectors.Add((manifestSectorAddress, manifestSector, label));
        }

        AppendLog(Tf(
            "log",
            "universal_image_entry_count_synced",
            "Synchronized universal menu manifest entry count: {0} -> {1}.",
            previousEntryCount,
            entryCount));
    }

    private void RewriteNorSectorBatch(
        WaterDeviceProtocol protocol,
        IReadOnlyList<(uint Address, byte[] Data, string Label)> sectors,
        NorGeometryInfo geometry,
        ProgrammerKind programmerKind,
        bool emitLogs = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sectors.Count == 0)
        {
            return;
        }

        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;

        foreach (var sector in sectors)
        {
            byte[] data = sector.Data;
            string label = sector.Label;
            if (data.Length != sectorSize)
            {
                throw new InvalidOperationException(Tf("error", "sector_data_size_invalid", "{0} data size is invalid.", label));
            }

            if (geometry.SectorSizeBytes > 0 && geometry.SectorSizeBytes != data.Length)
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "nor_sector_size_mismatch",
                    "NOR sector size does not match the target data size: {0} / {1}",
                    geometry.SectorSizeBytes,
                    data.Length));
            }
        }

        ushort bufferWriteBytes = GetEffectiveBufferWriteBytes(geometry);

        if (emitLogs)
        {
            LogWaterSysInfoProbe(protocol, programmerKind, "before erase");
        }

        foreach (var sector in sectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint sectorAddress = sector.Address;
            string label = sector.Label;
            if (emitLogs)
            {
                AppendLog(Tf("log", "erase_sector", "Erase {0}: 0x{1:X6}", label, sectorAddress));
            }

            if (!TryEraseRomSectorForProgrammer(protocol, programmerKind, sectorAddress))
            {
                throw new InvalidOperationException(
                    Tf("error", "erase_sector_failed", "Failed to erase {0}.", label) +
                    BuildProgrammerFailureDetail(protocol, sectorAddress, sectorSize, 0, sectorSize));
            }

            AdvanceMeasuredProgress(
                sectorSize,
                1,
                BuildProgressDetailText(label, "progress_phase_erase", "Erase"));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (emitLogs)
        {
            LogWaterSysInfoProbe(protocol, programmerKind, "after erase / before download");
        }

        foreach (var sector in sectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint sectorAddress = sector.Address;
            byte[] sectorData = sector.Data;
            string label = sector.Label;
            if (IsAllBytes(sectorData, 0xFF))
            {
                VerifyMenuSector(protocol, programmerKind, sectorAddress, sectorData, cancellationToken);
                if (emitLogs)
                {
                    AppendLog(Tf("log", "skip_blank_sector", "Skip programming blank {0}: 0x{1:X6}", label, sectorAddress));
                }
                continue;
            }

            if (emitLogs)
            {
                AppendLog(Tf("log", "write_sector", "Write {0}: 0x{1:X6}", label, sectorAddress));
            }

            for (int offset = 0; offset < sectorData.Length; offset += NorProgramChunkSizeBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int writeLength = Math.Min(NorProgramChunkSizeBytes, sectorData.Length - offset);
                byte[] chunk = sectorData.AsSpan(offset, writeLength).ToArray();
                uint chunkAddress = sectorAddress + (uint)offset;

                if (emitLogs)
                {
                    AppendLog(Tf(
                        "log",
                        "write_chunk",
                        "  Write chunk: 0x{0:X6}+0x{1:X4} ({2} bytes, buffer {3} bytes)",
                        sectorAddress,
                        offset,
                        writeLength,
                        bufferWriteBytes));
                }

                if (!TryProgramRomForProgrammer(protocol, programmerKind, chunkAddress, chunk, bufferWriteBytes, 60000))
                {
                    throw new InvalidOperationException(
                        Tf(
                            "error",
                            "segment_program_failed",
                            "Failed to program {0} at offset 0x{1:X}.",
                            label,
                            offset) +
                        BuildProgrammerFailureDetail(
                            protocol,
                            chunkAddress,
                            sectorSize,
                            offset,
                            writeLength));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDrainRomProgramForProgrammer(protocol, programmerKind, sectorAddress, 60000))
            {
                throw new InvalidOperationException(
                    Tf(
                        "error",
                        "segment_program_finish_failed",
                        "Failed to finish programming {0}.",
                        label) +
                    BuildProgrammerFailureDetail(protocol, sectorAddress, sectorSize));
            }

            VerifyMenuSector(protocol, programmerKind, sectorAddress, sectorData, cancellationToken);
            AdvanceMeasuredProgress(
                sectorData.Length,
                1,
                BuildProgressDetailText(label, "progress_phase_write", "Write"));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (emitLogs)
        {
            LogWaterSysInfoProbe(protocol, programmerKind, "after download");
        }
    }

    private void LogWaterSysInfoProbe(WaterDeviceProtocol protocol, ProgrammerKind programmerKind, string phase)
    {
        if (programmerKind != ProgrammerKind.WaterLink)
        {
            return;
        }

        if (!protocol.TryReadSysInfo(out WaterSysInfo info))
        {
            AppendLog(Tf(
                "log",
                "waterlink_sys_info_read_failed",
                "WaterLink OSC probe failed ({0}).",
                phase));
            return;
        }

        AppendLog(Tf(
            "log",
            "waterlink_sys_info_probe",
            "WaterLink OSC {0}: SYS={1} Hz, PCLK={2} Hz, PLL={3} Hz, HSI={4} Hz, HSE={5} Hz, CLK=0x{6:X8}, PBUS=0x{7:X8}, SRC={8}, SCLK={9}/{10}",
            phase,
            info.SysClockHz,
            info.PeripheralClockHz,
            info.PllClockHz,
            info.HsiClockHz,
            info.HseClockHz,
            info.ClockControl,
            info.PeripheralBusDivider,
            info.ClockSource,
            info.SclkDividerHigh,
            info.SclkDividerLow));
    }

    private void RewriteNorSector(
        WaterDeviceProtocol protocol,
        uint sectorAddress,
        byte[] sectorData,
        NorGeometryInfo geometry,
        string label,
        ProgrammerKind programmerKind,
        bool emitLogs = true)
    {
        RewriteNorSectorBatch(
            protocol,
            [(sectorAddress, sectorData, label)],
            geometry,
            programmerKind,
            emitLogs);
    }

    private string BuildProgrammerFailureDetail(
        WaterDeviceProtocol protocol,
        uint byteAddress,
        int sectorSize,
        int? dataOffset = null,
        int? chunkLength = null)
    {
        uint sectorAddress = sectorSize > 0
            ? byteAddress - (byteAddress % (uint)sectorSize)
            : byteAddress;
        long sectorIndex = sectorSize > 0
            ? sectorAddress / (uint)sectorSize
            : 0L;

        string detail = $"{protocol.DescribeLastAckFailure()}; address=0x{byteAddress:X6}; sector={sectorIndex} @ 0x{sectorAddress:X6}";
        if (dataOffset.HasValue)
        {
            detail += $"; data offset=0x{dataOffset.Value:X}";
        }

        if (chunkLength.HasValue)
        {
            detail += $"; chunk=0x{chunkLength.Value:X}";
        }

        return $" [{detail}]";
    }

    private string BuildProgrammerRangeFailureDetail(
        WaterDeviceProtocol protocol,
        uint startByteAddress,
        uint totalBytes,
        int sectorSize)
    {
        uint endByteAddress = totalBytes == 0u
            ? startByteAddress
            : startByteAddress + totalBytes - 1u;
        string detail = $"{protocol.DescribeLastAckFailure()}; range=0x{startByteAddress:X6}-0x{endByteAddress:X6}";

        if (sectorSize > 0)
        {
            uint firstSectorAddress = startByteAddress - (startByteAddress % (uint)sectorSize);
            uint lastSectorAddress = endByteAddress - (endByteAddress % (uint)sectorSize);
            long firstSectorIndex = firstSectorAddress / (uint)sectorSize;
            long lastSectorIndex = lastSectorAddress / (uint)sectorSize;
            detail += $"; sectors={firstSectorIndex}-{lastSectorIndex} @ 0x{firstSectorAddress:X6}-0x{lastSectorAddress:X6}";
        }

        return $" [{detail}]";
    }

    private bool TryEraseRomSectorForProgrammer(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        uint byteAddress,
        int timeoutMs = 60000)
    {
        if (programmerKind != ProgrammerKind.BeggarLegacy)
        {
            return TryEraseWaterLinkSectorPassthrough(protocol, byteAddress, timeoutMs);
        }

        int bucket = (int)(byteAddress / WaterCartScanner.BucketSizeBytes);
        uint localAddress = byteAddress % (uint)WaterCartScanner.BucketSizeBytes;

        if (!TrySwitchLegacyBeggarBucketLocal(protocol, bucket, timeoutMs) ||
            !TryEraseLegacyBeggarSector(protocol, localAddress, timeoutMs))
        {
            return false;
        }

        return TrySwitchLegacyBeggarBucketLocal(protocol, 0, 3000);
    }

    /// <summary>
    /// WaterLink 透传扇区擦除：通过 0xF5 raw write 拼 NOR 解锁序列 + 0x30，
    /// 然后用 0xF6 raw read 轮询直到 0xFFFF。
    /// 绕过 0xF3 命令，等同上位机自己控制擦除时序。
    ///
    /// NOTE: 多 die NOR 时，解锁地址必须加上 chip base，否则解锁序列发给了
    /// die 0 而擦除命令发给了 die N，导致第二 die 永远擦不动。
    /// WaterLink 固件硬编码每颗 NOR chip = 128 MB（word count = 0x4000000）。
    /// </summary>
    private static bool TryEraseWaterLinkSectorPassthrough(
        WaterDeviceProtocol protocol,
        uint byteAddress,
        int timeoutMs)
    {
        if ((byteAddress & 1u) != 0u)
        {
            return false;
        }

        const uint NorChipWords = 128u * 1024u * 1024u / 2u; // 0x0400_0000

        int effectiveTimeoutMs = timeoutMs < 0 ? Timeout.Infinite : Math.Max(timeoutMs, 5000);
        uint sectorWordAddress = byteAddress >> 1;
        uint chipBase = sectorWordAddress & ~(NorChipWords - 1u);

        // Reset to read-array mode, then issue chip-relative AMD erase commands.
        if (!TryWriteWaterLinkCommandWord(protocol, chipBase + 0x000, 0xF0, effectiveTimeoutMs) ||
            !TryWriteWaterLinkCommandWord(protocol, chipBase + 0x555, 0xAA, effectiveTimeoutMs) ||
            !TryWriteWaterLinkCommandWord(protocol, chipBase + 0x2AA, 0x55, effectiveTimeoutMs) ||
            !TryWriteWaterLinkCommandWord(protocol, chipBase + 0x555, 0x80, effectiveTimeoutMs) ||
            !TryWriteWaterLinkCommandWord(protocol, chipBase + 0x555, 0xAA, effectiveTimeoutMs) ||
            !TryWriteWaterLinkCommandWord(protocol, chipBase + 0x2AA, 0x55, effectiveTimeoutMs) ||
            !TryWriteWaterLinkCommandWord(protocol, sectorWordAddress, 0x30, effectiveTimeoutMs) ||
            !WaitForWaterLinkSectorErase(protocol, byteAddress, effectiveTimeoutMs))
        {
            return false;
        }

        return TryWriteWaterLinkCommandWord(protocol, chipBase + 0x000, 0xF0, 3000);
    }

    private static bool TryWriteWaterLinkCommandWord(
        WaterDeviceProtocol protocol,
        uint wordAddress,
        ushort value,
        int timeoutMs)
    {
        return protocol.TryWriteRom(wordAddress, BitConverter.GetBytes(value), timeoutMs);
    }

    /// <summary>
    /// 等待 chip/sector erase 结束。
    ///
    /// 功耗要点：每次回读 DQ7 都要占用一整条 USB -> MCU -> CPLD -> NOR 总线，
    /// 所以这里刻意采用「首次短探 + 之后长间隔」的策略：命令刚下发的头几十毫秒
    /// 是擦除刚开始的窗口，此时才值得密集探测；一旦确认还在擦除中，就退到
    /// <see cref="ErasePollDelayMs"/> 的长间隔，把总线活动量压到最低。
    /// 对照实验：把 ErasePollDelayMs 改成 20 即可回到旧的高频轮询行为。
    /// </summary>
    private static bool WaitForWaterLinkSectorErase(
        WaterDeviceProtocol protocol,
        uint byteAddress,
        int timeoutMs)
    {
        byte[] word = new byte[2];
        Stopwatch stopwatch = Stopwatch.StartNew();
        int pollDelayMs = ErasePollFirstProbeDelayMs;
        bool firstProbe = true;

        while (true)
        {
            if (!protocol.TryReadRom(byteAddress, word, ErasePollReadTimeoutMs))
            {
                return false;
            }

            if (BitConverter.ToUInt16(word, 0) == 0xFFFF)
            {
                return true;
            }

            if (timeoutMs >= 0 && stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                throw new TimeoutException($"NOR erase timeout at byte address 0x{byteAddress:X8}.");
            }

            Thread.Sleep(pollDelayMs);
            if (firstProbe)
            {
                // 第一次确认「仍在擦除」之后，转入低占空比轮询。
                firstProbe = false;
                pollDelayMs = ErasePollDelayMs;
            }
        }
    }

    private bool TryProgramRomForProgrammer(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        uint byteAddress,
        byte[] bytes,
        ushort bufferWriteBytes,
        int timeoutMs = 60000)
    {
        if (programmerKind != ProgrammerKind.BeggarLegacy)
        {
            return protocol.TryProgramRom(byteAddress, bytes, bufferWriteBytes, timeoutMs);
        }

        int bucket = (int)(byteAddress / WaterCartScanner.BucketSizeBytes);
        uint localAddress = byteAddress % (uint)WaterCartScanner.BucketSizeBytes;

        if (!TrySwitchLegacyBeggarBucketLocal(protocol, bucket, timeoutMs) ||
            !protocol.TryProgramRom(localAddress, bytes, bufferWriteBytes, timeoutMs))
        {
            return false;
        }

        return TrySwitchLegacyBeggarBucketLocal(protocol, 0, 3000);
    }

    private bool TryDrainRomProgramForProgrammer(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        uint byteAddress,
        int timeoutMs = 60000)
    {
        byte[] drainProbe = new byte[2];
        if (programmerKind != ProgrammerKind.BeggarLegacy)
        {
            return protocol.TryReadRom(byteAddress, drainProbe, timeoutMs);
        }

        int bucket = (int)(byteAddress / WaterCartScanner.BucketSizeBytes);
        uint localAddress = byteAddress % (uint)WaterCartScanner.BucketSizeBytes;

        if (!TrySwitchLegacyBeggarBucketLocal(protocol, bucket, timeoutMs) ||
            !protocol.TryReadRom(localAddress, drainProbe, timeoutMs))
        {
            return false;
        }

        return TrySwitchLegacyBeggarBucketLocal(protocol, 0, 3000);
    }

    private static bool TryEraseLegacyBeggarSector(
        WaterDeviceProtocol protocol,
        uint localByteAddress,
        int timeoutMs)
    {
        if ((localByteAddress & 1u) != 0u)
        {
            return false;
        }

        // The firmware-side F3 handler uses a fixed 2 s watchdog. On slower
        // S70GL02GS sector erases that can expire before the chip reaches read
        // array mode, so we issue the legacy erase sequence directly and poll.
        int effectiveTimeoutMs = timeoutMs < 0 ? Timeout.Infinite : Math.Max(timeoutMs, 5000);
        uint sectorWordAddress = localByteAddress >> 1;

        if (!TryWriteLegacyBeggarCommandWord(protocol, 0x000, 0xF0, effectiveTimeoutMs) ||
            !TryWriteLegacyBeggarCommandWord(protocol, 0x555, 0xAA, effectiveTimeoutMs) ||
            !TryWriteLegacyBeggarCommandWord(protocol, 0x2AA, 0x55, effectiveTimeoutMs) ||
            !TryWriteLegacyBeggarCommandWord(protocol, 0x555, 0x80, effectiveTimeoutMs) ||
            !TryWriteLegacyBeggarCommandWord(protocol, 0x555, 0xAA, effectiveTimeoutMs) ||
            !TryWriteLegacyBeggarCommandWord(protocol, 0x2AA, 0x55, effectiveTimeoutMs) ||
            !TryWriteLegacyBeggarCommandWord(protocol, sectorWordAddress, 0x30, effectiveTimeoutMs) ||
            !WaitForLegacyBeggarSectorErase(protocol, localByteAddress, effectiveTimeoutMs))
        {
            return false;
        }

        return TryWriteLegacyBeggarCommandWord(protocol, 0x000, 0xF0, 3000);
    }

    private static bool TryWriteLegacyBeggarCommandWord(
        WaterDeviceProtocol protocol,
        uint wordAddress,
        ushort value,
        int timeoutMs)
    {
        return protocol.TryWriteRom(wordAddress, BitConverter.GetBytes(value), timeoutMs);
    }

    private static bool WaitForLegacyBeggarSectorErase(
        WaterDeviceProtocol protocol,
        uint localByteAddress,
        int timeoutMs)
    {
        byte[] word = new byte[2];
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (true)
        {
            if (!protocol.TryReadRom(localByteAddress, word, 3000))
            {
                return false;
            }

            if (BitConverter.ToUInt16(word, 0) == 0xFFFF)
            {
                return true;
            }

            if (timeoutMs >= 0 && stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                throw new TimeoutException($"NOR erase timeout at local byte address 0x{localByteAddress:X8}.");
            }

            Thread.Sleep(20);
        }
    }

    private void VerifyMenuSector(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        uint byteAddress,
        byte[] expected,
        CancellationToken cancellationToken)
    {
        if (byteAddress >= WaterCartScanner.BucketSizeBytes) return;

        for (int offset = 0; offset < expected.Length; offset += NorProgramChunkSizeBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] actual = new byte[Math.Min(NorProgramChunkSizeBytes, expected.Length - offset)];
            uint address = byteAddress + (uint)offset;
            if (!TryReadRomRangeForProgrammer(protocol, programmerKind, address, actual))
            {
                throw new InvalidOperationException(Tf("error", "menu_verify_read_failed",
                    "Unable to read menu verification data at 0x{0:X8}.", address));
            }

            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[offset + i])
                {
                    throw new InvalidOperationException(Tf("error", "menu_verify_mismatch",
                        "Menu verification failed at 0x{0:X8}: expected 0x{1:X2}, read 0x{2:X2}.",
                        address + (uint)i, expected[offset + i], actual[i]));
                }
            }
        }
    }

    private static bool IsAllBytes(byte[] buffer, byte value)
    {
        foreach (byte item in buffer)
        {
            if (item != value)
            {
                return false;
            }
        }

        return true;
    }

    private static string TrimLogTitle(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        if (maxLength <= 3)
        {
            return text[..maxLength];
        }

        return text[..(maxLength - 3)] + "...";
    }

    private static string FormatSlotListForLog(IEnumerable<int> slots)
    {
        List<int> slotList = slots
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();

        return slotList.Count == 0
            ? LocalizedText.T("value", "none", "None")
            : string.Join(", ", slotList.Select(slot => (slot + 1).ToString("000", CultureInfo.InvariantCulture)));
    }

    private static int CountNorSectors(long totalBytes, NorGeometryInfo geometry)
    {
        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;

        if (totalBytes <= 0 || sectorSize <= 0)
        {
            return 1;
        }

        return (int)Math.Max(1, (totalBytes + sectorSize - 1) / sectorSize);
    }

    private void SetBusy(bool busy, string statusText)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<bool, string>(SetBusy), busy, statusText);
            return;
        }

        isBusy = busy;
        comboBoxPorts.Enabled = !busy;
        comboBoxLanguage.Enabled = !busy;
        comboBoxBurnProfileMode.Enabled = !busy && burnProfileModeOptions.Count > 0;
        buttonRefreshPorts.Enabled = !busy;
        dataGridViewRoms.Enabled = !busy && !isAnalyzingQueueImports;
        UpdateActionButtons(busy);
        RefreshGbcFontSelectorVisibility();
        labelStatus.Text = statusText;
        UseWaitCursor = busy;
    }

    private void AppendLog(string message)
    {
        AppendLogCore(message, replaceKey: null);
    }

    private void AppendProgressLog(string progressKey, string message)
    {
        if (InvokeRequired)
        {
            QueuePendingProgressLog(progressKey, message);
            return;
        }

        AppendLogCore(message, progressKey);
    }

    private void AppendLogCore(string message, string? replaceKey)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string, string?>(AppendLogCore), message, replaceKey);
            return;
        }

        if (string.IsNullOrWhiteSpace(replaceKey))
        {
            ClearReplaceableLogState();
            AppendLogLine(CreateLogTimestamp(), message);
            return;
        }

        if (!TryReplaceLastProgressLog(replaceKey, message))
        {
            AppendLogLine(CreateLogTimestamp(), message, replaceKey);
        }
    }

    private string CreateLogTimestamp() =>
        DateTime.Now.ToString("[HH:mm:ss.fff] ", CultureInfo.InvariantCulture);

    private void AppendLogLine(string timestamp, string message, string? replaceKey = null)
    {
        string line = timestamp + message + Environment.NewLine;
        int start = textBoxLog.TextLength;
        textBoxLog.AppendText(line);
        textBoxLog.SelectionStart = textBoxLog.TextLength;
        textBoxLog.SelectionLength = 0;
        textBoxLog.ScrollToCaret();

        if (string.IsNullOrWhiteSpace(replaceKey))
        {
            ClearReplaceableLogState();
            return;
        }

        replaceableLogKey = replaceKey;
        replaceableLogStart = start;
        replaceableLogLength = line.Length;
        replaceableLogTimestamp = timestamp;
    }

    private bool TryReplaceLastProgressLog(string replaceKey, string message)
    {
        if (!string.Equals(replaceableLogKey, replaceKey, StringComparison.Ordinal) ||
            replaceableLogStart < 0 ||
            replaceableLogLength <= 0)
        {
            return false;
        }

        string replacement = replaceableLogTimestamp + message + Environment.NewLine;
        if (replaceableLogStart + replaceableLogLength > textBoxLog.TextLength)
        {
            ClearReplaceableLogState();
            return false;
        }

        bool wasReadOnly = textBoxLog.ReadOnly;
        textBoxLog.ReadOnly = false;
        textBoxLog.Select(replaceableLogStart, replaceableLogLength);
        textBoxLog.SelectedText = replacement;
        textBoxLog.ReadOnly = wasReadOnly;

        replaceableLogLength = replacement.Length;
        textBoxLog.SelectionStart = textBoxLog.TextLength;
        textBoxLog.SelectionLength = 0;
        textBoxLog.ScrollToCaret();
        return true;
    }

    private void ClearReplaceableLogState()
    {
        replaceableLogKey = null;
        replaceableLogStart = -1;
        replaceableLogLength = 0;
        replaceableLogTimestamp = string.Empty;
    }

    private void QueuePendingProgressLog(string progressKey, string message)
    {
        bool scheduleFlush;
        lock (pendingProgressLogSync)
        {
            pendingProgressLogs[progressKey] = message;
            scheduleFlush = Interlocked.Exchange(ref pendingProgressLogFlushScheduled, 1) == 0;
        }

        if (scheduleFlush)
        {
            BeginInvoke(new Action(FlushPendingProgressLogs));
        }
    }

    private void FlushPendingProgressLogs()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(FlushPendingProgressLogs));
            return;
        }

        List<KeyValuePair<string, string>> logsToFlush;
        lock (pendingProgressLogSync)
        {
            logsToFlush = pendingProgressLogs.ToList();
            pendingProgressLogs.Clear();
        }

        foreach (KeyValuePair<string, string> entry in logsToFlush)
        {
            AppendLogCore(entry.Value, entry.Key);
        }

        Interlocked.Exchange(ref pendingProgressLogFlushScheduled, 0);

        bool hasPendingLogs;
        lock (pendingProgressLogSync)
        {
            hasPendingLogs = pendingProgressLogs.Count > 0;
        }

        if (hasPendingLogs &&
            Interlocked.Exchange(ref pendingProgressLogFlushScheduled, 1) == 0)
        {
            BeginInvoke(new Action(FlushPendingProgressLogs));
        }
    }
}
