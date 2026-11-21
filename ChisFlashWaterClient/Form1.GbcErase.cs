using System.Threading;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private WaterCartScanResult? GetEntireNorEraseScanSnapshot() => IsFcWorkspaceActive
        ? fcHardwareInfo
        : HasActiveScanResult() ? currentScanResult : null;

    private void EraseEntireGbcNorOnDevice(
        string portName,
        ProgrammerKind programmerKind,
        WaterCartScanResult scan)
    {
        int capacity = scan.Geometry.DeviceSizeBytes;
        if (capacity <= 0 || capacity > 512 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);
        BeginMeasuredProgress(T("ui", "erase_entire_nor", "Erase Entire NOR"),
            capacity, 1);
        BeginMeasuredProgressPhase(1, true, T("status", "erase_entire_nor_busy", "Erasing entire NOR…"));
        try
        {
            if (!TrySwitchBeggarMaxRomBank(protocol, 0))
                throw new IOException(T("error", "gbc_erase_prepare_failed", "Failed to prepare the GB NOR chip for erase."));

            long eraseStart = StartLandscapeErase();
            foreach (var command in new (uint Address, byte Value)[]
            {
                (0x0AAA, 0xAA), (0x0555, 0x55), (0x0AAA, 0x80),
                (0x0AAA, 0xAA), (0x0555, 0x55), (0x0AAA, 0x10),
            })
            {
                if (!protocol.TryWriteGbc(command.Address, [command.Value], Timeout.Infinite))
                    throw new IOException(Tf("error", "gbc_erase_command_failed",
                        "GB chip erase command failed at 0x{0:X4}: {1}", command.Address, protocol.DescribeLastAckFailure()));
            }

            byte[] status = new byte[1];
            while (true)
            {
                if (!protocol.TryReadGbc(0, status, Timeout.Infinite))
                    throw new IOException(T("error", "gbc_erase_poll_failed", "Failed to read GB chip erase status."));
                if (status[0] == 0xFF) break;
                Thread.Sleep(LegacyChipErasePollDelayMs);
            }

            FinishLandscapeErase(eraseStart, capacity);
            CompleteMeasuredProgress(T("ui", "erase_entire_nor", "Erase Entire NOR"));
        }
        catch
        {
            AbortMeasuredProgress();
            throw;
        }
    }
}
