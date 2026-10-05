using System.Globalization;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using ChisFlashWaterClient.Services;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private const int NorProgramChunkSizeBytes = 4096;
    private const string WorkspaceRowDragFormat = "ChisFlashWaterClient.WorkspaceRowIndex";
    private static readonly byte[] CapacityPlanningPlaceholderRom = [0x00];

    private sealed class PlannedWorkspaceImport
    {
        public PendingRomImportItem PendingItem { get; init; } = null!;
        public WaterRomImportPlan Plan { get; init; } = null!;
        public IReadOnlyList<WaterNorWriteSegment> WriteSegments { get; set; } = [];
    }

    private sealed class PendingRomPreparation
    {
        public PendingRomImportItem PendingItem { get; init; } = null!;
        public Task<byte[]>? PatchedRomTask { get; init; }
        public byte[]? PreparedRomBytes { get; set; }
    }

    private sealed class WorkspaceMetadataPlan
    {
        public WaterMenuConfigInfo MenuConfig { get; init; } = new();
        public byte[] ItemListSector { get; set; } = [];
        public List<(uint Address, byte[] Data, string Label)> CoverSectors { get; set; } = [];
        public byte[]? CoverMenuArea { get; set; }
        public byte[]? StatusSector { get; init; }
        public WaterWlMappingInfo? WlMapping { get; init; }
        public byte[]? MappingSector { get; init; }
    }

    private sealed class WorkspaceHeadAreaRewritePlan
    {
        public bool UsesWlMode { get; init; }
        public byte[] HeadAreaImage { get; init; } = [];
        public string HeadAreaLabel { get; init; } = string.Empty;
        public string MenuRomPath { get; init; } = string.Empty;
        public string MenuHeaderTitle { get; init; } = string.Empty;
    }

    private sealed class PendingRomAnalysisResult
    {
        public int Index { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public List<PendingRomImportItem> PendingItems { get; init; } = [];
        public List<string> Messages { get; init; } = [];
        public Exception? Error { get; init; }
    }

    private void ConfigureDragDropTargets()
    {
        ConfigureExternalFileDrop(this);
        ConfigureExternalFileDrop(groupBoxList);
    }

    private void ConfigureExternalFileDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter -= ExternalFileDrop_DragEnter;
        control.DragDrop -= ExternalFileDrop_DragDrop;
        control.DragEnter += ExternalFileDrop_DragEnter;
        control.DragDrop += ExternalFileDrop_DragDrop;
    }

    private void ExternalFileDrop_DragEnter(object? sender, DragEventArgs e)
    {
        if (isBusy)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        string[]? files = e.Data?.GetData(DataFormats.FileDrop) as string[];
        bool hasSupportedFiles = files is not null && files.Any(IsSupportedRomFile);
        if (!hasSupportedFiles)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Copy;
    }

    private async void ExternalFileDrop_DragDrop(object? sender, DragEventArgs e)
    {
        string[]? droppedFiles = e.Data?.GetData(DataFormats.FileDrop) as string[];
        if (droppedFiles is null || droppedFiles.Length == 0)
        {
            return;
        }

        string[] romFiles = droppedFiles
            .Where(IsSupportedRomFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (romFiles.Length == 0)
        {
            AppendLog(T("log", "rom_drop_no_valid_files", "No supported ROM files were found in the drag-and-drop payload."));
            return;
        }

        await RunUiActionSafelyAsync(() => AddPendingRomFilesAsync(romFiles));
    }

    private async Task SelectRomFilesForQueueAsync()
    {
        if (IsGbcRawImageMode()) { await SelectGbcImageFileAsync(); return; }
        if (IsMbc5EightMiBMode()) { await SelectMbc5FilesAsync(); return; }
        if (isBusy)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Multiselect = true,
            CheckFileExists = true,
            Title = T("dialog", "queue_add_rom_title", "Add ROMs To Queue"),
            Filter = GetRomOpenFileDialogFilter(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.FileNames.Length == 0)
        {
            return;
        }

        await AddPendingRomFilesAsync(dialog.FileNames);
    }

    private async Task AddPendingRomFilesAsync(IEnumerable<string> filePaths)
    {
        if (IsGbcRawImageMode()) { await AddGbcImageFilesAsync(filePaths); return; }
        if (IsMbc5EightMiBMode()) { await AddMbc5FilesAsync(filePaths); return; }
        if (isBusy)
        {
            return;
        }

        if (IsFcWorkspaceActive)
        {
            await fcWorkspace!.ImportAsync(filePaths);
            return;
        }

        if (IsWorkspaceModificationLocked())
        {
            await ImportMdWithoutCardAsync(filePaths);
            return;
        }

        string[] romFiles = RomArchiveImportService.ExpandPaths(filePaths)
            .Where(IsSupportedRomFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (romFiles.Length == 0)
        {
            AppendLog(T("log", "rom_drop_no_valid_files", "No supported ROM files were found in the drag-and-drop payload."));
            return;
        }

        HashSet<string> existingFiles = pendingImportItems
            .Select(item => string.IsNullOrWhiteSpace(item.SourceImportKey) ? item.FilePath : item.SourceImportKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> filesToAnalyze = [];
        List<string> skippedFiles = [];

        foreach (string filePath in romFiles)
        {
            if (!existingFiles.Add(filePath))
            {
                skippedFiles.Add(Path.GetFileName(filePath));
                continue;
            }

            filesToAnalyze.Add(filePath);
        }

        if (filesToAnalyze.Count == 0)
        {
            if (skippedFiles.Count > 0)
            {
                AppendLog(Tf(
                    "log",
                    "queue_add_duplicate_skipped",
                    "The following files are already in the burn queue and were skipped: {0}",
                    string.Join(", ", skippedFiles)));
            }

            return;
        }

        AppendLog(Tf(
            "log",
            "queue_add_start",
            "Start analyzing {0} ROM file(s) in the background.",
            filesToAnalyze.Count));

        int selectionIndex = workspaceRows.Count;
        List<PendingRomImportItem> placeholderItems = filesToAnalyze
            .Select(CreatePendingRomPlaceholderItem)
            .ToList();
        foreach (PendingRomImportItem item in placeholderItems)
        {
            pendingImportItems.Add(item);
            workspaceRows.Add(new WorkspaceListRow
            {
                PendingItem = item,
                IsLoadingPlaceholder = true,
                GbcDedicatedSaveAllocationOverride = item.GbcDedicatedSaveAllocationOverride,
            });
        }

        RefreshWorkspaceGrid(selectionIndex);
        SetQueueAnalysisState(true, filesToAnalyze.Count);

        PendingRomAnalysisResult[] analysisResults;
        try
        {
            bool isGbcWorkspaceMode = IsGbcWorkspaceMode();
            analysisResults = await AnalyzePendingRomFilesAsync(filesToAnalyze.ToArray(), isGbcWorkspaceMode);

            List<PendingRomImportItem> newItems = [];
            foreach (PendingRomAnalysisResult result in analysisResults.OrderBy(result => result.Index))
            {
                PendingRomImportItem placeholderItem = placeholderItems[result.Index];
                int pendingIndex = pendingImportItems.FindIndex(item => ReferenceEquals(item, placeholderItem));
                int workspaceIndex = workspaceRows.FindIndex(row => ReferenceEquals(row.PendingItem, placeholderItem));
                if (pendingIndex < 0 && workspaceIndex < 0)
                {
                    continue;
                }

                if (result.PendingItems.Count > 0)
                {
                    foreach (string message in result.Messages)
                    {
                        AppendLog(message);
                    }

                    newItems.AddRange(result.PendingItems);

                    if (pendingIndex >= 0)
                    {
                        pendingImportItems.RemoveAt(pendingIndex);
                    }

                    if (workspaceIndex >= 0)
                    {
                        workspaceRows.RemoveAt(workspaceIndex);
                    }

                    if (pendingIndex >= 0)
                    {
                        pendingImportItems.InsertRange(pendingIndex, result.PendingItems);
                    }

                    if (workspaceIndex >= 0)
                    {
                        workspaceRows.InsertRange(
                            workspaceIndex,
                            result.PendingItems.Select(item => new WorkspaceListRow
                            {
                                PendingItem = item,
                                IsLoadingPlaceholder = false,
                                GbcDedicatedSaveAllocationOverride = item.GbcDedicatedSaveAllocationOverride,
                            }));
                    }

                    continue;
                }

                if (pendingIndex >= 0)
                {
                    pendingImportItems.RemoveAt(pendingIndex);
                }

                if (workspaceIndex >= 0)
                {
                    workspaceRows.RemoveAt(workspaceIndex);
                }

                AppendLog(Tf(
                    "log",
                    "queue_add_file_failed",
                    "Failed to analyze {0}: {1}",
                    Path.GetFileName(result.FilePath),
                    result.Error?.Message ?? T("value", "unknown", "Unknown")));
            }

            MatchPendingGbaCovers(newItems);
            RefreshWorkspaceGrid(GetSafeSelectionIndex(selectionIndex, workspaceRows.Count));

            if (newItems.Count == 0)
            {
                if (skippedFiles.Count > 0)
                {
                    AppendLog(Tf(
                        "log",
                        "queue_add_duplicate_skipped",
                        "The following files are already in the burn queue and were skipped: {0}",
                    string.Join(", ", skippedFiles)));
                }

                return;
            }

            AppendLog(Tf(
                "log",
                "queue_add_done",
                "Added {0} ROM file(s) to the burn queue. You can drag rows to reorder them before burning.",
                newItems.Count));

            foreach (PendingRomImportItem item in newItems)
            {
                if(item.MdRom is not null)continue;
                AppendLog(Tf(
                    "log",
                    "queue_add_analyzed",
                    "Analyzed {0} | save={1} | patch={2} | alloc={3}",
                    item.FileName,
                    FormatDetectedSaveTypeDisplay(item.SaveTypeInfo),
                    FormatPendingPatchDisplay(item),
                    FormatAllocatedSizeText(GetPendingAllocatedSizeBytes(item))));
            }

            if (skippedFiles.Count > 0)
            {
                AppendLog(Tf(
                    "log",
                    "queue_add_duplicate_skipped",
                    "The following files are already in the burn queue and were skipped: {0}",
                    string.Join(", ", skippedFiles)));
            }
        }
        finally
        {
            SetQueueAnalysisState(false, filesToAnalyze.Count);
        }
    }

    private async Task<PendingRomAnalysisResult[]> AnalyzePendingRomFilesAsync(
        string[] romFiles,
        bool isGbcWorkspaceMode)
    {
        using SemaphoreSlim semaphore = new(8, 8);
        Task<PendingRomAnalysisResult>[] tasks = romFiles
            .Select((filePath, index) => AnalyzePendingRomFileAsync(index, filePath, semaphore, isGbcWorkspaceMode))
            .ToArray();
        return await Task.WhenAll(tasks);
    }

    private async Task<PendingRomAnalysisResult> AnalyzePendingRomFileAsync(
        int index,
        string filePath,
        SemaphoreSlim semaphore,
        bool isGbcWorkspaceMode)
    {
        await semaphore.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    (List<PendingRomImportItem> pendingItems, List<string> messages) =
                        CreatePendingRomImportItemsFromSource(filePath, isGbcWorkspaceMode);
                    return new PendingRomAnalysisResult
                    {
                        Index = index,
                        FilePath = filePath,
                        PendingItems = pendingItems,
                        Messages = messages,
                    };
                }
                catch (Exception ex)
                {
                    return new PendingRomAnalysisResult
                    {
                        Index = index,
                        FilePath = filePath,
                        Error = ex,
                    };
                }
            });
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void SetQueueAnalysisState(bool analyzing, int fileCount = 0)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action<bool, int>(SetQueueAnalysisState), analyzing, fileCount);
            return;
        }

        if (analyzing)
        {
            activeQueueAnalysisBatchCount++;
            queueAnalysisFileCount += Math.Max(1, fileCount);
        }
        else
        {
            activeQueueAnalysisBatchCount = Math.Max(0, activeQueueAnalysisBatchCount - 1);
            queueAnalysisFileCount = activeQueueAnalysisBatchCount == 0
                ? 0
                : Math.Max(1, queueAnalysisFileCount - Math.Max(1, fileCount));
        }

        isAnalyzingQueueImports = activeQueueAnalysisBatchCount > 0;
        dataGridViewRoms.Enabled = !isBusy;
        labelStatus.Text = isAnalyzingQueueImports
            ? GetQueueAnalysisStatusText()
            : !isBusy
                ? T("status", "ready", "Ready")
                : labelStatus.Text;
        UpdateActionButtons(isBusy);

        if (!isAnalyzingQueueImports)
        {
            ShowWorkspaceCapacityWarningIfExceeded();
        }
    }

    private void ShowWorkspaceCapacityWarningIfExceeded()
    {
        if (isAnalyzingQueueImports || currentScanResult is null || IsWorkspaceModificationLocked())
        {
            return;
        }

        if (TryValidateWorkspaceCapacity(out _))
        {
            return;
        }

        AppendLog(T("log", "workspace_capacity_exceeded_hint", "Current workspace exceeds cart capacity. Remove queued mappings until Start Burn becomes available."));
        MessageBox.Show(
            this,
            T(
                "dialog",
                "workspace_capacity_exceeded_after_analysis_message",
                "The current workspace exceeds the cart capacity. Start Burn is disabled.\n\nRemove queued mappings until the workspace fits again."),
            T("dialog", "workspace_capacity_exceeded_title", "Workspace Exceeds Capacity"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private string GetQueueAnalysisStatusText()
    {
        return Tf(
            "status",
            "queue_analyzing",
            "Analyzing {0} ROM file(s)...",
            Math.Max(1, queueAnalysisFileCount));
    }

    private static PendingRomImportItem CreatePendingRomPlaceholderItem(string filePath)
    {
        FileInfo info = new(filePath);
        return new PendingRomImportItem
        {
            FilePath = filePath,
            SourceImportKey = filePath,
            FileName = info.Name,
            IsLoadingPlaceholder = true,
            DisplayTitle = info.Name,
            HeaderTitle = string.Empty,
            FileSizeBytes = info.Exists ? info.Length : 0,
        };
    }

    private (List<PendingRomImportItem> Items, List<string> Messages) CreatePendingRomImportItemsFromSource(
        string filePath,
        bool isGbcWorkspaceMode)
    {
        var sources = RomArchiveImportService.Preview(filePath);
        if (sources.Any(s=>s.MdRom is not null || s.EntryIndex is not null || s.Platform is RomPlatform.Fc or RomPlatform.Snes))
        {
            var imported = new List<PendingRomImportItem>();
            foreach(var source in sources)
            {
                if(source.Platform==RomPlatform.Snes)
                {
                    var snes=GbaMultiMenuBuilder.Snes.SnesRomImportService.CreateGame(GbaMultiMenuBuilder.Snes.SnesRomImportService.ReadRom(source),source.Title);
                    imported.Add(new PendingRomImportItem{ImportedPlatform=RomPlatform.Snes,FilePath=RomArchiveImportService.Materialize(source),SourceImportKey=filePath,
                        FileName=source.EntryName,DisplayTitle=source.Title,HeaderTitle="SNES · "+snes.Snes!.MappingDisplay,FileSizeBytes=snes.RomBytes,
                        SaveTypeInfo=new(DetectedSaveTypeKind.Unknown,null)});
                    continue;
                }
                if(source.MdRom is null)
                {
                    string actual=RomArchiveImportService.Materialize(source);
                    if(source.Platform==RomPlatform.Fc)
                    {
                        var info=GbaMultiMenuBuilder.Fc.NesRomHeaderService.Parse(File.ReadAllBytes(actual));
                        imported.Add(new PendingRomImportItem{ImportedPlatform=RomPlatform.Fc,FcRom=info,FilePath=actual,SourceImportKey=filePath,
                            FileName=source.EntryName,DisplayTitle=source.Title,HeaderTitle="NES",FileSizeBytes=source.Length});
                    }
                    else imported.Add(CreatePendingRomImportItem(actual,filePath,source.Title,isGbcWorkspaceModeOverride:source.Platform==RomPlatform.Gbc));
                    continue;
                }
                var md=source.MdRom with{Title=source.Title};
                string? error=null;
                if(md.CanRead)try{_ = GbaMultiMenuBuilder.Md.MdRomImportService.ReadRom(md);}catch(Exception ex)when(ex is IOException or InvalidDataException){error=ex.Message;}
                imported.Add(new PendingRomImportItem { ImportedPlatform=RomPlatform.Md,MdRom=md,MdReadError=error,FilePath=md.SourcePath,SourceImportKey=filePath,
                    FileName=md.EntryName,DisplayTitle=md.Title,HeaderTitle=md.Assessment.ProductCode.Trim(),FileSizeBytes=md.RomBytes,
                    SaveTypeInfo=new(DetectedSaveTypeKind.Unknown,null) });
            }
            return (imported,[]);
        }
        if(Path.GetExtension(filePath).Equals(".zip",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP中没有可识别的GBA、GB/GBC、NES或MD游戏。");
        WaterMulticartImportAnalysis? multicartAnalysis = WaterMulticartRomAnalyzer.TryAnalyze(filePath);
        if (multicartAnalysis is null)
        {
            return ([CreatePendingRomImportItem(filePath, isGbcWorkspaceModeOverride: isGbcWorkspaceMode)], []);
        }

        List<PendingRomImportItem> items = multicartAnalysis.ExtractedRoms
            .OrderBy(item => item.OrderIndex)
            .Select(item => CreatePendingRomImportItem(
                item.FilePath,
                sourceImportKey: filePath,
                displayTitleOverride: item.DisplayTitle,
                importedFromMulticart: true,
                isGbcWorkspaceModeOverride: isGbcWorkspaceMode))
            .ToList();

        return (items, multicartAnalysis.Messages.ToList());
    }

    private PendingRomImportItem CreatePendingRomImportItem(
        string filePath,
        string? sourceImportKey = null,
        string? displayTitleOverride = null,
        bool importedFromMulticart = false,
        bool? isGbcWorkspaceModeOverride = null)
    {
        if(Path.GetExtension(filePath).Equals(".zip",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("ZIP must be expanded before ROM analysis.");
        GbaMultiMenuBuilder.Md.MdCartridgePolicy.RejectImport(filePath);
        FileInfo info = new(filePath);
        byte[] romBytes = File.ReadAllBytes(filePath);
        bool isGbcImport = (isGbcWorkspaceModeOverride ?? IsGbcWorkspaceMode()) ||
            Path.GetExtension(filePath).Equals(".gb", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(filePath).Equals(".gbc", StringComparison.OrdinalIgnoreCase);
        string displayTitle = WaterMenuConfigCodec.NormalizeTitle(displayTitleOverride ?? Path.GetFileNameWithoutExtension(filePath));
        string fileName = string.IsNullOrWhiteSpace(displayTitleOverride)
            ? info.Name
            : $"{displayTitle}{Path.GetExtension(filePath)}";
        string headerTitle;
        DetectedSaveTypeInfo saveTypeInfo;
        GameBoyRomMetadata? gameBoyMetadata = null;
        string patchTargetIdentifier;
        string appliedPatchIdentifier;
        BatterylessPatchInspection batterylessInspection;
        BatterylessPatchInspection? preparedBatterylessInspection = null;

        if (isGbcImport)
        {
            GameBoyRomMetadata metadata = GameBoyRomHeaderService.Read(filePath, romBytes);
            gameBoyMetadata = metadata;
            headerTitle = WaterMenuConfigCodec.NormalizeTitle(metadata.HeaderTitle);
            int effectiveSaveBytes = metadata.CartridgeType is 0x05 or 0x06
                ? 0x200
                : metadata.EffectiveRamBankCount * GameBoyRomHeaderService.RamBankSize;
            saveTypeInfo = effectiveSaveBytes <= 0
                ? new DetectedSaveTypeInfo(DetectedSaveTypeKind.None, null)
                : new DetectedSaveTypeInfo(DetectedSaveTypeKind.Sram, effectiveSaveBytes switch
                {
                    <= 0x200 => "SRAM 512B",
                    <= 0x2000 => "SRAM 2K",
                    <= 0x8000 => "SRAM 8K",
                    <= 0x10000 => "SRAM 64K",
                    _ => "SRAM 128K",
                });
            patchTargetIdentifier = string.Empty;
            appliedPatchIdentifier = string.Empty;
            batterylessInspection = new BatterylessPatchInspection(false, true, true);
        }
        else
        {
            headerTitle = WaterMenuConfigCodec.NormalizeTitle(WaterCartScanner.DecodeGbaHeaderTitle(romBytes));
            saveTypeInfo = SaveTypeDetectorService.Detect(romBytes);
            patchTargetIdentifier = sramPatchService.DetectPatchTarget(romBytes) ?? string.Empty;
            appliedPatchIdentifier = sramPatchService.DetectAppliedPatchTarget(romBytes) ?? string.Empty;
            batterylessInspection = batterylessPatchService.Analyze(romBytes);
        }

        if (string.IsNullOrWhiteSpace(displayTitle))
        {
            displayTitle = headerTitle;
        }

        bool applySramPatch = !isGbcImport &&
            ShouldApplySramPatch(saveTypeInfo, patchTargetIdentifier, appliedPatchIdentifier, batterylessInspection);
        if (applySramPatch)
        {
            SramPatchResult preparedSramPatchResult = sramPatchService.TryApply(romBytes);
            if (preparedSramPatchResult.Patched)
            {
                preparedBatterylessInspection = batterylessPatchService.Analyze(preparedSramPatchResult.RomData);
            }
        }

        return new PendingRomImportItem
        {
            ImportedPlatform=isGbcImport?RomPlatform.Gbc:RomPlatform.Gba,
            FilePath = filePath,
            SourceImportKey = string.IsNullOrWhiteSpace(sourceImportKey) ? filePath : sourceImportKey,
            ImportedFromMulticart = importedFromMulticart,
            FileName = fileName,
            DisplayTitle = displayTitle,
            HeaderTitle = headerTitle,
            GbaGameCode = isGbcImport ? string.Empty : WaterCartScanner.DecodeGbaHeaderGameCode(romBytes),
            FileSizeBytes = info.Length,
            SaveTypeInfo = saveTypeInfo,
            GameBoyCartridgeType = gameBoyMetadata?.CartridgeType,
            GameBoyHasBattery = gameBoyMetadata?.HasBattery == true,
            GameBoyEffectiveRamBankCount = gameBoyMetadata?.EffectiveRamBankCount ?? 0,
            GameBoyEffectiveRomSizeBytes = gameBoyMetadata?.EffectiveRomSizeBytes ?? 0,
            SramPatchTargetIdentifier = patchTargetIdentifier,
            AppliedSramPatchIdentifier = appliedPatchIdentifier,
            ApplySramPatch = applySramPatch,
            BatterylessAlreadyPatched = batterylessInspection.AlreadyPatched,
            BatterylessHasPayloadSpace = batterylessInspection.HasPayloadSpace,
            BatterylessCanExpand = batterylessInspection.CanExpand,
            PreparedBatterylessInspection = preparedBatterylessInspection,
        };
    }

    private static string TryReadQueuedRomHeaderTitle(string filePath)
    {
        try
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[192];
            int readLength = stream.Read(header, 0, header.Length);
            if (readLength < header.Length)
            {
                Array.Resize(ref header, readLength);
            }

            string title = WaterCartScanner.DecodeGbaHeaderTitle(header);
            return WaterMenuConfigCodec.NormalizeTitle(title);
        }
        catch
        {
            return string.Empty;
        }
    }

    private void RebuildWorkspaceRows()
    {
        RebuildWorkspaceRows(-1);
    }

    private void RebuildWorkspaceRows(int selectedIndex)
    {
        if (IsMbc5EightMiBMode()) { RefreshMbc5Grid(selectedIndex); return; }
        workspaceRows.Clear();

        if (currentScanResult is not null)
        {
            foreach (WaterCartEntry entry in GetWorkspaceBaseEntries(currentScanResult))
            {
                workspaceRows.Add(new WorkspaceListRow
                {
                    CartEntry = entry,
                    GbcDedicatedSaveAllocationOverride = entry.HasBattery && entry.RamBankCount > 0
                        ? !entry.UsesSharedSave
                        : null,
                });
            }
        }

        foreach (PendingRomImportItem item in pendingImportItems)
        {
            workspaceRows.Add(new WorkspaceListRow
            {
                PendingItem = item,
                IsLoadingPlaceholder = item.IsLoadingPlaceholder,
                GbcDedicatedSaveAllocationOverride = item.GbcDedicatedSaveAllocationOverride,
            });
        }

        RefreshWorkspaceGrid(selectedIndex);
    }

    private IEnumerable<WaterCartEntry> GetWorkspaceBaseEntries(WaterCartScanResult scanResult)
    {
        IEnumerable<WaterCartEntry> baseEntries = scanResult.Entries
            .Where(entry => !entry.IsMenu);

        if (UsesEffectiveWlWorkspaceMode(scanResult))
        {
            return baseEntries.Where(entry => entry.FoundInWlMapping || entry.FoundInMenuConfig);
        }

        if (scanResult.MenuConfig.IsDetected)
        {
            return baseEntries.Where(entry => entry.FoundInMenuConfig);
        }

        return baseEntries;
    }

    private void RefreshWorkspaceGrid(int selectedIndex = -1)
    {
        if (IsMbc5EightMiBMode()) { RefreshMbc5Grid(selectedIndex); return; }
        RestoreMbc5GridColumns();
        dataGridViewRoms.Rows.Clear();
        if (dataGridViewRoms.Columns["GbcDedicatedSave"] is DataGridViewColumn dedicatedSaveColumn)
        {
            dedicatedSaveColumn.Visible = IsGbcWorkspaceMode();
        }

        for (int i = 0; i < workspaceRows.Count; i++)
        {
            WorkspaceListRow row = workspaceRows[i];
            bool isPending = row.IsPending;
            bool isLoadingPlaceholder = row.IsLoadingPlaceholder;
            WaterCartEntry? entry = row.CartEntry;
            PendingRomImportItem? pending = row.PendingItem;
            string saveStatusText = isLoadingPlaceholder
                ? string.Empty
                : entry is not null
                    ? FormatWorkspaceSaveStatusText(entry)
                    : string.Empty;

            int rowIndex = dataGridViewRoms.Rows.Add(
                FormatWorkspaceOrderText(row, i),
                isLoadingPlaceholder
                    ? T("value", "row_state_loading", "Loading")
                    : pending?.MdRom is not null ? "MD导入" : isPending
                    ? T("value", "row_state_pending", "Pending")
                    : T("value", "row_state_cart", "On Cart"),
                pending?.MdRom?.ArchiveIndex is not null ? "ZIP" : isPending
                    ? T("value", "source_file", "File")
                    : entry?.SourceText ?? string.Empty,
                isLoadingPlaceholder
                    ? string.Empty
                    : pending?.ImportedPlatform is { } platform ? (platform == RomPlatform.Fc ? "NES" : platform.ToString().ToUpperInvariant()) : isPending
                    ? T("value", "kind_game", "Game")
                    : entry?.KindText ?? string.Empty,
                isLoadingPlaceholder
                    ? pending?.FileName ?? GetWorkspaceRowDisplayTitle(row)
                    : GetWorkspaceRowDisplayTitle(row),
                isLoadingPlaceholder
                    ? string.Empty
                    : isPending
                    ? pending?.HeaderTitle ?? string.Empty
                    : entry?.HeaderTitle ?? string.Empty,
                isLoadingPlaceholder
                    ? string.Empty
                    : isPending
                    ? FormatAllocatedSizeText(GetWorkspaceRowAllocatedSizeBytes(row))
                    : FormatAllocatedSizeText(GetWorkspaceRowAllocatedSizeBytes(row)),
                isLoadingPlaceholder
                    ? string.Empty
                    : isPending
                    ? FormatDetectedSaveTypeDisplay(pending!.SaveTypeInfo)
                    : entry?.SaveTypeText ?? string.Empty,
                !isLoadingPlaceholder && ResolveWorkspaceRowUsesDedicatedSave(row),
                isLoadingPlaceholder
                    ? string.Empty
                    : FormatWorkspacePatchOrMbcDisplay(row),
                isPending
                    ? entry is not null ? FormatWorkspaceSaveSlotText(entry) : string.Empty
                    : entry is not null ? FormatWorkspaceSaveSlotText(entry) : string.Empty,
                saveStatusText);
            DataGridViewRow gridRow = dataGridViewRoms.Rows[rowIndex];
            if(pending?.MdRom is not null)
            {
                foreach(DataGridViewCell cell in gridRow.Cells)cell.ToolTipText=pending.MdRom.SourceLabel+"\n"+(pending.MdReadError??pending.MdRom.Assessment.Message);
                if(pending.MdRom.Assessment.Excluded||pending.MdReadError is not null)gridRow.DefaultCellStyle.ForeColor=Color.Firebrick;
            }
            gridRow.Tag = row;
            ApplyGbcDedicatedSaveCellStyle(gridRow, row);
            ApplySaveStatusCellStyle(gridRow, saveStatusText);
            if (isLoadingPlaceholder)
            {
                ApplyLoadingPlaceholderRowStyle(gridRow);
            }
        }

        UpdateWorkspaceGroupTitle();

        if (selectedIndex >= 0 && selectedIndex < dataGridViewRoms.Rows.Count)
        {
            dataGridViewRoms.ClearSelection();
            dataGridViewRoms.Rows[selectedIndex].Selected = true;
            if (dataGridViewRoms.Columns.Count > 0)
            {
                dataGridViewRoms.CurrentCell = dataGridViewRoms.Rows[selectedIndex].Cells[0];
            }
        }

        dataGridViewRoms.Enabled = !isBusy;
        UpdateActionButtons(isBusy);
    }

    private string FormatWorkspaceOrderText(WorkspaceListRow row, int rowIndex)
    {
        string orderText = (rowIndex + 1).ToString("000", CultureInfo.CurrentCulture);
        return row.CartEntry is not null && IsCurrentChipSaveEntry(row.CartEntry)
            ? orderText + "★"
            : orderText;
    }

    private static void ApplyLoadingPlaceholderRowStyle(DataGridViewRow row)
    {
        Color mutedBackColor = SystemColors.ControlLight;

        row.DefaultCellStyle.BackColor = mutedBackColor;
        row.DefaultCellStyle.SelectionBackColor = mutedBackColor;
        row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
        row.DefaultCellStyle.SelectionForeColor = SystemColors.GrayText;

        foreach (DataGridViewCell cell in row.Cells)
        {
            cell.Style.BackColor = mutedBackColor;
            cell.Style.SelectionBackColor = mutedBackColor;
            cell.Style.ForeColor = SystemColors.GrayText;
            cell.Style.SelectionForeColor = SystemColors.GrayText;
        }

        int titleColumnIndex = row.DataGridView?.Columns["Title"]?.Index ?? -1;
        if (titleColumnIndex >= 0 && titleColumnIndex < row.Cells.Count)
        {
            row.Cells[titleColumnIndex].Style.ForeColor = SystemColors.ControlText;
            row.Cells[titleColumnIndex].Style.SelectionForeColor = SystemColors.ControlText;
        }
    }

    private void ApplyGbcDedicatedSaveCellStyle(DataGridViewRow gridRow, WorkspaceListRow row)
    {
        int columnIndex = gridRow.DataGridView?.Columns["GbcDedicatedSave"]?.Index ?? -1;
        if (columnIndex < 0 || columnIndex >= gridRow.Cells.Count)
        {
            return;
        }

        DataGridViewCell cell = gridRow.Cells[columnIndex];
        bool selectable = IsGbcDedicatedSaveAllocationSelectable(row);
        cell.ReadOnly = !selectable;
        cell.ToolTipText = selectable
            ? T("tooltip", "gbc_save_allocation_selectable", "Checked = dedicated save banks; unchecked = shared RAM banks.")
            : T("tooltip", "gbc_save_allocation_locked", "Only battery-backed GBC RAM games can be assigned dedicated save banks.");

        if (!selectable)
        {
            cell.Style.BackColor = SystemColors.ControlLight;
            cell.Style.SelectionBackColor = SystemColors.ControlLight;
            cell.Style.ForeColor = SystemColors.GrayText;
            cell.Style.SelectionForeColor = SystemColors.GrayText;
        }
    }

    private void ApplySaveStatusCellStyle(DataGridViewRow gridRow, string saveStatusText)
    {
        int columnIndex = gridRow.DataGridView?.Columns["SaveStatus"]?.Index ?? -1;
        if (columnIndex < 0 || columnIndex >= gridRow.Cells.Count)
        {
            return;
        }

        DataGridViewCell cell = gridRow.Cells[columnIndex];
        cell.Style.Font = IsPlainSaveStatusText(saveStatusText)
            ? dataGridViewRoms.Font
            : dataGridViewRoms.Columns["SaveStatus"]!.DefaultCellStyle.Font;
    }

    private bool IsPlainSaveStatusText(string saveStatusText)
    {
        if (string.IsNullOrWhiteSpace(saveStatusText))
        {
            return true;
        }

        string text = saveStatusText.Trim();
        return text.Equals(T("value", "save_status_empty_slot", "Empty Slot"), StringComparison.OrdinalIgnoreCase) ||
            text.Equals(T("value", "save_status_no_data", "No Save"), StringComparison.OrdinalIgnoreCase) ||
            text.Equals(T("value", "save_status_has_data", "Has Save"), StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Empty Slot", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("No Save", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Has Save", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("空槽", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("无存档", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("有存档", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("NONE", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetSafeSelectionIndex(int preferredIndex, int itemCount)
    {
        return itemCount > 0
            ? Math.Clamp(preferredIndex, 0, itemCount - 1)
            : -1;
    }

    private IReadOnlyList<WorkspaceListRow> CreateWorkspaceSnapshot()
    {
        return workspaceRows
            .Select(row => new WorkspaceListRow
            {
                CartEntry = row.CartEntry,
                PendingItem = row.PendingItem,
                IsLoadingPlaceholder = row.IsLoadingPlaceholder,
                DisplayTitleOverride = row.DisplayTitleOverride,
                GbcDedicatedSaveAllocationOverride = row.GbcDedicatedSaveAllocationOverride,
            })
            .ToList();
    }

    private IReadOnlyList<WorkspaceListRow> CreateWorkspaceCartSnapshot(IReadOnlyList<WorkspaceListRow> workspaceSnapshot)
    {
        return workspaceSnapshot
            .Where(row => row.CartEntry is not null && row.PendingItem is null)
            .Select(row => new WorkspaceListRow
            {
                CartEntry = row.CartEntry,
                DisplayTitleOverride = row.DisplayTitleOverride,
                GbcDedicatedSaveAllocationOverride = row.GbcDedicatedSaveAllocationOverride,
            })
            .ToList();
    }

    private bool HasWorkspaceChanges()
    {
        return HasWorkspaceChanges(workspaceRows);
    }

    private bool HasWorkspaceChanges(IReadOnlyList<WorkspaceListRow> workspaceSnapshot)
    {
        if (currentScanResult is null)
        {
            return false;
        }

        if (HasBurnProfileMetadataChanges())
        {
            return true;
        }

        if (HasGbaMenuSelectionChanges(currentScanResult))
        {
            return true;
        }

        if (workspaceSnapshot.Any(row => row.IsPending))
        {
            return true;
        }

        List<WaterCartEntry> baseEntries = GetWorkspaceBaseEntries(currentScanResult).ToList();
        if (workspaceSnapshot.Count != baseEntries.Count)
        {
            return true;
        }

        for (int i = 0; i < baseEntries.Count; i++)
        {
            WorkspaceListRow row = workspaceSnapshot[i];
            WaterCartEntry expectedEntry = baseEntries[i];
            if (row.PendingItem is not null || !ReferenceEquals(row.CartEntry, expectedEntry))
            {
                return true;
            }

            if (!string.Equals(GetWorkspaceRowDisplayTitle(row), expectedEntry.Title, StringComparison.Ordinal))
            {
                return true;
            }

            bool expectedDedicatedSave = expectedEntry.HasBattery &&
                expectedEntry.RamBankCount > 0 &&
                !expectedEntry.UsesSharedSave;
            if (ResolveWorkspaceRowUsesDedicatedSave(row) != expectedDedicatedSave)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryValidateWorkspaceCapacity(out string errorMessage)
    {
        errorMessage = string.Empty;

        if (currentScanResult is null || isAnalyzingQueueImports)
        {
            return true;
        }

        return TryValidateWorkspaceCapacity(CreateWorkspaceSnapshot(), out errorMessage);
    }

    private bool TryValidateWorkspaceCapacity(IReadOnlyList<WorkspaceListRow> workspaceSnapshot, out string errorMessage)
    {
        errorMessage = string.Empty;
        if(HasUnreadyPlatformImports(workspaceSnapshot)){errorMessage="列表含其他平台或尚未开放的核心，请选择对应模式后烧录。";return false;}

        if (currentScanResult is null)
        {
            return true;
        }

        if (!HasWorkspaceChanges(workspaceSnapshot) || workspaceSnapshot.All(row => !row.IsPending))
        {
            return true;
        }

        BurnProfileMode burnProfileMode = GetSelectedBurnProfileMode();
        ProgrammerKind programmerKind = GetSelectedProgrammerKind() ?? ProgrammerKind.WaterLink;

        if (currentScanResult.IsBeggarMaxGbcMode)
        {
            try
            {
                ValidateBeggarMaxWorkspaceSnapshot(
                    workspaceSnapshot,
                    burnProfileMode,
                    GetSelectedGbcMenuLanguageMode());
                return true;
            }
            catch (InvalidOperationException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        try
        {
            WaterCartScanResult plannedScan = BuildWorkspacePlanningScanResult(
                currentScanResult,
                workspaceSnapshot,
                burnProfileMode);

            foreach (WorkspaceListRow row in workspaceSnapshot)
            {
                if (!row.IsPending || row.PendingItem is null)
                {
                    continue;
                }

                PendingRomImportItem item = row.PendingItem;
                long fileSizeBytes = Math.Max(1L, item.FileSizeBytes);
                int plannedRomLengthBytes = GetEstimatedPreparedRomLengthBytes(item, fileSizeBytes, burnProfileMode);
                byte? legacyMenuSaveTypeOverride = ResolveLegacyMenuSaveTypeOverride(item);
                WaterRomImportPlan plan = WaterRomImportPlanner.Plan(
                    plannedScan,
                    item.FilePath,
                    CapacityPlanningPlaceholderRom,
                    GetWorkspaceRowDisplayTitle(row),
                    plannedRomLengthBytes,
                    legacyMenuSaveTypeOverride,
                    row.CartEntry);
                plannedScan = RebuildImportedScanResult(plannedScan, plan, string.Empty);
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private Task<(bool IsConnected, string FailureReason)> VerifyDeviceConnectionAsync(string portName, ProgrammerKind programmerKind)
    {
        return Task.Run(() =>
        {
            bool isConnected = TryVerifyDeviceConnection(portName, programmerKind, out string failureReason);
            return (isConnected, failureReason);
        });
    }

    private bool TryVerifyDeviceConnection(string portName, ProgrammerKind programmerKind, out string failureReason)
    {
        const int WaterLinkVerifyTimeoutMs = 1500;
        const int BeggarVerifyTimeoutMs = 3000;

        try
        {
            using var protocol = new WaterDeviceProtocol(portName);
            OpenProgrammerProtocol(protocol, programmerKind);
            bool isConnected = programmerKind == ProgrammerKind.BeggarLegacy
                ? protocol.TryReadLegacyNorId(out _, BeggarVerifyTimeoutMs)
                : protocol.TryReadLegacyNorId(out _, WaterLinkVerifyTimeoutMs);

            if (isConnected)
            {
                failureReason = string.Empty;
                return true;
            }

            failureReason = T("error", "device_not_responding", "The current programmer device did not respond.");
            return false;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    private bool TryGetSelectedWorkspaceRow(out WorkspaceListRow? row, out int rowIndex)
    {
        row = null;
        rowIndex = -1;

        if (dataGridViewRoms.SelectedRows.Count <= 0)
        {
            return false;
        }

        DataGridViewRow selectedRow = dataGridViewRoms.SelectedRows[0];
        rowIndex = selectedRow.Index;
        row = selectedRow.Tag as WorkspaceListRow;
        return row is not null;
    }

    private bool TryGetSelectedPendingImportItem(out PendingRomImportItem? item, out int rowIndex)
    {
        item = null;
        return TryGetSelectedWorkspaceRow(out WorkspaceListRow? row, out rowIndex) &&
               (item = row?.PendingItem) is not null;
    }

    private void RemoveSelectedQueuedRom()
    {
        if (IsGbcRawImageMode()) { ClearGbcImage(); return; }
        if (IsMbc5EightMiBMode()) { RemoveMbc5Slot(); return; }
        if (IsFcWorkspaceActive) { fcWorkspace!.RemoveSelected(); return; }
        if (isBusy)
        {
            return;
        }

        if (IsWorkspaceModificationLocked())
        {
            AppendLog(GetWorkspaceLockedMessage());
            return;
        }

        if (!TryGetSelectedPendingImportItem(out PendingRomImportItem? item, out int rowIndex) || item is null)
        {
            AppendLog(T("log", "queue_remove_no_selection", "Please select a queued ROM first."));
            return;
        }

        pendingImportItems.Remove(item);

        WorkspaceListRow? selectedRow = workspaceRows.FirstOrDefault(row => ReferenceEquals(row.PendingItem, item));
        if (selectedRow is not null && selectedRow.CartEntry is not null)
        {
            selectedRow.PendingItem = null;
            selectedRow.IsLoadingPlaceholder = false;
            RefreshWorkspaceGrid(GetSafeSelectionIndex(rowIndex, workspaceRows.Count));
            AppendLog(Tf("log", "replace_rom_removed", "Cancelled ROM replacement: {0}", item.FileName));
            return;
        }

        workspaceRows.RemoveAll(row => ReferenceEquals(row.PendingItem, item));
        RefreshWorkspaceGrid(GetSafeSelectionIndex(rowIndex, workspaceRows.Count));
        AppendLog(Tf("log", "queue_remove_done", "Removed queued ROM: {0}", item.FileName));
    }

    private void ClearQueuedRoms()
    {
        if (IsGbcRawImageMode()) { ClearGbcImage(); return; }
        if (IsMbc5EightMiBMode()) { ClearMbc5Slots(); return; }
        if (IsFcWorkspaceActive) { fcWorkspace!.ClearGames(); return; }
        if (isBusy)
        {
            return;
        }

        if (IsWorkspaceModificationLocked())
        {
            AppendLog(GetWorkspaceLockedMessage());
            return;
        }

        if (!workspaceRows.Any(row => row.PendingItem is not null))
        {
            return;
        }

        pendingImportItems.Clear();
        for (int i = workspaceRows.Count - 1; i >= 0; i--)
        {
            WorkspaceListRow row = workspaceRows[i];
            if (row.PendingItem is null)
            {
                continue;
            }

            if (row.CartEntry is not null)
            {
                row.PendingItem = null;
                row.IsLoadingPlaceholder = false;
                continue;
            }

            workspaceRows.RemoveAt(i);
        }
        RefreshWorkspaceGrid();
        AppendLog(T("log", "queue_clear_done", "Cleared the burn queue."));
    }

    private async Task BurnQueuedRomFilesAsync()
    {
        if (!EnsureSelectedProgrammerSupportsCurrentCartWrite(out string unsupportedReason))
        {
            AppendLog(unsupportedReason);
            return;
        }

        if (IsWorkspaceModificationLocked())
        {
            AppendLog(GetWorkspaceLockedMessage());
            return;
        }

        if (!HasWorkspaceChanges())
        {
            AppendLog(T("log", "workspace_no_changes", "The current workspace has no unapplied changes."));
            return;
        }

        await ImportRomFilesAsync(CreateWorkspaceSnapshot());
    }

    private async Task ImportRomFilesAsync(IReadOnlyList<WorkspaceListRow> workspaceSnapshot)
    {
        if(HasUnreadyPlatformImports(workspaceSnapshot)){AppendLog("列表含其他平台或尚未开放的核心，请选择对应模式后烧录。");return;}
        if (isBusy)
        {
            return;
        }

        if (!EnsureSelectedProgrammerSupportsCurrentCartWrite(out string unsupportedReason))
        {
            AppendLog(unsupportedReason);
            return;
        }

        if (currentScanResult is null)
        {
            AppendLog(T("log", "scan_before_import", "Please scan the cart before importing ROMs."));
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        List<PendingRomImportItem> pendingRows = workspaceSnapshot
            .Where(row => row.IsPending)
            .Select(row => row.PendingItem!)
            .ToList();
        bool hasWorkspaceChanges = HasWorkspaceChanges(workspaceSnapshot);
        if (!hasWorkspaceChanges)
        {
            AppendLog(T("log", "workspace_no_changes", "The current workspace has no unapplied changes."));
            return;
        }

        bool metadataOnly = pendingRows.Count == 0;
        bool updatesGbaMenu = HasGbaMenuSelectionChanges(currentScanResult);
        ProgrammerKind programmerKind = GetSelectedProgrammerKind() ?? ProgrammerKind.WaterLink;
        bool applyWaitcntPatch = !metadataOnly && checkBoxWaitcntPatch.Checked && !IsGbcWorkspaceMode();
        if (!TryValidateWorkspaceCapacity(workspaceSnapshot, out string capacityErrorMessage))
        {
            AppendLog(Tf(
                "log",
                "workspace_capacity_exceeded_detail",
                "Current workspace exceeds cart capacity: {0}",
                capacityErrorMessage));

            MessageBox.Show(
                this,
                Tf(
                    "dialog",
                    "workspace_capacity_exceeded_message",
                    "The current workspace exceeds the cart capacity and cannot be burned.\n\nDetails: {0}",
                    capacityErrorMessage),
                T("dialog", "workspace_capacity_exceeded_title", "Workspace Exceeds Capacity"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        (bool isConnected, string connectionFailureReason) = await VerifyDeviceConnectionAsync(portName, programmerKind);
        if (!isConnected)
        {
            RefreshPorts();

            AppendLog(Tf(
                "log",
                "burn_connection_lost",
                "Start Burn pre-check failed because the device is disconnected or not responding: {0}",
                connectionFailureReason));

            MessageBox.Show(
                this,
                Tf(
                    "dialog",
                    "burn_connection_lost_message",
                    "The current WaterLink device is no longer connected or not responding.\n\nNo data was written to the cart.\nReconnect the programmer and click Start Burn again after the connection is stable.\n\nDetails: {0}",
                    connectionFailureReason),
                T("dialog", "burn_connection_lost_title", "Device Disconnected"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        string modeText = currentScanResult.IsBeggarMaxGbcMode
            ? T("value", "mode_gbc_max", "GBC MAX/MBCX")
            : UsesWlManagementMode(currentScanResult, GetSelectedBurnProfileMode())
                ? T("value", "mode_magic", "Magic")
                : T("value", "mode_legacy", "Legacy");

        string message;
        string statusBusyText;
        string statusDoneText;
        string statusFailedText;
        if (metadataOnly)
        {
            string mappingText = UsesWlManagementMode(currentScanResult, GetSelectedBurnProfileMode())
                ? T("value", "apply_workspace_mapping_suffix", " and WL mapping")
                : string.Empty;
            message = Tf(
                "dialog",
                updatesGbaMenu ? "apply_gba_menu_confirm_message" : "apply_workspace_confirm_message",
                updatesGbaMenu
                    ? "Apply the selected GBA menu to the cart in {0} mode?\n\nNo game ROM data will be written. This will rewrite the head-area menu program and menu metadata{1}."
                    : "Apply the current workspace changes to the cart in {0} mode?\n\nNo new ROM data will be written. This will only rewrite the menu metadata{1}.",
                modeText,
                mappingText);
            statusBusyText = T("status", "apply_workspace_busy", "Applying workspace changes...");
            statusDoneText = T("status", "apply_workspace_done", "Workspace changes applied");
            statusFailedText = T("status", "apply_workspace_failed", "Applying workspace changes failed");
        }
        else
        {
            string fileListText = string.Join("\n", pendingRows.Select(item => "- " + item.FileName));
            string waitcntPatchText = applyWaitcntPatch
                ? "\n\n" + T("dialog", "import_rom_confirm_waitcnt_patch", "WAITCNT patch is enabled. Each GBA ROM will be patched locally before it is written.")
                : string.Empty;
            message = Tf(
                "dialog",
                "import_rom_confirm_message",
                "Import {0} ROM file(s) to the current cart in {1} mode?\n\nDropped files:\n{2}\n\nThis will erase and rewrite the target NOR area for each ROM, then update the menu metadata.{3}",
                pendingRows.Count,
                modeText,
                fileListText,
                waitcntPatchText);
            statusBusyText = T("status", "import_rom_busy", "Importing ROMs...");
            statusDoneText = T("status", "import_rom_done", "ROM import complete");
            statusFailedText = T("status", "import_rom_failed", "ROM import failed");
        }

        if (currentScanResult.IsBeggarMaxGbcMode &&
            GetSelectedGbcMenuLanguageMode() == GbcMenuLanguageMode.English)
        {
            DialogResult updateMenuConfirm = MessageBox.Show(
                this,
                T(
                    "dialog",
                    "gbc_english_start_burn_update_menu_confirm_message",
                    "The GBC menu language is set to English. Start Burn will rewrite the MAX/MBCX menu as the English menu and rebuild the game index. Font rendering will not be used.\n\nDo you want to continue?"),
                T("dialog", "gbc_english_start_burn_update_menu_confirm_title", "Update English Menu"),
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (updateMenuConfirm != DialogResult.OK)
            {
                return;
            }
        }

        DialogResult confirm = MessageBox.Show(
            this,
            message,
            metadataOnly
                ? T("dialog", "apply_workspace_confirm_title", "Apply Workspace Changes")
                : T("dialog", "import_rom_confirm_title", "Import ROMs"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        CancellationTokenSource burnCancellation = new();
        activeBurnCancellation = burnCancellation;
        isBurnCancellationRequested = false;

        SetBusy(true, statusBusyText);
        if (metadataOnly)
        {
            AppendLog(Tf(
                "log",
                "apply_workspace_start",
                "Start applying current workspace changes in {0} mode. No ROM data will be rewritten.",
                modeText));
        }
        else
        {
            AppendLog(Tf("log", "import_rom_start_batch", "Start importing {0} ROM file(s) in {1} mode.", pendingRows.Count, modeText));
            if (applyWaitcntPatch)
            {
                AppendLog(T("log", "waitcnt_patch_enabled", "WAITCNT patch is enabled. Queued GBA ROM data will be patched before burning."));
            }
        }

        try
        {
            WaterCartScanResult scanSnapshot = currentScanResult;
            BurnProfileMode burnProfileMode = GetSelectedBurnProfileMode();
            GbcMenuLanguageMode gbcMenuLanguageSnapshot = scanSnapshot.IsBeggarMaxGbcMode
                ? GetSelectedGbcMenuLanguageMode()
                : GbcMenuLanguageMode.Chinese;
            GbaMenuSelection gbaMenuSelectionSnapshot = GetSelectedGbaMenuSelection();
            string? gbcFontPathSnapshot = scanSnapshot.IsBeggarMaxGbcMode
                ? ResolveSelectedGbcFontPath()
                : null;
            string? gbcBackgroundPathSnapshot = scanSnapshot.IsBeggarMaxGbcMode
                ? ResolveSelectedGbcBackgroundPath()
                : null;
            // Use TCS + QueueUserWorkItem instead of Task.Run so that
            // OperationCanceledException thrown by ThrowIfCancellationRequested()
            // is caught inside the worker and communicated via TrySetCanceled.
            // This ensures the cancellation exception is raised on the main thread
            // rather than on the thread pool, avoiding unobserved-exception issues.
            TaskCompletionSource<WaterCartScanResult> tcs = new();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    WaterCartScanResult scanResult = ImportRomFilesOnDevice(
                        portName,
                        programmerKind,
                        scanSnapshot,
                        workspaceSnapshot,
                        burnProfileMode,
                        applyWaitcntPatch,
                        gbcFontPathSnapshot,
                        gbcBackgroundPathSnapshot,
                        gbcMenuLanguageSnapshot,
                        gbaMenuSelectionSnapshot,
                        burnCancellation.Token);
                    tcs.TrySetResult(scanResult);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(burnCancellation.Token);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            WaterCartScanResult refreshed = await tcs.Task;
            pendingImportItems.Clear();
            workspaceRows.RemoveAll(row => row.IsPending);
            ApplyScanResult(refreshed);
            ClearActiveBurnCancellation(burnCancellation);
            SetBusy(false, statusDoneText);
        }
        catch (OperationCanceledException)
        {
            AbortMeasuredProgress();
            AppendLog(T("log", "burn_cancelled", "Burn cancelled. The queued workspace changes were kept; click Start Burn again when ready."));
            ClearActiveBurnCancellation(burnCancellation);
            SetBusy(false, T("status", "burn_cancelled", "Burn cancelled. Ready to burn."));
        }
        catch (Exception ex)
        {
            AbortMeasuredProgress();
            AppendLog(Tf(
                "log",
                metadataOnly ? "apply_workspace_failed" : "import_rom_failed",
                metadataOnly ? "Applying workspace changes failed: {0}" : "ROM import failed: {0}",
                ex.Message));
            ClearActiveBurnCancellation(burnCancellation);
            SetBusy(false, statusFailedText);
        }
        finally
        {
            ClearActiveBurnCancellation(burnCancellation);
            burnCancellation.Dispose();
        }
    }

    private WaterCartScanResult ImportRomFilesOnDevice(
        string portName,
        ProgrammerKind programmerKind,
        WaterCartScanResult initialScan,
        IReadOnlyList<WorkspaceListRow> workspaceSnapshot,
        BurnProfileMode burnProfileMode,
        bool applyWaitcntPatch,
        string? gbcFontPathSnapshot = null,
        string? gbcBackgroundPathSnapshot = null,
        GbcMenuLanguageMode gbcMenuLanguageSnapshot = GbcMenuLanguageMode.Chinese,
        GbaMenuSelection gbaMenuSelection = GbaMenuSelection.Simplified,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);
        cancellationToken.ThrowIfCancellationRequested();

        if (initialScan.IsBeggarMaxGbcMode)
        {
            return ImportBeggarMaxRomFilesOnDevice(
                protocol,
                initialScan,
                workspaceSnapshot,
                burnProfileMode,
                gbcFontPathSnapshot,
                gbcBackgroundPathSnapshot,
                gbcMenuLanguageSnapshot,
                clearConfigArea: false,
                allowEmptyWorkspace: false,
                cancellationToken: cancellationToken);
        }

        WaterCartScanResult liveScan = BuildWorkspacePlanningScanResult(initialScan, workspaceSnapshot, burnProfileMode);
        List<PlannedWorkspaceImport> plannedImports = [];
        Dictionary<PendingRomImportItem, PendingRomPreparation> pendingPreparations = new();

        for (int rowIndex = 0; rowIndex < workspaceSnapshot.Count; rowIndex++)
        {
            WorkspaceListRow row = workspaceSnapshot[rowIndex];
            if (!row.IsPending || row.PendingItem is null)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] romBytes = File.ReadAllBytes(row.PendingItem.FilePath);
            cancellationToken.ThrowIfCancellationRequested();
            pendingPreparations[row.PendingItem] = BuildPendingRomPreparation(row.PendingItem, romBytes, liveScan, burnProfileMode, applyWaitcntPatch, cancellationToken);
            int plannedRomLengthBytes = GetEstimatedPreparedRomLengthBytes(row.PendingItem, romBytes.Length, burnProfileMode);
            byte? legacyMenuSaveTypeOverride = ResolveLegacyMenuSaveTypeOverride(row.PendingItem);
            WaterRomImportPlan plan = WaterRomImportPlanner.Plan(
                liveScan,
                row.PendingItem.FilePath,
                romBytes,
                GetWorkspaceRowDisplayTitle(row),
                plannedRomLengthBytes,
                legacyMenuSaveTypeOverride,
                row.CartEntry);
            LogImportPlan(plan, rowIndex + 1);
            plannedImports.Add(new PlannedWorkspaceImport
            {
                PendingItem = row.PendingItem,
                Plan = plan,
                WriteSegments = plan.RomSegments,
            });
            liveScan = RebuildImportedScanResult(
                liveScan,
                plan,
                WaterCartScanner.DecodeGbaHeaderGameCode(romBytes));
        }

        int autoPatchCount = plannedImports.Count(import =>
            pendingPreparations.TryGetValue(import.PendingItem, out PendingRomPreparation? preparation) &&
            preparation.PatchedRomTask is not null);
        int directWriteCount = plannedImports.Count - autoPatchCount;
        if (autoPatchCount > 0)
        {
            AppendLog(Tf(
                "log",
                "import_patch_pipeline",
                "Will program {0} ROM(s) without extra patching first, while preparing patch pipelines for the remaining {1} ROM(s) in the background.",
                directWriteCount,
                autoPatchCount));
        }

        WorkspaceMetadataPlan metadataPlan = BuildWorkspaceMetadataPlan(initialScan, workspaceSnapshot, plannedImports, burnProfileMode);
        WorkspaceHeadAreaRewritePlan? headAreaRewritePlan =
            BuildWorkspaceHeadAreaRewritePlan(initialScan, plannedImports, metadataPlan, burnProfileMode, gbaMenuSelection);
        PrepareWorkspaceGbaCovers(protocol, programmerKind, initialScan, workspaceSnapshot, metadataPlan, headAreaRewritePlan);
        ProgramWorkspaceImportBatch(
            protocol,
            programmerKind,
            initialScan,
            plannedImports,
            metadataPlan,
            pendingPreparations,
            headAreaRewritePlan,
            cancellationToken);
        return BuildImportedScanResult(liveScan, metadataPlan, headAreaRewritePlan);
    }

    private WaterCartScanResult BuildWorkspacePlanningScanResult(
        WaterCartScanResult initialScan,
        IReadOnlyList<WorkspaceListRow> workspaceSnapshot,
        BurnProfileMode burnProfileMode)
    {
        IReadOnlyList<WorkspaceListRow> cartRows = CreateWorkspaceCartSnapshot(workspaceSnapshot);
        WorkspaceMetadataPlan metadataPlan = BuildWorkspaceMetadataPlan(initialScan, cartRows, [], burnProfileMode);
        List<DetectedNorGameInfo> workspaceGames = BuildWorkspaceGameSnapshot(initialScan, cartRows, burnProfileMode);
        WaterWlMappingInfo? wlMappingOverride = metadataPlan.WlMapping;
        if (!UsesWlTargetPlanningMode(burnProfileMode))
        {
            wlMappingOverride = new WaterWlMappingInfo();
        }

        return WaterCartScanner.RebuildCachedScanResult(
            initialScan,
            wlMappingOverride: wlMappingOverride,
            menuConfigOverride: metadataPlan.MenuConfig,
            gamesOverride: workspaceGames);
    }

    private List<DetectedNorGameInfo> BuildWorkspaceGameSnapshot(
        WaterCartScanResult initialScan,
        IReadOnlyList<WorkspaceListRow> cartRows,
        BurnProfileMode burnProfileMode)
    {
        bool usesWlMode = UsesWlTargetPlanningMode(burnProfileMode);
        List<DetectedNorGameInfo> games = initialScan.Games
            .Where(game => game.IsMenu)
            .Select(game => new DetectedNorGameInfo
            {
                StartBucket = game.StartBucket,
                EndBucket = game.EndBucket,
                Title = game.Title,
                GameCode = game.GameCode,
                IsMenu = true,
            })
            .ToList();

        foreach (WorkspaceListRow row in cartRows)
        {
            if (row.CartEntry is null)
            {
                continue;
            }

            MenuItemConfigInfo item = BuildWorkspaceMenuItem(row.CartEntry, usesWlMode, GetWorkspaceRowDisplayTitle(row));
            int startBucket = usesWlMode
                ? row.CartEntry.PhysicalBuckets.FirstOrDefault(row.CartEntry.StartBucket)
                : item.StartBucket;
            int endBucket = usesWlMode
                ? (row.CartEntry.PhysicalBuckets.Count > 0
                    ? row.CartEntry.PhysicalBuckets.Max()
                    : startBucket + Math.Max(1, row.CartEntry.LogicalBucketCount) - 1)
                : item.EndBucket;
            string title = !string.IsNullOrWhiteSpace(row.CartEntry.HeaderTitle)
                ? row.CartEntry.HeaderTitle
                : item.Title;

            games.Add(new DetectedNorGameInfo
            {
                StartBucket = startBucket,
                EndBucket = Math.Max(startBucket, endBucket),
                Title = title,
                GameCode = row.CartEntry.HeaderGame?.GameCode ?? string.Empty,
                IsMenu = false,
            });
        }

        return games
            .OrderBy(game => game.StartBucket)
            .ToList();
    }

    private void LogImportPlan(WaterRomImportPlan plan, int targetOrder)
    {
        string modeText = plan.UsesWlMode
            ? T("value", "mode_magic", "Magic")
            : T("value", "mode_legacy", "Legacy");

        AppendLog(Tf(
            "log",
            "import_plan_summary",
            "Import plan | {0} | {1} | file {2} | alloc {3} | slot #{4:000}",
            modeText,
            plan.DisplayTitle,
            FormatMiBSize(plan.FileSizeBytes),
            FormatMiBSize(plan.AllocatedSizeBytes),
            targetOrder));

        if (plan.UsesWlMode)
        {
            AppendLog(Tf(
                "log",
                "import_plan_magic_buckets",
                "Magic bucket map: logical {0} | physical {1}",
                plan.LogicalBucketCount,
                WaterCartEntry.FormatBucketList(plan.PhysicalBuckets)));
        }
        else
        {
            AppendLog(Tf(
                "log",
                "import_plan_legacy_range",
                "Legacy range: 0x{0:X6}-0x{1:X6} | blocks {2}/{3}",
                plan.RomSegments[0].StartAddress,
                plan.RomSegments[0].StartAddress + (uint)plan.RomSegments[0].Data.Length - 1,
                plan.MenuItem.RomOffsetBlocks,
                plan.MenuItem.RomSizeBlocks));
        }
    }

    private WorkspaceHeadAreaRewritePlan? BuildWorkspaceHeadAreaRewritePlan(
        WaterCartScanResult initialScan,
        IReadOnlyList<PlannedWorkspaceImport> plannedImports,
        WorkspaceMetadataPlan metadataPlan,
        BurnProfileMode burnProfileMode,
        GbaMenuSelection gbaMenuSelection)
    {
        bool targetUsesWlMode = UsesWlTargetPlanningMode(burnProfileMode);
        bool requiresTargetMenuRewrite = targetUsesWlMode && !UsesWlMode(initialScan);
        bool requiresSelectedMenuRewrite = HasGbaMenuSelectionChanges(initialScan, gbaMenuSelection);
        int multicartImportCount = plannedImports.Count(import => IsImportedFromMulticart(import.PendingItem));
        if (multicartImportCount <= 0 && !requiresTargetMenuRewrite && !requiresSelectedMenuRewrite)
        {
            return null;
        }

        if (metadataPlan.StatusSector is null)
        {
            throw new InvalidOperationException(T(
                "error",
                "workspace_status_sector_missing",
                "The workspace status sector is missing, so the head area cannot be rebuilt."));
        }

        if (multicartImportCount > 0)
        {
            AppendLog(Tf(
                "log",
                "multicart_head_rewrite_prepare",
                "Detected {0} multicart-derived queued ROM(s). This burn will rebuild and rewrite the full head 4MiB menu area together with metadata to avoid mixed head data.",
                multicartImportCount));
        }

        if (requiresTargetMenuRewrite)
        {
            AppendLog(T(
                "log",
                "workspace_target_wl_menu_rewrite",
                "The current cart is not using the WL menu yet. Because the flash-cart target is set to WaterLink, this burn will rewrite the full head 4MiB area to the bundled WL menu."));
        }

        if (requiresSelectedMenuRewrite)
        {
            AppendLog(Tf(
                "log",
                "workspace_selected_gba_menu_rewrite",
                "The selected GBA menu differs from the menu detected on the cart. Start Burn will rewrite the head area with: {0}.",
                gbaMenuSelection));
        }

        if (targetUsesWlMode)
        {
            byte[] mappingSector = metadataPlan.MappingSector ?? WaterMagicUpgradeBuilder.BuildMappingSector(metadataPlan.MenuConfig);
            byte[] menuRomBytes = LoadSelectedGbaMenuRom(usesWlLayout: true, gbaMenuSelection, out string menuRomPath);
            byte[] headAreaImage = WaterMagicUpgradeBuilder.BuildHeadAreaImage(
                menuRomBytes,
                metadataPlan.MenuConfig,
                metadataPlan.ItemListSector,
                metadataPlan.StatusSector,
                mappingSector);

            return new WorkspaceHeadAreaRewritePlan
            {
                UsesWlMode = true,
                HeadAreaImage = headAreaImage,
                HeadAreaLabel = T("value", "magic_head_area_label", "Magic head 4MiB"),
                MenuRomPath = menuRomPath,
                MenuHeaderTitle = ResolveMenuHeaderTitle(menuRomBytes, "WL MULTIMENU"),
            };
        }

        byte[] legacyMenuRomBytes = LoadSelectedGbaMenuRom(usesWlLayout: false, gbaMenuSelection, out string legacyMenuRomPath);
        byte[] legacyHeadAreaImage = WaterLegacyMenuRepairBuilder.BuildHeadAreaImage(
            legacyMenuRomBytes,
            metadataPlan.MenuConfig,
            metadataPlan.ItemListSector,
            metadataPlan.StatusSector);

        return new WorkspaceHeadAreaRewritePlan
        {
            UsesWlMode = false,
            HeadAreaImage = legacyHeadAreaImage,
            HeadAreaLabel = T("value", "legacy_head_area_label", "Legacy head 4MiB"),
            MenuRomPath = legacyMenuRomPath,
            MenuHeaderTitle = ResolveMenuHeaderTitle(legacyMenuRomBytes, "LK MULTIMENU"),
        };
    }

    private static bool IsImportedFromMulticart(PendingRomImportItem item)
    {
        if (item.ImportedFromMulticart)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(item.SourceImportKey) &&
               !string.Equals(item.SourceImportKey, item.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMenuHeaderTitle(byte[] menuRomBytes, string fallbackTitle)
    {
        string title = WaterMenuConfigCodec.NormalizeTitle(WaterCartScanner.DecodeGbaHeaderTitle(menuRomBytes));
        return string.IsNullOrWhiteSpace(title) ? fallbackTitle : title;
    }

    private void ProgramWorkspaceImportBatch(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        WaterCartScanResult scanResult,
        IReadOnlyList<PlannedWorkspaceImport> plannedImports,
        WorkspaceMetadataPlan metadataPlan,
        IReadOnlyDictionary<PendingRomImportItem, PendingRomPreparation> pendingPreparations,
        WorkspaceHeadAreaRewritePlan? headAreaRewritePlan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PrepareWorkspaceImportWriteSegments(plannedImports, pendingPreparations, scanResult.Geometry, cancellationToken);

        long totalBytes = plannedImports.Sum(import => import.WriteSegments.Sum(segment => CountSegmentProgressWorkBytes(segment, scanResult.Geometry)))
            + (headAreaRewritePlan is not null
                ? CountMenuHeadWriteWorkBytes(headAreaRewritePlan.HeadAreaImage, scanResult.Geometry, metadataPlan.MenuConfig.Status, headAreaRewritePlan.UsesWlMode, headAreaRewritePlan.HeadAreaLabel)
                : CountWorkspaceMetadataPlanWorkBytes(metadataPlan, scanResult.Geometry));
        int totalSectors = plannedImports.Sum(import => import.WriteSegments.Sum(segment => CountSegmentProgressUnits(segment, scanResult.Geometry)))
            + (headAreaRewritePlan is not null
                ? CountMenuHeadWriteUnits(headAreaRewritePlan.HeadAreaImage, scanResult.Geometry, metadataPlan.MenuConfig.Status, headAreaRewritePlan.UsesWlMode, headAreaRewritePlan.HeadAreaLabel)
                : CountWorkspaceMetadataPlanUnits(metadataPlan));
        string operationTitle = plannedImports.Count == 0
            ? T("value", "import_progress_title_metadata", "Apply Workspace Changes")
            : plannedImports.Count == 1
            ? Tf("value", "import_progress_title_single", "Import: {0}", plannedImports[0].Plan.DisplayTitle)
            : Tf("value", "import_progress_title_batch", "Import Batch ({0} ROMs)", plannedImports.Count);

        BeginMeasuredProgress(operationTitle, totalBytes, totalSectors);

        foreach (PlannedWorkspaceImport import in plannedImports
                     .OrderBy(item =>
                         pendingPreparations.TryGetValue(item.PendingItem, out PendingRomPreparation? preparation) &&
                         preparation.PatchedRomTask is not null
                             ? 1
                             : 0))
        {
            IReadOnlyList<WaterNorWriteSegment> writeSegments = import.WriteSegments;
            for (int segmentIndex = 0; segmentIndex < writeSegments.Count; segmentIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProgramNorSegment(protocol, programmerKind, scanResult.Geometry, writeSegments[segmentIndex], segmentIndex + 1, writeSegments.Count, cancellationToken);
            }
        }

        if (headAreaRewritePlan is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (metadataPlan.CoverMenuArea is null)
            {
                PreserveCurrentGbaMenuCovers(
                    protocol,
                    programmerKind,
                    scanResult,
                    headAreaRewritePlan.HeadAreaImage);
            }
            AppendLog(Tf(
                "log",
                headAreaRewritePlan.UsesWlMode ? "use_magic_menu_rom" : "use_legacy_menu_rom",
                headAreaRewritePlan.UsesWlMode ? "Use magic menu ROM: {0}" : "Use legacy menu ROM: {0}",
                headAreaRewritePlan.MenuRomPath));
            AppendLog(Tf(
                "log",
                headAreaRewritePlan.UsesWlMode ? "write_magic_head_area" : "write_legacy_head_area",
                headAreaRewritePlan.UsesWlMode
                    ? "Rewrite Magic head area: 0x000000-0x{0:X6} | {1} sector(s)"
                    : "Rewrite legacy head area: 0x000000-0x{0:X6} | {1} sector(s)",
                headAreaRewritePlan.HeadAreaImage.Length - 1,
                headAreaRewritePlan.HeadAreaImage.Length / scanResult.Geometry.SectorSizeBytes));
            RewriteMenuHeadArea(
                protocol,
                programmerKind,
                scanResult.Geometry,
                headAreaRewritePlan.HeadAreaImage,
                metadataPlan.MenuConfig.Status,
                headAreaRewritePlan.UsesWlMode,
                headAreaRewritePlan.HeadAreaLabel,
                cancellationToken);
            CompleteMeasuredProgress(operationTitle);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        WriteWorkspaceMetadataSectors(
            protocol,
            programmerKind,
            scanResult.Geometry,
            metadataPlan,
            T("value", "final_metadata_label", "final metadata"),
            cancellationToken);

        CompleteMeasuredProgress(operationTitle);
    }

    private long CountWorkspaceMetadataPlanWorkBytes(WorkspaceMetadataPlan metadataPlan, NorGeometryInfo geometry) =>
        BuildWorkspaceMetadataSectorBatch(metadataPlan)
            .Sum(sector => CountRewriteSectorProgressWorkBytes(sector.Data, geometry));

    private int CountWorkspaceMetadataPlanUnits(WorkspaceMetadataPlan metadataPlan) =>
        BuildWorkspaceMetadataSectorBatch(metadataPlan)
            .Sum(sector => CountRewriteSectorProgressUnits(sector.Data));

    private List<(uint Address, byte[] Data, string Label)> BuildWorkspaceMetadataSectorBatch(WorkspaceMetadataPlan metadataPlan)
    {
        List<(uint Address, byte[] Data, string Label)> metadataSectors = metadataPlan.CoverSectors.ToList();
        if (metadataPlan.StatusSector is not null)
        {
            metadataSectors.Add((
                (uint)metadataPlan.MenuConfig.Status.StatusAddress,
                metadataPlan.StatusSector,
                T("value", "status_label", "status")));
        }

        metadataSectors.Add((
            (uint)metadataPlan.MenuConfig.Status.ItemListAddress,
            metadataPlan.ItemListSector,
            T("value", "itemlist_label", "itemlist")));

        if (metadataPlan.MappingSector is not null)
        {
            metadataSectors.Add((
                (uint)WaterWlMappingCodec.Offset,
                metadataPlan.MappingSector,
                T("value", "wl_mapping_label", "WL mapping")));
        }

        return metadataSectors;
    }

    private void WriteWorkspaceMetadataSectors(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        NorGeometryInfo geometry,
        WorkspaceMetadataPlan metadataPlan,
        string phaseLabel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendLog(Tf(
            "log",
            "write_status_sector",
            "Write status sector: 0x{0:X6}-0x{1:X6}",
            metadataPlan.MenuConfig.Status.StatusAddress,
            metadataPlan.MenuConfig.Status.StatusAddress + metadataPlan.MenuConfig.Status.SectorSize - 1));

        AppendLog(Tf(
            "log",
            "write_itemlist_sector",
            "Write itemlist sector: 0x{0:X6}-0x{1:X6}",
            metadataPlan.MenuConfig.Status.ItemListAddress,
            metadataPlan.MenuConfig.Status.ItemListAddress + metadataPlan.MenuConfig.Status.SectorSize - 1));

        if (metadataPlan.MappingSector is not null)
        {
            AppendLog(Tf(
                "log",
                "write_wl_mapping_sector",
                "Write WL mapping sector: 0x{0:X6}-0x{1:X6}",
                WaterWlMappingCodec.Offset,
                WaterWlMappingCodec.Offset + WaterWlMappingCodec.SectorSize - 1));
        }

        List<(uint Address, byte[] Data, string Label)> sectors = BuildWorkspaceMetadataSectorBatch(metadataPlan)
            .Select(sector => (sector.Address, sector.Data, $"{phaseLabel} {sector.Label}"))
            .ToList();
        AddUniversalManifestSyncSector(
            protocol,
            programmerKind,
            geometry,
            metadataPlan.MenuConfig.ItemCount,
            sectors);

        RewriteNorSectorBatch(
            protocol,
            sectors,
            geometry,
            programmerKind,
            cancellationToken: cancellationToken);
    }

    private void PrepareWorkspaceImportWriteSegments(
        IReadOnlyList<PlannedWorkspaceImport> plannedImports,
        IReadOnlyDictionary<PendingRomImportItem, PendingRomPreparation> pendingPreparations,
        NorGeometryInfo geometry,
        CancellationToken cancellationToken = default)
    {
        foreach (PlannedWorkspaceImport import in plannedImports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            import.WriteSegments = import.Plan.RomSegments;

            if (!pendingPreparations.TryGetValue(import.PendingItem, out PendingRomPreparation? preparation) ||
                preparation.PatchedRomTask is null)
            {
                continue;
            }

            byte[] preparedRomBytes = preparation.PreparedRomBytes ?? preparation.PatchedRomTask.GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePreparedRomFitsAllocatedSpace(import.Plan, import.PendingItem, preparedRomBytes);
            preparation.PreparedRomBytes = preparedRomBytes;
            import.WriteSegments = WaterRomImportPlanner.BuildWriteSegments(import.Plan, preparedRomBytes, geometry.SectorSizeBytes);

            AppendLog(Tf(
                "log",
                "import_patch_prepared",
                "Patch pipeline prepared: {0}",
                import.Plan.DisplayTitle));
        }
    }

    private void EnsurePreparedRomFitsAllocatedSpace(
        WaterRomImportPlan plan,
        PendingRomImportItem item,
        byte[] preparedRomBytes)
    {
        if (preparedRomBytes.Length <= plan.AllocatedSizeBytes)
        {
            return;
        }

        throw new InvalidOperationException(Tf(
            "error",
            "prepared_rom_exceeds_allocated_space",
            "After patch preparation, {0} needs {1}, but the allocated mapping only allows {2}.",
            item.FileName,
            FormatMiBSize(preparedRomBytes.Length),
            FormatMiBSize(plan.AllocatedSizeBytes)));
    }

    private WorkspaceMetadataPlan BuildWorkspaceMetadataPlan(
        WaterCartScanResult initialScan,
        IReadOnlyList<WorkspaceListRow> workspaceSnapshot,
        IReadOnlyList<PlannedWorkspaceImport> plannedImports,
        BurnProfileMode burnProfileMode)
    {
        Dictionary<PendingRomImportItem, WaterRomImportPlan> plansByPending = plannedImports
            .ToDictionary(item => item.PendingItem, item => item.Plan);
        bool usesWlMode = UsesWlTargetPlanningMode(burnProfileMode);

        List<MenuItemConfigInfo> finalItems = [];
        List<WlRomMapRecordInfo> finalRecords = [];

        foreach (WorkspaceListRow row in workspaceSnapshot)
        {
            if (row.PendingItem is not null)
            {
                if (!plansByPending.TryGetValue(row.PendingItem, out WaterRomImportPlan? plan))
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "workspace_pending_plan_missing",
                        "Missing import plan for queued ROM: {0}.",
                        row.PendingItem.FileName));
                }

                finalItems.Add(WaterMenuConfigCodec.Clone(plan.MenuItem));
                if (usesWlMode)
                {
                    finalRecords.Add(new WlRomMapRecordInfo
                    {
                        EntrySlot = finalRecords.Count,
                        LogicalBucketCount = plan.LogicalBucketCount,
                        PhysicalBuckets = plan.PhysicalBuckets.ToList(),
                    });
                }

                continue;
            }

            if (row.CartEntry is null)
            {
                continue;
            }

            finalItems.Add(BuildWorkspaceMenuItem(row.CartEntry, usesWlMode, GetWorkspaceRowDisplayTitle(row)));
            if (usesWlMode)
            {
                finalRecords.Add(BuildWorkspaceWlRecord(row.CartEntry, finalRecords.Count));
            }
        }

        int itemListAddress = usesWlMode
            ? WaterMagicUpgradeBuilder.WlItemListOffset
            : initialScan.MenuConfig.Status.ItemListAddress;
        List<MenuItemConfigInfo> indexedItems = WaterMenuConfigCodec.ReindexItems(finalItems, itemListAddress);
        MenuFlashStatusInfo status = CreateWorkspaceStatusForBurn(initialScan.MenuConfig.Status, burnProfileMode);
        if (UsesChm1Mode(burnProfileMode) && !status.BatteryPresent)
            foreach (var item in indexedItems) { item.SaveType = 0; item.SaveIndex = 0; }

        if (!usesWlMode && status.BatteryPresent && !initialScan.IsChm1)
        {
            NormalizeLegacyWorkspaceSaveSlots(indexedItems, initialScan.Geometry, status);
        }

        WaterMenuConfigInfo menuConfig = new()
        {
            IsDetected = true,
            Status = status,
            Items = indexedItems,
        };

        if (usesWlMode && !UsesChm1Mode(burnProfileMode))
        {
            menuConfig.Status = WaterMagicUpgradeBuilder.NormalizeWlStatus(new MenuFlashStatusInfo
            {
                StatusAddress = WaterMagicUpgradeBuilder.WlStatusOffset,
                SectorSize = WaterWlMappingCodec.SectorSize,
                Version = menuConfig.Status.Version,
                BatteryPresent = menuConfig.Status.BatteryPresent,
                LastBootMenuIndex = menuConfig.Status.LastBootMenuIndex,
                LastBootSaveIndex = menuConfig.Status.LastBootSaveIndex,
                LastBootSaveType = menuConfig.Status.LastBootSaveType,
                SramBankType = menuConfig.Status.SramBankType,
            });
        }
        if (UsesChm1Mode(burnProfileMode) || (initialScan.IsChm1 && !usesWlMode && status.BatteryPresent))
        {
            menuConfig.Status = Chm1MenuBuilder.CreateStatus(status, indexedItems, usesWlMode, status.BatteryPresent);
        }

        byte[] itemListSector = WaterMenuConfigCodec.BuildItemListSector(
            menuConfig.Items,
            menuConfig.Status.ItemListAddress,
            menuConfig.Status.SectorSize);
        byte[] statusSector = WaterMenuConfigCodec.BuildStatusSector(menuConfig.Status);

        if (!usesWlMode)
        {
            return new WorkspaceMetadataPlan
            {
                MenuConfig = menuConfig,
                ItemListSector = itemListSector,
                StatusSector = statusSector,
            };
        }

        byte[] mappingSector = UsesChm1Mode(burnProfileMode)
            ? Chm1Layout.BuildMappingSector(finalRecords.Select(record => record.PhysicalBuckets.ToArray()).ToArray(), initialScan.Geometry.DeviceSizeBytes)
            : WaterMagicUpgradeBuilder.BuildMappingSector(finalRecords);
        if (!WaterWlMappingCodec.TryParse(mappingSector, WaterWlMappingCodec.Offset, false, out WaterWlMappingInfo wlMapping, out string parseError))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(parseError)
                ? T("error", "wl_sector_format_invalid", "WL mapping sector format is invalid.")
                : parseError);
        }

        return new WorkspaceMetadataPlan
        {
            MenuConfig = menuConfig,
            ItemListSector = itemListSector,
            StatusSector = statusSector,
            WlMapping = wlMapping,
            MappingSector = mappingSector,
        };
    }

    private static MenuFlashStatusInfo ClearWorkspaceLastBootSelection(MenuFlashStatusInfo source) =>
        new()
        {
            StatusAddress = source.StatusAddress,
            ExplicitItemListAddress = source.ExplicitItemListAddress,
            ExplicitSaveAreaAddress = source.ExplicitSaveAreaAddress,
            SectorSize = source.SectorSize,
            Version = source.Version,
            BatteryPresent = source.BatteryPresent,
            LastBootMenuIndex = 0xFFFF,
            LastBootSaveIndex = 0xFF,
            LastBootSaveType = 0,
            SramBankType = source.SramBankType,
        };

    private static void NormalizeLegacyWorkspaceSaveSlots(
        IReadOnlyList<MenuItemConfigInfo> indexedItems,
        NorGeometryInfo geometry,
        MenuFlashStatusInfo status)
    {
        if (geometry.SectorSizeBytes <= 0)
        {
            throw new InvalidOperationException(
                LocalizedText.T("error", "legacy_itemlist_invalid", "Legacy itemlist address is invalid."));
        }

        int maxSaveSlotCount = Math.Max(
            0,
            (WaterCartScanner.BucketSizeBytes / geometry.SectorSizeBytes) -
            ((status.StatusAddress / geometry.SectorSizeBytes) + 1));

        foreach (MenuItemConfigInfo item in indexedItems)
        {
            if (item.SaveType == 0)
            {
                item.SaveIndex = 0;
                continue;
            }

            if (item.Index < 0 || item.Index > byte.MaxValue || item.Index >= maxSaveSlotCount)
            {
                throw new InvalidOperationException(
                    LocalizedText.Tf(
                        "error",
                        "legacy_mode_save_slot_out_of_range",
                        "Legacy save slot index is out of range for {0}.",
                        item.Title));
            }

            item.SaveIndex = checked((byte)item.Index);
        }
    }

    private MenuFlashStatusInfo CreateWorkspaceStatusForBurn(
        MenuFlashStatusInfo source,
        BurnProfileMode burnProfileMode)
    {
        bool batteryPresent = IsBatteryPresentForSelectedMode(burnProfileMode);

        return new MenuFlashStatusInfo
        {
            StatusAddress = source.StatusAddress,
            ExplicitItemListAddress = source.ExplicitItemListAddress,
            ExplicitSaveAreaAddress = source.ExplicitSaveAreaAddress,
            SectorSize = source.SectorSize,
            Version = source.Version,
            BatteryPresent = batteryPresent,
            LastBootMenuIndex = source.LastBootMenuIndex,
            LastBootSaveIndex = batteryPresent ? source.LastBootSaveIndex : (byte)0xFF,
            LastBootSaveType = batteryPresent ? source.LastBootSaveType : (byte)0,
            SramBankType = source.SramBankType,
        };
    }

    private static MenuItemConfigInfo BuildWorkspaceMenuItem(WaterCartEntry entry, bool usesWlMode, string displayTitle)
    {
        MenuItemConfigInfo item = entry.MenuItem is not null
            ? WaterMenuConfigCodec.Clone(entry.MenuItem)
            : new MenuItemConfigInfo
            {
                Font = 0,
                SaveType = 0,
                SaveIndex = 0,
                HiddenKeysMask = 0,
                Title = entry.Title,
            };

        int startBucket = usesWlMode
            ? entry.PhysicalBuckets.FirstOrDefault(entry.StartBucket)
            : entry.MenuItem?.StartBucket ?? entry.HeaderGame?.StartBucket ?? entry.StartBucket;
        int logicalBucketCount = usesWlMode
            ? Math.Max(1, entry.LogicalBucketCount)
            : entry.MenuItem?.CoveredBucketCount
                ?? Math.Max(1, entry.EndBucket - entry.StartBucket + 1);

        item.RomOffsetBlocks = checked((ushort)(startBucket * 8));
        item.RomSizeBlocks = checked((ushort)(logicalBucketCount * 8));
        item.Title = WaterMenuConfigCodec.NormalizeTitle(string.IsNullOrWhiteSpace(displayTitle) ? entry.Title : displayTitle);
        item.TitleLength = (byte)Math.Min(item.Title.Length, WaterMenuConfigCodec.TitleCharCapacity);
        return item;
    }

    private static WlRomMapRecordInfo BuildWorkspaceWlRecord(WaterCartEntry entry, int entrySlot)
    {
        List<int> physicalBuckets = entry.PhysicalBuckets.ToList();
        if (physicalBuckets.Count == 0)
        {
            physicalBuckets.Add(entry.StartBucket);
        }

        return new WlRomMapRecordInfo
        {
            EntrySlot = entrySlot,
            LogicalBucketCount = Math.Max(1, entry.WlRecord?.LogicalBucketCount ?? physicalBuckets.Count),
            PhysicalBuckets = physicalBuckets,
        };
    }

    private static WaterCartScanResult BuildImportedScanResult(
        WaterCartScanResult liveScan,
        WorkspaceMetadataPlan metadataPlan,
        WorkspaceHeadAreaRewritePlan? headAreaRewritePlan)
    {
        List<DetectedNorGameInfo>? rebuiltGames = null;
        if (headAreaRewritePlan is not null)
        {
            rebuiltGames = liveScan.Games
                .Where(game => game.StartBucket != 0)
                .Select(game => new DetectedNorGameInfo
                {
                    StartBucket = game.StartBucket,
                    EndBucket = game.EndBucket,
                    Title = game.Title,
                    GameCode = game.GameCode,
                    IsMenu = game.IsMenu,
                })
                .ToList();

            rebuiltGames.Add(new DetectedNorGameInfo
            {
                StartBucket = 0,
                EndBucket = 0,
                Title = headAreaRewritePlan.MenuHeaderTitle,
                IsMenu = true,
            });

            rebuiltGames = rebuiltGames
                .OrderBy(game => game.StartBucket)
                .ToList();
        }

        WaterCartScanResult rebuilt = WaterCartScanner.RebuildCachedScanResult(
            liveScan,
            wlMappingOverride: metadataPlan.WlMapping,
            menuConfigOverride: metadataPlan.MenuConfig,
            gamesOverride: rebuiltGames);
        if (headAreaRewritePlan is not null)
        {
            ApplyGbaMenuImageState(
                rebuilt,
                headAreaRewritePlan.HeadAreaImage,
                metadataPlan.MenuConfig.Items,
                headAreaRewritePlan.UsesWlMode);
        }
        else if (metadataPlan.CoverMenuArea is not null)
        {
            rebuilt.GbaMenuFirstScreenPreview = GbaMenuFirstScreenPreviewCodec.Decode(
                metadataPlan.CoverMenuArea, metadataPlan.MenuConfig.Items);
        }

        return rebuilt;
    }

    private static void ApplyGbaMenuImageState(
        WaterCartScanResult result,
        byte[] headAreaImage,
        IReadOnlyList<MenuItemConfigInfo> items,
        bool usesWlLayout)
    {
        result.IsUniversalGbaMenuDetected = GbaMenuRomService.SupportsCovers(headAreaImage);
        result.IsSimplifiedNsGbaMenuDetected = GbaMenuRomService.HasSimplifiedNsMenuSignature(headAreaImage);
        result.GbaMenuFirstScreenPreview = GbaMenuFirstScreenPreviewCodec.Decode(headAreaImage, items);
        result.MenuBackgroundBitmapData = [];
        result.MenuBackgroundPaletteData = [];

        if (result.GbaMenuFirstScreenPreview.IsDetected ||
            !TryResolveMenuBackgroundRegion(headAreaImage, usesWlLayout, out int backgroundOffset))
        {
            return;
        }

        const int bitmapLength = BackgroundImageEncoder.Width * BackgroundImageEncoder.Height;
        const int paletteLength = 0x200;
        if (backgroundOffset < 0 ||
            backgroundOffset + bitmapLength + paletteLength > headAreaImage.Length)
        {
            return;
        }

        result.MenuBackgroundBitmapData = headAreaImage.AsSpan(backgroundOffset, bitmapLength).ToArray();
        result.MenuBackgroundPaletteData = headAreaImage.AsSpan(backgroundOffset + bitmapLength, paletteLength).ToArray();
    }

    private static WaterCartScanResult RebuildImportedScanResult(
        WaterCartScanResult source,
        WaterRomImportPlan plan,
        string gameCode)
    {
        string headerTitle = !string.IsNullOrWhiteSpace(plan.HeaderTitle)
            ? plan.HeaderTitle
            : plan.DisplayTitle;

        int startBucket = plan.PhysicalBuckets.Count > 0
            ? plan.PhysicalBuckets[0]
            : plan.MenuItem.StartBucket;
        int endBucket = plan.UsesWlMode
            ? startBucket
            : plan.MenuItem.EndBucket;

        List<DetectedNorGameInfo> games = source.Games
            .Where(game => game.StartBucket != startBucket)
            .Select(game => new DetectedNorGameInfo
            {
                StartBucket = game.StartBucket,
                EndBucket = game.EndBucket,
                Title = game.Title,
                GameCode = game.GameCode,
                IsMenu = game.IsMenu,
            })
            .ToList();

        games.Add(new DetectedNorGameInfo
        {
            StartBucket = startBucket,
            EndBucket = endBucket,
            Title = headerTitle,
            GameCode = gameCode,
            IsMenu = false,
        });

        games = games
            .OrderBy(game => game.StartBucket)
            .ToList();

        return WaterCartScanner.RebuildCachedScanResult(
            source,
            wlMappingOverride: plan.UpdatedWlMapping,
            menuConfigOverride: plan.UpdatedMenuConfig,
            gamesOverride: games);
    }

    private PendingRomPreparation BuildPendingRomPreparation(
        PendingRomImportItem item,
        byte[] romBytes,
        WaterCartScanResult scanResult,
        BurnProfileMode burnProfileMode,
        bool applyWaitcntPatch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BatterylessPatchInspection inspection = GetBatterylessInspection(item);
        bool alreadyPrepared = HasPreparedSavePatch(
            item.SramPatchTargetIdentifier,
            item.AppliedSramPatchIdentifier,
            inspection);
        bool shouldApplySramPatch = item.ApplySramPatch &&
            !alreadyPrepared &&
            !string.IsNullOrWhiteSpace(item.SramPatchTargetIdentifier);
        bool shouldApplyBatterylessPatch = CanApplyBatterylessPatch(item, burnProfileMode);

        if (RequiresBatterylessPatchButUnavailable(item, burnProfileMode))
        {
            throw new InvalidOperationException(Tf(
                "error",
                "batteryless_patch_unavailable_for_file",
                "The current no-battery mode requires a batteryless patch for {0}, but no payload space is available in this ROM.",
                item.FileName));
        }

        bool shouldApplyWaitcntPatch = applyWaitcntPatch;

        if (!shouldApplyWaitcntPatch && !shouldApplySramPatch && !shouldApplyBatterylessPatch)
        {
            return new PendingRomPreparation
            {
                PendingItem = item,
            };
        }

        CartridgeDefinition? cartridgeDefinition = shouldApplyBatterylessPatch
            ? ResolveBatterylessCartridge(scanResult)
            : null;

        return new PendingRomPreparation
        {
            PendingItem = item,
            PatchedRomTask = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] workingRomBytes = romBytes.ToArray();

                if (shouldApplyWaitcntPatch)
                {
                    WaitcntPatchService.Apply(workingRomBytes);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (shouldApplySramPatch)
                {
                    var sramPatchResult = sramPatchService.TryApply(workingRomBytes);
                    if (!sramPatchResult.Patched || sramPatchResult.RomData.Length != workingRomBytes.Length)
                    {
                        throw new InvalidOperationException(Tf(
                            "error",
                            "sram_patch_failed_for_file",
                            "Unable to apply the SRAM patch for {0}.",
                            item.FileName));
                    }

                    workingRomBytes = sramPatchResult.RomData;
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (shouldApplyBatterylessPatch)
                {
                    if (cartridgeDefinition is null)
                    {
                        throw new InvalidOperationException(Tf(
                            "error",
                            "batteryless_patch_chip_unsupported",
                            "The current NOR chip is not mapped to a supported batteryless patch profile."));
                    }

                    var batterylessResult = batterylessPatchService.TryApply(workingRomBytes, cartridgeDefinition);
                    if (batterylessResult.Patched)
                    {
                        workingRomBytes = batterylessResult.RomData;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                return workingRomBytes;
            }, cancellationToken),
        };
    }

    private void ProgramNorSegment(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        NorGeometryInfo geometry,
        WaterNorWriteSegment segment,
        int segmentIndex,
        int segmentCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;

        if (segment.Data.Length == 0 || segment.Data.Length % sectorSize != 0)
        {
            throw new InvalidOperationException(
                Tf(
                    "error",
                    "import_segment_alignment_invalid",
                    "Import segment {0} is not aligned to the NOR sector size.",
                    segment.Label));
        }

        int sectorCount = segment.Data.Length / sectorSize;
        string eraseProgressLogKey = $"segment:{segmentIndex}:erase";
        string writeProgressLogKey = $"segment:{segmentIndex}:write";
        AppendLog(Tf(
            "log",
            segment.EraseOnly ? "erase_segment_begin" : "program_segment_begin",
            segment.EraseOnly
                ? "Erase segment {0}/{1}: {2} | 0x{3:X6}-0x{4:X6} | {5} sector(s)"
                : "Program segment {0}/{1}: {2} | 0x{3:X6}-0x{4:X6} | {5} sector(s)",
            segmentIndex,
            segmentCount,
            segment.Label,
            segment.StartAddress,
            segment.StartAddress + (uint)segment.Data.Length - 1,
            sectorCount));

        if (segment.EraseOnly)
        {
            LogWaterSysInfoProbe(protocol, programmerKind, "before erase");

            for (int sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sectorCount <= 4 || sectorIndex == 0 || sectorIndex == sectorCount - 1 || sectorIndex % 8 == 0)
                {
                    AppendProgressLog(eraseProgressLogKey, Tf(
                        "log",
                        "erase_segment_progress",
                        "  {0}: sector {1}/{2}",
                        segment.Label,
                        sectorIndex + 1,
                        sectorCount));
                }

                uint sectorAddress = segment.StartAddress + (uint)(sectorIndex * sectorSize);
                if (!TryEraseRomSectorForProgrammer(protocol, programmerKind, sectorAddress))
                {
                    throw new InvalidOperationException(
                        Tf(
                            "error",
                            "erase_segment_failed",
                            "Failed to erase {0} at offset 0x{1:X}.",
                            segment.Label,
                            sectorIndex * sectorSize) +
                        BuildProgrammerFailureDetail(
                            protocol,
                            sectorAddress,
                            sectorSize,
                            sectorIndex * sectorSize,
                            sectorSize));
                }

                AdvanceMeasuredProgress(
                    sectorSize,
                    1,
                    BuildProgressDetailText(segment.Label, "progress_phase_erase", "Erase"));
                cancellationToken.ThrowIfCancellationRequested();
            }

            LogWaterSysInfoProbe(protocol, programmerKind, "after erase");
            return;
        }

        ushort bufferWriteBytes = GetEffectiveBufferWriteBytes(geometry);
        AppendLog(Tf(
            "log",
            "program_segment_erase_prepare",
            "Erase-before-write: {0} | {1} sector(s)",
            segment.Label,
            sectorCount));

        LogWaterSysInfoProbe(protocol, programmerKind, "before erase");

        for (int sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sectorCount <= 4 || sectorIndex == 0 || sectorIndex == sectorCount - 1 || sectorIndex % 8 == 0)
            {
                AppendProgressLog(eraseProgressLogKey, Tf(
                    "log",
                    "erase_segment_progress",
                    "  erase {0}: sector {1}/{2}",
                    segment.Label,
                    sectorIndex + 1,
                    sectorCount));
            }

            uint sectorAddress = segment.StartAddress + (uint)(sectorIndex * sectorSize);
            if (!TryEraseRomSectorForProgrammer(protocol, programmerKind, sectorAddress))
            {
                throw new InvalidOperationException(
                    Tf(
                        "error",
                        "erase_segment_failed",
                        "Failed to erase {0} at offset 0x{1:X}.",
                        segment.Label,
                        sectorIndex * sectorSize) +
                    BuildProgrammerFailureDetail(
                        protocol,
                        sectorAddress,
                        sectorSize,
                        sectorIndex * sectorSize,
                        sectorSize));
            }

            AdvanceMeasuredProgress(
                sectorSize,
                1,
                BuildProgressDetailText(segment.Label, "progress_phase_erase", "Erase"));
            cancellationToken.ThrowIfCancellationRequested();
        }

        LogWaterSysInfoProbe(protocol, programmerKind, "after erase / before download");

        for (int sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sectorOffset = sectorIndex * sectorSize;
            uint sectorAddress = segment.StartAddress + (uint)sectorOffset;

            byte[] sectorData = segment.Data.AsSpan(sectorOffset, sectorSize).ToArray();
            if (!IsAllBytes(sectorData, 0xFF))
            {
                for (int chunkOffset = 0; chunkOffset < sectorSize; chunkOffset += NorProgramChunkSizeBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int writeLength = Math.Min(NorProgramChunkSizeBytes, sectorSize - chunkOffset);
                    byte[] chunk = sectorData.AsSpan(chunkOffset, writeLength).ToArray();

                    if (!TryProgramRomForProgrammer(protocol, programmerKind, sectorAddress + (uint)chunkOffset, chunk, bufferWriteBytes, 60000))
                    {
                        throw new InvalidOperationException(
                            Tf(
                                "error",
                                "segment_program_failed",
                                "Failed to program {0} at offset 0x{1:X}.",
                                segment.Label,
                                sectorOffset + chunkOffset) +
                            BuildProgrammerFailureDetail(
                                protocol,
                                sectorAddress + (uint)chunkOffset,
                                sectorSize,
                                sectorOffset + chunkOffset,
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
                            segment.Label) +
                        BuildProgrammerFailureDetail(
                            protocol,
                            sectorAddress,
                            sectorSize,
                            sectorOffset,
                            sectorSize));
                }
            }

            VerifyMenuSector(protocol, programmerKind, sectorAddress, sectorData, cancellationToken);
            if (sectorCount <= 4 || sectorIndex == 0 || sectorIndex == sectorCount - 1 || (sectorIndex + 1) % 8 == 0)
            {
                AppendProgressLog(writeProgressLogKey, Tf(
                    "log",
                    "program_segment_progress",
                    "  {0}: sector {1}/{2}",
                    segment.Label,
                    sectorIndex + 1,
                    sectorCount));
            }

            AdvanceMeasuredProgress(
                sectorSize,
                1,
                BuildProgressDetailText(segment.Label, "progress_phase_write", "Write"));
            cancellationToken.ThrowIfCancellationRequested();
        }

        LogWaterSysInfoProbe(protocol, programmerKind, "after download");
    }

    private void DataGridViewRoms_MouseDown(object? sender, MouseEventArgs e)
    {
        workspaceDragSourceIndex = -1;
        workspaceDragStartPoint = e.Location;

        DataGridView.HitTestInfo hit = dataGridViewRoms.HitTest(e.X, e.Y);
        if (e.Button != MouseButtons.Left || hit.RowIndex < 0)
        {
            return;
        }

        workspaceDragSourceIndex = hit.RowIndex;
        dataGridViewRoms.ClearSelection();
        dataGridViewRoms.Rows[hit.RowIndex].Selected = true;
        if (hit.ColumnIndex >= 0)
        {
            dataGridViewRoms.CurrentCell = dataGridViewRoms.Rows[hit.RowIndex].Cells[hit.ColumnIndex];
        }
    }

    private void DataGridViewRoms_MouseMove(object? sender, MouseEventArgs e)
    {
        if (IsMbc5EightMiBMode()) return;
        if (isBusy ||
            isAnalyzingQueueImports ||
            IsWorkspaceModificationLocked() ||
            e.Button != MouseButtons.Left ||
            workspaceDragSourceIndex < 0 ||
            workspaceDragSourceIndex >= workspaceRows.Count)
        {
            return;
        }

        Size dragSize = SystemInformation.DragSize;
        Rectangle dragRectangle = new(
            workspaceDragStartPoint.X - dragSize.Width / 2,
            workspaceDragStartPoint.Y - dragSize.Height / 2,
            dragSize.Width,
            dragSize.Height);

        if (dragRectangle.Contains(e.Location))
        {
            return;
        }

        dataGridViewRoms.DoDragDrop(
            new DataObject(WorkspaceRowDragFormat, workspaceDragSourceIndex),
            DragDropEffects.Move);
    }

    private void DataGridViewRoms_DragEnter(object? sender, DragEventArgs e)
    {
        if (IsMbc5EightMiBMode()) { e.Effect = !isBusy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None; return; }
        if (isBusy)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
            e.Effect = files is not null && files.Any(IsSupportedRomFile)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            return;
        }

        if (IsWorkspaceModificationLocked())
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = e.Data?.GetDataPresent(WorkspaceRowDragFormat) == true
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private void DataGridViewRoms_DragOver(object? sender, DragEventArgs e)
    {
        if (IsMbc5EightMiBMode())
        {
            Point location = dataGridViewRoms.PointToClient(new Point(e.X, e.Y));
            int slot = dataGridViewRoms.HitTest(location.X, location.Y).RowIndex;
            e.Effect = !isBusy && slot >= 0 && mbc5Layout.GetSlot(slot).Enabled && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            return;
        }
        if (isBusy)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            return;
        }

        if (IsWorkspaceModificationLocked())
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        if (e.Data?.GetDataPresent(WorkspaceRowDragFormat) != true)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Move;
        Point clientPoint = dataGridViewRoms.PointToClient(new Point(e.X, e.Y));
        int insertIndex = GetWorkspaceInsertIndex(clientPoint);
        int selectionIndex = GetSafeSelectionIndex(
            insertIndex == dataGridViewRoms.Rows.Count ? insertIndex - 1 : insertIndex,
            dataGridViewRoms.Rows.Count);
        if (selectionIndex >= 0 && selectionIndex < dataGridViewRoms.Rows.Count)
        {
            dataGridViewRoms.ClearSelection();
            dataGridViewRoms.Rows[selectionIndex].Selected = true;
        }
    }

    private async void DataGridViewRoms_DragDrop(object? sender, DragEventArgs e)
    {
        if (IsMbc5EightMiBMode())
        {
            Point location = dataGridViewRoms.PointToClient(new Point(e.X, e.Y));
            int slot = dataGridViewRoms.HitTest(location.X, location.Y).RowIndex;
            if (slot >= 0 && e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                await RunUiActionSafelyAsync(() => AddMbc5FilesAsync(paths, slot));
            return;
        }
        if (isBusy)
        {
            return;
        }

        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            if (IsWorkspaceModificationLocked())
            {
                AppendLog(GetWorkspaceLockedMessage());
                return;
            }

            string[]? droppedFiles = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (droppedFiles is not null)
            {
                await RunUiActionSafelyAsync(() => AddPendingRomFilesAsync(droppedFiles));
            }

            return;
        }

        if (e.Data?.GetDataPresent(WorkspaceRowDragFormat) != true)
        {
            return;
        }

        if (IsWorkspaceModificationLocked())
        {
            AppendLog(GetWorkspaceLockedMessage());
            return;
        }

        if (e.Data.GetData(WorkspaceRowDragFormat) is not int sourceIndex ||
            sourceIndex < 0 ||
            sourceIndex >= workspaceRows.Count)
        {
            return;
        }

        Point clientPoint = dataGridViewRoms.PointToClient(new Point(e.X, e.Y));
        int insertIndex = GetWorkspaceInsertIndex(clientPoint);
        MoveWorkspaceRow(sourceIndex, insertIndex);
    }

    private int GetWorkspaceInsertIndex(Point clientPoint)
    {
        if (dataGridViewRoms.Rows.Count == 0)
        {
            return 0;
        }

        DataGridView.HitTestInfo hit = dataGridViewRoms.HitTest(clientPoint.X, clientPoint.Y);
        if (hit.RowIndex >= 0)
        {
            Rectangle rowRect = dataGridViewRoms.GetRowDisplayRectangle(hit.RowIndex, false);
            return clientPoint.Y > rowRect.Top + rowRect.Height / 2
                ? hit.RowIndex + 1
                : hit.RowIndex;
        }

        Rectangle firstRowRect = dataGridViewRoms.GetRowDisplayRectangle(0, false);
        if (clientPoint.Y < firstRowRect.Top)
        {
            return 0;
        }

        return dataGridViewRoms.Rows.Count;
    }

    private void MoveWorkspaceRow(int sourceIndex, int insertIndex)
    {
        if (isAnalyzingQueueImports ||
            IsWorkspaceModificationLocked() ||
            sourceIndex < 0 ||
            sourceIndex >= workspaceRows.Count)
        {
            return;
        }

        insertIndex = Math.Clamp(insertIndex, 0, workspaceRows.Count);
        WorkspaceListRow row = workspaceRows[sourceIndex];
        workspaceRows.RemoveAt(sourceIndex);

        if (insertIndex > sourceIndex)
        {
            insertIndex--;
        }

        insertIndex = Math.Clamp(insertIndex, 0, workspaceRows.Count);
        if (insertIndex == sourceIndex)
        {
            workspaceRows.Insert(sourceIndex, row);
            return;
        }

        workspaceRows.Insert(insertIndex, row);
        RefreshWorkspaceGrid(insertIndex);
    }

    private void DataGridViewRoms_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (IsMbc5EightMiBMode()) { if (e.RowIndex >= 0 && mbc5Layout.GetSlot(e.RowIndex).Enabled) _ = RunUiActionSafelyAsync(SelectMbc5FilesAsync); return; }
        if (isBusy || isAnalyzingQueueImports || IsWorkspaceModificationLocked() || e.RowIndex < 0)
        {
            return;
        }

        if (e.ColumnIndex != dataGridViewRoms.Columns["Title"]?.Index)
        {
            return;
        }

        dataGridViewRoms.CurrentCell = dataGridViewRoms.Rows[e.RowIndex].Cells[e.ColumnIndex];
        dataGridViewRoms.BeginEdit(true);
    }

    private void DataGridViewRoms_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.ColumnIndex != dataGridViewRoms.Columns["GbcDedicatedSave"]?.Index)
        {
            return;
        }

        ToggleWorkspaceGbcDedicatedSaveAllocation(e.RowIndex);
    }

    private void ToggleWorkspaceGbcDedicatedSaveAllocation(int rowIndex)
    {
        if (isBusy ||
            isAnalyzingQueueImports ||
            IsWorkspaceModificationLocked() ||
            rowIndex < 0 ||
            rowIndex >= workspaceRows.Count)
        {
            return;
        }

        WorkspaceListRow row = workspaceRows[rowIndex];
        if (!IsGbcDedicatedSaveAllocationSelectable(row))
        {
            return;
        }

        bool usesDedicatedSave = !ResolveWorkspaceRowUsesDedicatedSave(row);
        SetWorkspaceRowDedicatedSaveAllocationOverride(row, usesDedicatedSave);
        RefreshWorkspaceGrid(rowIndex);
        AppendLog(Tf(
            "log",
            "gbc_save_allocation_updated",
            "Save allocation updated: {0} -> {1}",
            GetWorkspaceRowDisplayTitle(row),
            usesDedicatedSave
                ? T("value", "gbc_save_dedicated", "Dedicated")
                : T("value", "gbc_save_shared", "Shared")));
    }

    private void DataGridViewRoms_CellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (isBusy || isAnalyzingQueueImports || IsWorkspaceModificationLocked() || e.RowIndex < 0)
        {
            e.Cancel = true;
            return;
        }

        if (e.ColumnIndex != dataGridViewRoms.Columns["Title"]?.Index)
        {
            e.Cancel = true;
            return;
        }

        if (e.RowIndex < 0 || e.RowIndex >= workspaceRows.Count)
        {
            e.Cancel = true;
            return;
        }

        workspaceTitleEditRowIndex = e.RowIndex;
        workspaceTitleEditOriginalValue = GetWorkspaceRowDisplayTitle(workspaceRows[e.RowIndex]);
    }

    private void DataGridViewRoms_EditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (dataGridViewRoms.CurrentCell?.ColumnIndex != dataGridViewRoms.Columns["Title"]?.Index)
        {
            return;
        }

        if (e.Control is TextBox textBox)
        {
            textBox.MaxLength = IsGbcEnglishMenuTitleEditMode()
                ? 16
                : WaterMenuConfigCodec.TitleCharCapacity;
            textBox.KeyDown -= DataGridViewRoms_TitleEditingControl_KeyDown;
            textBox.KeyDown += DataGridViewRoms_TitleEditingControl_KeyDown;
            BeginInvoke(new Action(textBox.SelectAll));
        }
    }

    private void DataGridViewRoms_TitleEditingControl_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            dataGridViewRoms.EndEdit();
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            dataGridViewRoms.CancelEdit();
        }
    }

    private void DataGridViewRoms_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.ColumnIndex != dataGridViewRoms.Columns["Title"]?.Index ||
            e.RowIndex != workspaceTitleEditRowIndex ||
            e.RowIndex >= workspaceRows.Count)
        {
            workspaceTitleEditRowIndex = -1;
            workspaceTitleEditOriginalValue = string.Empty;
            return;
        }

        WorkspaceListRow row = workspaceRows[e.RowIndex];
        string originalTitle = workspaceTitleEditOriginalValue;
        string editedTitle = Convert.ToString(dataGridViewRoms.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? string.Empty;
        string normalizedTitle = NormalizeEditedWorkspaceTitle(editedTitle, originalTitle);

        workspaceTitleEditRowIndex = -1;
        workspaceTitleEditOriginalValue = string.Empty;

        if (string.Equals(normalizedTitle, originalTitle, StringComparison.Ordinal))
        {
            return;
        }

        if (row.PendingItem is not null)
        {
            row.PendingItem.DisplayTitle = normalizedTitle;
        }
        else
        {
            row.DisplayTitleOverride = normalizedTitle;
        }

        RefreshWorkspaceGrid(e.RowIndex);
        AppendLog(Tf(
            "log",
            "workspace_title_updated",
            "Display title updated: {0}",
            normalizedTitle));
    }

    private bool IsGbcEnglishMenuTitleEditMode() =>
        currentScanResult?.IsBeggarMaxGbcMode == true &&
        GetSelectedGbcMenuLanguageMode() == GbcMenuLanguageMode.English;

    private string NormalizeEditedWorkspaceTitle(string editedTitle, string originalTitle)
    {
        if (!IsGbcEnglishMenuTitleEditMode())
        {
            return string.IsNullOrWhiteSpace(editedTitle)
                ? originalTitle
                : WaterMenuConfigCodec.NormalizeTitle(editedTitle);
        }

        string source = string.IsNullOrWhiteSpace(editedTitle) ? originalTitle : editedTitle;
        StringBuilder normalized = new(16);
        foreach (char ch in source.Trim())
        {
            if (normalized.Length >= 16)
            {
                break;
            }

            normalized.Append(ch is >= ' ' and <= '~' ? ch : '?');
        }

        string result = normalized.ToString().TrimEnd();
        return string.IsNullOrWhiteSpace(result) ? originalTitle : result;
    }

    private static bool IsSupportedRomFile(string path)
    {
        if(Directory.Exists(path))return true;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        string extension = Path.GetExtension(path);
        return extension.Equals(".zip",StringComparison.OrdinalIgnoreCase) || extension.Equals(".nes",StringComparison.OrdinalIgnoreCase) || GbaMultiMenuBuilder.Md.MdCartridgePolicy.IsDiagnosticExtension(extension) ||
               extension.Equals(".gba", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".agb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gbc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bin", StringComparison.OrdinalIgnoreCase);
    }
}
