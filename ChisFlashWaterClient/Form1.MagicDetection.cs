namespace ChisFlashWaterClient;

public partial class Form1
{
    private async Task DetectMagicSupportAsync()
    {
        if (!EnsureSelectedPortHasActiveScan(out string reason))
        {
            AppendLog(reason);
            return;
        }

        string portName = GetSelectedPortName()!;
        ProgrammerKind kind = GetRequiredSelectedProgrammerKind();
        SetBusy(true, T("status", "detect_magic_busy", "Reading Magic capability..."));
        try
        {
            bool declaredSupport = await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, kind);
                return ReadDeclaredMagicSupport(protocol, kind);
            });

            string result = declaredSupport
                ? kind == ProgrammerKind.BeggarLegacy
                    ? T("value", "magic_support_01g", "Detected a standalone 01G/02G card with Legacy and fragmented mapping, 128 KiB SRAM, and battery/FRAM or batteryless save modes.")
                    : T("value", "magic_support_declared", "Device reports WaterLink fixed-layout support. Only the capability flag was read; mapper switching was not tested.")
                : T("value", "magic_support_unknown", "Unable to confirm: this device does not provide a recognized read-only capability flag. ROM menu data alone does not prove hardware support.");
            if (kind == ProgrammerKind.BeggarLegacy && currentScanResult is not null)
            {
                currentScanResult.IsChm1 = declaredSupport;
                RefreshBurnProfileModeOptions(declaredSupport
                    ? GetDetectedBurnProfileMode(currentScanResult, kind, UsesChm1Mode(GetSelectedBurnProfileMode()) ? GetSelectedBurnProfileMode() : BurnProfileMode.Chm1)
                    : GetDefaultBurnProfileMode(kind));
            }
            AppendLog(result);
            SetBusy(false, T("status", "detect_magic_done", "Capability read complete"));
            MessageBox.Show(this, result, T("ui", "detect_magic", "Detect Magic Support"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            SetBusy(false, T("status", "detect_magic_failed", "Capability read failed"));
        }
    }

    private bool ReadDeclaredMagicSupport(WaterDeviceProtocol protocol, ProgrammerKind kind)
    {
        if (kind == ProgrammerKind.BeggarLegacy)
        {
            if (!protocol.TryReadLocalMagicId(out byte[] identifier))
            {
                throw new IOException(T("error", "magic_capability_read_failed", "Unable to read the device capability report."));
            }
            return identifier.AsSpan().SequenceEqual(new byte[] { 0x43, 0x48, 0x4D, 0x31, 1, 3, 63, 8 });
        }

        if (kind != ProgrammerKind.WaterLink) return false;
        if (!protocol.TryReadExtendedId(out byte[] payload))
        {
            throw new IOException(T("error", "magic_capability_read_failed", "Unable to read the device capability report."));
        }

        WaterRuntimeStatusInfo status = WaterCartScanner.ParseExtendedRuntimeStatus(payload);
        return status.IsAvailable && status.SupportsWaterLinkFixedLayout;
    }
}
