using System.Text;
using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private const int SaveSlotTestCount = WaterSaveSlotSummaryInfo.SlotCount;
    private const int SaveSlotPreviewBytes = 64;
    private const uint SaveSlotSummaryMagic = 0x31534C53u;
    private const byte SaveSlotSummaryFlagFullAb = 0x80;
    private const byte SaveSlotSummaryCountMask = 0x3F;
    private const byte SaveSlotEntryBank0ActiveHasData = 0x01;
    private const byte SaveSlotEntryBank1ActiveHasData = 0x02;
    private const byte SaveSlotEntryBank0SelectB = 0x04;
    private const byte SaveSlotEntryBank1SelectB = 0x08;
    private const byte SaveSlotEntrySlotHasAnyData = 0x10;
    private const byte SaveSlotEntryBank0InactiveHasData = 0x20;
    private const byte SaveSlotEntryBank1InactiveHasData = 0x40;
    private const uint SaveSlotBank0AOffset = 0x00000u;
    private const uint SaveSlotBank0BOffset = 0x10000u;
    private const uint SaveSlotBank1AOffset = 0x20000u;
    private const uint SaveSlotBank1BOffset = 0x30000u;

    private static readonly SaveSlotWindowDefinition[] SaveSlotWindowDefinitions =
    [
        new("bank0_A", SaveSlotBank0AOffset, 0, false),
        new("bank0_B", SaveSlotBank0BOffset, 0, true),
        new("bank1_A", SaveSlotBank1AOffset, 1, false),
        new("bank1_B", SaveSlotBank1BOffset, 1, true),
    ];

    private sealed record SaveSlotWindowDefinition(string Name, uint Offset, int BankIndex, bool CopyB);

    private async Task RunFragmentedSaveSlotTestAsync()
    {
        if (GetSelectedProgrammerKind() != ProgrammerKind.WaterLink)
        {
            AppendLog(T(
                "error",
                "fragment_test_requires_waterlink",
                "Fragmented save-slot test is currently available only through the WaterLink programmer path."));
            return;
        }

        if (!EnsureSelectedPortHasActiveScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        if (!EnsureCurrentScanCapacityValid(out reason))
        {
            AppendLog(reason);
            return;
        }

        if (currentScanResult is null)
        {
            AppendLog(T("error", "select_and_scan_first", "Please select and scan the current cart first."));
            return;
        }

        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a compatible programmer port first."));
            return;
        }

        WaterCartScanResult scanSnapshot = currentScanResult;
        List<int> testSlots = BuildFragmentTestSlots(scanSnapshot);

        SetBusy(true, T("status", "fragment_test_busy", "Testing fragmented save slots..."));
        AppendLog(Tf(
            "log",
            "fragment_test_start",
            "Start fragmented save-slot test on {0}. This is a read-only diagnostic.",
            portName));
        AppendLog(Tf(
            "log",
            "fragment_test_slot_plan",
            "Logical save slots selected for this test: {0}",
            FormatSlotListForLog(testSlots)));

        try
        {
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, ProgrammerKind.WaterLink);
                RunFragmentedSaveSlotTestOnDevice(protocol, scanSnapshot, testSlots);
            });

            SetBusy(false, T("status", "fragment_test_done", "Fragment test complete"));
        }
        catch (Exception ex)
        {
            AppendLog(Tf(
                "log",
                "fragment_test_failed",
                "Fragmented save-slot test failed: {0}",
                ex.Message));
            SetBusy(false, T("status", "fragment_test_failed", "Fragment test failed"));
        }
    }

    private void RunFragmentedSaveSlotTestOnDevice(
        WaterDeviceProtocol protocol,
        WaterCartScanResult scanSnapshot,
        IReadOnlyList<int> testSlots)
    {
        if (protocol.TryReadExtendedId(out byte[] extendedPayload))
        {
            LogFragmentedSaveStatus(extendedPayload);
        }
        else
        {
            AppendLog(T(
                "log",
                "fragment_test_extended_status_unavailable",
                "Fragment test: extended slot status (0xA4) did not respond. Continuing with save-slot summary only."));
        }

        if (!protocol.TryReadSaveSlotSummary(out byte[] summaryPayload))
        {
            throw new InvalidOperationException(T(
                "error",
                "fragment_test_summary_unavailable",
                "The device did not respond to save-slot summary read (0xA5)."));
        }

        WaterSaveSlotSummaryInfo summary = ParseSaveSlotSummary(summaryPayload);
        List<int> nonBlankSlots = Enumerable.Range(0, SaveSlotTestCount)
            .Where(slot => SlotHasAnyData(summary.GetEntryFlags(slot)))
            .ToList();

        AppendLog(Tf(
            "log",
            "fragment_test_summary",
            "Fragment test: save-slot summary reports {0} non-blank slot(s): {1}",
            nonBlankSlots.Count,
            FormatSlotListForLog(nonBlankSlots)));

        List<int> extraNonBlankSlots = nonBlankSlots
            .Except(testSlots)
            .ToList();
        if (extraNonBlankSlots.Count > 0)
        {
            AppendLog(Tf(
                "log",
                "fragment_test_extra_nonblank",
                "Fragment test: non-blank slots outside the current mapping selection: {0}",
                FormatSlotListForLog(extraNonBlankSlots)));
        }

        if (testSlots.Count == 0)
        {
            AppendLog(T(
                "log",
                "fragment_test_no_slots",
                "Fragment test: the current scan did not resolve any logical save slots to probe."));
            return;
        }

        foreach (int gameSlot in testSlots)
        {
            LogFragmentedSaveSlotDetails(protocol, scanSnapshot, summary, gameSlot);
        }
    }

    private void LogFragmentedSaveStatus(byte[] extendedPayload)
    {
        if (extendedPayload.Length < 32)
        {
            return;
        }

        WaterRuntimeStatusInfo runtimeStatus = WaterCartScanner.ParseExtendedRuntimeStatus(extendedPayload);
        uint saveDelayMs = BitConverter.ToUInt32(extendedPayload, 28);
        string currentSlotText = runtimeStatus.CurrentGameSlot is null
            ? T("value", "none", "None")
            : (runtimeStatus.CurrentGameSlot.Value + 1).ToString("000");

        AppendLog(Tf(
            "log",
            "fragment_test_current_status",
            "Fragment test: currentSlot={0}, bucket={1}, mapperLocked={2}, gameModeActive={3}, fixedLayout={4}, saveDelay={5}",
            currentSlotText,
            runtimeStatus.BucketId,
            runtimeStatus.MapperLocked,
            runtimeStatus.GameModeActive,
            runtimeStatus.SupportsWaterLinkFixedLayout,
            FormatSaveDelayWord(saveDelayMs)));
    }

    private void LogFragmentedSaveSlotDetails(
        WaterDeviceProtocol protocol,
        WaterCartScanResult scanSnapshot,
        WaterSaveSlotSummaryInfo summary,
        int gameSlot)
    {
        WaterCartEntry? entry = scanSnapshot.Entries
            .Where(item => !item.IsMenu && item.EntrySlot == gameSlot)
            .OrderBy(item => item.StartBucket)
            .FirstOrDefault();

        string title = entry is null
            ? Tf("value", "untitled_entry_slot", "Untitled Entry #{0:000}", gameSlot + 1)
            : ShortenForFragmentLog(entry.Title, 40);
        string physicalBuckets = entry?.BucketRangeText ?? T("value", "none", "None");
        string logicalBucketCount = entry?.LogicalBucketCount.ToString() ?? "?";
        int? legacyBucketSlot = entry?.StartBucket is > 0 and <= SaveSlotTestCount
            ? entry.StartBucket - 1
            : null;
        string legacyBucketSlotText = legacyBucketSlot.HasValue
            ? (legacyBucketSlot.Value + 1).ToString("000")
            : T("value", "none", "None");
        bool fragmentedRoute = legacyBucketSlot.HasValue && legacyBucketSlot.Value != gameSlot;

        byte summaryFlags = summary.GetEntryFlags(gameSlot);
        AppendLog(Tf(
            "log",
            "fragment_test_slot_header",
            "Slot #{0:000} {1} | physical={2} | logicalBlocks={3} | legacyBucketSlot={4} | fragmentedRoute={5} | flags=0x{6:X2}",
            gameSlot + 1,
            title,
            physicalBuckets,
            logicalBucketCount,
            legacyBucketSlotText,
            fragmentedRoute,
            summaryFlags));
        AppendLog(Tf(
            "log",
            "fragment_test_slot_summary",
            "  summary | bank0 {0}/{1} | bank1 {2}/{3} | anyData={4}",
            DescribeBankCopy(summary, summaryFlags, 0, false),
            DescribeBankCopy(summary, summaryFlags, 0, true),
            DescribeBankCopy(summary, summaryFlags, 1, false),
            DescribeBankCopy(summary, summaryFlags, 1, true),
            SlotHasAnyData(summaryFlags)));

        foreach (SaveSlotWindowDefinition window in SaveSlotWindowDefinitions)
        {
            byte[] preview = new byte[SaveSlotPreviewBytes];
            if (!protocol.TryReadSaveSlotWindow((byte)gameSlot, window.Offset, preview))
            {
                throw new InvalidOperationException(Tf(
                    "error",
                    "fragment_test_window_read_failed",
                    "Failed to read save-slot window: slot #{0:000}, offset 0x{1:X5}. If 0xA5 works but this read does not, the firmware may be too old for logical save-slot window reads.",
                    gameSlot + 1,
                    window.Offset));
            }

            AppendLog(Tf(
                "log",
                "fragment_test_slot_window",
                "  {0} {1} @ +0x{2:X5} | sig=0x{3:X8} | {4}",
                window.Name,
                DescribeWindowRole(summaryFlags, window.BankIndex, window.CopyB),
                window.Offset,
                ComputePreviewSignature(preview),
                FormatPreviewBytes(preview)));
        }
    }

    private static List<int> BuildFragmentTestSlots(WaterCartScanResult scanSnapshot)
    {
        List<int> wlSlots = scanSnapshot.WlMapping.Records
            .Select(record => record.EntrySlot)
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();
        if (wlSlots.Count > 0)
        {
            return wlSlots;
        }

        List<int> menuSlots = scanSnapshot.MenuConfig.Items
            .Select(item => item.Index)
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();
        if (menuSlots.Count > 0)
        {
            return menuSlots;
        }

        return Enumerable.Range(0, SaveSlotTestCount).ToList();
    }

    private static WaterSaveSlotSummaryInfo ParseSaveSlotSummary(byte[] payload)
    {
        if (payload.Length < 5)
        {
            throw new InvalidOperationException("Save-slot summary payload is too short.");
        }

        uint magic = BitConverter.ToUInt32(payload, 0);
        if (magic != SaveSlotSummaryMagic)
        {
            throw new InvalidOperationException($"Save-slot summary magic mismatch: 0x{magic:X8}.");
        }

        int slotCount = payload[4] & SaveSlotSummaryCountMask;
        if (slotCount < 0 || payload.Length < 5 + slotCount)
        {
            throw new InvalidOperationException("Save-slot summary payload length is invalid.");
        }

        byte[] entries = new byte[SaveSlotTestCount];
        int copyCount = Math.Min(SaveSlotTestCount, slotCount);
        if (copyCount > 0)
        {
            Array.Copy(payload, 5, entries, 0, copyCount);
        }

        return new WaterSaveSlotSummaryInfo
        {
            IsAvailable = true,
            HasFullAbPreview = (payload[4] & SaveSlotSummaryFlagFullAb) != 0,
            Entries = entries,
        };
    }

    private static bool SlotHasAnyData(byte summaryFlags) =>
        (summaryFlags & SaveSlotEntrySlotHasAnyData) != 0;

    private static bool IsBankSelectB(byte summaryFlags, int bankIndex) =>
        bankIndex == 0
            ? (summaryFlags & SaveSlotEntryBank0SelectB) != 0
            : (summaryFlags & SaveSlotEntryBank1SelectB) != 0;

    private static bool ActiveBankHasData(byte summaryFlags, int bankIndex) =>
        bankIndex == 0
            ? (summaryFlags & SaveSlotEntryBank0ActiveHasData) != 0
            : (summaryFlags & SaveSlotEntryBank1ActiveHasData) != 0;

    private static bool InactiveBankHasData(byte summaryFlags, int bankIndex) =>
        bankIndex == 0
            ? (summaryFlags & SaveSlotEntryBank0InactiveHasData) != 0
            : (summaryFlags & SaveSlotEntryBank1InactiveHasData) != 0;

    private static string DescribeBankCopy(WaterSaveSlotSummaryInfo summary, byte summaryFlags, int bankIndex, bool copyB)
    {
        bool selectedB = IsBankSelectB(summaryFlags, bankIndex);
        bool activeCopy = selectedB == copyB;
        string copyText = copyB ? "B" : "A";
        string dataState;

        if (activeCopy)
        {
            dataState = ActiveBankHasData(summaryFlags, bankIndex) ? "data" : "blank";
        }
        else if (summary.HasFullAbPreview)
        {
            dataState = InactiveBankHasData(summaryFlags, bankIndex) ? "data" : "blank";
        }
        else
        {
            dataState = "unknown";
        }

        return $"{copyText}:{dataState}{(activeCopy ? "*" : string.Empty)}";
    }

    private static string DescribeWindowRole(byte summaryFlags, int bankIndex, bool copyB)
    {
        bool activeCopy = IsBankSelectB(summaryFlags, bankIndex) == copyB;
        return activeCopy ? "(active)" : "(inactive)";
    }

    private static uint ComputePreviewSignature(ReadOnlySpan<byte> data)
    {
        uint hash = 2166136261u;
        foreach (byte value in data)
        {
            hash ^= value;
            hash *= 16777619u;
        }

        return hash;
    }

    private static string FormatPreviewBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return "empty";
        }

        bool allFf = true;
        bool all00 = true;
        foreach (byte value in data)
        {
            allFf &= value == 0xFF;
            all00 &= value == 0x00;
        }

        if (allFf)
        {
            return "all FF";
        }

        if (all00)
        {
            return "all 00";
        }

        int prefixLength = Math.Min(16, data.Length);
        byte[] prefix = data[..prefixLength].ToArray();
        string hex = BitConverter.ToString(prefix).Replace("-", " ");
        var ascii = new StringBuilder(prefixLength);
        foreach (byte value in prefix)
        {
            ascii.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
        }

        return $"hex[{prefixLength}]={hex} | ascii={ascii}";
    }

    private static string FormatSaveDelayWord(uint saveDelayMs)
    {
        return saveDelayMs is >= 100u and <= 10000u
            ? $"{saveDelayMs} ms"
            : $"raw=0x{saveDelayMs:X8} (blank/out-of-range; runtime falls back to 1000 ms)";
    }

    private static string ShortenForFragmentLog(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        if (maxLength <= 3)
        {
            return normalized[..maxLength];
        }

        return normalized[..(maxLength - 3)] + "...";
    }
}
