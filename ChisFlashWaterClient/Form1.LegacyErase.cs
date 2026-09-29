namespace ChisFlashWaterClient;

public partial class Form1
{
    private void EraseEntireLegacyNorOnDevice(string portName, WaterCartScanResult scan, int targetCount)
    {
        const int TargetBytes = 128 * 1024 * 1024;
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
        BeginMeasuredProgress(T("ui", "erase_entire_nor", "Erase Entire NOR"),
            scan.Geometry.DeviceSizeBytes, targetCount);

        for (int target = 0; target < targetCount; target++)
        {
            int start = target * TargetBytes;
            int length = Math.Min(TargetBytes, scan.Geometry.DeviceSizeBytes - start);
            AppendLog(Tf("log", "erase_entire_nor_target_start",
                "Erase NOR target {0}: 0x{1:X8}-0x{2:X8}.", target, start, start + length - 1));

            if (!TrySwitchLegacyBeggarBucketLocal(protocol, start / WaterCartScanner.BucketSizeBytes, 3000) ||
                !protocol.TryEraseLegacyRomChip() ||
                !WaitForLegacyBeggarSectorErase(protocol, 0, 20 * 60 * 1000))
            {
                throw new IOException(Tf("error", "erase_entire_nor_target_failed",
                    "NOR target {0} chip erase failed or timed out: {1}", target, protocol.DescribeLastAckFailure()));
            }

            VerifyErasedNorTarget(protocol, ProgrammerKind.BeggarLegacy, target, start, length);
            AdvanceMeasuredProgress(length, 1,
                Tf("value", "erase_entire_nor_target_progress", "Verified target {0}/{1}", target + 1, targetCount));
            AppendLog(Tf("log", "erase_entire_nor_target_done",
                "NOR target {0} erase completed and passed bucket sampling verification.", target));
        }

        if (!TrySwitchLegacyBeggarBucketLocal(protocol, 0, 3000))
        {
            throw new IOException(T("error", "erase_restore_bank_failed", "Erase completed, but restoring ROM bank 0 failed."));
        }
        CompleteMeasuredProgress(T("ui", "erase_entire_nor", "Erase Entire NOR"));
    }
}
