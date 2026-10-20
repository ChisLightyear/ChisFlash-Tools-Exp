using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text;
using System.Windows.Forms;
using ChisFlashWaterClient.Services;
using GbaMultiMenuBuilder.Services;
using ClientUiLanguage = ChisFlashWaterClient.Services.UiLanguage;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private const int SaveStatusChunkBytes = 0x2000;

    private sealed record ScanReadAttempt(WaterCartScanResult? Result, string FailureMessage);
    private bool applyingSelectedConfiguration;

    private void RefreshPorts(bool allowFallbackSelection = true, bool resetAnalysisState = false)
    {
        InvalidateFcDevice();
        if (resetAnalysisState)
        {
            ClearGbcProfileCapacity();
            ResetCurrentCartAnalysisView();
        }

        string? previousPortName = GetSelectedPortName();
        ProgrammerKind? previousProgrammerKind = GetSelectedProgrammerKind();

        comboBoxPorts.Items.Clear();
        serialPorts.Clear();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");
            foreach (ManagementObject device in searcher.Get())
            {
                string caption = device["Caption"]?.ToString() ?? string.Empty;
                string name = device["Name"]?.ToString() ?? string.Empty;
                string pnpDeviceId = device["PNPDeviceID"]?.ToString() ?? string.Empty;

                string? portName = ExtractComPortName(caption) ?? ExtractComPortName(name);
                if (string.IsNullOrWhiteSpace(portName))
                {
                    continue;
                }

                string productName = UsbDeviceIdMatches(pnpDeviceId, TargetUsbVid, TargetUsbPid, TargetUsbInterface)
                    ? ReadUsbProductName(device, pnpDeviceId)
                    : string.Empty;
                if (!TryGetSupportedProgrammerKind(pnpDeviceId, productName, out ProgrammerKind kind))
                {
                    continue;
                }

                AddSerialPortEntry(portName, pnpDeviceId, kind, productName);
            }
        }
        catch (ManagementException ex)
        {
            AppendLog(Tf("log", "wmi_port_detect_failed", "WMI port detection failed: {0}", ex.Message));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "port_enum_failed", "Port enumeration failed: {0}", ex.Message));
        }

        RestoreSelectedSerialPort(previousPortName, previousProgrammerKind, allowFallbackSelection);
        UpdateActionButtons(isBusy);

        int waterLinkCount = serialPorts.Values.Count(entry => entry.Kind == ProgrammerKind.WaterLink);
        int beggarCount = serialPorts.Values.Count(entry => entry.Kind == ProgrammerKind.BeggarLegacy);
        int unknownCount = serialPorts.Values.Count(entry => entry.Kind == ProgrammerKind.Unknown);

        AppendLog(comboBoxPorts.Items.Count > 0
            ? Tf("log", "ports_detected", "Detected {0} port(s). WaterLink={1}, Beggar={2}, Unknown={3}.", comboBoxPorts.Items.Count, waterLinkCount, beggarCount, unknownCount)
            : T("log", "ports_not_found", "No serial ports were found."));
    }

    private void ResetCurrentCartAnalysisView()
    {
        lastScannedPortName = string.Empty;
        currentScanResult = null;
        ResetInfoValues();
        workspaceRows.Clear();
        RefreshWorkspaceGrid();
        if (!isBusy && !isAnalyzingQueueImports)
        {
            labelStatus.Text = T("status", "ready", "Ready");
        }
    }

    private async Task ScanSelectedPortAsync()
    {
        string? portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            AppendLog(T("log", "select_port_first", "Please select a serial port first."));
            return;
        }

        if (!EnsureSelectedPortAvailableForScan(ref portName))
        {
            return;
        }

        if (!TryGetSelectedProgrammerKind(out _, out string programmerReason))
        {
            AppendLog(programmerReason);
            return;
        }

        string activePortName = portName!;
        SetBusy(true, Tf("status", "scanning_port", "Reading {0}...", activePortName));
        AppendLog(Tf(
            "log",
            "scan_start",
            "Start reading cart info from {0} via {1}.",
            activePortName,
            GetSelectedProgrammerDisplayName()));

        try
        {
            ScanReadAttempt attempt = await ReadScanResultAsync(activePortName, analyzeConfiguration: false);
            if (attempt.Result is not WaterCartScanResult result)
            {
                ResetCurrentCartAnalysisView();
                AppendLog(Tf("log", "scan_failed", "Read failed: {0}", attempt.FailureMessage));
                SetBusy(false, T("status", "scan_failed", "Read failed"));
                return;
            }

            if (IsFcWorkspaceActive)
            {
                UpdateFcHardwareInfo(result);
                SetBusy(false, T("status", "scan_complete", "Read complete"));
                return;
            }
            ApplyScanResult(result);
            if (result.NoCartridgeDetected)
            {
                SetBusy(false, T("status", "scan_failed", "Read failed"));
                return;
            }

            AppendLog(Tf("log", "scan_complete", "Read completed. Output {0} visible entrie(s).", GetWorkspaceBaseEntries(result).Count()));
            SetBusy(false, T("status", "scan_complete", "Read complete"));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("log", "scan_failed", "Read failed: {0}", GetFriendlyScanFailureMessage(ex)));
            SetBusy(false, T("status", "scan_failed", "Read failed"));
        }
    }

    private string GetFriendlyScanFailureMessage(Exception ex, string? portName = null)
    {
        Exception actual = ex.GetBaseException();
        string? selectedPortName = string.IsNullOrWhiteSpace(portName)
            ? GetSelectedPortName()
            : portName;
        if (actual is UnauthorizedAccessException &&
            !string.IsNullOrWhiteSpace(selectedPortName) &&
            ExceptionRefersToSerialPort(actual, selectedPortName))
        {
            return Tf(
                "error",
                "serial_port_in_use",
                "Serial port {0} is being used by another program. Close the other programmer or serial tool, then retry.",
                selectedPortName);
        }

        if (actual is FileNotFoundException && !string.IsNullOrWhiteSpace(selectedPortName))
        {
            return Tf(
                "error",
                "selected_port_unavailable",
                "The selected serial port {0} is no longer available. Please refresh or reconnect the programmer.",
                selectedPortName);
        }

        if (IsCartMissingOrUnresponsiveScanFailure(actual))
        {
            return T(
                "error",
                "cart_not_detected_or_unresponsive",
                "No cart was detected, or the cart did not respond. Please reseat the cart and try again.");
        }

        return string.IsNullOrWhiteSpace(actual.Message)
            ? T("error", "device_not_responding", "The current programmer device did not respond.")
            : actual.Message;
    }

    private static bool ExceptionRefersToSerialPort(Exception exception, string portName)
    {
        return string.Equals(exception.Source, "System.IO.Ports", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains(portName, StringComparison.OrdinalIgnoreCase);
    }

    private string GetFriendlyUiFailureMessage(Exception ex) =>
        GetFriendlyScanFailureMessage(ex, GetSelectedPortName());

    private bool EnsureSelectedPortAvailableForScan(ref string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return false;
        }

        if (IsPortCurrentlyPresent(portName))
        {
            return true;
        }

        string previousPortName = portName;
        AppendLog(Tf(
            "log",
            "selected_port_missing_refresh",
            "The previously selected port {0} is no longer present. Refreshing the port list.",
            previousPortName));

        RefreshPorts(allowFallbackSelection: false, resetAnalysisState: false);
        portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            if (comboBoxPorts.Items.Count > 0)
            {
                AppendLog(Tf(
                    "log",
                    "selected_port_requires_manual_reselect",
                    "Port {0} is no longer available. The port list was refreshed, but the client will not automatically switch to another programmer. Please select the target port manually.",
                    previousPortName));
            }
            else
            {
                AppendLog(Tf(
                    "log",
                    "selected_port_missing_after_refresh",
                    "Port {0} is no longer available, and no serial device is currently connected.",
                    previousPortName));
            }

            return false;
        }

        return true;
    }

    private static bool IsPortCurrentlyPresent(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return false;
        }

        return SerialPort.GetPortNames()
            .Any(name => string.Equals(name, portName, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsCartMissingOrUnresponsiveScanFailure(Exception ex)
    {
        return ExceptionMessageMatches(ex, "error", "read_nor_id_failed", "Failed to read NOR ID.") ||
               ExceptionMessageMatches(ex, "error", "send_cfi_query_failed", "Failed to send CFI Query.") ||
               ExceptionMessageMatches(ex, "error", "read_cfi_failed", "Failed to read CFI data.");
    }

    private bool ExceptionMessageMatches(Exception ex, string section, string key, string fallback)
    {
        return string.Equals(ex.Message, T(section, key, fallback), StringComparison.Ordinal);
    }

    private async Task ScanSelectedConfigurationAsync()
    {
        if (GetSelectedBurnProfileMode() == BurnProfileMode.FcMbcx)
        {
            await ActivateFcWorkspaceAsync();
            await fcWorkspace!.ScanAsync();
            return;
        }

        string port = GetSelectedPortName() ?? throw new InvalidOperationException(T("log", "select_port_first", "Please select a serial port first."));
        SetBusy(true, T("ui", "analyze_configuration", "分析配置"));
        try
        {
            var attempt = await ReadScanResultAsync(port, analyzeConfiguration: true);
            if (attempt.Result is null) throw new IOException(attempt.FailureMessage);
            applyingSelectedConfiguration = true;
            try { ApplyScanResult(attempt.Result); }
            finally { applyingSelectedConfiguration = false; }
        }
        finally { SetBusy(false, T("status", "ready", "Ready")); }
    }

    private async Task<ScanReadAttempt> ReadScanResultAsync(string portName, bool analyzeConfiguration = true)
    {
        ProgrammerKind programmerKind = GetRequiredSelectedProgrammerKind();
        BeggarCartMode beggarCartMode = GetSelectedBeggarCartMode();
        BurnProfileMode burnProfileMode = GetSelectedBurnProfileMode();

        return await Task.Run(() =>
        {
            try
            {
                using var protocol = new WaterDeviceProtocol(portName, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, programmerKind);
                WaterCartScanResult result;
                if (programmerKind == ProgrammerKind.BeggarLegacy)
                {
                    if (beggarCartMode == BeggarCartMode.Gb)
                    {
                        AppendLog(T(
                            "log",
                            "beggar_scan_mode_gb",
                            "Beggar cart mode is set to GB. Using the GBC/MAX probe."));

                        // The outer Analyze Cart action is intentionally NOR-only.
                        // MIX metadata is read by the dedicated MIX workspace
                        // command after the user selects MIX.
                        if (!WaterCartScanner.TryScanBeggarMaxGbc(protocol, AppendLog, out result, scanContent: analyzeConfiguration))
                        {
                            return new ScanReadAttempt(
                                null,
                                T(
                                    "error",
                                    "beggar_max_probe_failed",
                                    "The GBC/MAX mode probe failed. Please check the cart connection and firmware."));
                        }
                    }
                    else
                    {
                        AppendLog(T(
                            "log",
                            "beggar_scan_mode_gba",
                            "Beggar cart mode is set to GBA. Using the legacy NOR scan."));
                        result = WaterCartScanner.ScanLegacyBeggar(protocol, AppendLog, scanContent: analyzeConfiguration, scanGameHeaders: !analyzeConfiguration);
                        bool isChm1 = burnProfileMode != BurnProfileMode.ChisFlash10 && analyzeConfiguration && !IsSingleGameOnlyGbaScan(result) && ReadDeclaredMagicSupport(protocol, programmerKind);
                        if (isChm1) AppendLog(T("log", "chm1_detected", "Standalone 01G/02G fragment management cartridge detected."));
                        result.IsChm1 = isChm1;
                        if (result.UsesChm1Layout)
                            result.MenuConfig.Status.ExplicitSaveAreaAddress = Chm1Layout.SaveAreaOffset;
                    }
                }
                else
                {
                    result = WaterCartScanner.Scan(
                        protocol,
                        AppendLog,
                        useWaterLinkMultiNor: burnProfileMode == BurnProfileMode.WaterLinkMultiNor,
                        scanContent: analyzeConfiguration,
                        scanGameHeaders: !analyzeConfiguration && beggarCartMode == BeggarCartMode.Gba);
                }

                if (analyzeConfiguration && burnProfileMode is (BurnProfileMode.Mbc5EightMiB or BurnProfileMode.GbcSixteenInOne))
                {
                    int fixedCapacity = burnProfileMode == BurnProfileMode.GbcSixteenInOne ? 0x2000000 : 0x800000;
                    if (result.IsBeggarMaxGbcMode && result.Geometry.DeviceSizeBytes == fixedCapacity)
                        result.Mbc5FixedSlotRomImage = ReadMbc5ImageFromDevice(protocol, fixedCapacity);
                }
                else if (analyzeConfiguration && !IsSingleGameOnlyGbaScan(result) && (!result.IsBeggarMaxGbcMode || burnProfileMode == BurnProfileMode.BeggarMaxSram) && !result.IsFcMbcxContent && !result.IsMdMbcxContent)
                {
                    PopulateSavePresenceForScan(protocol, result, programmerKind);
                    PopulateMenuBackgroundPreviewForScan(protocol, result, programmerKind);
                }
                return new ScanReadAttempt(result, string.Empty);
            }
            catch (Exception ex)
            {
                return new ScanReadAttempt(null, GetFriendlyScanFailureMessage(ex, portName));
            }
        });
    }

    private bool TryApplyScanReadAttempt(ScanReadAttempt attempt, out string failureMessage)
    {
        if (attempt.Result is null)
        {
            ClearGbcProfileCapacity();
            RefreshBurnProfileModeOptions(BurnProfileMode.GbcUnselected);
            ResetCurrentCartAnalysisView();
            failureMessage = attempt.FailureMessage;
            return false;
        }

        ApplyScanResult(attempt.Result);
        failureMessage = string.Empty;
        return true;
    }

    private void PopulateMenuBackgroundPreviewForScan(
        WaterDeviceProtocol protocol,
        WaterCartScanResult result,
        ProgrammerKind programmerKind)
    {
        result.MenuBackgroundBitmapData = [];
        result.MenuBackgroundPaletteData = [];
        result.IsUniversalGbaMenuDetected = false;
        result.IsSimplifiedNsGbaMenuDetected = false;
        result.GbaMenuFirstScreenPreview = new GbaMenuFirstScreenPreviewInfo();

        if (result.NoCartridgeDetected || !result.MenuConfig.IsDetected)
        {
            return;
        }

        int menuAreaLength = result.MenuConfig.Status.ItemListAddress;
        if (menuAreaLength <= 0 || menuAreaLength > WaterCartScanner.BucketSizeBytes)
        {
            return;
        }

        byte[] currentMenuArea;
        try
        {
            currentMenuArea = ReadRomRangeFromDevice(protocol, programmerKind, 0, menuAreaLength);
        }
        catch
        {
            return;
        }

        result.IsUniversalGbaMenuDetected = GbaMenuRomService.SupportsCovers(currentMenuArea);
        result.IsSimplifiedNsGbaMenuDetected = GbaMenuRomService.HasSimplifiedNsMenuSignature(currentMenuArea);

        result.GbaMenuFirstScreenPreview = GbaMenuFirstScreenPreviewCodec.Decode(
            currentMenuArea,
            result.MenuConfig.Items);

        if (!TryResolveMenuBackgroundRegion(currentMenuArea, UsesWlMode(result), out int backgroundOffset))
        {
            return;
        }

        const int bitmapLength = BackgroundImageEncoder.Width * BackgroundImageEncoder.Height;
        const int paletteLength = 0x200;
        if (backgroundOffset < 0 ||
            backgroundOffset + bitmapLength + paletteLength > currentMenuArea.Length)
        {
            return;
        }

        result.MenuBackgroundBitmapData = currentMenuArea.AsSpan(backgroundOffset, bitmapLength).ToArray();
        result.MenuBackgroundPaletteData = currentMenuArea.AsSpan(backgroundOffset + bitmapLength, paletteLength).ToArray();
    }

    private void PopulateSavePresenceForScan(
        WaterDeviceProtocol protocol,
        WaterCartScanResult result,
        ProgrammerKind programmerKind)
    {
        if (programmerKind == ProgrammerKind.WaterLink)
        {
            (Dictionary<int, bool> waterLinkPresence, Dictionary<int, string> waterLinkStatusText) =
                ReadWaterLinkSavePresence(protocol, result);
            result.WaterLinkSaveDataByEntrySlot = waterLinkPresence;
            result.WaterLinkSaveStatusTextByEntrySlot = waterLinkStatusText;
            return;
        }

        if (programmerKind != ProgrammerKind.BeggarLegacy)
        {
            return;
        }

        if (result.IsBeggarMaxGbcMode)
        {
            (Dictionary<int, bool> maxPresence, Dictionary<int, string> maxStatusText) =
                ReadBeggarMaxSavePresence(protocol, result);
            result.LegacySaveDataByEntrySlot = maxPresence;
            result.LegacySaveStatusTextByEntrySlot = maxStatusText;
            return;
        }

        BurnProfileMode detectedMode = GetDetectedBurnProfileMode(
            result,
            programmerKind,
            GetDefaultBurnProfileMode(programmerKind));

        if (UsesBatterylessPatch(detectedMode))
        {
            result.BatterylessSaveDataByEntrySlot = ReadBatterylessSavePresence(protocol, result, programmerKind);
            return;
        }

        (Dictionary<int, bool> legacyPresence, Dictionary<int, string> legacyStatusText) =
            ReadLegacyMenuSavePresence(protocol, result, programmerKind);
        result.LegacySaveDataByEntrySlot = legacyPresence;
        result.LegacySaveStatusTextByEntrySlot = legacyStatusText;
    }

    private (Dictionary<int, bool> Presence, Dictionary<int, string> StatusText) ReadWaterLinkSavePresence(
        WaterDeviceProtocol protocol,
        WaterCartScanResult result)
    {
        if (!TryReadWaterLinkSelectorState(protocol, out byte[] bank0CopySelect, out byte[] bank1CopySelect))
        {
            return ([], []);
        }

        return BuildWaterLinkSaveStateFromSelectors(protocol, result, bank0CopySelect, bank1CopySelect);
    }

    private (Dictionary<int, bool> Presence, Dictionary<int, string> StatusText) BuildWaterLinkSaveStateFromSelectors(
        WaterDeviceProtocol protocol,
        WaterCartScanResult result,
        byte[] bank0CopySelect,
        byte[] bank1CopySelect)
    {
        Dictionary<int, bool> map = [];
        Dictionary<int, string> textMap = [];

        foreach (WaterCartEntry entry in result.Entries.Where(entry => !entry.IsMenu && entry.EntrySlot >= 0))
        {
            string occupancyText = ReadWaterLinkSelectedSaveOccupancyText(
                protocol,
                entry.EntrySlot,
                bank0CopySelect,
                bank1CopySelect,
                out bool activeSaveHasData);
            if (!string.IsNullOrWhiteSpace(occupancyText))
            {
                textMap[entry.EntrySlot] = occupancyText;
            }

            map[entry.EntrySlot] = activeSaveHasData;
        }

        return (map, textMap);
    }

    private string ReadWaterLinkSelectedSaveOccupancyText(
        WaterDeviceProtocol protocol,
        int entrySlot,
        byte[] bank0CopySelect,
        byte[] bank1CopySelect,
        out bool hasAnyData)
    {
        hasAnyData = false;
        if (entrySlot < 0 || entrySlot >= WaterSaveSlotSummaryInfo.SlotCount)
        {
            return string.Empty;
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
            AppendLog(Tf(
                "log",
                "wl_save_distribution_direct_read_failed",
                "WL save distribution direct SPI read failed: slot #{0:000}, bank0=0x{1:X6}, bank1=0x{2:X6}.",
                entrySlot + 1,
                bank0Address,
                bank1Address));
            return string.Empty;
        }

        return BuildLocalSaveOccupancyText(0x20000, saveData, out hasAnyData);
    }

    private bool TryReadWaterLinkSelectedSaveImage(
        WaterDeviceProtocol protocol,
        int entrySlot,
        byte[] bank0CopySelect,
        byte[] bank1CopySelect,
        out byte[] saveData,
        out uint bank0Address,
        out uint bank1Address)
    {
        saveData = new byte[0x20000];
        uint slotBaseAddress = GetWaterLinkSaveSlotBaseAddress(entrySlot);
        bool bank0SelectB = entrySlot < bank0CopySelect.Length && bank0CopySelect[entrySlot] != 0;
        bool bank1SelectB = entrySlot < bank1CopySelect.Length && bank1CopySelect[entrySlot] != 0;
        bank0Address = slotBaseAddress + (bank0SelectB ? 0x10000u : 0x00000u);
        bank1Address = slotBaseAddress + (bank1SelectB ? 0x30000u : 0x20000u);

        byte[] bank0 = new byte[0x10000];
        byte[] bank1 = new byte[0x10000];
        if (!protocol.TryReadSpiFlashRange(bank0Address, bank0, 4096, 3000) ||
            !protocol.TryReadSpiFlashRange(bank1Address, bank1, 4096, 3000))
        {
            return false;
        }

        Buffer.BlockCopy(bank0, 0, saveData, 0, bank0.Length);
        Buffer.BlockCopy(bank1, 0, saveData, bank0.Length, bank1.Length);
        return true;
    }

    private bool TryReadWaterLinkSelectorState(
        WaterDeviceProtocol protocol,
        out byte[] bank0CopySelect,
        out byte[] bank1CopySelect)
    {
        bank0CopySelect = new byte[WaterSaveSlotSummaryInfo.SlotCount];
        bank1CopySelect = new byte[WaterSaveSlotSummaryInfo.SlotCount];
        byte[] journalBytes = new byte[0x30000];
        if (!protocol.TryReadSpiFlashRange(0x010000u, journalBytes, 4096, 3000))
        {
            AppendLog(T("log", "wl_selector_journal_direct_read_failed", "WL selector journal direct SPI read failed."));
            return false;
        }

        for (int offset = 0; offset + 16 <= journalBytes.Length; offset += 16)
        {
            uint word0 = BitConverter.ToUInt32(journalBytes, offset);
            uint word1 = BitConverter.ToUInt32(journalBytes, offset + 4);
            uint word2 = BitConverter.ToUInt32(journalBytes, offset + 8);
            uint word3 = BitConverter.ToUInt32(journalBytes, offset + 12);

            if (word0 == 0xFFFFFFFFu &&
                word1 == 0xFFFFFFFFu &&
                word2 == 0xFFFFFFFFu &&
                word3 == 0xFFFFFFFFu)
            {
                break;
            }

            if (word0 != 0x534A4E31u ||
                word2 != ~word1 ||
                (word1 & ~0x3FFu) != 0u)
            {
                continue;
            }

            int slot = (int)(word1 & 0xFFu);
            if (slot < 0 || slot >= WaterSaveSlotSummaryInfo.SlotCount)
            {
                continue;
            }

            bank0CopySelect[slot] = (byte)((word1 >> 8) & 0x1u);
            bank1CopySelect[slot] = (byte)((word1 >> 9) & 0x1u);
        }

        return true;
    }

    private (Dictionary<int, bool> Presence, Dictionary<int, string> StatusText) ReadLegacyMenuSavePresence(
        WaterDeviceProtocol protocol,
        WaterCartScanResult result,
        ProgrammerKind programmerKind)
    {
        Dictionary<int, bool> map = [];
        Dictionary<int, string> textMap = [];
        if (!result.MenuConfig.IsDetected)
        {
            return (map, textMap);
        }

        int sectorSize = result.MenuConfig.Status.SectorSize > 0
            ? result.MenuConfig.Status.SectorSize
            : result.Geometry.SectorSizeBytes;
        if (sectorSize <= 0)
        {
            return (map, textMap);
        }

        uint saveAreaBase = (uint)result.MenuConfig.Status.SaveAreaAddress;
        foreach (WaterCartEntry entry in result.Entries.Where(entry => !entry.IsMenu && entry.EntrySlot >= 0))
        {
            bool hasData = false;
            if (entry.SaveType is > 0 && entry.SaveIndex.HasValue)
            {
                uint saveAddress = saveAreaBase + (uint)(entry.SaveIndex.Value * sectorSize);
                string occupancyText = ReadSaveOccupancyText(protocol, programmerKind, saveAddress, sectorSize, out hasData);
                if (!string.IsNullOrWhiteSpace(occupancyText))
                {
                    textMap[entry.EntrySlot] = occupancyText;
                }
            }

            map[entry.EntrySlot] = hasData;
        }

        return (map, textMap);
    }

    private (Dictionary<int, bool> Presence, Dictionary<int, string> StatusText) ReadBeggarMaxSavePresence(
        WaterDeviceProtocol protocol,
        WaterCartScanResult result)
    {
        Dictionary<int, bool> map = [];
        Dictionary<int, string> textMap = [];

        foreach (WaterCartEntry entry in result.Entries.Where(entry => !entry.IsMenu && entry.EntrySlot is >= 0))
        {
            int saveLength = entry.SaveLengthBytes;
            if (saveLength <= 0)
            {
                map[entry.EntrySlot] = false;
                continue;
            }

            if (!TryReadBeggarMaxSaveOccupancy(protocol, entry, saveLength, out bool hasData, out string occupancyText))
            {
                map[entry.EntrySlot] = false;
                continue;
            }

            map[entry.EntrySlot] = hasData;
            if (!string.IsNullOrWhiteSpace(occupancyText))
            {
                textMap[entry.EntrySlot] = occupancyText;
            }
        }

        return (map, textMap);
    }

    private Dictionary<int, bool> ReadBatterylessSavePresence(
        WaterDeviceProtocol protocol,
        WaterCartScanResult result,
        ProgrammerKind programmerKind)
    {
        Dictionary<int, bool> map = [];
        foreach (WaterCartEntry entry in result.Entries.Where(entry => !entry.IsMenu && entry.EntrySlot >= 0))
        {
            bool hasData = false;
            if (TryLocateBatterylessSaveRegion(
                protocol,
                programmerKind,
                entry,
                result.Geometry.DeviceSizeBytes,
                out uint saveAddress,
                out int saveLength) &&
                TryIsRomRangeAllFf(protocol, programmerKind, saveAddress, saveLength, out bool isAllFf))
            {
                hasData = !isAllFf;
            }

            map[entry.EntrySlot] = hasData;
        }

        return map;
    }

    private bool TryLocateBatterylessSaveRegion(
        WaterDeviceProtocol protocol, ProgrammerKind programmerKind, WaterCartEntry entry,
        int deviceSizeBytes, out uint saveAddress, out int saveLength) =>
        TryLocateBatterylessSaveRegion(entry, deviceSizeBytes,
            (address, buffer) => TryReadRomRangeForProgrammer(protocol, programmerKind, address, buffer),
            out saveAddress, out saveLength);

    private static bool TryLocateBatterylessSaveRegion(
        WaterCartEntry entry, int deviceSizeBytes, Func<uint, byte[], bool> readPhysical,
        out uint saveAddress, out int saveLength)
    {
        saveAddress = 0;
        saveLength = 0;
        var buckets = entry.PhysicalBuckets;
        uint logicalSize = checked((uint)buckets.Count * WaterCartScanner.BucketSizeBytes);
        bool ReadLogical(uint address, byte[] buffer)
        {
            if ((ulong)address + (uint)buffer.Length > logicalSize) return false;
            for (int offset = 0; offset < buffer.Length;)
            {
                uint logical = address + (uint)offset;
                int bank = (int)(logical / WaterCartScanner.BucketSizeBytes);
                int within = (int)(logical % WaterCartScanner.BucketSizeBytes);
                int count = Math.Min(buffer.Length - offset, WaterCartScanner.BucketSizeBytes - within);
                uint physical = checked((uint)(buckets[bank] * WaterCartScanner.BucketSizeBytes + within));
                if ((ulong)physical + (uint)count > (ulong)deviceSizeBytes) return false;
                byte[] chunk = new byte[count];
                if (!readPhysical(physical, chunk)) return false;
                chunk.CopyTo(buffer, offset);
                offset += count;
            }
            return true;
        }

        byte[] boot = new byte[4];
        if (!ReadLogical(0, boot)) return false;
        uint searchAddress = ((BitConverter.ToUInt32(boot, 0) & 0x00FFFFFFu) + 2u) << 2;
        if (searchAddress >= logicalSize) return false;
        byte[] searchBuffer = new byte[Math.Min(0x2000u, logicalSize - searchAddress)];
        if (!ReadLogical(searchAddress, searchBuffer)) return false;
        byte[] marker = Encoding.ASCII.GetBytes("<3 from Maniac");
        for (int i = 0; i <= searchBuffer.Length - marker.Length - 2; i++)
        {
            if (!searchBuffer.AsSpan(i, marker.Length).SequenceEqual(marker)) continue;
            int payloadSize = searchBuffer[i + 0x0E] | (searchBuffer[i + 0x0F] << 8);
            if (payloadSize == 0) payloadSize = 0x414;
            uint offset = searchAddress + (uint)i + 0x10u;
            if (offset < payloadSize) continue;
            byte[] header = new byte[12];
            if (!ReadLogical(offset - (uint)payloadSize, header)) return false;
            int detectedSize = BitConverter.ToInt32(header, 8);
            // Existing payloads keep the save in one aligned NOR sector.
            if (detectedSize <= 0 || (ulong)offset + (uint)detectedSize > logicalSize ||
                offset % WaterCartScanner.BucketSizeBytes + detectedSize > WaterCartScanner.BucketSizeBytes) return false;
            saveAddress = checked((uint)buckets[(int)(offset / WaterCartScanner.BucketSizeBytes)] * WaterCartScanner.BucketSizeBytes
                + offset % WaterCartScanner.BucketSizeBytes);
            saveLength = detectedSize;
            return true;
        }
        return false;
    }

    private bool TryIsRomRangeAllFf(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        uint startAddress,
        int byteCount,
        out bool isAllFf)
    {
        isAllFf = false;
        byte[] buffer = new byte[Math.Min(4096, Math.Max(1, byteCount))];

        for (int offset = 0; offset < byteCount; offset += buffer.Length)
        {
            int readLength = Math.Min(buffer.Length, byteCount - offset);
            byte[] chunk = readLength == buffer.Length ? buffer : new byte[readLength];
            if (!TryReadRomRangeForProgrammer(protocol, programmerKind, startAddress + (uint)offset, chunk))
            {
                return false;
            }

            for (int i = 0; i < readLength; i++)
            {
                if (chunk[i] != 0xFF)
                {
                    isAllFf = false;
                    return true;
                }
            }
        }

        isAllFf = true;
        return true;
    }

    private bool TryReadBeggarMaxSaveOccupancy(
        WaterDeviceProtocol protocol,
        WaterCartEntry entry,
        int saveLengthBytes,
        out bool hasAnyData,
        out string occupancyText)
    {
        hasAnyData = false;
        occupancyText = string.Empty;

        if (saveLengthBytes <= 0)
        {
            return false;
        }

        if (!TryReadBeggarMaxRamImage(protocol, entry, saveLengthBytes, out byte[] saveData))
        {
            return false;
        }

        occupancyText = BuildBeggarMaxSaveOccupancyText(entry, saveLengthBytes, saveData, out hasAnyData);
        return true;
    }

    private bool TryReadBeggarMaxRamImage(
        WaterDeviceProtocol protocol,
        WaterCartEntry entry,
        int saveLengthBytes,
        out byte[] saveData)
    {
        saveData = [];
        if (saveLengthBytes <= 0)
        {
            return false;
        }

        if (!TryConfigureBeggarMaxGameMapping(protocol, entry))
        {
            return false;
        }

        if (!TrySetBeggarMaxRamEnabled(protocol, true))
        {
            return false;
        }

        try
        {
            byte[] buffer = new byte[saveLengthBytes];
            int readOffset = 0;
            int currentBank = -1;
            int[] ramBankSequence = ResolveBeggarMaxSaveReadBanks(entry);
            if (ramBankSequence.Length == 0)
            {
                return false;
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
                        return false;
                    }
                }

                byte[] chunk = new byte[readLength];
                if (!protocol.TryReadGbc((uint)(0xA000 + bankOffset), chunk, 3000))
                {
                    return false;
                }

                Buffer.BlockCopy(chunk, 0, buffer, readOffset, readLength);
                readOffset += readLength;
            }

            saveData = buffer;
            return true;
        }
        finally
        {
            TrySetBeggarMaxRamEnabled(protocol, false);
        }
    }

    private static bool TrySetBeggarMaxRamEnabled(WaterDeviceProtocol protocol, bool enabled)
    {
        byte value = enabled ? (byte)0x0A : (byte)0x00;
        return protocol.TryWriteGbc(0x0000, [value], 3000);
    }

    private static bool TrySwitchBeggarMaxRamBank(WaterDeviceProtocol protocol, int bank)
    {
        return protocol.TryWriteGbc(0x4000, [(byte)(bank & 0xFF)], 3000);
    }

    private bool TryIsRomRangeEmptyForSaveStatus(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        uint startAddress,
        int byteCount,
        out bool isEmpty)
    {
        isEmpty = false;
        byte[] buffer = new byte[Math.Min(4096, Math.Max(1, byteCount))];
        bool allFf = true;
        bool all00 = true;

        for (int offset = 0; offset < byteCount; offset += buffer.Length)
        {
            int readLength = Math.Min(buffer.Length, byteCount - offset);
            byte[] chunk = readLength == buffer.Length ? buffer : new byte[readLength];
            if (!TryReadRomRangeForProgrammer(protocol, programmerKind, startAddress + (uint)offset, chunk))
            {
                return false;
            }

            for (int i = 0; i < readLength; i++)
            {
                byte value = chunk[i];
                allFf &= value == 0xFF;
                all00 &= value == 0x00;
                if (!allFf && !all00)
                {
                    isEmpty = false;
                    return true;
                }
            }
        }

        isEmpty = allFf || all00;
        return true;
    }

    private static bool IsSaveStatusChunkEmpty(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return true;
        }

        bool allFf = true;
        bool all00 = true;
        foreach (byte value in chunk)
        {
            allFf &= value == 0xFF;
            all00 &= value == 0x00;
            if (!allFf && !all00)
            {
                return false;
            }
        }

        return true;
    }

    private string ReadSaveOccupancyText(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        uint startAddress,
        int byteCount,
        out bool hasAnyData)
    {
        hasAnyData = false;
        if (byteCount <= 0)
        {
            return string.Empty;
        }

        int chunkCount = Math.Max(1, (byteCount + SaveStatusChunkBytes - 1) / SaveStatusChunkBytes);
        bool[] occupiedChunks = new bool[chunkCount];

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            int offset = chunkIndex * SaveStatusChunkBytes;
            int chunkLength = Math.Min(SaveStatusChunkBytes, byteCount - offset);
            if (chunkLength <= 0)
            {
                break;
            }

            if (!TryIsRomRangeEmptyForSaveStatus(protocol, programmerKind, startAddress + (uint)offset, chunkLength, out bool isEmpty))
            {
                hasAnyData = false;
                return string.Empty;
            }

            bool occupied = !isEmpty;
            occupiedChunks[chunkIndex] = occupied;
            hasAnyData |= occupied;
        }

        return FormatSaveOccupancyText(occupiedChunks);
    }

    private static string FormatSaveOccupancyText(IReadOnlyList<bool> occupiedChunks)
    {
        if (occupiedChunks.Count == 0)
        {
            return string.Empty;
        }

        int halfIndex = occupiedChunks.Count / 2;
        StringBuilder builder = new(occupiedChunks.Count * 3);
        for (int i = 0; i < occupiedChunks.Count; i++)
        {
            if (i > 0)
            {
                if (i == halfIndex)
                {
                    builder.Append("　｜　");
                }
                else if (i % 4 == 0)
                {
                    builder.Append('　');
                }
            }

            builder.Append(occupiedChunks[i] ? '●' : '○');
        }

        return builder.ToString();
    }

    private bool TryReadRomRangeForProgrammer(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        uint startAddress,
        byte[] buffer,
        int timeoutMsPerChunk = 3000)
    {
        if (programmerKind != ProgrammerKind.BeggarLegacy)
        {
            return protocol.TryReadRomRange(startAddress, buffer, 4096, timeoutMsPerChunk);
        }

        const int LegacyBeggarReadChunkBytes = 4096;
        int written = 0;
        int currentBucket = -1;
        while (written < buffer.Length)
        {
            uint absoluteAddress = startAddress + (uint)written;
            int bucket = (int)(absoluteAddress / WaterCartScanner.BucketSizeBytes);
            int bucketOffset = (int)(absoluteAddress % WaterCartScanner.BucketSizeBytes);
            int readLength = Math.Min(buffer.Length - written, WaterCartScanner.BucketSizeBytes - bucketOffset);
            readLength = Math.Min(readLength, LegacyBeggarReadChunkBytes);

            if (bucket != currentBucket)
            {
                if (!TrySwitchLegacyBeggarBucketLocal(protocol, bucket, timeoutMsPerChunk))
                {
                    return false;
                }
                currentBucket = bucket;
            }

            byte[] chunk = new byte[readLength];
            if (!protocol.TryReadRom((uint)bucketOffset, chunk, timeoutMsPerChunk))
            {
                return false;
            }

            Buffer.BlockCopy(chunk, 0, buffer, written, readLength);
            written += readLength;
        }

        return true;
    }

    private static bool TrySwitchLegacyBeggarBucketLocal(WaterDeviceProtocol protocol, int bucket, int timeoutMs)
    {
        if (bucket < 0)
        {
            return false;
        }

        int high = ((bucket / 8) & 0x0F) << 4;
        int low = 0x40 | ((bucket % 8) << 3);

        return protocol.TryWriteRam(2, [(byte)high], timeoutMs) &&
               protocol.TryWriteRam(3, [(byte)low], timeoutMs);
    }

    private static string? ExtractComPortName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int start = text.IndexOf("(COM", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += 1;
        int end = text.IndexOf(')', start);
        if (end <= start)
        {
            return null;
        }

        return text[start..end].Trim();
    }

    private static bool TryGetSupportedProgrammerKind(string? pnpDeviceId, string? productName, out ProgrammerKind kind)
    {
        kind = ProgrammerKind.Unknown;

        bool waterLinkUsbIdMatch = UsbDeviceIdMatches(pnpDeviceId, TargetUsbVid, TargetUsbPid, TargetUsbInterface);
        bool beggarUsbIdMatch = UsbDeviceIdMatches(pnpDeviceId, BeggarUsbVid, BeggarUsbPid);
        if (waterLinkUsbIdMatch && HasWaterLinkProductIdentity(productName))
        {
            kind = ProgrammerKind.WaterLink;
            return true;
        }

        if (beggarUsbIdMatch)
        {
            kind = ProgrammerKind.BeggarLegacy;
            return true;
        }

        // Keep unmatched COM devices selectable. The selected GBA/GB mode
        // resolves the command family later; identity alone never blocks use.
        return true;
    }

    private void AddSerialPortEntry(string portName, string pnpDeviceId, ProgrammerKind kind, string? productName = null)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        string baseName = kind switch
        {
            ProgrammerKind.WaterLink => TargetDisplayName,
            ProgrammerKind.BeggarLegacy => BeggarDisplayName,
            _ => T("value", "programmer_unknown", "Unknown"),
        };
        if (kind == ProgrammerKind.Unknown && !string.IsNullOrWhiteSpace(productName))
            baseName += " · " + productName;
        string displayName = $"{baseName} ({portName})";
        while (serialPorts.ContainsKey(displayName))
        {
            displayName += " *";
        }

        var entry = new SerialPortEntry
        {
            DisplayName = displayName,
            PortName = portName,
            IsTargetMcu = true,
            Kind = kind,
            PnpDeviceId = pnpDeviceId ?? string.Empty,
        };

        serialPorts.Add(displayName, entry);
        comboBoxPorts.Items.Add(displayName);
    }

    private bool TrySelectSerialPortByName(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return false;
        }

        for (int i = 0; i < comboBoxPorts.Items.Count; i++)
        {
            string displayName = comboBoxPorts.Items[i]?.ToString() ?? string.Empty;
            if (!serialPorts.TryGetValue(displayName, out SerialPortEntry? entry))
            {
                continue;
            }

            if (string.Equals(entry.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                comboBoxPorts.SelectedIndex = i;
                return true;
            }
        }

        return false;
    }

    private bool TrySelectFirstRecognizedSerialPort()
    {
        for (int i = 0; i < comboBoxPorts.Items.Count; i++)
        {
            string displayName = comboBoxPorts.Items[i]?.ToString() ?? string.Empty;
            if (!serialPorts.TryGetValue(displayName, out SerialPortEntry? entry))
            {
                continue;
            }

            if (entry.Kind is ProgrammerKind.WaterLink or ProgrammerKind.BeggarLegacy or ProgrammerKind.Unknown)
            {
                comboBoxPorts.SelectedIndex = i;
                return true;
            }
        }

        return false;
    }

    private void RestoreSelectedSerialPort(
        string? portName,
        ProgrammerKind? previousProgrammerKind,
        bool allowFallbackSelection = true)
    {
        comboBoxPorts.SelectedIndex = -1;

        bool previousWasRecognized = previousProgrammerKind is ProgrammerKind.WaterLink or ProgrammerKind.BeggarLegacy or ProgrammerKind.Unknown;

        if (previousWasRecognized)
        {
            if (TrySelectSerialPortByName(portName))
            {
                return;
            }
        }
        else if (allowFallbackSelection && TrySelectFirstRecognizedSerialPort())
        {
            return;
        }

        if (TrySelectSerialPortByName(portName))
        {
            return;
        }

        if (!allowFallbackSelection)
        {
            return;
        }

        if (TrySelectFirstRecognizedSerialPort())
        {
            return;
        }

        if (comboBoxPorts.Items.Count > 0)
        {
            comboBoxPorts.SelectedIndex = 0;
        }
    }

    private string? GetSelectedPortName()
    {
        string? displayName = comboBoxPorts.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        return serialPorts.TryGetValue(displayName, out SerialPortEntry? entry)
            ? entry.PortName
            : null;
    }

    private ProgrammerKind? GetSelectedProgrammerKind()
    {
        string? displayName = comboBoxPorts.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(displayName) ||
            !serialPorts.TryGetValue(displayName, out SerialPortEntry? entry))
        {
            return null;
        }

        return entry.Kind;
    }

    // USB identity is only a hint. Unknown COM ports remain usable and follow
    // the command family selected by the GBA/GB controls.
    private static bool IsBeggarCompatibleProgrammer(ProgrammerKind? kind) =>
        kind is ProgrammerKind.BeggarLegacy or ProgrammerKind.Unknown;

    private bool IsBeggarCompatibleSelectedProgrammer() =>
        IsBeggarCompatibleProgrammer(GetSelectedProgrammerKind());

    private string GetSelectedProgrammerDisplayName()
    {
        ProgrammerKind? kind = GetSelectedProgrammerKind();
        return kind switch
        {
            ProgrammerKind.BeggarLegacy => T("value", "programmer_beggar", "Beggar"),
            ProgrammerKind.WaterLink => T("value", "programmer_waterlink", "WaterLink"),
            ProgrammerKind.Unknown => T("value", "programmer_unknown", "Unknown"),
            _ => T("value", "programmer_unknown", "Unknown"),
        };
    }

    private void RefreshBurnProfileModeOptions(BurnProfileMode preferredMode)
    {
        ProgrammerKind? programmerKind = GetSelectedProgrammerKind();
        BurnProfileMode defaultMode = GetDefaultBurnProfileMode(programmerKind);
        BeggarCartMode beggarCartMode = GetSelectedBeggarCartMode();

        burnProfileModeOptions = programmerKind != ProgrammerKind.WaterLink && beggarCartMode == BeggarCartMode.Gb
            ? GetGbcProfileModes(GetGbcProfileCapacity()).Select(mode => new BurnProfileModeOption(GetBurnProfileModeDisplayName(mode), mode)).ToList()
            : programmerKind switch
            {
                ProgrammerKind.BeggarLegacy =>
                [
                    new BurnProfileModeOption(T("value", "burn_profile_waterlink_sram", "WaterLink (SRAM)"), BurnProfileMode.WaterLinkSram),
                    new BurnProfileModeOption(T("value", "burn_profile_beggar_no_battery", "Beggar No Battery (SRAM + Batteryless)"), BurnProfileMode.BeggarNoBattery),
                    new BurnProfileModeOption(T("value", "burn_profile_beggar_fram", "Beggar Battery/FRAM (SRAM)"), BurnProfileMode.BeggarFram),
                ],
                ProgrammerKind.Unknown =>
                [
                    new BurnProfileModeOption(T("value", "burn_profile_waterlink_sram", "WaterLink (SRAM)"), BurnProfileMode.WaterLinkSram),
                    new BurnProfileModeOption(T("value", "burn_profile_beggar_no_battery", "Beggar No Battery (SRAM + Batteryless)"), BurnProfileMode.BeggarNoBattery),
                    new BurnProfileModeOption(T("value", "burn_profile_beggar_fram", "Beggar Battery/FRAM (SRAM)"), BurnProfileMode.BeggarFram),
                ],
                ProgrammerKind.WaterLink =>
                [
                    new BurnProfileModeOption(T("value", "burn_profile_waterlink_sram", "WaterLink (SRAM)"), BurnProfileMode.WaterLinkSram),
                    new BurnProfileModeOption(T("value", "burn_profile_waterlink_multi_nor", "WL multi-nor"), BurnProfileMode.WaterLinkMultiNor),
                ],
                _ =>
                [
                    new BurnProfileModeOption(T("value", "burn_profile_waterlink_sram", "WaterLink (SRAM)"), BurnProfileMode.WaterLinkSram),
                    new BurnProfileModeOption(T("value", "burn_profile_beggar_no_battery", "Beggar No Battery (SRAM + Batteryless)"), BurnProfileMode.BeggarNoBattery),
                    new BurnProfileModeOption(T("value", "burn_profile_beggar_fram", "Beggar Battery/FRAM (SRAM)"), BurnProfileMode.BeggarFram),
                ],
            };

        burnProfileModeSelectionInitialized = false;
        if (beggarCartMode == BeggarCartMode.Gba)
            burnProfileModeOptions.AddRange(GetGbaSingleProfileOptions());
        if (IsBeggarCompatibleProgrammer(programmerKind) && beggarCartMode == BeggarCartMode.Gba && currentScanResult?.IsChm1 == true)
        {
            burnProfileModeOptions.RemoveAll(option => UsesWaterLinkTargetMode(option.Mode));
            burnProfileModeOptions.Insert(0, new BurnProfileModeOption(GetBurnProfileModeDisplayName(BurnProfileMode.Chm1), BurnProfileMode.Chm1));
            burnProfileModeOptions.Insert(1, new BurnProfileModeOption(GetBurnProfileModeDisplayName(BurnProfileMode.Chm1NoBattery), BurnProfileMode.Chm1NoBattery));
        }
        if (IsGbaSingleGameOnlyHardware())
        {
            burnProfileModeOptions = GetGbaSingleProfileOptions().ToList();
            if (!IsGbaSingleProfile(preferredMode)) preferredMode = BurnProfileMode.GbaSingle;
        }
        comboBoxBurnProfileMode.DataSource = null;
        BindBurnProfileOptions();
        comboBoxBurnProfileMode.SelectedItem = burnProfileModeOptions.FirstOrDefault(option => option.Mode == preferredMode)
            ?? burnProfileModeOptions.FirstOrDefault(option => option.Mode == defaultMode)
            ?? burnProfileModeOptions.FirstOrDefault();
        burnProfileModeSelectionInitialized = true;
        comboBoxBurnProfileMode.Enabled = !isBusy && burnProfileModeOptions.Count > 0;
        if (IsMbc5FixedSlotMode()) RefreshMbc5Grid();
        else if (mbc5ActiveProfile is not null)
        {
            HandleMbc5ProfileSelection();
            RefreshWorkspaceGrid();
        }
        ApplyGbcProfileWorkspace();
        ApplyChisFlash10Workspace();
    }

    private void AdjustPortDropDownWidth()
    {
        int width = comboBoxPorts.Width;

        foreach (object item in comboBoxPorts.Items)
        {
            string text = comboBoxPorts.GetItemText(item) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            int itemWidth = TextRenderer.MeasureText(text, comboBoxPorts.Font).Width
                + SystemInformation.VerticalScrollBarWidth
                + 12;
            if (itemWidth > width)
            {
                width = itemWidth;
            }
        }

        int maxWidth = Screen.FromControl(comboBoxPorts).WorkingArea.Width - 40;
        if (maxWidth < comboBoxPorts.Width)
        {
            maxWidth = comboBoxPorts.Width;
        }

        comboBoxPorts.DropDownWidth = Math.Min(width, maxWidth);
    }

    private void ApplyScanResult(WaterCartScanResult result, bool appendUpgradeHint = true)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<WaterCartScanResult, bool>(ApplyScanResult), result, appendUpgradeHint);
            return;
        }

        // 新扫描结果按实际菜单重新建立工作区；同一结果的界面刷新保留草稿。
        if (!ReferenceEquals(currentScanResult, result)) ResetGbaMenuDraft();
        RememberGbcProfileCapacity(result);
        lastScannedPortName = GetSelectedPortName() ?? string.Empty;
        currentScanResult = result;
        RefreshBurnProfileModeOptions(applyingSelectedConfiguration ? GetSelectedBurnProfileMode() :
            GetDetectedBurnProfileMode(result, GetSelectedProgrammerKind(), GetSelectedBurnProfileMode()));
        if (IsMbc5FixedSlotMode() && (result.Mbc5FixedSlotRomImage ?? result.Mbc5EightMiBRomImage) is { } mbc5Image && !ReferenceEquals(mbc5LoadedScanImage, mbc5Image))
        {
            try { LoadMbc5ScanImage(mbc5Image); mbc5LoadedScanImage = mbc5Image; }
            catch { currentScanResult = null; lastScannedPortName = string.Empty; throw; }
        }
        if (result.NoCartridgeDetected)
        {
            currentScanResult = null;
            labelNorIdValue.Text = result.RawNorIdHex;
            labelChipNameValue.Text = T("ui", "not_read", "Not Read");
            labelCapacityValue.Text = T("ui", "not_read", "Not Read");
            labelSectorInfoValue.Text = T("ui", "not_read", "Not Read");
            labelRomCountValue.Text = "0";
            labelScanMethodValue.Text = T("ui", "scan_method_nor_only", "NOR 4MiB Header Scan");
            labelMenuConfigValue.Text = T("ui", "not_detected", "Not Detected");
            labelMenuItemCountValue.Text = "0";
            labelMenuTitleValue.Text = T("ui", "not_detected", "Not Detected");
            UpdateMenuBackgroundPreview(null);
            RebuildWorkspaceRows();
            UpdateActionButtons(isBusy);
            return;
        }

        int visibleEntryCount = GetWorkspaceBaseEntries(result).Count();

        labelNorIdValue.Text = result.RawNorIdHex;
        labelChipNameValue.Text = !string.IsNullOrWhiteSpace(result.ChipName)
            ? result.ChipName
            : WaterCartScanner.GetChipName(result.NorId, result.Geometry.DeviceSizeBytes)
            ?? T("value", "chip_unknown", "Unknown / Needs confirmation");
        labelCapacityValue.Text = BuildCapacityValueText(result);
        labelSectorInfoValue.Text = FormatSectorSummary(result.Geometry);
        labelRomCountValue.Text = visibleEntryCount.ToString(CultureInfo.CurrentCulture);
        labelScanMethodValue.Text = UsesWlMode(result)
            ? T("ui", "scan_method_wl_menu_nor", "WL Mapping + Menu Config + NOR Headers")
            : result.IsBeggarMaxGbcMode
                ? result.Entries.Any(entry => !entry.IsMenu)
                    ? T("ui", "scan_method_gbc_max_headers", "GBC/MAX Flash ID + CFI + MBC5 Header Scan")
                    : T("ui", "scan_method_gbc_max", "GBC/MAX Flash ID + CFI Probe")
            : result.MenuConfig.IsDetected
                ? T("ui", "scan_method_menu_nor", "Menu Config + NOR Headers")
                : T("ui", "scan_method_nor_only", "NOR 4MiB Header Scan");
        labelMenuConfigValue.Text = BuildMenuConfigValue(result);
        labelMenuItemCountValue.Text = result.IsBeggarMaxGbcMode
            ? result.Entries.Count(entry => !entry.IsMenu).ToString(CultureInfo.CurrentCulture)
            : result.MenuConfig.IsDetected
            ? result.MenuConfig.ItemCount.ToString(CultureInfo.CurrentCulture)
            : "0";
        labelMenuTitleValue.Text = BuildMenuTitleValue(result);
        RebuildWorkspaceRows();
        UpdateMenuBackgroundPreview(result);

        if (result.IsNorProbeOnly)
        {
            labelScanMethodValue.Text = GetNorProbeScanMethodText(result);
            labelMenuConfigValue.Text = T("ui", "configuration_not_read", "Configuration not analyzed");
            labelMenuItemCountValue.Text = labelMenuTitleValue.Text = T("ui", "not_read", "Not Read");
            if (!result.HasGbaHeaderScan) labelRomCountValue.Text = T("ui", "not_read", "Not Read");
        }

        if (result.IsWriteProtected && !string.IsNullOrWhiteSpace(result.WriteProtectionReason))
        {
            AppendLog(result.WriteProtectionReason);
        }

        UpdateActionButtons(isBusy);
        if (GetSelectedBeggarCartMode() == BeggarCartMode.Gba && IsSingleGameOnlyGbaScan(result))
        {
            labelScanMethodValue.Text = GbaCardText("single_header_scan", "NOR ID/CFI＋地址0单ROM头", "NOR ID/CFI + single ROM header at 0");
            labelMenuConfigValue.Text = GbaCardText("single_no_config", "单卡，不适用", "Single game; not applicable");
            labelMenuItemCountValue.Text = labelMenuTitleValue.Text = "—";
        }
        if (result.IsNorProbeOnly) labelScanMethodValue.Text = GetNorProbeScanMethodText(result);
        if (!appendUpgradeHint)
        {
            return;
        }

        if (result.IsNorProbeOnly)
        {
            AppendLog(result.HasGbaHeaderScan
                ? GbaCardText("header_scan_configuration_hint", "NOR信息及4MiB游戏头扫描完成。菜单目录和存档信息请使用“卡带 → 分析配置”读取。", "NOR information and 4 MiB headers read. Use Cartridge > Analyze Configuration for menu and save metadata.")
                : T("log", "nor_probe_configuration_hint", "NOR information read. Use Cartridge > Analyze Configuration to read the selected multicart mode."));
            return;
        }

        if (IsMbc5FixedSlotMode()) return;

        if (IsBeggarCompatibleSelectedProgrammer())
        {
            AppendLog(T(
                "log",
                "beggar_programmer_legacy_hint",
                "Detected the Beggar programmer. This port always uses the Beggar erase-then-write path for NOR writes."));
            AppendLog(T(
                "log",
                "beggar_legacy_only_hint",
                "On the Beggar path, selecting the WaterLink flash-cart target unlocks WL menu and fragmented WL mapping management. If you keep a legacy flash-cart target selected, workspace burns stay on the legacy menu/config path."));
            if (result.IsBeggarMaxGbcMode)
            {
                AppendLog(T(
                    "log",
                    "beggar_max_detected_hint",
                    "已在 GBC 模式下识别到 MAX。桌面端现在可以根据当前卡上的内容，重写自带的 MAX/MBCX 菜单并重建索引。"));

                if (!result.HasBeggarMaxDirectoryHeaders &&
                    result.Geometry.DeviceSizeBytes > 0 &&
                    result.Entries.Any(entry => !entry.IsMenu && entry.RomOffsetBank >= 0 && entry.RomBankCount > 0))
                {
                    AppendLog(T(
                        "log",
                        "beggar_max_update_menu_hint",
                        "还没有识别到有效的 MBCX 目录，但 NOR 芯片和 MAX 槽位头部已经识别成功。点击“更新菜单”即可重写自带 MAX 菜单，并按当前识别到的槽位重建索引。"));
                }
            }

            if (result.MenuConfig.IsDetected &&
                !UsesWaterLinkTargetMode(GetSelectedBurnProfileMode()))
            {
                AppendLog(Tf(
                    "log",
                    "beggar_mode_auto_selected",
                    "Legacy menu status indicates battery mode = {0}. The flash-cart mode was auto-selected accordingly.",
                    result.MenuConfig.Status.BatteryPresent
                        ? T("value", "state_on", "On")
                        : T("value", "state_off", "Off")));
            }
        }

        bool hasDetectedMenuHeader = result.Games.Any(game => game.IsMenu);

        if (result.IsChm1 && (NeedsChm1LayoutChange() || !result.MenuConfig.IsDetected))
        {
            AppendLog(T("log", "chm1_convert_first", "Update the menu to convert the cartridge layout before editing its game list."));
        }
        else if (!result.MenuConfig.IsDetected && CanRepairMenuCurrentScan(out _))
        {
            AppendLog(T(
                "log",
                UsesSelectedWaterLinkTargetMode() ? "missing_menu_install_hint_wl" : "missing_menu_install_hint_legacy",
                UsesSelectedWaterLinkTargetMode()
                    ? "No menu config was detected. ROM list editing is locked. Click 'Repair Menu' in the workspace toolbar to erase the head 4 MiB, install the WL menu, and generate a WaterLink multicart."
                    : "No menu config was detected. ROM list editing is locked. Click 'Repair Menu' in the workspace toolbar to erase the head 4 MiB, install the legacy menu, and generate a Chis legacy multicart."));

            WaterCartEntry? headGame = GetHeadAreaGame();
            if (headGame is not null)
            {
                AppendLog(Tf(
                    "log",
                    "head_game_overwrite_warn",
                    "The head 4 MiB area is currently occupied by {0}. Repairing/installing the menu will overwrite that game.",
                    headGame.Title));
            }
        }
        else if (!result.MenuConfig.IsDetected && hasDetectedMenuHeader)
        {
            AppendLog(T(
                "log",
                result.IsBeggarMaxGbcMode && CanUpdateBeggarMaxMenuCurrentScan(out _)
                    ? "beggar_max_update_menu_hint"
                    : "menu_header_without_config_hint",
                result.IsBeggarMaxGbcMode && CanUpdateBeggarMaxMenuCurrentScan(out _)
                    ? "还没有识别到有效的 MBCX 目录，但 NOR 芯片和 MAX 槽位头部已经识别成功。点击“更新菜单”即可重写自带 MAX 菜单，并按当前识别到的槽位重建索引。"
                    : "头部区域检测到菜单 ROM 头，但尚未识别到菜单条目/状态区。找到有效修复路径前，修复菜单功能会保持不可用。"));
        }
        else if (CanRepairLegacyBatterySaveMetadataCurrentScan(out _))
        {
            AppendLog(T(
                "log",
                "legacy_battery_save_repair_hint",
                "Battery/FRAM mode is enabled, but one or more legacy menu entries still use save=NONE. Click 'Repair Menu' to assign save slots to the current entries."));
        }
        else if (UsesSelectedWaterLinkTargetMode() && result.WlMapping.LegacyVersionDetected)
        {
            AppendLog(T(
                "log",
                "wl_mapping_legacy_repair_hint",
                "检测到旧版无校验 WL 映射格式。新菜单不再接受这种格式。点击“更新菜单”即可重写自带新菜单并重建映射区。"));
        }
        else if (CanUpgradeCurrentScan(out _))
        {
            AppendLog(T("log", "legacy_menu_upgrade_hint", "检测到旧菜单，可以点击“更新菜单”。"));
        }
        else if (UsesSelectedWaterLinkTargetMode() && result.MagicMetadataSync.IsApplicable && result.MagicMetadataSync.NeedsRepair)
        {
            AppendLog(Tf(
                "log",
                "magic_metadata_repair_hint",
                "检测到魔术管理元数据不同步。仅菜单项: {0}，仅映射项: {1}，不匹配: {2}，顺序不连续: {3}。可以点击“更新菜单”。",
                FormatSlotListForHint(result.MagicMetadataSync.MenuOnlySlots),
                FormatSlotListForHint(result.MagicMetadataSync.MappingOnlySlots),
                FormatSlotListForHint(result.MagicMetadataSync.SlotRangeMismatchSlots),
                FormatSlotListForHint(result.MagicMetadataSync.NonSequentialRecordSlots)));
        }
    }

    private string GetNorProbeScanMethodText(WaterCartScanResult result) => result.HasGbaHeaderScan
        ? GbaCardText("nor_headers_probe", "NOR ID/CFI＋4MiB游戏头", "NOR ID/CFI + 4 MiB headers")
        : T("ui", "scan_method_nor_probe", "NOR ID / CFI");

    private string BuildMenuConfigValue(WaterCartScanResult result)
    {
        if (result.IsBeggarMaxGbcMode)
        {
            return T("ui", "menu_config_gbc_slots", "MBC5 Header Slots (Read Only)");
        }

        if (result.MenuConfig.IsDetected)
        {
            return UsesWlMode(result)
                ? Tf(
                    "ui",
                    "menu_config_status_wl",
                    "Status 0x{0:X6} / WL 0x{1:X6}",
                    result.MenuConfig.Status.StatusAddress,
                    result.WlMapping.SourceAddress)
                : Tf(
                    "ui",
                    "menu_config_status_itemlist",
                    "Status 0x{0:X6} / ItemList 0x{1:X6}",
                    result.MenuConfig.Status.StatusAddress,
                    result.MenuConfig.Status.ItemListAddress);
        }

        if (UsesWlMode(result))
        {
            return Tf("ui", "menu_config_only_wl", "WL 0x{0:X6}", result.WlMapping.SourceAddress);
        }

        return T("ui", "not_detected", "Not Detected");
    }

    private string BuildMenuTitleValue(WaterCartScanResult result)
    {
        DetectedNorGameInfo? menuGame = result.Games
            .FirstOrDefault(game => game.IsMenu && game.StartBucket == 0)
            ?? result.Games.FirstOrDefault(game => game.IsMenu);

        if (menuGame is null || string.IsNullOrWhiteSpace(menuGame.Title))
        {
            return T("ui", "not_detected", "Not Detected");
        }

        return menuGame.Title;
    }

    private void DataGridViewRoms_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || e.Button != MouseButtons.Right)
        {
            return;
        }

        dataGridViewRoms.ClearSelection();
        dataGridViewRoms.Rows[e.RowIndex].Selected = true;
        if (e.ColumnIndex >= 0)
        {
            dataGridViewRoms.CurrentCell = dataGridViewRoms.Rows[e.RowIndex].Cells[e.ColumnIndex];
        }
    }

    private void RomContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        UpdateSelectionCommandStates();
    }

    private void UpdateSelectionCommandStates()
    {
        if (IsChisFlash10Mode()) { UpdateChisFlash10ActionButtons(isBusy); return; }
        if (IsGbcRawImageMode() || IsGbcProfileUnselected()) { UpdateGbcProfileActionButtons(isBusy); return; }
        if (IsMbc5FixedSlotMode()) { UpdateMbc5ActionButtons(isBusy); return; }
        _ = TryGetSelectedEntry(out WaterCartEntry? selectedEntry);
        bool hasActiveScan = HasActiveScanResult();
        bool scanCapacityInvalid = IsCurrentScanCapacityInvalid();
        bool scanWritable = hasActiveScan && EnsureCurrentScanIsWritable(out _);
        bool canExportRom = false;
        bool canExport = false;
        bool canImport = false;
        bool canClear = false;
        bool canExportChip = false;
        bool canImportChip = false;
        bool canClearChip = false;
        bool canReplace = !IsWorkspaceModificationLocked() &&
            selectedEntry is not null &&
            !selectedEntry.IsMenu &&
            (selectedEntry.FoundInMenuConfig || selectedEntry.FoundInWlMapping || selectedEntry.RomOffsetBank >= 0);
        bool canDelete = !IsWorkspaceModificationLocked() &&
            TryGetSelectedEntry(out WaterCartEntry? entry) &&
            entry is not null &&
            entry.CanDelete;

        if (selectedEntry is not null &&
            currentScanResult is not null &&
            hasActiveScan &&
            !scanCapacityInvalid &&
            !selectedEntry.IsMenu)
        {
            canExportRom = true;
            ProgrammerKind? programmerKind = GetSelectedProgrammerKind();
            bool hasEntrySave = programmerKind == ProgrammerKind.WaterLink
                ? selectedEntry.EntrySlot is >= 0 and < WaterSaveSlotSummaryInfo.SlotCount
                : HasEntryAccessibleSave(selectedEntry, currentScanResult);
            if (!hasEntrySave)
            {
                canExport = false;
                canImport = false;
                canClear = false;
            }
            else if (currentScanResult.IsBeggarMaxGbcMode)
            {
                canExport = selectedEntry.EntrySlot is >= 0 &&
                    selectedEntry.HasBattery &&
                    selectedEntry.SaveLengthBytes > 0;
                canImport = scanWritable && canExport;
                canClear = scanWritable && canExport;
            }
            else if (programmerKind == ProgrammerKind.WaterLink)
            {
                canExport = selectedEntry.EntrySlot >= 0;
                canImport = scanWritable && selectedEntry.EntrySlot >= 0;
                canClear = scanWritable && selectedEntry.EntrySlot >= 0;
            }
            else if (programmerKind == ProgrammerKind.BeggarLegacy &&
                     currentScanResult.MenuConfig.IsDetected &&
                     currentScanResult.MenuConfig.Status.BatteryPresent)
            {
                canExport = selectedEntry.SaveType is > 0 && selectedEntry.SaveIndex.HasValue;
                canImport = scanWritable && canExport;
                canClear = scanWritable && canExport;
            }
            else if (programmerKind == ProgrammerKind.BeggarLegacy)
            {
                canExport = true;
                canImport = scanWritable;
                canClear = scanWritable;
            }

            if (IsCurrentChipSaveEntry(selectedEntry))
            {
                canExportChip = hasEntrySave;
                canImportChip = scanWritable && hasEntrySave;
                canClearChip = scanWritable && hasEntrySave;

                if (currentScanResult.MenuConfig.IsDetected && currentScanResult.MenuConfig.Status.BatteryPresent)
                {
                    canImport = false;
                    canClear = false;
                }
            }
        }

        menuItemReplaceRom.Enabled = canReplace;
        menuItemDeleteMapping.Enabled = canDelete;
        menuItemExportRom.Enabled = canExportRom;
        menuItemExportSave.Enabled = canExport;
        menuItemImportSave.Enabled = canImport;
        menuItemClearSave.Enabled = canClear;
        menuItemChipSaveSeparator.Visible = canExportChip || canImportChip;
        menuItemExportChipSaveContext.Visible = canExportChip;
        menuItemImportChipSaveContext.Visible = canImportChip;
        menuItemClearChipSaveContext.Visible = canClearChip;
        menuItemExportChipSaveContext.Enabled = canExportChip;
        menuItemImportChipSaveContext.Enabled = canImportChip;
        menuItemClearChipSaveContext.Enabled = canClearChip;
        menuReplaceSelectedRom.Enabled = canReplace;
        menuDeleteSelectedMapping.Enabled = canDelete;
        menuExportSelectedRom.Enabled = canExportRom;
        menuExportSelectedSave.Enabled = canExport;
        menuImportSelectedSave.Enabled = canImport;
        menuClearSelectedSave.Enabled = canClear;
        menuChipSaveSeparator.Visible = canExportChip || canImportChip || canClearChip;
        menuExportChipSave.Visible = canExportChip;
        menuImportChipSave.Visible = canImportChip;
        menuClearChipSave.Visible = canClearChip;
        menuExportChipSave.Enabled = canExportChip;
        menuImportChipSave.Enabled = canImportChip;
        menuClearChipSave.Enabled = canClearChip;
    }

    private bool TryGetSelectedEntry(out WaterCartEntry? entry)
    {
        entry = null;
        return TryGetSelectedWorkspaceRow(out WorkspaceListRow? row, out _) &&
               (entry = row?.CartEntry) is not null;
    }

    private void comboBoxLanguage_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (!languageSelectionInitialized || comboBoxLanguage.SelectedItem is not ClientUiLanguage selectedLanguage)
        {
            return;
        }

        if (string.Equals(selectedLanguage.Code, language.Code, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyLanguage(selectedLanguage);
    }

    private string FormatSlotListForHint(IEnumerable<int> slots)
    {
        List<int> slotList = slots
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();

        return slotList.Count == 0
            ? T("value", "none", "None")
            : string.Join(", ", slotList.Select(slot => (slot + 1).ToString("000", CultureInfo.InvariantCulture)));
    }
}
