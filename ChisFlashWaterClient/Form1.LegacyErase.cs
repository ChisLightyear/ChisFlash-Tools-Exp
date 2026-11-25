namespace ChisFlashWaterClient;

public partial class Form1
{
    private const int LegacyChipErasePollDelayMs = 1000;

    private void EraseEntireLegacyNorOnDevice(string portName, WaterCartScanResult scan, int targetCount)
    {
        const int TargetBytes = 128 * 1024 * 1024;
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
        BeginMeasuredProgress(T("ui", "erase_entire_nor", "Erase Entire NOR"),
            scan.Geometry.DeviceSizeBytes, targetCount);
        BeginMeasuredProgressPhase(targetCount, true, T("status", "erase_entire_nor_busy", "Erasing entire NOR…"));

        int selectedBucket = -1;
        for (int target = 0; target < targetCount; target++)
        {
            int start = target * TargetBytes;
            int length = targetCount == 1 ? Math.Max(0, scan.Geometry.DeviceSizeBytes) : TargetBytes;
            AppendLog(Tf("log", "erase_entire_nor_target_start",
                "Erase NOR target {0}: 0x{1:X8}-0x{2:X8}.", target, start, start + length - 1));

            long eraseStart = StartLandscapeErase();
            if (!TrySelectLegacyBeggarBucket(protocol, start / WaterCartScanner.BucketSizeBytes, ref selectedBucket) ||
                !protocol.TryEraseLegacyRomChip() ||
                !WaitForLegacyBeggarSectorErase(protocol, 0, LegacyChipErasePollDelayMs))
            {
                throw new IOException(Tf("error", "erase_entire_nor_target_failed",
                    "NOR target {0} chip erase failed or timed out: {1}", target, protocol.DescribeLastAckFailure()));
            }

            FinishLandscapeErase(eraseStart, length);
            AdvanceMeasuredProgress(length, 1,
                Tf("value", "erase_entire_nor_target_progress", "Erased target {0}/{1}", target + 1, targetCount));
            AppendLog(Tf("log", "erase_entire_nor_target_done",
                "NOR target {0} erase completed.", target));
        }

        RestoreGbaBatchBank(protocol, selectedBucket);
        CompleteMeasuredProgress(T("ui", "erase_entire_nor", "Erase Entire NOR"));
    }
}
