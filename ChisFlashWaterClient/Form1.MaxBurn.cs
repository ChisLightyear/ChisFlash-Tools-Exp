using System.IO;
using ChisFlashWaterClient.Services;
using GbaMultiMenuBuilder.Models;
using GbaMultiMenuBuilder.Services;
using BuilderDetectedSaveTypeKind = GbaMultiMenuBuilder.Models.DetectedSaveTypeKind;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private const int BeggarMaxReadChunkBytes = 4096;
    private const int BeggarMaxWriteTimeoutMs = Timeout.Infinite;
    private const int BeggarMaxSectorErasePollDelayMs = 20;
    private const int BeggarMaxFixedMapReservedBytes = 1 * 1024 * 1024;
    private const int BeggarMaxPayloadStartBank = BeggarMaxFixedMapReservedBytes / GameBoyRomHeaderService.RomBankSize;
    private const int BeggarMaxIndependentSaveBankCapacity = 60;
    private const int BeggarMaxSharedSaveBankStart = 60;
    private const int BeggarMaxSharedSaveBankCount = 4;
    private const int BeggarMaxGameSramBankLimit = CompilationBuilder.GbcMaxGameSramBanks;

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
        public required int RamBankCount { get; init; }
        public required int SaveLength { get; init; }
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
        public required List<BeggarMaxWorkspaceRowPlan> RowPlans { get; init; }
        public required byte[] FixedMapRegion { get; init; }
        public byte[]? OriginalFixedMapRegion { get; init; }
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
            CompilationBuilder.ValidateGbcSaveBankCount(plan.MbcType, plan.RamBankCount, plan.UsesDedicatedSave);
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

    private WaterCartScanResult BurnBeggarMaxSingleRomOnDevice(
        WaterDeviceProtocol protocol, WaterCartScanResult scan, PendingRomImportItem item, CancellationToken cancellationToken)
    {
        byte[] rom = File.ReadAllBytes(item.FilePath);
        int capacity = scan.Geometry.DeviceSizeBytes;
        if (!WaterCartScanner.TryBuildBeggarMaxSingleRomEntry(rom, capacity, out var entry) ||
            entry.AllocatedSizeBytesOverride > rom.Length || (capacity > 0 && rom.Length > capacity))
            throw new InvalidDataException(ShellText("游戏头无效、文件不完整或超过卡带容量。",
                "The game header is invalid, the ROM is incomplete, or it exceeds cartridge capacity."));

        int sectorSize = scan.Geometry.SectorSizeBytes > 0 ? scan.Geometry.SectorSizeBytes : 0x20000;
        int writeEnd = checked((rom.Length + sectorSize - 1) / sectorSize * sectorSize);
        int eraseEnd = scan.HasBeggarMaxDirectoryHeaders ? Math.Max(BeggarMaxFixedMapReservedBytes, writeEnd) : writeEnd;
        string title = T("ui", "queue_burn", "Start Burn");
        string eraseDetail = BuildProgressDetailText(item.FileName, "progress_phase_erase", "Erase");
        string writeDetail = BuildProgressDetailText(item.FileName, "progress_phase_write", "Write");
        int selectedBank = -1;
        BeginMeasuredProgress(title, (long)eraseEnd + writeEnd, (eraseEnd + writeEnd) / sectorSize);
        for (int offset = 0; offset < eraseEnd; offset += sectorSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int completedSectors = offset / sectorSize;
            BeginMeasuredProgressPhase(eraseEnd / sectorSize, true, eraseDetail, completedUnits: completedSectors);
            if (offset < rom.Length)
            {
                byte[] sector = Enumerable.Repeat((byte)0xFF, sectorSize).ToArray();
                rom.AsSpan(offset, Math.Min(sectorSize, rom.Length - offset)).CopyTo(sector);
                RewriteBeggarMaxSectorWithBank(protocol, offset, sector, scan.Geometry, item.FileName, ref selectedBank, cancellationToken,
                    beforeProgram: () => BeginMeasuredProgressPhase(writeEnd / sectorSize, false, writeDetail, completedUnits: completedSectors));
            }
            else
            {
                EraseBeggarMaxRange(protocol, offset, offset + sectorSize - 1, sectorSize, ref selectedBank, cancellationToken);
                AdvanceMeasuredProgress(sectorSize, 1, BuildProgressDetailText(item.FileName, "progress_phase_erase", "Erase"));
            }
        }
        CompleteMeasuredProgress(title);

        var result = BuildScanAfterNorErase(scan, entireNor: false);
        result.Games.Add(entry.HeaderGame!);
        result.Entries.Add(entry);
        return result;
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
        EnsureCfiWindowAllowsBurn(initialScan.Geometry);
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

        WaterCartScanResult rebuilt = BuildBeggarMaxCachedScanResult(initialScan, burnPlan);
        rebuilt.IsBeggarMaxGbcMode = true;
        if (clearConfigArea)
        {
            rebuilt.HasBeggarMaxDirectoryHeaders = true;
            rebuilt.Games.Clear();
            rebuilt.Entries.Clear();
        }
        rebuilt.NorId = initialScan.NorId.ToArray();
        rebuilt.GbcFlashId = initialScan.GbcFlashId.ToArray();
        rebuilt.RawNorIdHex = initialScan.RawNorIdHex;
        rebuilt.ChipName = initialScan.ChipName;
        rebuilt.CapacitySummary = initialScan.CapacitySummary;
        rebuilt.SectorSummary = initialScan.SectorSummary;
        rebuilt.Geometry = initialScan.Geometry;
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
        byte[]? originalFixedMapRegion = null;
        if (initialScan.HasBeggarMaxDirectoryHeaders && !clearConfigArea)
        {
            originalFixedMapRegion = ReadBeggarMaxFixedMapRegionFromDevice(protocol, initialScan, cancellationToken);
            fixedMapRegion = builder.UpdateExistingGbcFixedMapRegion(
                originalFixedMapRegion,
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
            RowPlans = rowPlans,
            FixedMapRegion = fixedMapRegion,
            OriginalFixedMapRegion = originalFixedMapRegion,
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
                int allocatedBytes = Math.Max(GetPendingAllocatedSizeBytes(pending, burnProfileMode), checked((int)metadata.EffectiveRomSizeBytes));
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
                    SaveLength = !metadata.HasBattery ? 0 : metadata.CartridgeType == 0x06 ? 512 : GetGbcSramLength(metadata),
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
                FileLength = entry.RomOffsetBank == 0 ? entry.RomBankCount * (long)GameBoyRomHeaderService.RomBankSize : 0,
                AllocatedBytes = Math.Max(GameBoyRomHeaderService.RomBankSize * 2, entry.RomBankCount * GameBoyRomHeaderService.RomBankSize),
                AllocatedRomBanks = Math.Max(2, entry.RomBankCount),
                SaveLength = entry.SaveLengthBytes,
                RamBankCount = entry.RamBankCount,
                MbcType = entry.MbcType,
                HasBattery = entry.HasBattery,
                UsesDedicatedSave = ResolveWorkspaceRowUsesDedicatedSave(row),
                IsChanged = entry.RomOffsetBank == 0,
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

            if (plan.ExistingEntry is { RomOffsetBank: >= BeggarMaxPayloadStartBank } &&
                plan.ExistingEntry.RomBankCount >= plan.AllocatedRomBanks)
            {
                plan.FinalRomOffsetBank = plan.ExistingEntry.RomOffsetBank;
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
            if (plan.ExistingEntry is null || plan.ExistingEntry.RomOffsetBank == 0)
            {
                continue;
            }

            if (plan.IsChanged && plan.ExistingEntry.RomBankCount < plan.AllocatedRomBanks)
            {
                continue;
            }

            RemoveBeggarMaxRange(ranges, plan.ExistingEntry.RomOffsetBank, plan.ExistingEntry.RomOffsetBank + plan.AllocatedRomBanks - 1);
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
        bool[] used = new bool[BeggarMaxIndependentSaveBankCapacity];
        var dedicated = rowPlans.Where(plan => plan.UsesDedicatedSave && plan.RamBankCount > 0).ToList();
        int required = dedicated.Sum(plan => plan.RamBankCount);
        if (required > used.Length)
            throw new InvalidDataException(Tf("error", "gbc_sram_capacity_exceeded",
                "Battery-backed SRAM allocation exceeds the MAX/MBCX independent save capacity ({0} banks).", used.Length));

        foreach (var plan in rowPlans)
        {
            CompilationBuilder.ValidateGbcSaveBankCount(plan.MbcType, plan.RamBankCount, plan.UsesDedicatedSave);
            if (!plan.UsesDedicatedSave && plan.RamBankCount > 0)
            {
                plan.FinalSaveBankMap = BuildBeggarMaxSharedSaveMap(plan.RamBankCount);
                plan.FinalUsesSharedSave = true;
            }
        }

        bool[] originalUsed = new bool[BeggarMaxIndependentSaveBankCapacity];
        foreach (var plan in rowPlans.Where(plan => plan.ReuseExistingSaveLayout))
        {
            if (plan.ExistingEntry is not { HasBattery: true, UsesSharedSave: false, RamBankCount: > 0 } entry) continue;
            if (!ValidateBeggarMaxDedicatedSaveMap(entry.SaveBankMap, entry.RamBankCount, originalUsed, out string error))
                throw new InvalidDataException($"{plan.DisplayTitle}: {error}");
            foreach (byte bank in entry.SaveBankMap.Take(entry.RamBankCount)) originalUsed[bank] = true;
        }

        // 原位保留连续独占区，尚未分配的游戏优先使用连续空洞。
        foreach (var plan in dedicated)
        {
            if (!plan.ReuseExistingSaveLayout || plan.ExistingEntry is not { UsesSharedSave: false } entry) continue;
            var banks = entry.SaveBankMap.Take(plan.RamBankCount).ToArray();
            if (!banks.Select((bank, index) => bank - index).All(start => start == banks[0])) continue;
            AssignRange(plan, banks[0]);
        }

        foreach (var plan in dedicated.Where(plan => plan.FinalSaveBankMap[0] == 0xFF))
        {
            int first = 0;
            while (first + plan.RamBankCount <= used.Length &&
                used.AsSpan(first, plan.RamBankCount).Contains(true)) first++;
            if (first + plan.RamBankCount <= used.Length)
            {
                AssignRange(plan, first);
                continue;
            }

            // 总容量已检查，连续空间不足才重新排列独占区。
            int cursor = 0;
            foreach (var game in dedicated)
            {
                AssignRange(game, cursor);
                cursor += game.RamBankCount;
            }
            break;
        }

        void AssignRange(BeggarMaxWorkspaceRowPlan plan, int first)
        {
            for (int i = 0; i < plan.RamBankCount; i++)
            {
                used[first + i] = true;
                plan.FinalSaveBankMap[i] = (byte)(first + i);
            }
        }
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

    private WaterCartScanResult BuildBeggarMaxCachedScanResult(WaterCartScanResult initialScan, BeggarMaxBurnPlan burnPlan)
    {
        var games = BuildBeggarMaxPlannedGames(burnPlan);
        var result = WaterCartScanner.RebuildCachedScanResult(initialScan, gamesOverride: games);
        result.IsBeggarMaxGbcMode = true;
        result.HasBeggarMaxDirectoryHeaders = true;
        result.SaveSlotSummary = new();
        result.Entries = burnPlan.RowPlans.Select((plan, index) =>
        {
            int saveLength = plan.SaveLength;
            string saveText = saveLength == 0 ? T("value", "save_type_none", "NONE") :
                plan.MbcType is 0x05 or 0x06 ? "MBC2" : $"RAM {FormatKiBSize(saveLength)}" + (plan.FinalUsesSharedSave ? " (Shared)" : "");
            return new WaterCartEntry
            {
                HeaderGame = games[index], AllocatedSizeBytesOverride = plan.AllocatedBytes,
                EntrySlotOverride = plan.FinalEntrySlot, SaveLengthBytesOverride = saveLength,
                SourceTextOverride = T("value", "source_gbc", "GBC"), SaveTypeTextOverride = saveText,
                RomOffsetBankOverride = plan.FinalRomOffsetBank, RomBankCountOverride = plan.AllocatedRomBanks,
                SaveBankMapOverride = plan.FinalSaveBankMap.ToArray(), UsesSharedSaveOverride = plan.FinalUsesSharedSave,
                MbcTypeOverride = plan.MbcType, RamBankCountOverride = plan.RamBankCount,
                HasBatteryOverride = plan.HasBattery, DirectoryTitleOverride = plan.DisplayTitle,
            };
        }).ToList();
        return result;
    }

    private void ProgramBeggarMaxBurnPlan(
        WaterDeviceProtocol protocol,
        WaterCartScanResult scanResult,
        BeggarMaxBurnPlan burnPlan,
        bool forceFullFixedMapRewrite = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateIndeterminateProgress(
            T("value", "gbc_progress_preparing_payload", "Preparing GBC/MAX ROM sectors..."));
        Dictionary<int, byte[]> payloadSectors = BuildBeggarMaxPayloadSectorRewriteMap(protocol, scanResult, burnPlan, cancellationToken);
        UpdateIndeterminateProgress(
            T("value", "gbc_progress_preparing_menu", "Preparing GBC/MAX menu sectors..."));
        Dictionary<int, byte[]> finalFixedMapSectors = BuildBeggarMaxFixedMapSectorRewriteMap(
            protocol,
            scanResult,
            burnPlan.FixedMapRegion,
            existingFixedMapRegion: burnPlan.OriginalFixedMapRegion,
            forceAll: forceFullFixedMapRewrite,
            cancellationToken: cancellationToken);
        long totalBytes =
            payloadSectors.Values.Sum(sector => CountRewriteSectorProgressWorkBytes(sector, scanResult.Geometry)) +
            finalFixedMapSectors.Values.Sum(sector => CountRewriteSectorProgressWorkBytes(sector, scanResult.Geometry));
        int totalUnits =
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

        var relocatedSaves = BackupRelocatedMaxSaves(protocol, burnPlan, cancellationToken, out string? backupFolder);
        bool migrationStarted = false;
        BeginMeasuredProgress(T("ui", "queue_burn", "Start Burn"), totalBytes, totalUnits);
        int selectedBank = -1;
        try
        {
            if (payloadSectors.Count > 0)
            {
                BeginMeasuredProgressPhase(payloadSectors.Count, true, BuildProgressDetailText("MAX/MBCX ROM", "progress_phase_erase", "Erase"));
                EraseBeggarMaxSectorBatch(
                    protocol,
                    payloadSectors,
                    scanResult.Geometry,
                    "MAX/MBCX ROM",
                    ref selectedBank,
                    cancellationToken);

                AppendLog(T(
                    "log",
                    "beggar_max_rom_erase_complete",
                    "All continuous MAX/MBCX ROM ranges have been erased. Start programming ROM data."));
                int payloadWriteCount = payloadSectors.Values.Count(sector => !IsAllBytes(sector, 0xFF));
                if (payloadWriteCount > 0)
                    BeginMeasuredProgressPhase(payloadWriteCount, false, BuildProgressDetailText("MAX/MBCX ROM", "progress_phase_write", "Write"));
                foreach ((int sectorAddress, byte[] sectorData) in payloadSectors.OrderBy(pair => pair.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProgramBeggarMaxSector(
                        protocol,
                        sectorAddress,
                        sectorData,
                        scanResult.Geometry,
                        "MAX/MBCX ROM",
                        ref selectedBank,
                        cancellationToken);
                }
            }

            if (relocatedSaves.Count > 0)
            {
                // 搬移开始后，先提交与存档配套的目录再结束，避免用户取消后用旧表重读。
                cancellationToken.ThrowIfCancellationRequested();
                migrationStarted = true;
                cancellationToken = CancellationToken.None;
                RestoreRelocatedMaxSaves(protocol, relocatedSaves);
            }
            int mapWriteCount = finalFixedMapSectors.Values.Count(sector => !IsAllBytes(sector, 0xFF));
            string mapLabel = forceFullFixedMapRewrite ? "MAX/MBCX fresh menu" : "MAX/MBCX final map";
            string eraseMapDetail = BuildProgressDetailText(mapLabel, "progress_phase_erase", "Erase");
            string writeMapDetail = BuildProgressDetailText(mapLabel, "progress_phase_write", "Write");
            if (forceFullFixedMapRewrite && finalFixedMapSectors.Count > 0)
            {
                BeginMeasuredProgressPhase(finalFixedMapSectors.Count, true, eraseMapDetail);
                EraseBeggarMaxSectorBatch(
                    protocol,
                    finalFixedMapSectors,
                    scanResult.Geometry,
                    mapLabel,
                    ref selectedBank,
                    cancellationToken);
                if (mapWriteCount > 0)
                    BeginMeasuredProgressPhase(mapWriteCount, false, writeMapDetail);
            }

            int mapErased = 0;
            int mapWritten = 0;
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
                        mapLabel,
                        ref selectedBank,
                        cancellationToken);
                }
                else
                {
                    BeginMeasuredProgressPhase(finalFixedMapSectors.Count, true, eraseMapDetail, completedUnits: mapErased++);
                    RewriteBeggarMaxSectorWithBank(protocol, sectorAddress, sectorData, scanResult.Geometry,
                        mapLabel, ref selectedBank, cancellationToken,
                        beforeProgram: () => BeginMeasuredProgressPhase(mapWriteCount, false, writeMapDetail, completedUnits: mapWritten++));
                }
            }

            CompleteMeasuredProgress(T("ui", "queue_burn", "Start Burn"));
        }
        catch (Exception ex) when (migrationStarted)
        {
            AbortMeasuredProgress();
            throw new IOException(ShellText("存档迁移中断。请重新分析卡带，并用这次迁移前的备份恢复受影响的游戏：",
                "Save migration was interrupted. Rescan, then recover affected games from the original backups: ") +
                backupFolder + Environment.NewLine + ex.Message, ex);
        }
        catch
        {
            AbortMeasuredProgress();
            throw;
        }
    }

    private sealed record MaxSaveMove(WaterCartEntry Source, byte[] Destination, byte[] Data);

    private List<MaxSaveMove> BackupRelocatedMaxSaves(WaterDeviceProtocol protocol,
        BeggarMaxBurnPlan burnPlan, CancellationToken cancellationToken, out string? backupFolder)
    {
        backupFolder = null;
        List<MaxSaveMove> moves = [];
        foreach (var plan in burnPlan.RowPlans)
        {
            if (!plan.ReuseExistingSaveLayout || plan.ExistingEntry is not { HasBattery: true } source ||
                source.SaveLengthBytes <= 0) continue;
            var oldBanks = ResolveBeggarMaxPhysicalBanks(source, source.SaveLengthBytes);
            if (oldBanks.Length == 0)
                throw new InvalidDataException($"{plan.DisplayTitle}: SRAM bank map");
            byte[] destination = plan.FinalSaveBankMap[..oldBanks.Length];
            if (oldBanks.SequenceEqual(destination.Select(bank => (int)bank))) continue;
            if (plan.FinalUsesSharedSave && burnPlan.RowPlans.Any(other => other != plan &&
                other.FinalUsesSharedSave && other.HasBattery && other.ReuseExistingSaveLayout))
                throw new InvalidOperationException(ShellText("多份保留的存档共用同一目标位置，无法分别迁移。请给要保留的游戏选择独占存档。",
                    "Several retained saves would share the same destination. Select dedicated saves to preserve each game."));
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data = ReadBeggarMaxSave(protocol, source, source.SaveLengthBytes);
            moves.Add(new MaxSaveMove(source, destination, data));
        }
        if (moves.Count == 0) return moves;

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChisFlashWaterClient", "MAX-Save-Backups", $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        foreach (var move in moves)
        {
            string name = $"{move.Source.EntrySlot + 1:D3}-{SanitizeSaveFileName(move.Source.Title)}.sav";
            File.WriteAllBytes(Path.Combine(folder, name), move.Data);
        }
        if (burnPlan.OriginalFixedMapRegion is { } original)
            File.WriteAllBytes(Path.Combine(folder, "original-menu.bin"), original);
        File.WriteAllText(Path.Combine(folder, "mapping.json"), System.Text.Json.JsonSerializer.Serialize(
            moves.Select(move => new { move.Source.EntrySlot, move.Source.Title, move.Source.RomOffsetBank,
                OldBanks = move.Source.SaveBankMap.Take(move.Destination.Length).Select(b => (int)b),
                NewBanks = move.Destination.Select(b => (int)b) })));
        backupFolder = folder;
        AppendLog(ShellText("MAX 存档迁移备份已保存到 ", "MAX save migration backup: ") + folder);
        return moves;
    }

    private void RestoreRelocatedMaxSaves(WaterDeviceProtocol protocol, List<MaxSaveMove> moves)
    {
        // 所有源存档已备份，此处按新地址写入，可以处理互换和重叠。
        foreach (var move in moves)
        {
            uint start = (uint)(move.Destination[0] * GameBoyRomHeaderService.RamBankSize);
            try { TransferGbcPhysicalSave(protocol, start, move.Data.Length, move.Data); }
            finally { TrySetBeggarMaxRamEnabled(protocol, false); }
        }
    }

    private Dictionary<int, byte[]> BuildBeggarMaxFixedMapSectorRewriteMap(
        WaterDeviceProtocol protocol,
        WaterCartScanResult scanResult,
        byte[] fixedMapRegion,
        byte[]? existingFixedMapRegion,
        bool forceAll = false,
        CancellationToken cancellationToken = default)
    {
        int sectorSize = scanResult.Geometry.SectorSizeBytes > 0
            ? scanResult.Geometry.SectorSizeBytes
            : 0x20000;
        Dictionary<int, byte[]> sectorMap = [];
        int selectedBank = -1;

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

            ReadOnlySpan<byte> existingSectorData = existingFixedMapRegion is not null && offset + sectorSize <= existingFixedMapRegion.Length
                ? existingFixedMapRegion.AsSpan(offset, sectorSize)
                : ReadBeggarMaxSectorImage(protocol, offset, sectorSize, ref selectedBank, cancellationToken);
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
        Dictionary<int, List<(int Start, int End)>> sectorCoverage = [];
        int sourceBank = -1;

        foreach (BeggarMaxWorkspaceRowPlan plan in burnPlan.RowPlans.Where(plan => plan.IsChanged))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] payload = plan.RomPath.Length == 0 && plan.ExistingEntry is { RomOffsetBank: 0 }
                ? ReadBeggarMaxSectorImage(protocol, 0, plan.AllocatedBytes, ref sourceBank, cancellationToken)
                : BuildBeggarMaxRomPayload(plan, cancellationToken);
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
                    sectorData = new byte[sectorSize];
                    sectorMap.Add(sectorAddress, sectorData);
                    sectorCoverage.Add(sectorAddress, []);
                }
                Buffer.BlockCopy(payload, payloadOffset, sectorData, sectorOffset, copyLength);
                sectorCoverage[sectorAddress].Add((sectorOffset, sectorOffset + copyLength));
                payloadOffset += copyLength;
            }
        }

        int selectedBank = -1;
        foreach (var (sectorAddress, ranges) in sectorCoverage.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ranges.Sort();
            byte[] sectorData = sectorMap[sectorAddress];
            byte[]? original = null;
            int coveredEnd = 0;
            // 只为未覆盖部分读卡。首尾以外也可能有待保留的空隙。
            foreach (var (start, end) in ranges)
            {
                if (start > coveredEnd)
                {
                    original ??= ReadBeggarMaxSectorImage(protocol, sectorAddress, sectorSize, ref selectedBank, cancellationToken);
                    original.AsSpan(coveredEnd, start - coveredEnd).CopyTo(sectorData.AsSpan(coveredEnd));
                }
                coveredEnd = Math.Max(coveredEnd, end);
            }
            if (coveredEnd < sectorSize)
            {
                original ??= ReadBeggarMaxSectorImage(protocol, sectorAddress, sectorSize, ref selectedBank, cancellationToken);
                original.AsSpan(coveredEnd).CopyTo(sectorData.AsSpan(coveredEnd));
            }
        }

        return sectorMap;
    }

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
        int selectedBank = -1;

        for (int offset = 0; offset < fixedMap.Length; offset += sectorSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int readLength = Math.Min(sectorSize, fixedMap.Length - offset);
            byte[] sectorImage = ReadBeggarMaxSectorImage(protocol, offset, sectorSize, ref selectedBank, cancellationToken);
            Buffer.BlockCopy(sectorImage, 0, fixedMap, offset, readLength);
        }

        return fixedMap;
    }

    private byte[] ReadBeggarMaxSectorImage(
        WaterDeviceProtocol protocol,
        int sectorAddress,
        int sectorSize,
        ref int selectedBank,
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
            if (!TrySelectBeggarMaxRomBank(protocol, bank, ref selectedBank))
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
            if (!protocol.TryReadGbc((uint)cartAddress, chunk, Timeout.Infinite))
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
        int selectedBank = -1;
        RewriteBeggarMaxSectorWithBank(protocol, sectorAddress, sectorData, geometry, label, ref selectedBank, cancellationToken);
    }

    private void RewriteBeggarMaxSectorWithBank(
        WaterDeviceProtocol protocol,
        int sectorAddress,
        byte[] sectorData,
        NorGeometryInfo geometry,
        string label,
        ref int selectedBank,
        CancellationToken cancellationToken,
        Action? beforeProgram = null)
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
        EraseBeggarMaxRange(protocol, sectorAddress, sectorAddress + sectorSize - 1, sectorSize, ref selectedBank, cancellationToken);

        AdvanceMeasuredProgress(
            sectorSize,
            1,
            BuildProgressDetailText(label, "progress_phase_erase", "Erase"));
        cancellationToken.ThrowIfCancellationRequested();

        if (beforeProgram is not null && !IsAllBytes(sectorData, 0xFF))
            beforeProgram();
        ProgramBeggarMaxSector(protocol, sectorAddress, sectorData, geometry, label, ref selectedBank, cancellationToken);
    }

    private void EraseBeggarMaxSectorBatch(
        WaterDeviceProtocol protocol,
        IReadOnlyDictionary<int, byte[]> sectorMap,
        NorGeometryInfo geometry,
        string label,
        ref int selectedBank,
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
                ref selectedBank,
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
        ref int selectedBank,
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
        while (offset < sectorData.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int absoluteAddress = sectorAddress + offset;
            int bank = absoluteAddress >> 14;
            int bankOffset = absoluteAddress & 0x3FFF;
            int writeLength = Math.Min(4096, sectorData.Length - offset);
            writeLength = Math.Min(writeLength, 0x4000 - bankOffset);

            if (!TrySelectBeggarMaxRomBank(protocol, bank, ref selectedBank))
                throw new InvalidOperationException(T("error", "segment_program_begin_failed", "Failed to switch MAX ROM bank before programming."));

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
        ref int selectedBank,
        CancellationToken cancellationToken = default,
        Action<int>? afterSectorErased = null)
    {
        int alignedEndAddress = endAddress - (endAddress % sectorSize);

        for (int sectorAddress = alignedEndAddress; sectorAddress >= startAddress; sectorAddress -= sectorSize)
        {
            long eraseStart = StartLandscapeErase();
            cancellationToken.ThrowIfCancellationRequested();
            if (!TrySelectBeggarMaxRomBank(protocol, sectorAddress >> 14, ref selectedBank))
            {
                throw new InvalidOperationException(T("error", "erase_segment_failed", "Failed to switch MAX ROM bank before erase."));
            }

            // 0x30 命令与状态轮询都必须落在扇区自身地址上，不能只用 bank 基址：
            // 等长 64 KiB 扇区的 bank 内偏移恒为 0（与旧写法等价），
            // 8 KiB 小扇区共用一个 bank 时若丢掉偏移，会反复擦第一个扇区并误判完成。
            uint commandAddress = (uint)GbcNorImageWriter.SectorCommandAddress(sectorAddress);
            if (!protocol.TryWriteGbc(0x0AAA, [0xAA], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0555, [0x55], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0AAA, [0x80], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0AAA, [0xAA], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(0x0555, [0x55], BeggarMaxWriteTimeoutMs) ||
                !protocol.TryWriteGbc(commandAddress, [0x30], BeggarMaxWriteTimeoutMs))
            {
                throw new InvalidOperationException(T("error", "erase_segment_failed", "Failed to erase a MAX flash sector."));
            }

            byte[] poll = [0x00];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!protocol.TryReadGbc(commandAddress, poll, BeggarMaxWriteTimeoutMs))
                    throw new InvalidOperationException(T("error", "erase_segment_failed", "Failed to poll MAX flash sector erase status."));
                if (poll[0] == 0xFF) break;
                Thread.Sleep(BeggarMaxSectorErasePollDelayMs);
            }
            cancellationToken.ThrowIfCancellationRequested();
            FinishLandscapeErase(eraseStart, sectorSize);
            afterSectorErased?.Invoke(sectorAddress);
        }
    }

    private static bool TrySelectBeggarMaxRomBank(WaterDeviceProtocol protocol, int bank, ref int selectedBank)
    {
        if (bank == selectedBank) return true;
        selectedBank = -1;
        if (!TrySwitchBeggarMaxRomBank(protocol, bank)) return false;
        selectedBank = bank;
        return true;
    }

    private static bool TrySwitchBeggarMaxRomBank(WaterDeviceProtocol protocol, int bank)
    {
        byte low = (byte)(bank & 0xFF);
        byte high = (byte)((bank >> 8) & 0xFF);
        return protocol.TryWriteGbc(0x3000, [high], Timeout.Infinite) &&
               protocol.TryWriteGbc(0x2000, [low], Timeout.Infinite);
    }

    private static int GetBeggarMaxBankBaseAddress(int bank) =>
        bank == 0 ? 0x0000 : 0x4000;
}
