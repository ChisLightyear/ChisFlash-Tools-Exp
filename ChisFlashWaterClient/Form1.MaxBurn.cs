using System.IO;
using ChisFlashWaterClient.Services;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;
using BuilderDetectedSaveTypeKind = GbaMultiMenuBuilder.Models.DetectedSaveTypeKind;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private const int BeggarMaxReadChunkBytes = 4096;
    private const int BeggarMaxWriteTimeoutMs = 60000;
    private const int BeggarMaxSectorErasePollDelayMs = 20;
    private const int BeggarMaxFixedMapReservedBytes = 1 * 1024 * 1024;
    private const int BeggarMaxConfigOffsetBytes = 0x020000;
    private const int BeggarMaxRomDirectoryOffsetBytes = 0x040000;
    private const int BeggarMaxPayloadStartBank = BeggarMaxFixedMapReservedBytes / GameBoyRomHeaderService.RomBankSize;
    private const int BeggarMaxIndependentSaveBankCapacity = 60;
    private const int BeggarMaxSharedSaveBankStart = 60;
    private const int BeggarMaxSharedSaveBankCount = 4;
    private const int BeggarMaxGameSramBankLimit = 8;

    private sealed class BeggarMaxWorkspaceRowPlan
    {
        public WorkspaceListRow Row { get; init; } = null!;
        public WaterCartEntry? ExistingEntry { get; init; }
        public PendingRomImportItem? PendingItem { get; init; }
        public required string DisplayTitle { get; init; }
        public required string HeaderTitle { get; init; }
        public required string RomPath { get; init; }
        public required long FileLength { get; init; }
        public required int AllocatedBytes { get; set; }
        public required int AllocatedRomBanks { get; set; }
        public required int RequiredRomBanks { get; init; }
        public required int RamBankCount { get; init; }
        public required byte MbcType { get; init; }
        public required bool HasBattery { get; init; }
        public required bool UsesDedicatedSave { get; init; }
        public required bool IsChanged { get; init; }
        public required bool ReuseExistingSaveLayout { get; init; }
        public int FinalRomOffsetBank { get; set; } = -1;
        public byte[] FinalSaveBankMap { get; set; } = Enumerable.Repeat((byte)0xFF, BeggarMaxGameSramBankLimit).ToArray();
        public bool FinalUsesSharedSave { get; set; }
        public int FinalEntrySlot { get; set; } = -1;
    }

    private sealed class BeggarMaxBurnPlan
    {
        public required CartridgeDefinition Cartridge { get; init; }
        public required BuildRequest BuildRequest { get; init; }
        public required List<BeggarMaxWorkspaceRowPlan> RowPlans { get; init; }
        public required byte[] FixedMapRegion { get; init; }
        public required int UsedRomBytes { get; init; }
    }

    private void ValidateBeggarMaxWorkspaceSnapshot(
        IReadOnlyList<WorkspaceListRow> workspaceSnapshot,
        BurnProfileMode burnProfileMode,
        GbcMenuLanguageMode gbcMenuLanguage)
    {
        List<WorkspaceListRow> nonMenuRows = workspaceSnapshot
            .Where(row => !row.IsLoadingPlaceholder && (row.CartEntry is null || !row.CartEntry.IsMenu))
            .ToList();

        if (nonMenuRows.Count == 0)
        {
            throw new InvalidOperationException(T("error", "add_one_rom", "Add at least one ROM before building."));
        }

        if (nonMenuRows.Count > 99)
        {
            throw new InvalidOperationException(Tf(
                "error",
                "gbc_too_many_games",
                "The current MAX/MBCX menu supports at most {0} ROM entries.",
                99));
        }

        List<BeggarMaxWorkspaceRowPlan> plans = AnalyzeBeggarMaxWorkspaceRows(
            workspaceSnapshot,
            burnProfileMode,
            gbcMenuLanguage);
        int totalPayloadBytes = BeggarMaxFixedMapReservedBytes + plans.Sum(plan => plan.AllocatedBytes);
        int dedicatedBankCursor = 0;

        foreach (BeggarMaxWorkspaceRowPlan plan in plans)
        {
            if (plan.UsesDedicatedSave)
            {
                dedicatedBankCursor += plan.RamBankCount;
                if (dedicatedBankCursor > BeggarMaxIndependentSaveBankCapacity)
                {
                    throw new InvalidOperationException(Tf(
                        "error",
                        "gbc_sram_capacity_exceeded",
                        "Battery-backed SRAM allocation exceeds the MAX/MBCX independent save capacity ({0} banks).",
                        BeggarMaxIndependentSaveBankCapacity));
                }
            }
            else if (plan.RamBankCount > BeggarMaxSharedSaveBankCount)
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "gbc_ram_too_large_shared",
                    "ROM requires {0} SRAM banks, but the shared MAX/MBCX save area currently provides at most {1} SRAM banks: {2}",
                    plan.RamBankCount,
                    BeggarMaxSharedSaveBankCount,
                    Path.GetFileName(plan.RomPath)));
            }
        }

        if (currentScanResult is not null &&
            totalPayloadBytes > CompilationBuilder.GetGbcAccessibleFlashSize(ResolveBeggarMaxCartridge(currentScanResult)))
        {
            throw new InvalidOperationException(Tf(
                "error",
                "workspace_capacity_exceeded",
                "The current MAX workspace needs {0}, but the detected flash only provides {1}.",
                FormatMiBSize(totalPayloadBytes),
                FormatMiBSize(currentScanResult.Geometry.DeviceSizeBytes)));
        }
    }

    private WaterCartScanResult ImportBeggarMaxRomFilesOnDevice(
        WaterDeviceProtocol protocol,
        WaterCartScanResult initialScan,
        IReadOnlyList<WorkspaceListRow> workspaceSnapshot,
        BurnProfileMode burnProfileMode,
        string? gbcFontPath,
        string? gbcBackgroundPath,
        GbcMenuLanguageMode gbcMenuLanguage,
        bool clearConfigArea = false,
        bool allowEmptyWorkspace = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!clearConfigArea && !allowEmptyWorkspace)
        {
            ValidateBeggarMaxWorkspaceSnapshot(workspaceSnapshot, burnProfileMode, gbcMenuLanguage);
        }
        cancellationToken.ThrowIfCancellationRequested();
        BeginIndeterminateProgress(
            T("ui", "queue_burn", "Start Burn"),
            T("value", "gbc_progress_preparing", "Preparing GBC/MAX burn..."));
        BeggarMaxBurnPlan burnPlan = BuildBeggarMaxBurnPlan(
            protocol,
            initialScan,
            workspaceSnapshot,
            burnProfileMode,
            gbcFontPath,
            gbcBackgroundPath,
            gbcMenuLanguage,
            clearConfigArea,
            allowEmptyWorkspace,
            cancellationToken);

        ProgramBeggarMaxBurnPlan(
            protocol,
            initialScan,
            burnPlan,
            forceFullFixedMapRewrite: clearConfigArea,
            cancellationToken: cancellationToken);

        WaterCartScanResult rebuilt = WaterCartScanner.TryScanBeggarMaxGbc(protocol, AppendLog, out WaterCartScanResult liveScan)
            ? liveScan
            : WaterCartScanner.RebuildCachedScanResult(
                initialScan,
                gamesOverride: BuildBeggarMaxPlannedGames(burnPlan),
                log: AppendLog);
        rebuilt.IsBeggarMaxGbcMode = true;
        if (clearConfigArea)
        {
            rebuilt.HasBeggarMaxDirectoryHeaders = true;
            rebuilt.Games.Clear();
            rebuilt.Entries.Clear();
        }
        rebuilt.NorId = initialScan.NorId.ToArray();
        rebuilt.RawNorIdHex = initialScan.RawNorIdHex;
        rebuilt.ChipName = initialScan.ChipName;
        rebuilt.CapacitySummary = initialScan.CapacitySummary;
        rebuilt.SectorSummary = initialScan.SectorSummary;
        rebuilt.Geometry = initialScan.Geometry;
        PopulateSavePresenceForScan(protocol, rebuilt, ProgrammerKind.BeggarLegacy);
        return rebuilt;
    }

    private WaterCartScanResult RewriteEmptyBeggarMaxMenuOnDevice(
        string portName,
        WaterCartScanResult initialScan,
        BurnProfileMode burnProfileMode,
        string? gbcFontPath,
        string? gbcBackgroundPath,
        GbcMenuLanguageMode gbcMenuLanguage,
        bool clearConfigArea,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
        return ImportBeggarMaxRomFilesOnDevice(
            protocol,
            initialScan,
            Array.Empty<WorkspaceListRow>(),
            burnProfileMode,
            gbcFontPath,
            gbcBackgroundPath,
            gbcMenuLanguage,
            clearConfigArea: clearConfigArea,
            allowEmptyWorkspace: true,
            cancellationToken: cancellationToken);
    }

    private BeggarMaxBurnPlan BuildBeggarMaxBurnPlan(
        WaterDeviceProtocol protocol,
        WaterCartScanResult initialScan,
        IReadOnlyList<WorkspaceListRow> workspaceSnapshot,
        BurnProfileMode burnProfileMode,
        string? gbcFontPath,
        string? gbcBackgroundPath,
        GbcMenuLanguageMode gbcMenuLanguage,
        bool clearConfigArea = false,
        bool allowEmptyWorkspace = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool buildEmptyMenu = clearConfigArea ||
            (allowEmptyWorkspace && workspaceSnapshot.All(row => row.CartEntry?.IsMenu == true));
        List<BeggarMaxWorkspaceRowPlan> rowPlans = buildEmptyMenu
            ? []
            : AnalyzeBeggarMaxWorkspaceRows(
                workspaceSnapshot,
                burnProfileMode,
                gbcMenuLanguage);
        cancellationToken.ThrowIfCancellationRequested();
        AllocateBeggarMaxRomBanks(rowPlans);
        AllocateBeggarMaxSaveBanks(rowPlans);

        for (int i = 0; i < rowPlans.Count; i++)
        {
            rowPlans[i].FinalEntrySlot = i;
        }

        CartridgeDefinition cartridge = ResolveBeggarMaxCartridge(initialScan);
        string baseMenuPath = ResolveBeggarMaxBaseMenuPath(gbcMenuLanguage);
        BuildRequest request = new(
            baseMenuPath,
            Path.Combine(Path.GetTempPath(), "unused_max_fixed_map.gbc"),
            gbcBackgroundPath,
            gbcFontPath,
            cartridge,
            GameBoyRomHeaderService.RomBankSize,
            FlashCartMode.GbcMaxMbcx512K,
            GbaMenuType.Classic,
            true,
            false,
            rowPlans.Select(plan => new BuildGameEntry(
                true,
                plan.DisplayTitle,
                plan.RomPath,
                null,
                0,
                false,
                false,
                false,
                false,
                false,
                BuilderDetectedSaveTypeKind.None,
                plan.UsesDedicatedSave,
                false,
                1)).ToList(),
            Family: CompilationFamily.Gbc);

        List<CompilationBuilder.GbcPreparedEntry> preparedEntries = rowPlans
            .Select((plan, index) => CompilationBuilder.CreateGbcPreparedEntry(
                index,
                index,
                plan.DisplayTitle,
                plan.HeaderTitle,
                plan.RomPath,
                plan.FileLength,
                plan.AllocatedBytes,
                plan.AllocatedRomBanks,
                plan.RamBankCount,
                plan.MbcType,
                plan.HasBattery,
                plan.FinalRomOffsetBank,
                plan.FinalSaveBankMap))
            .ToList();

        int usedRomBytes = rowPlans.Count == 0
            ? BeggarMaxFixedMapReservedBytes
            : rowPlans.Max(plan => (plan.FinalRomOffsetBank * GameBoyRomHeaderService.RomBankSize) + plan.AllocatedBytes);

        CompilationBuilder builder = new();
        byte[] fixedMapRegion;
        if (initialScan.HasBeggarMaxDirectoryHeaders && !clearConfigArea)
        {
            byte[] existingFixedMapRegion = ReadBeggarMaxFixedMapRegionFromDevice(protocol, initialScan, cancellationToken);
            fixedMapRegion = builder.UpdateExistingGbcFixedMapRegion(
                existingFixedMapRegion,
                request,
                preparedEntries,
                usedRomBytes,
                preserveMenuRuntime: false);
        }
        else
        {
            fixedMapRegion = builder.BuildGbcFixedMapRegion(request, preparedEntries, usedRomBytes);
        }

        return new BeggarMaxBurnPlan
        {
            Cartridge = cartridge,
            BuildRequest = request,
            RowPlans = rowPlans,
            FixedMapRegion = fixedMapRegion,
            UsedRomBytes = usedRomBytes,
        };
    }

    private List<BeggarMaxWorkspaceRowPlan> AnalyzeBeggarMaxWorkspaceRows(
        IReadOnlyList<WorkspaceListRow> workspaceSnapshot,
        BurnProfileMode burnProfileMode,
        GbcMenuLanguageMode gbcMenuLanguage)
    {
        List<BeggarMaxWorkspaceRowPlan> plans = [];
        foreach (WorkspaceListRow row in workspaceSnapshot)
        {
            if (row.CartEntry?.IsMenu ?? false)
            {
                continue;
            }

            string displayTitle = WaterMenuConfigCodec.NormalizeTitle(GetWorkspaceRowDisplayTitle(
                row,
                isBeggarMaxGbcMode: true,
                gbcMenuLanguage));
            if (row.PendingItem is not null)
            {
                PendingRomImportItem pending = row.PendingItem;
                GameBoyRomMetadata metadata = GameBoyRomHeaderService.Read(pending.FilePath);
                int allocatedBytes = GetPendingAllocatedSizeBytes(pending, burnProfileMode);
                bool usesDedicatedSave = ShouldUseDedicatedBeggarMaxSave(metadata, row);
                plans.Add(new BeggarMaxWorkspaceRowPlan
                {
                    Row = row,
                    ExistingEntry = row.CartEntry,
                    PendingItem = pending,
                    DisplayTitle = displayTitle,
                    HeaderTitle = string.IsNullOrWhiteSpace(pending.HeaderTitle) ? displayTitle : pending.HeaderTitle,
                    RomPath = pending.FilePath,
                    FileLength = Math.Max(1L, pending.FileSizeBytes),
                    AllocatedBytes = allocatedBytes,
                    AllocatedRomBanks = allocatedBytes / GameBoyRomHeaderService.RomBankSize,
                    RequiredRomBanks = Math.Max(2, metadata.EffectiveRomBankCount),
                    RamBankCount = metadata.EffectiveRamBankCount,
                    MbcType = metadata.CartridgeType,
                    HasBattery = metadata.HasBattery,
                    UsesDedicatedSave = usesDedicatedSave,
                    IsChanged = true,
                    ReuseExistingSaveLayout = row.CartEntry is not null &&
                                              row.CartEntry.MbcType == metadata.CartridgeType &&
                                              row.CartEntry.RamBankCount == metadata.EffectiveRamBankCount &&
                                              row.CartEntry.HasBattery == metadata.HasBattery,
                });
                continue;
            }

            if (row.CartEntry is null)
            {
                continue;
            }

            WaterCartEntry entry = row.CartEntry;
            if (entry.RomOffsetBank < 0 || entry.RomBankCount <= 0)
            {
                continue;
            }

            plans.Add(new BeggarMaxWorkspaceRowPlan
            {
                Row = row,
                ExistingEntry = entry,
                PendingItem = null,
                DisplayTitle = displayTitle,
                HeaderTitle = string.IsNullOrWhiteSpace(entry.HeaderTitle) ? entry.Title : entry.HeaderTitle,
                RomPath = string.Empty,
                FileLength = 0,
                AllocatedBytes = Math.Max(GameBoyRomHeaderService.RomBankSize * 2, entry.RomBankCount * GameBoyRomHeaderService.RomBankSize),
                AllocatedRomBanks = Math.Max(2, entry.RomBankCount),
                RequiredRomBanks = Math.Max(2, entry.RomBankCount),
                RamBankCount = entry.RamBankCount,
                MbcType = entry.MbcType,
                HasBattery = entry.HasBattery,
                UsesDedicatedSave = ResolveWorkspaceRowUsesDedicatedSave(row),
                IsChanged = false,
                ReuseExistingSaveLayout = true,
            });
        }

        if (plans.Count == 0)
        {
            throw new InvalidOperationException(T("error", "add_one_rom", "Add at least one ROM before building."));
        }

        return plans;
    }

    private void AllocateBeggarMaxRomBanks(List<BeggarMaxWorkspaceRowPlan> rowPlans)
    {
        List<(int Start, int End)> freeRanges = BuildBeggarMaxFreeRomRanges(rowPlans);

        foreach (BeggarMaxWorkspaceRowPlan plan in rowPlans)
        {
            if (plan.ExistingEntry is not null &&
                !plan.IsChanged)
            {
                plan.FinalRomOffsetBank = plan.ExistingEntry.RomOffsetBank;
                continue;
            }

            if (plan.ExistingEntry is not null &&
                plan.ExistingEntry.RomBankCount >= plan.RequiredRomBanks)
            {
                plan.FinalRomOffsetBank = plan.ExistingEntry.RomOffsetBank;
                plan.AllocatedRomBanks = plan.ExistingEntry.RomBankCount;
                plan.AllocatedBytes = plan.AllocatedRomBanks * GameBoyRomHeaderService.RomBankSize;
                continue;
            }

            int rangeIndex = freeRanges.FindIndex(range => (range.End - range.Start + 1) >= plan.AllocatedRomBanks);
            if (rangeIndex < 0)
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "workspace_capacity_exceeded",
                    "The current MAX workspace needs {0}, but the detected flash only provides {1}.",
                    FormatMiBSize(BeggarMaxFixedMapReservedBytes + rowPlans.Sum(item => item.AllocatedBytes)),
                    currentScanResult is not null ? FormatMiBSize(currentScanResult.Geometry.DeviceSizeBytes) : "?"));
            }

            (int Start, int End) range = freeRanges[rangeIndex];
            plan.FinalRomOffsetBank = range.Start;
            int newStart = range.Start + plan.AllocatedRomBanks;
            if (newStart > range.End)
            {
                freeRanges.RemoveAt(rangeIndex);
            }
            else
            {
                freeRanges[rangeIndex] = (newStart, range.End);
            }
        }
    }

    private List<(int Start, int End)> BuildBeggarMaxFreeRomRanges(List<BeggarMaxWorkspaceRowPlan> rowPlans)
    {
        int accessibleBankCount = CompilationBuilder.GetGbcAccessibleFlashSize(
            ResolveBeggarMaxCartridge(currentScanResult ?? throw new InvalidOperationException("No MAX scan result.")))
            / GameBoyRomHeaderService.RomBankSize;
        List<(int Start, int End)> ranges = [(BeggarMaxPayloadStartBank, accessibleBankCount - 1)];

        foreach (BeggarMaxWorkspaceRowPlan plan in rowPlans)
        {
            if (plan.ExistingEntry is null)
            {
                continue;
            }

            if (plan.IsChanged && plan.ExistingEntry.RomBankCount < plan.RequiredRomBanks)
            {
                continue;
            }

            RemoveBeggarMaxRange(ranges, plan.ExistingEntry.RomOffsetBank, plan.ExistingEntry.RomOffsetBank + plan.ExistingEntry.RomBankCount - 1);
        }

        return ranges;
    }

    private static void RemoveBeggarMaxRange(List<(int Start, int End)> ranges, int removeStart, int removeEnd)
    {
        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            (int Start, int End) range = ranges[i];
            if (removeEnd < range.Start || removeStart > range.End)
            {
                continue;
            }

            if (removeStart <= range.Start && removeEnd >= range.End)
            {
                ranges.RemoveAt(i);
                continue;
            }

            if (removeStart > range.Start && removeEnd < range.End)
            {
                ranges[i] = (range.Start, removeStart - 1);
                ranges.Insert(i + 1, (removeEnd + 1, range.End));
                continue;
            }

            if (removeStart <= range.Start)
            {
                ranges[i] = (removeEnd + 1, range.End);
            }
            else
            {
                ranges[i] = (range.Start, removeStart - 1);
            }
        }
    }

    private void AllocateBeggarMaxSaveBanks(List<BeggarMaxWorkspaceRowPlan> rowPlans)
    {
        bool[] usedBanks = new bool[BeggarMaxIndependentSaveBankCapacity];
        foreach (BeggarMaxWorkspaceRowPlan plan in rowPlans.Where(plan => !plan.IsChanged && plan.ExistingEntry is not null))
        {
            if (plan.RamBankCount <= 0)
            {
                plan.FinalSaveBankMap = Enumerable.Repeat((byte)0xFF, BeggarMaxGameSramBankLimit).ToArray();
                plan.FinalUsesSharedSave = false;
                continue;
            }

            if (!plan.UsesDedicatedSave)
            {
                plan.FinalSaveBankMap = BuildBeggarMaxSharedSaveMap(plan.RamBankCount);
                plan.FinalUsesSharedSave = true;
                continue;
            }

            byte[] preservedMap = NormalizeBeggarMaxSaveMap(plan.ExistingEntry!.SaveBankMap, plan.RamBankCount);
            if (!ValidateBeggarMaxDedicatedSaveMap(preservedMap, plan.RamBankCount, usedBanks, out string validationError))
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "gbc_sram_existing_map_invalid",
                    "Existing SRAM map for \"{0}\" is invalid and cannot be preserved: {1}",
                    plan.DisplayTitle,
                    validationError));
            }

            plan.FinalSaveBankMap = preservedMap;
            plan.FinalUsesSharedSave = false;
            MarkBeggarMaxDedicatedSaveMapUsed(preservedMap, plan.RamBankCount, usedBanks);
        }

        foreach (BeggarMaxWorkspaceRowPlan plan in rowPlans.Where(plan => plan.IsChanged || plan.ExistingEntry is null))
        {
            if (plan.RamBankCount <= 0)
            {
                plan.FinalSaveBankMap = Enumerable.Repeat((byte)0xFF, BeggarMaxGameSramBankLimit).ToArray();
                plan.FinalUsesSharedSave = false;
                continue;
            }

            if (plan.UsesDedicatedSave &&
                plan.ReuseExistingSaveLayout &&
                plan.ExistingEntry is not null &&
                plan.ExistingEntry.SaveBankMap.Count >= plan.RamBankCount)
            {
                byte[] preservedMap = NormalizeBeggarMaxSaveMap(plan.ExistingEntry.SaveBankMap, plan.RamBankCount);
                if (ValidateBeggarMaxDedicatedSaveMap(preservedMap, plan.RamBankCount, usedBanks, out _))
                {
                    plan.FinalSaveBankMap = preservedMap;
                    plan.FinalUsesSharedSave = false;
                    MarkBeggarMaxDedicatedSaveMapUsed(preservedMap, plan.RamBankCount, usedBanks);
                    continue;
                }
            }

            if (!plan.UsesDedicatedSave)
            {
                plan.FinalSaveBankMap = BuildBeggarMaxSharedSaveMap(plan.RamBankCount);
                plan.FinalUsesSharedSave = true;
                continue;
            }

            byte[] dedicatedMap = Enumerable.Repeat((byte)0xFF, BeggarMaxGameSramBankLimit).ToArray();
            List<int> freeBanks = Enumerable.Range(0, BeggarMaxIndependentSaveBankCapacity)
                .Where(index => !usedBanks[index])
                .Take(plan.RamBankCount)
                .ToList();
            if (freeBanks.Count != plan.RamBankCount)
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "gbc_sram_capacity_exceeded",
                    "Battery-backed SRAM allocation exceeds the MAX/MBCX independent save capacity ({0} banks).",
                    BeggarMaxIndependentSaveBankCapacity));
            }

            for (int i = 0; i < freeBanks.Count; i++)
            {
                dedicatedMap[i] = (byte)freeBanks[i];
                usedBanks[freeBanks[i]] = true;
            }

            plan.FinalSaveBankMap = dedicatedMap;
            plan.FinalUsesSharedSave = false;
        }
    }

    private static byte[] NormalizeBeggarMaxSaveMap(IReadOnlyList<byte> source, int ramBankCount)
    {
        byte[] result = Enumerable.Repeat((byte)0xFF, BeggarMaxGameSramBankLimit).ToArray();
        for (int i = 0; i < result.Length && i < source.Count && i < Math.Max(1, ramBankCount); i++)
        {
            result[i] = source[i];
        }
        return result;
    }

    private static bool ValidateBeggarMaxDedicatedSaveMap(
        IReadOnlyList<byte> map,
        int ramBankCount,
        IReadOnlyList<bool> usedBanks,
        out string error)
    {
        HashSet<byte> localBanks = [];
        for (int i = 0; i < ramBankCount; i++)
        {
            if (i >= map.Count)
            {
                error = $"missing bank at slot {i}";
                return false;
            }

            byte bank = map[i];
            if (bank >= BeggarMaxIndependentSaveBankCapacity)
            {
                error = $"bank {bank} at slot {i} is outside independent range 0..{BeggarMaxIndependentSaveBankCapacity - 1}";
                return false;
            }

            if (!localBanks.Add(bank))
            {
                error = $"bank {bank} is duplicated in the same game";
                return false;
            }

            if (bank < usedBanks.Count && usedBanks[bank])
            {
                error = $"bank {bank} is already used by another battery-backed game";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static void MarkBeggarMaxDedicatedSaveMapUsed(IReadOnlyList<byte> map, int ramBankCount, bool[] usedBanks)
    {
        for (int i = 0; i < ramBankCount && i < map.Count; i++)
        {
            byte bank = map[i];
            if (bank < usedBanks.Length)
            {
                usedBanks[bank] = true;
            }
        }
    }

    private static byte[] BuildBeggarMaxSharedSaveMap(int ramBankCount)
    {
        byte[] result = Enumerable.Repeat((byte)0xFF, BeggarMaxGameSramBankLimit).ToArray();
        for (int i = 0; i < ramBankCount && i < BeggarMaxSharedSaveBankCount && i < result.Length; i++)
        {
            result[i] = (byte)(BeggarMaxSharedSaveBankStart + i);
        }
        return result;
    }

    private List<DetectedNorGameInfo> BuildBeggarMaxPlannedGames(BeggarMaxBurnPlan burnPlan)
    {
        return burnPlan.RowPlans
            .Select((plan, index) => new DetectedNorGameInfo
            {
                StartBucket = index,
                EndBucket = index,
                Title = plan.HeaderTitle,
                IsMenu = false,
            })
            .ToList();
    }

    private void ProgramBeggarMaxBurnPlan(
        WaterDeviceProtocol protocol,
        WaterCartScanResult scanResult,
        BeggarMaxBurnPlan burnPlan,
        bool forceFullFixedMapRewrite = false,
        CancellationToken cancellationToken = default)
    {
        var fcConfig=ReadBeggarMaxSectorImage(protocol,0x20000,64,cancellationToken);
        if(fcConfig.AsSpan(0,4).SequenceEqual("MD02"u8))
            throw new InvalidOperationException("MD data card: use the MD cartridge writer instead of the GB menu writer.");
        if(GbaMultiMenuBuilder.Fc.FcMbcxCodec.HasFcIdentity(fcConfig))
            throw new InvalidOperationException("FC data card: use the FC workspace instead of the GB menu writer.");
        cancellationToken.ThrowIfCancellationRequested();
        UpdateIndeterminateProgress(
            T("value", "gbc_progress_reading_existing", "Reading existing GBC/MAX flash content..."));
        byte[]? preDeleteFixedMapRegion = BuildBeggarMaxPreWriteFixedMapRegion(protocol, scanResult, burnPlan, cancellationToken);
        Dictionary<int, byte[]> preDeleteFixedMapSectors = preDeleteFixedMapRegion is not null
            ? BuildBeggarMaxFixedMapSectorRewriteMap(protocol, scanResult, preDeleteFixedMapRegion, cancellationToken: cancellationToken)
            : [];
        UpdateIndeterminateProgress(
            T("value", "gbc_progress_preparing_payload", "Preparing GBC/MAX ROM sectors..."));
        Dictionary<int, byte[]> payloadSectors = BuildBeggarMaxPayloadSectorRewriteMap(protocol, scanResult, burnPlan, cancellationToken);
        UpdateIndeterminateProgress(
            T("value", "gbc_progress_preparing_menu", "Preparing GBC/MAX menu sectors..."));
        Dictionary<int, byte[]> finalFixedMapSectors = BuildBeggarMaxFixedMapSectorRewriteMap(
            protocol,
            scanResult,
            burnPlan.FixedMapRegion,
            forceAll: forceFullFixedMapRewrite || preDeleteFixedMapSectors.Count > 0,
            cancellationToken: cancellationToken);
        long totalBytes =
            preDeleteFixedMapSectors.Values.Sum(sector => CountRewriteSectorProgressWorkBytes(sector, scanResult.Geometry)) +
            payloadSectors.Values.Sum(sector => CountRewriteSectorProgressWorkBytes(sector, scanResult.Geometry)) +
            finalFixedMapSectors.Values.Sum(sector => CountRewriteSectorProgressWorkBytes(sector, scanResult.Geometry));
        int totalUnits =
            preDeleteFixedMapSectors.Values.Sum(CountRewriteSectorProgressUnits) +
            payloadSectors.Values.Sum(CountRewriteSectorProgressUnits) +
            finalFixedMapSectors.Values.Sum(CountRewriteSectorProgressUnits);

        foreach (BeggarMaxWorkspaceRowPlan plan in burnPlan.RowPlans.Where(plan => plan.IsChanged))
        {
            AppendLog(Tf(
                "log",
                "placed_rom",
                "Placed: {0}",
                plan.DisplayTitle));
        }

        BeginMeasuredProgress(T("ui", "queue_burn", "Start Burn"), totalBytes, totalUnits);
        try
        {
            if (preDeleteFixedMapSectors.Count > 0)
            {
                AppendLog(T(
                    "log",
                    "predelete_rom_mappings",
                    "Pre-delete queued ROM mappings before erase/write. If the burn is interrupted, the menu will not point at partially written ROM data."));
                foreach ((int sectorAddress, byte[] sectorData) in preDeleteFixedMapSectors.OrderBy(pair => pair.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RewriteBeggarMaxSector(protocol, sectorAddress, sectorData, scanResult.Geometry, "MAX/MBCX pre-delete map", cancellationToken);
                }
            }

            if (payloadSectors.Count > 0)
            {
                EraseBeggarMaxSectorBatch(
                    protocol,
                    payloadSectors,
                    scanResult.Geometry,
                    "MAX/MBCX ROM",
                    cancellationToken);

                AppendLog(T(
                    "log",
                    "beggar_max_rom_erase_complete",
                    "All continuous MAX/MBCX ROM ranges have been erased. Start programming ROM data."));
                foreach ((int sectorAddress, byte[] sectorData) in payloadSectors.OrderBy(pair => pair.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProgramBeggarMaxSector(
                        protocol,
                        sectorAddress,
                        sectorData,
                        scanResult.Geometry,
                        "MAX/MBCX ROM",
                        cancellationToken);
                }
            }

            if (forceFullFixedMapRewrite)
            {
                EraseBeggarMaxSectorBatch(
                    protocol,
                    finalFixedMapSectors,
                    scanResult.Geometry,
                    "MAX/MBCX fresh menu",
                    cancellationToken);
            }

            foreach ((int sectorAddress, byte[] sectorData) in finalFixedMapSectors.OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (forceFullFixedMapRewrite)
                {
                    ProgramBeggarMaxSector(
                        protocol,
                        sectorAddress,
                        sectorData,
                        scanResult.Geometry,
                        "MAX/MBCX fresh menu",
                        cancellationToken);
                }
                else
                {
                    RewriteBeggarMaxSector(protocol, sectorAddress, sectorData, scanResult.Geometry, "MAX/MBCX final map", cancellationToken);
                }
            }

            CompleteMeasuredProgress(T("ui", "queue_burn", "Start Burn"));
        }
        catch
        {
            AbortMeasuredProgress();
            throw;
        }
    }

    private byte[]? BuildBeggarMaxPreWriteFixedMapRegion(
        WaterDeviceProtocol protocol,
        WaterCartScanResult scanResult,
        BeggarMaxBurnPlan burnPlan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!scanResult.HasBeggarMaxDirectoryHeaders || burnPlan.RowPlans.All(plan => !plan.IsChanged))
        {
            return null;
        }

        byte[] existingFixedMapRegion = ReadBeggarMaxFixedMapRegionFromDevice(protocol, scanResult, cancellationToken);
        List<BeggarMaxWorkspaceRowPlan> retainedPlans = burnPlan.RowPlans
            .Where(plan => !plan.IsChanged && plan.ExistingEntry is not null)
            .ToList();
        if (retainedPlans.Count == 0)
        {
            byte[] fixedMapRegion = existingFixedMapRegion.ToArray();
            fixedMapRegion.AsSpan(BeggarMaxConfigOffsetBytes, BeggarMaxFixedMapReservedBytes - BeggarMaxConfigOffsetBytes).Fill(0xFF);
            byte[] configHeader = CompilationBuilder.BuildGbcConfigHeader(burnPlan.BuildRequest, 0);
            byte[] directoryHeader = CompilationBuilder.BuildGbcDirectoryHeader(0);
            Buffer.BlockCopy(configHeader, 0, fixedMapRegion, BeggarMaxConfigOffsetBytes, configHeader.Length);
            Buffer.BlockCopy(directoryHeader, 0, fixedMapRegion, BeggarMaxRomDirectoryOffsetBytes, directoryHeader.Length);
            return fixedMapRegion;
        }

        CompilationBuilder builder = new();
        return builder.UpdateExistingGbcFixedMapRegion(
            existingFixedMapRegion,
            burnPlan.BuildRequest,
            BuildBeggarMaxPreparedEntries(retainedPlans),
            burnPlan.UsedRomBytes,
            preserveMenuRuntime: true);
    }

    private Dictionary<int, byte[]> BuildBeggarMaxFixedMapSectorRewriteMap(
        WaterDeviceProtocol protocol,
        WaterCartScanResult scanResult,
        byte[] fixedMapRegion,
        bool forceAll = false,
        CancellationToken cancellationToken = default)
    {
        int sectorSize = scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : 0x20000;
        Dictionary<int, byte[]> sectorMap = [];

        for (int offset = 0; offset < fixedMapRegion.Length; offset += sectorSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int writeLength = Math.Min(sectorSize, fixedMapRegion.Length - offset);
            byte[] sectorData = fixedMapRegion.AsSpan(offset, writeLength).ToArray();
            if (sectorData.Length != sectorSize)
            {
                Array.Resize(ref sectorData, sectorSize);
                Array.Fill(sectorData, (byte)0xFF, writeLength, sectorData.Length - writeLength);
            }

            if (forceAll)
            {
                sectorMap[offset] = sectorData;
                continue;
            }

            byte[] existingSectorData = ReadBeggarMaxSectorImage(protocol, offset, sectorSize, cancellationToken);
            if (!existingSectorData.SequenceEqual(sectorData))
            {
                sectorMap[offset] = sectorData;
            }
        }

        return sectorMap;
    }

    private Dictionary<int, byte[]> BuildBeggarMaxPayloadSectorRewriteMap(
        WaterDeviceProtocol protocol,
        WaterCartScanResult scanResult,
        BeggarMaxBurnPlan burnPlan,
        CancellationToken cancellationToken = default)
    {
        int sectorSize = scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : 0x20000;
        Dictionary<int, byte[]> sectorMap = [];

        foreach (BeggarMaxWorkspaceRowPlan plan in burnPlan.RowPlans.Where(plan => plan.IsChanged))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] payload = BuildBeggarMaxRomPayload(plan, cancellationToken);
            int payloadOffset = 0;
            int absoluteBaseAddress = plan.FinalRomOffsetBank * GameBoyRomHeaderService.RomBankSize;
            while (payloadOffset < payload.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int absoluteAddress = absoluteBaseAddress + payloadOffset;
                int sectorAddress = (absoluteAddress / sectorSize) * sectorSize;
                int sectorOffset = absoluteAddress - sectorAddress;
                int copyLength = Math.Min(payload.Length - payloadOffset, sectorSize - sectorOffset);

                if (!sectorMap.TryGetValue(sectorAddress, out byte[]? sectorData))
                {
                    sectorData = ReadBeggarMaxSectorImage(protocol, sectorAddress, sectorSize, cancellationToken);
                    sectorMap[sectorAddress] = sectorData;
                }

                Buffer.BlockCopy(payload, payloadOffset, sectorData, sectorOffset, copyLength);
                payloadOffset += copyLength;
            }
        }

        if (sectorMap.Count > 1)
        {
            int firstSectorAddress = sectorMap.Keys.Min();
            int lastSectorAddress = sectorMap.Keys.Max();
            for (int sectorAddress = firstSectorAddress; sectorAddress <= lastSectorAddress; sectorAddress += sectorSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sectorMap.ContainsKey(sectorAddress))
                {
                    sectorMap[sectorAddress] = ReadBeggarMaxSectorImage(
                        protocol,
                        sectorAddress,
                        sectorSize,
                        cancellationToken);
                }
            }
        }

        return sectorMap;
    }

    private static List<CompilationBuilder.GbcPreparedEntry> BuildBeggarMaxPreparedEntries(IReadOnlyList<BeggarMaxWorkspaceRowPlan> rowPlans) =>
        rowPlans
            .Select((plan, index) => CompilationBuilder.CreateGbcPreparedEntry(
                index,
                index,
                plan.DisplayTitle,
                plan.HeaderTitle,
                plan.RomPath,
                plan.FileLength,
                plan.AllocatedBytes,
                plan.AllocatedRomBanks,
                plan.RamBankCount,
                plan.MbcType,
                plan.HasBattery,
                plan.FinalRomOffsetBank,
                plan.FinalSaveBankMap))
            .ToList();

    private static byte[] BuildBeggarMaxRomPayload(
        BeggarMaxWorkspaceRowPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] romBytes = File.ReadAllBytes(plan.RomPath);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] payload = Enumerable.Repeat((byte)0xFF, plan.AllocatedBytes).ToArray();
        Buffer.BlockCopy(romBytes, 0, payload, 0, Math.Min(romBytes.Length, payload.Length));
        return payload;
    }

    private byte[] ReadBeggarMaxFixedMapRegionFromDevice(
        WaterDeviceProtocol protocol,
        WaterCartScanResult scanResult,
        CancellationToken cancellationToken = default)
    {
        int sectorSize = scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : 0x20000;
        byte[] fixedMap = new byte[BeggarMaxFixedMapReservedBytes];

        for (int offset = 0; offset < fixedMap.Length; offset += sectorSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int readLength = Math.Min(sectorSize, fixedMap.Length - offset);
            byte[] sectorImage = ReadBeggarMaxSectorImage(protocol, offset, sectorSize, cancellationToken);
            Buffer.BlockCopy(sectorImage, 0, fixedMap, offset, readLength);
        }

        return fixedMap;
    }

    private byte[] ReadBeggarMaxSectorImage(
        WaterDeviceProtocol protocol,
        int sectorAddress,
        int sectorSize,
        CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[sectorSize];
        int offset = 0;
        while (offset < sectorSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentAddress = sectorAddress + offset;
            int bank = currentAddress >> 14;
            int bankOffset = currentAddress & 0x3FFF;
            int readLength = Math.Min(BeggarMaxReadChunkBytes, sectorSize - offset);
            readLength = Math.Min(readLength, 0x4000 - bankOffset);
            if (!TrySwitchBeggarMaxRomBank(protocol, bank))
            {
                AppendLog(Tf(
                    "log",
                    "beggar_max_read_sector_failed_detail",
                    "MAX sector read failed before data read: sector=0x{0:X6} bank={1} bankOffset=0x{2:X4} op=switch-bank ack={3}",
                    sectorAddress,
                    bank,
                    bankOffset,
                    protocol.DescribeLastAckFailure()));
                throw new InvalidOperationException(Tf(
                    "error",
                    "beggar_max_read_sector_failed",
                    "Failed to read the existing MAX flash sector image. sector=0x{0:X6} bank={1} bankOffset=0x{2:X4} op=switch-bank",
                    sectorAddress,
                    bank,
                    bankOffset));
            }

            byte[] chunk = new byte[readLength];
            int cartAddress = GetBeggarMaxBankBaseAddress(bank) + bankOffset;
            if (!protocol.TryReadGbc((uint)cartAddress, chunk, 3000))
            {
                AppendLog(Tf(
                    "log",
                    "beggar_max_read_sector_failed_detail",
                    "MAX sector read failed: sector=0x{0:X6} current=0x{1:X6} bank={2} bankOffset=0x{3:X4} cart=0x{4:X4} len={5}",
                    sectorAddress,
                    currentAddress,
                    bank,
                    bankOffset,
                    cartAddress,
                    readLength));
                throw new InvalidOperationException(Tf(
                    "error",
                    "beggar_max_read_sector_failed",
                    "Failed to read the existing MAX flash sector image. sector=0x{0:X6} current=0x{1:X6} bank={2} bankOffset=0x{3:X4} cart=0x{4:X4} len={5}",
                    sectorAddress,
                    currentAddress,
                    bank,
                    bankOffset,
                    cartAddress,
                    readLength));
            }

            Buffer.BlockCopy(chunk, 0, buffer, offset, readLength);
            offset += readLength;
        }

        return buffer;
    }

    private void RewriteBeggarMaxSector(
        WaterDeviceProtocol protocol,
        int sectorAddress,
        byte[] sectorData,
        NorGeometryInfo geometry,
        string label,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : 0x20000;
        if (sectorData.Length != sectorSize)
        {
            throw new InvalidOperationException(Tf("error", "sector_data_size_invalid", "{0} data size is invalid.", label));
        }

        AppendProgressLog("beggar-max-erase", Tf("log", "erase_sector", "Erase {0}: 0x{1:X6}", label, sectorAddress));
        EraseBeggarMaxRange(protocol, sectorAddress, sectorAddress + sectorSize - 1, sectorSize, cancellationToken);

        AdvanceMeasuredProgress(
            sectorSize,
            1,
            BuildProgressDetailText(label, "progress_phase_erase", "Erase"));
        cancellationToken.ThrowIfCancellationRequested();

        ProgramBeggarMaxSector(protocol, sectorAddress, sectorData, geometry, label, cancellationToken);
    }

    private void EraseBeggarMaxSectorBatch(
        WaterDeviceProtocol protocol,
        IReadOnlyDictionary<int, byte[]> sectorMap,
        NorGeometryInfo geometry,
        string label,
        CancellationToken cancellationToken = default)
    {
        if (sectorMap.Count == 0)
        {
            return;
        }

        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : 0x20000;
        int[] sectorAddresses = sectorMap.Keys.Order().ToArray();
        List<(int Start, int End)> ranges = [];
        int rangeStart = sectorAddresses[0];
        int rangeEnd = rangeStart;

        foreach (int sectorAddress in sectorAddresses)
        {
            if (sectorAddress < 0 || sectorAddress % sectorSize != 0)
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "sector_address_invalid",
                    "{0} sector address 0x{1:X6} is invalid.",
                    label,
                    sectorAddress));
            }

            if (sectorMap[sectorAddress].Length != sectorSize)
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "sector_data_size_invalid",
                    "{0} data size is invalid.",
                    label));
            }

            if (sectorAddress == rangeStart)
            {
                continue;
            }

            if (sectorAddress == rangeEnd + sectorSize)
            {
                rangeEnd = sectorAddress;
                continue;
            }

            ranges.Add((rangeStart, rangeEnd));
            rangeStart = sectorAddress;
            rangeEnd = sectorAddress;
        }
        ranges.Add((rangeStart, rangeEnd));

        foreach ((int start, int end) in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sectorCount = ((end - start) / sectorSize) + 1;
            AppendProgressLog(
                "beggar-max-erase-range",
                Tf(
                    "log",
                    "beggar_max_erase_contiguous_range",
                    "Erase {0} continuous range: 0x{1:X6}-0x{2:X6} ({3} sectors)",
                    label,
                    start,
                    end + sectorSize - 1,
                    sectorCount));
            EraseBeggarMaxRange(
                protocol,
                start,
                end + sectorSize - 1,
                sectorSize,
                cancellationToken,
                _ => AdvanceMeasuredProgress(
                    sectorSize,
                    1,
                    BuildProgressDetailText(label, "progress_phase_erase", "Erase")));
        }
    }

    private void ProgramBeggarMaxSector(
        WaterDeviceProtocol protocol,
        int sectorAddress,
        byte[] sectorData,
        NorGeometryInfo geometry,
        string label,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : 0x20000;
        if (sectorData.Length != sectorSize)
        {
            throw new InvalidOperationException(Tf("error", "sector_data_size_invalid", "{0} data size is invalid.", label));
        }

        if (IsAllBytes(sectorData, 0xFF))
        {
            return;
        }

        ushort bufferWriteBytes = (ushort)Math.Clamp(geometry.BufferWriteBytes > 0 ? geometry.BufferWriteBytes : 256, 1, ushort.MaxValue);
        int offset = 0;
        int currentBank = -1;
        while (offset < sectorData.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int absoluteAddress = sectorAddress + offset;
            int bank = absoluteAddress >> 14;
            int bankOffset = absoluteAddress & 0x3FFF;
            int writeLength = Math.Min(4096, sectorData.Length - offset);
            writeLength = Math.Min(writeLength, 0x4000 - bankOffset);

            if (bank != currentBank)
            {
                currentBank = bank;
                if (!TrySwitchBeggarMaxRomBank(protocol, bank))
                {
                    throw new InvalidOperationException(T("error", "segment_program_begin_failed", "Failed to switch MAX ROM bank before programming."));
                }
            }

            byte[] chunk = sectorData.AsSpan(offset, writeLength).ToArray();
            int cartAddress = GetBeggarMaxBankBaseAddress(bank) + bankOffset;
            if (!protocol.TryProgramGbcRom((uint)cartAddress, chunk, bufferWriteBytes, BeggarMaxWriteTimeoutMs))
            {
                throw new InvalidOperationException(T("error", "segment_program_write_failed", "Failed to program MAX ROM data."));
            }

            offset += writeLength;
        }

        cancellationToken.ThrowIfCancellationRequested();
        AdvanceMeasuredProgress(
            sectorData.Length,
            1,
            BuildProgressDetailText(label, "progress_phase_write", "Write"));
    }

    private string ResolveBeggarMaxBaseMenuPath(GbcMenuLanguageMode menuLanguage)
    {
        string preferredFileName = menuLanguage == GbcMenuLanguageMode.Chinese
            ? "menu_zh.gb"
            : "menu_en.gb";

        string[] candidatePaths =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", preferredFileName),
            Path.Combine(AppContext.BaseDirectory, preferredFileName),
        ];

        foreach (string candidatePath in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new InvalidOperationException(Tf(
            "error",
            "base_menu_not_found_detail",
            "未找到基础菜单 ROM。请确认程序目录下存在 Assets\\menu_zh.gb 或 Assets\\menu_en.gb。\n\n已查找：\n{0}",
            string.Join("\n", candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))));
    }

    private static CartridgeDefinition ResolveBeggarMaxCartridge(WaterCartScanResult scanResult)
    {
        int size = scanResult.Geometry.DeviceSizeBytes;
        int cartridgeId = size <= 32 * 1024 * 1024
            ? CartridgeDefinition.Gbc32MiBId
            : size <= 128 * 1024 * 1024
            ? CartridgeDefinition.Gbc128MiBId
            : CartridgeDefinition.Gbc256MiBId;
        return CartridgeDefinition.All.First(cartridge => cartridge.Id == cartridgeId);
    }

    private static bool ShouldUseDedicatedBeggarMaxSave(GameBoyRomMetadata metadata, WorkspaceListRow row)
    {
        return metadata.HasBattery &&
            metadata.EffectiveRamBankCount > 0 &&
            (row.GbcDedicatedSaveAllocationOverride ??
             row.PendingItem?.GbcDedicatedSaveAllocationOverride ??
             (row.CartEntry is null || !row.CartEntry.UsesSharedSave));
    }

    private void EraseBeggarMaxRange(
        WaterDeviceProtocol protocol,
        int startAddress,
        int endAddress,
        int sectorSize,
        CancellationToken cancellationToken = default,
        Action<int>? afterSectorErased = null)
    {
        int alignedEndAddress = endAddress - (endAddress % sectorSize);

        for (int sectorAddress = alignedEndAddress; sectorAddress >= startAddress; sectorAddress -= sectorSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TrySwitchBeggarMaxRomBank(protocol, sectorAddress >> 14))
            {
                throw new InvalidOperationException(T("error", "erase_segment_failed", "Failed to switch MAX ROM bank before erase."));
            }

            int bankBaseAddress = GetBeggarMaxBankBaseAddress(sectorAddress >> 14);
            if (!protocol.TryWriteGbc(0x0AAA, [0xAA], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0555, [0x55], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0AAA, [0x80], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0AAA, [0xAA], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0555, [0x55], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc((uint)bankBaseAddress, [0x30], BeggarMaxWriteTimeoutMs))
            {
                throw new InvalidOperationException(T("error", "erase_segment_failed", "Failed to erase a MAX flash sector."));
            }

            byte[] poll = [0x00];
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(BeggarMaxSectorErasePollDelayMs);
                cancellationToken.ThrowIfCancellationRequested();
                if (!protocol.TryReadGbc((uint)bankBaseAddress, poll, BeggarMaxWriteTimeoutMs))
                {
                    throw new InvalidOperationException(T("error", "erase_segment_failed", "Failed to poll MAX flash sector erase status."));
                }
            }
            while (poll[0] != 0xFF);
            cancellationToken.ThrowIfCancellationRequested();
            afterSectorErased?.Invoke(sectorAddress);
        }
    }

    private static bool TrySwitchBeggarMaxRomBank(WaterDeviceProtocol protocol, int bank)
    {
        byte low = (byte)(bank & 0xFF);
        byte high = (byte)((bank >> 8) & 0xFF);
        return protocol.TryWriteGbc(0x3000, [high], 3000) &&
               protocol.TryWriteGbc(0x2000, [low], 3000);
    }

    private static int GetBeggarMaxBankBaseAddress(int bank) =>
        bank == 0 ? 0x0000 : 0x4000;
}
