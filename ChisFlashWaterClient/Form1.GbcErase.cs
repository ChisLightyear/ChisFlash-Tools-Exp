using System.Diagnostics;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private WaterCartScanResult? GetEntireNorEraseScanSnapshot() => IsFcWorkspaceActive
        ? fcWorkspace?.Snapshot is not null && CanUseFcDevice ? fcHardwareInfo : null
        : HasActiveScanResult() ? currentScanResult : null;

    private void EraseEntireGbcNorOnDevice(
        string portName,
        ProgrammerKind programmerKind,
        WaterCartScanResult scan,
        int eraseTimeoutMs = 20 * 60 * 1000)
    {
        int capacity = scan.Geometry.DeviceSizeBytes;
        if (capacity <= 0 || capacity > 512 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (eraseTimeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(eraseTimeoutMs));

        const int RomBankBytes = 0x4000;
        using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, programmerKind);
        BeginMeasuredProgress(T("ui", "erase_entire_nor", "Erase Entire NOR"),
            capacity, (capacity + RomBankBytes - 1) / RomBankBytes);
        bool bankRestored = false;

        try
        {
            if (!TrySwitchBeggarMaxRomBank(protocol, 0) ||
                !protocol.TryWriteGbc(0x0000, [0xF0], 3000))
                throw new IOException(T("error", "gbc_erase_prepare_failed", "Failed to prepare the GB NOR chip for erase."));

            foreach (var command in new (uint Address, byte Value)[]
            {
                (0x0AAA, 0xAA), (0x0555, 0x55), (0x0AAA, 0x80),
                (0x0AAA, 0xAA), (0x0555, 0x55), (0x0AAA, 0x10),
            })
            {
                if (!protocol.TryWriteGbc(command.Address, [command.Value], 3000))
                    throw new IOException(Tf("error", "gbc_erase_command_failed",
                        "GB chip erase command failed at 0x{0:X4}: {1}", command.Address, protocol.DescribeLastAckFailure()));
            }

            var timer = Stopwatch.StartNew();
            byte[] status = new byte[1];
            while (true)
            {
                if (!protocol.TryReadGbc(0, status, 3000))
                    throw new IOException(T("error", "gbc_erase_poll_failed", "Failed to read GB chip erase status."));
                if (status[0] == 0xFF) break;
                if (timer.ElapsedMilliseconds >= eraseTimeoutMs)
                    throw new TimeoutException(T("error", "gbc_erase_timeout", "GB NOR chip erase timed out."));
                Thread.Sleep(100);
            }

            for (int address = 0; address < capacity; address += RomBankBytes)
            {
                int bank = address / RomBankBytes;
                byte[] data = new byte[Math.Min(RomBankBytes, capacity - address)];
                uint readAddress = (uint)GetBeggarMaxBankBaseAddress(bank);
                if (!TrySwitchBeggarMaxRomBank(protocol, bank) || !protocol.TryReadGbc(readAddress, data, 3000))
                    throw new IOException(Tf("error", "gbc_erase_verify_read_failed",
                        "Failed to verify erased GB NOR at 0x{0:X8}.", address));
                int mismatch = Array.FindIndex(data, value => value != 0xFF);
                if (mismatch >= 0)
                    throw new InvalidDataException(Tf("error", "gbc_erase_verify_failed",
                        "GB NOR erase verification failed at 0x{0:X8}: 0x{1:X2}.", address + mismatch, data[mismatch]));
                AdvanceMeasuredProgress(data.Length, 1,
                    Tf("value", "gbc_erase_verify_progress", "Verified GB NOR: {0}/{1} KiB", (address + data.Length) / 1024, capacity / 1024));
            }

            if (!protocol.TryWriteGbc(0, [0xF0], 3000) || !TrySwitchBeggarMaxRomBank(protocol, 0))
                throw new IOException(T("error", "erase_restore_bank_failed", "Erase completed, but restoring ROM bank 0 failed."));
            bankRestored = true;
            CompleteMeasuredProgress(T("ui", "erase_entire_nor", "Erase Entire NOR"));
        }
        catch
        {
            AbortMeasuredProgress();
            throw;
        }
        finally
        {
            try
            {
                if (!bankRestored && !TrySwitchBeggarMaxRomBank(protocol, 0))
                    AppendLog(T("log", "gbc_erase_restore_failed", "Could not restore GB ROM bank 0; reconnect and rescan before continuing."));
            }
            catch (Exception ex)
            {
                AppendLog(Tf("log", "gbc_erase_restore_exception", "Could not restore GB ROM bank 0: {0}", ex.Message));
            }
        }
    }
}
