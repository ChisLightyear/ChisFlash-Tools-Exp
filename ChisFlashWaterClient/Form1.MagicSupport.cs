using ChisFlashWaterClient.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private const int MenuBackgroundRegionLengthBytes = 0x9800;

    private enum MenuUpdateActionMode
    {
        None,
        UpdateBeggarMaxMenu,
        UpdateWaterLinkMenu,
        UpdateLegacyMenu,
    }

    private enum MissingMenuActionMode
    {
        None,
        RepairWaterLinkMagic,
        UpgradeWaterLinkFromLegacyMenu,
        InstallWaterLinkMenu,
        InstallLegacyMenu,
        RepairLegacyBatterySaveMetadata,
    }

    private bool IsSelectedBeggarProgrammer()
    {
        return IsBeggarCompatibleSelectedProgrammer();
    }

    private bool HasActiveScanResult()
    {
        string? selectedPortName = GetSelectedPortName();
        return currentScanResult is not null &&
               !string.IsNullOrWhiteSpace(selectedPortName) &&
               string.Equals(selectedPortName, lastScannedPortName, StringComparison.OrdinalIgnoreCase);
    }

    private bool EnsureSelectedPortHasActiveScan(out string reason)
    {
        if (string.IsNullOrWhiteSpace(GetSelectedPortName()))
        {
            reason = T("error", "select_and_scan_first", "Please select and scan the current cart first.");
            return false;
        }

        if (!HasActiveScanResult())
        {
            reason = T("error", "select_and_scan_first", "Please select and scan the current cart first.");
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool IsCurrentScanCapacityInvalid()
    {
        return HasActiveScanResult() &&
               currentScanResult is not null &&
               (currentScanResult.NoCartridgeDetected || currentScanResult.Geometry.DeviceSizeBytes <= 0);
    }

    private bool EnsureCurrentScanCapacityValid(out string reason)
    {
        if (IsCurrentScanCapacityInvalid())
        {
            reason = string.IsNullOrWhiteSpace(currentScanResult?.WriteProtectionReason)
                ? T(
                    "error",
                    "scan_capacity_unavailable",
                    "A valid NOR capacity was not obtained. Read the cartridge again before writing.")
                : currentScanResult!.WriteProtectionReason;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool EnsureCurrentScanIsWritable(out string reason, bool allowLayoutReset = false)
    {
        if(currentScanResult?.IsMdMbcxContent == true){reason="此卡包含MD游戏，请使用MD合卡写入；GB菜单操作已禁用。";return false;}
        if(currentScanResult?.IsFcMbcxContent == true)
        {
            reason=T("fc","use_nes_workspace","This card contains NES games. Use the MIX workspace; GB menu actions are disabled.");
            return false;
        }
        if (!EnsureCurrentScanCapacityValid(out reason))
        {
            return false;
        }

        if (currentScanResult?.IsWriteProtected == true)
        {
            reason = string.IsNullOrWhiteSpace(currentScanResult.WriteProtectionReason)
                ? T("error", "current_scan_read_only", "The current scan is read-only, so write/erase actions are disabled.")
                : currentScanResult.WriteProtectionReason;
            return false;
        }

        if (!allowLayoutReset && currentScanResult?.UsesChm1Layout == true && !currentScanResult.IsChm1)
        {
            reason = T("error", "chm1_hardware_unconfirmed", "The image uses 01G/02G fragment management local saves, but 01G/02G fragment management hardware was not identified. Editing is unavailable; full erase remains available.");
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private bool EnsureSelectedProgrammerSupportsMagicUpgradeOrInstall(out string reason)
    {
        if (!TryGetSelectedProgrammerKind(out ProgrammerKind programmerKind, out reason))
        {
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool EnsureSelectedProgrammerSupportsCurrentCartWrite(out string reason)
    {
        if (!TryGetSelectedProgrammerKind(out _, out reason))
        {
            return false;
        }

        return EnsureCurrentScanIsWritable(out reason);
    }

    private bool CanUnlockPpbCurrentScan(out string reason)
    {
        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!TryGetSelectedProgrammerKind(out _, out reason))
        {
            return false;
        }

        if (currentScanResult?.NoCartridgeDetected == true)
        {
            reason = T("error", "unlock_ppb_no_cartridge", "No cartridge was detected, so PPB unlock cannot run.");
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool CanEraseEntireNorCurrentScan(out string reason)
    {
        WaterCartScanResult? scan = GetEntireNorEraseScanSnapshot();
        if (string.IsNullOrWhiteSpace(GetSelectedPortName()) || scan is null)
        {
            reason = T("error", "select_and_scan_first", "Please select and scan the current cart first.");
            return false;
        }

        if (!TryGetSelectedProgrammerKind(out _, out reason))
        {
            return false;
        }

        if (scan.NoCartridgeDetected)
        {
            reason = T("error", "erase_entire_nor_no_cartridge", "No cartridge was detected, so full NOR erase cannot run.");
            return false;
        }

        if (scan.IsWriteProtected)
        {
            reason = string.IsNullOrWhiteSpace(scan.WriteProtectionReason)
                ? T("error", "current_scan_read_only", "The current scan is read-only, so write/erase actions are disabled.")
                : scan.WriteProtectionReason;
            return false;
        }

        const int MaximumSupportedNorBytes = 4 * 128 * 1024 * 1024;
        if (scan.Geometry.DeviceSizeBytes <= 0 ||
            scan.Geometry.DeviceSizeBytes > MaximumSupportedNorBytes)
        {
            reason = T("error", "erase_entire_nor_capacity_invalid", "The detected NOR capacity is invalid.");
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool CanUpgradeCurrentScan(out string reason)
    {
        if (!UsesSelectedMagicTargetMode())
        {
            reason = T("error", "select_waterlink_flash_cart_first", "Please select the WaterLink flash-cart target first.");
            return false;
        }

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            return false;
        }

        if (!EnsureSelectedProgrammerSupportsMagicUpgradeOrInstall(out reason))
        {
            return false;
        }

        if (!WaterMagicUpgradeBuilder.CanUpgradeToMagic(currentScanResult, out reason))
        {
            return false;
        }

        return true;
    }

    private bool CanRepairCurrentScan(out string reason)
    {
        if (!UsesSelectedMagicTargetMode())
        {
            reason = T("error", "select_waterlink_flash_cart_first", "Please select the WaterLink flash-cart target first.");
            return false;
        }

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            return false;
        }

        if (!WaterMagicUpgradeBuilder.CanRepairMagicMetadata(currentScanResult, out reason))
        {
            return false;
        }

        return true;
    }

    private bool CanInstallCurrentScan(out string reason)
    {
        if (!UsesSelectedMagicTargetMode())
        {
            reason = T("error", "select_waterlink_flash_cart_first", "Please select the WaterLink flash-cart target first.");
            return false;
        }

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            return false;
        }

        if (!EnsureSelectedProgrammerSupportsMagicUpgradeOrInstall(out reason))
        {
            return false;
        }

        if (!WaterMagicUpgradeBuilder.CanInstallMagicMenuFromHeaders(currentScanResult, out reason))
        {
            return false;
        }

        return true;
    }

    private bool CanInstallLegacyCurrentScan(out string reason)
    {
        if (UsesSelectedMagicTargetMode())
        {
            reason = T("error", "select_legacy_flash_cart_first", "Please select a legacy flash-cart target first.");
            return false;
        }

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            return false;
        }

        if (currentScanResult?.IsBeggarMaxGbcMode == true)
        {
            reason = T(
                "error",
                "beggar_max_legacy_menu_unsupported",
                "MAX uses the new MBCX menu format. Legacy menu repair/install actions are not available on this path.");
            return false;
        }

        if (!IsSelectedBeggarProgrammer())
        {
            reason = T(
                "error",
                "legacy_menu_repair_requires_beggar",
                "Repairing a missing legacy menu config is currently exposed only on the Beggar programmer path.");
            return false;
        }

        if (!WaterLegacyMenuRepairBuilder.CanInstallLegacyMenuFromHeaders(currentScanResult, out reason))
        {
            return false;
        }

        return true;
    }

    private bool CanRepairLegacyCurrentScan(out string reason)
    {
        if (UsesSelectedMagicTargetMode())
        {
            reason = T("error", "select_legacy_flash_cart_first", "Please select a legacy flash-cart target first.");
            return false;
        }

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            return false;
        }

        if (currentScanResult?.IsBeggarMaxGbcMode == true)
        {
            reason = T(
                "error",
                "beggar_max_legacy_menu_unsupported",
                "MAX uses the new MBCX menu format. Legacy menu repair/install actions are not available on this path.");
            return false;
        }

        if (!IsSelectedBeggarProgrammer())
        {
            reason = T(
                "error",
                "legacy_menu_repair_requires_beggar",
                "Repairing a missing legacy menu config is currently exposed only on the Beggar programmer path.");
            return false;
        }

        if (!WaterLegacyMenuRepairBuilder.CanRepairLegacyMenu(currentScanResult, out reason))
        {
            return false;
        }

        return true;
    }

    private bool CanRepairMenuCurrentScan(out string reason)
    {
        return UsesSelectedMagicTargetMode()
            ? CanInstallCurrentScan(out reason)
            : CanInstallLegacyCurrentScan(out reason) || CanRepairLegacyBatterySaveMetadataCurrentScan(out reason);
    }

    private bool CanRepairLegacyBatterySaveMetadataCurrentScan(out string reason)
    {
        if (UsesSelectedMagicTargetMode())
        {
            reason = T("error", "select_legacy_flash_cart_first", "Please select a legacy flash-cart target first.");
            return false;
        }

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            return false;
        }

        if (currentScanResult?.IsBeggarMaxGbcMode == true)
        {
            reason = T(
                "error",
                "beggar_max_legacy_menu_unsupported",
                "MAX uses the new MBCX menu format. Legacy menu repair/install actions are not available on this path.");
            return false;
        }

        if (!IsSelectedBeggarProgrammer())
        {
            reason = T(
                "error",
                "legacy_menu_repair_requires_beggar",
                "Repairing a missing legacy menu config is currently exposed only on the Beggar programmer path.");
            return false;
        }

        if (!WaterLegacyMenuRepairBuilder.CanRepairLegacyBatterySaveMetadata(currentScanResult, out reason))
        {
            return false;
        }

        return true;
    }

    private bool IsWorkspaceModificationLocked()
    {
        if (IsGbaConversionDraft()) return false;
        return !HasActiveScanResult() || currentScanResult is null ||
               currentScanResult.NoCartridgeDetected || currentScanResult.Geometry.DeviceSizeBytes <= 0;
    }

    private string GetWorkspaceLockedMessage()
    {
        if (NeedsChm1LayoutChange()) return T("log", "chm1_convert_first", "Update the menu to convert the cartridge layout before editing its game list.");
        if (string.IsNullOrWhiteSpace(GetSelectedPortName()))
        {
            return T(
                "log",
                "select_port_first",
                "Please select a serial port first.");
        }

        if (!HasActiveScanResult())
        {
            return T(
                "log",
                "workspace_locked_scan_first",
                "The current cart has not been identified yet. Please click 'Read Cart ROMs' first.");
        }

        if (!EnsureCurrentScanCapacityValid(out string reason))
        {
            return reason;
        }

        if (currentScanResult?.IsBeggarMaxGbcMode == true)
        {
            return T(
                "log",
                "workspace_locked_beggar_max_missing_scan",
                "MAX was identified, but no valid MBCX game directory is available yet. Please read the cart again or reburn a new MBCX menu image.");
        }

        return UsesSelectedMagicTargetMode()
            ? T(
                "log",
                "workspace_locked_no_menu_wl",
                "The current cart has no menu config yet. ROM list editing is locked until you click 'Rebuild Menu'.")
            : T(
                "log",
                "workspace_locked_no_menu",
                "The current cart has no menu config yet. ROM list editing is locked until you click 'Repair Menu'.");
    }

    private WaterCartEntry? GetHeadAreaGame()
    {
        DetectedNorGameInfo? headGame = currentScanResult?.Games
            .FirstOrDefault(game => !game.IsMenu && game.StartBucket == 0);

        return headGame is null
            ? null
            : new WaterCartEntry
            {
                HeaderGame = headGame,
            };
    }

    private bool CanUpdateWaterLinkMenuCurrentScan(out string reason)
    {
        if (!UsesSelectedMagicTargetMode())
        {
            reason = T("error", "select_waterlink_flash_cart_first", "Please select the WaterLink flash-cart target first.");
            return false;
        }

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            return false;
        }

        if (currentScanResult is null)
        {
            reason = T("error", "select_and_scan_first", "Please select and scan the current cart first.");
            return false;
        }

        if (!EnsureSelectedProgrammerSupportsMagicUpgradeOrInstall(out reason))
        {
            return false;
        }

        if (currentScanResult.Geometry.SectorSizeBytes != WaterWlMappingCodec.SectorSize)
        {
            reason = Tf(
                "error",
                "magic_sector_size_unsupported",
                "Current NOR sector size is 0x{0:X}; one-click migration is not supported.",
                currentScanResult.Geometry.SectorSizeBytes);
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool CanUpdateLegacyMenuCurrentScan(out string reason)
    {
        if (UsesSelectedMagicTargetMode())
        {
            reason = T("error", "select_legacy_flash_cart_first", "Please select a legacy flash-cart target first.");
            return false;
        }

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            return false;
        }

        if (currentScanResult?.IsBeggarMaxGbcMode == true)
        {
            reason = T(
                "error",
                "beggar_max_legacy_menu_unsupported",
                "MAX uses the new MBCX menu format. Legacy menu update is not available on this path.");
            return false;
        }

        if (!IsSelectedBeggarProgrammer())
        {
            reason = T(
                "error",
                "legacy_menu_update_requires_beggar",
                "Updating to the bundled legacy menu is exposed only on the Beggar programmer path.");
            return false;
        }

        if (currentScanResult is null)
        {
            reason = T("error", "select_and_scan_first", "Please select and scan the current cart first.");
            return false;
        }

        if (!currentScanResult.MenuConfig.IsDetected)
        {
            reason = T(
                "error",
                "update_menu_requires_existing_menu",
                "No menu config was detected. Use 'Repair Menu' first.");
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool CanUpdateBeggarMaxMenuCurrentScan(out string reason)
    {
        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            return false;
        }

        if (currentScanResult?.IsBeggarMaxGbcMode != true)
        {
            reason = T(
                "error",
                "beggar_max_menu_update_requires_max_scan",
                "请先把卡切到 GB 模式，并识别成 MAX/MBCX 卡。");
            return false;
        }

        if (!IsSelectedBeggarProgrammer())
        {
            reason = T(
                "error",
                "beggar_max_menu_update_requires_beggar",
                "MAX/MBCX 菜单更新当前只在丐中丐上位机路径开放。");
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool CanUpdateMenuBackgroundCurrentScan(out string reason)
    {
        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanIsWritable(out reason))
        {
            return false;
        }

        if (currentScanResult is null)
        {
            reason = T("error", "select_and_scan_first", "Please select and scan the current cart first.");
            return false;
        }

        if (!currentScanResult.MenuConfig.IsDetected)
        {
            reason = T(
                "error",
                "update_menu_background_requires_existing_menu",
                "No menu config was detected. Use 'Repair Menu' first.");
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private MenuUpdateActionMode GetMenuUpdateActionMode()
    {
        if (CanManageChm1()) return MenuUpdateActionMode.UpdateWaterLinkMenu;
        if (currentScanResult?.IsBeggarMaxGbcMode == true)
        {
            return CanUpdateBeggarMaxMenuCurrentScan(out _)
                ? MenuUpdateActionMode.UpdateBeggarMaxMenu
                : MenuUpdateActionMode.None;
        }

        if (UsesSelectedMagicTargetMode())
        {
            return CanUpdateWaterLinkMenuCurrentScan(out _)
                ? MenuUpdateActionMode.UpdateWaterLinkMenu
                : MenuUpdateActionMode.None;
        }

        return CanUpdateLegacyMenuCurrentScan(out _)
            ? MenuUpdateActionMode.UpdateLegacyMenu
            : MenuUpdateActionMode.None;
    }

    private MissingMenuActionMode GetMissingMenuActionMode()
    {
        if (CanManageChm1() && (NeedsChm1LayoutChange() || !currentScanResult!.MenuConfig.IsDetected))
            return MissingMenuActionMode.InstallWaterLinkMenu;
        if (UsesSelectedMagicTargetMode())
        {
            if (CanRepairCurrentScan(out _))
            {
                return MissingMenuActionMode.RepairWaterLinkMagic;
            }

            if (CanUpgradeCurrentScan(out _))
            {
                return MissingMenuActionMode.UpgradeWaterLinkFromLegacyMenu;
            }

            return CanInstallCurrentScan(out _)
                ? MissingMenuActionMode.InstallWaterLinkMenu
                : MissingMenuActionMode.None;
        }

        if (CanInstallLegacyCurrentScan(out _))
        {
            return MissingMenuActionMode.InstallLegacyMenu;
        }

        return CanRepairLegacyBatterySaveMetadataCurrentScan(out _)
            ? MissingMenuActionMode.RepairLegacyBatterySaveMetadata
            : MissingMenuActionMode.None;
    }

    private bool TryResolveChipSaveExportTarget(out WaterCartEntry? entry, out int saveSizeBytes, out string reason)
    {
        entry = null;
        saveSizeBytes = 0;

        if (!IsBeggarCompatibleSelectedProgrammer())
        {
            reason = T(
                "error",
                "export_chip_save_requires_beggar",
                "Exporting the chip save is currently available only on the Beggar path.");
            return false;
        }

        if (!EnsureSelectedPortHasActiveScan(out reason))
        {
            return false;
        }

        if (!EnsureCurrentScanCapacityValid(out reason))
        {
            return false;
        }

        if (!TryResolveCurrentChipSaveEntry(out entry, out saveSizeBytes))
        {
            reason = T(
                "error",
                "export_chip_save_last_game_unknown",
                "The last played game could not be resolved from the menu status block.");
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private void RewriteNorRange(
        WaterDeviceProtocol protocol,
        uint startAddress,
        byte[] rangeData,
        NorGeometryInfo geometry,
        string label,
        ProgrammerKind programmerKind)
    {
        if (rangeData.Length == 0)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf("error", "rewrite_range_empty", "{0} data is empty.", label));
        }

        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;

        if (rangeData.Length % sectorSize != 0)
        {
            throw new InvalidOperationException(
                LocalizedText.Tf("error", "rewrite_range_length_invalid", "{0} data length is not an integer number of sectors.", label));
        }

        RewriteNorSectorBatch(
            protocol,
            BuildNorSectorWriteBatch(startAddress, rangeData, sectorSize, label),
            geometry,
            programmerKind);
    }

    private List<WaterNorWriteSegment> BuildMenuHeadWriteSegments(
        byte[] headAreaImage,
        MenuFlashStatusInfo status,
        bool usesWlMode,
        string headAreaLabel)
    {
        if (!status.BatteryPresent)
        {
            return
            [
                new WaterNorWriteSegment
                {
                    StartAddress = 0,
                    Data = headAreaImage,
                    Label = headAreaLabel,
                }
            ];
        }

        int saveAreaStart = status.SaveAreaAddress;
        if (saveAreaStart <= 0 || saveAreaStart > headAreaImage.Length)
        {
            return
            [
                new WaterNorWriteSegment
                {
                    StartAddress = 0,
                    Data = headAreaImage,
                    Label = headAreaLabel,
                }
            ];
        }

        List<WaterNorWriteSegment> segments =
        [
            new WaterNorWriteSegment
            {
                StartAddress = 0,
                Data = headAreaImage.AsSpan(0, saveAreaStart).ToArray(),
                Label = headAreaLabel,
            }
        ];

        if (usesWlMode)
        {
            segments.Add(new WaterNorWriteSegment
            {
                StartAddress = (uint)WaterWlMappingCodec.Offset,
                Data = headAreaImage.AsSpan(WaterWlMappingCodec.Offset, WaterWlMappingCodec.SectorSize).ToArray(),
                Label = T("value", "wl_mapping_label", "WL mapping"),
            });
        }

        return segments;
    }

    private static bool TryResolveMenuBackgroundRegion(
        byte[] source,
        bool usesWlLayout,
        out int backgroundOffset) =>
        GbaMultiMenuBuilder.Services.GbaMenuBackgroundLocator.TryFind(source, out backgroundOffset);

    private static int FindByteSequence(byte[] source, byte[] pattern)
    {
        if (source is null || pattern is null || source.Length == 0 || pattern.Length == 0 || pattern.Length > source.Length)
        {
            return -1;
        }

        for (int i = 0; i <= source.Length - pattern.Length; i++)
        {
            bool matched = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }

    private long CountMenuHeadWriteWorkBytes(
        byte[] headAreaImage,
        NorGeometryInfo geometry,
        MenuFlashStatusInfo status,
        bool usesWlMode,
        string headAreaLabel) =>
        BuildMenuHeadWriteSegments(headAreaImage, status, usesWlMode, headAreaLabel)
            .Sum(segment => CountSegmentProgressWorkBytes(segment, geometry));

    private int CountMenuHeadWriteUnits(
        byte[] headAreaImage,
        NorGeometryInfo geometry,
        MenuFlashStatusInfo status,
        bool usesWlMode,
        string headAreaLabel) =>
        BuildMenuHeadWriteSegments(headAreaImage, status, usesWlMode, headAreaLabel)
            .Sum(segment => CountSegmentProgressUnits(segment, geometry));

    private void RewriteMenuHeadArea(
        WaterDeviceProtocol protocol,
        ProgrammerKind programmerKind,
        NorGeometryInfo geometry,
        byte[] headAreaImage,
        MenuFlashStatusInfo status,
        bool usesWlMode,
        string headAreaLabel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<WaterNorWriteSegment> segments = BuildMenuHeadWriteSegments(headAreaImage, status, usesWlMode, headAreaLabel);

        if (status.BatteryPresent)
        {
            int saveAreaStart = status.SaveAreaAddress;
            int saveAreaEnd = usesWlMode
                ? WaterWlMappingCodec.Offset - 1
                : headAreaImage.Length - 1;
            AppendLog(Tf(
                "log",
                "preserve_save_area_rewrite",
                "Battery mode is enabled. Preserve save area 0x{0:X6}-0x{1:X6}; only rewrite menu metadata ranges.",
                saveAreaStart,
                saveAreaEnd));
        }

        foreach (WaterNorWriteSegment segment in segments)
        {
            AppendLog(Tf(
                "log",
                "rewrite_head_segment",
                "Rewrite range: 0x{0:X6}-0x{1:X6} | {2}",
                segment.StartAddress,
                segment.StartAddress + (uint)segment.Data.Length - 1,
                segment.Label));
        }

        int sectorSize = geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;
        List<(uint Address, byte[] Data, string Label)> sectorBatch = [];
        foreach (WaterNorWriteSegment segment in segments)
        {
            sectorBatch.AddRange(BuildNorSectorWriteBatch(segment.StartAddress, segment.Data, sectorSize, segment.Label));
        }

        RewriteNorSectorBatch(protocol, sectorBatch, geometry, programmerKind, cancellationToken: cancellationToken);
    }

    private void UpdateActionButtons(bool busy)
    {
        RefreshGbaCardModeUi();
        if (IsGbaSingleCardMode()) { UpdateGbaSingleButtons(busy); return; }
        if (IsChisFlash10Mode()) { UpdateChisFlash10ActionButtons(busy); return; }
        if (IsGbcRawImageMode() || IsGbcProfileUnselected()) { UpdateGbcProfileActionButtons(busy); return; }
        if (IsMbc5FixedSlotMode()) { UpdateMbc5ActionButtons(busy); return; }
        if (IsFcWorkspaceActive)
        {
            UpdateFcActionButtons(busy);
            return;
        }
        if(HasActiveMixWorkspace){UpdateMixRebuildMenu(busy,true);return;}
        menuRom.Enabled=true;
        if (menuFcTools is not null) menuFcTools.Visible = false;
        menuCartridge.Enabled = true;
        menuFcScan.Text = T("ui", "analyze_configuration", "分析配置");
        menuFcScan.Enabled = !busy;
        menuSave.Enabled = true;
        bool workspaceUiBusy = busy;
        bool hasMdImports = pendingImportItems.Any(item=>item.MdRom is not null);
        bool hasActiveScan = HasActiveScanResult();
        bool hasCachedScan = currentScanResult is not null;
        bool usesWaterLinkTargetMode = UsesSelectedMagicTargetMode();
        MenuUpdateActionMode menuUpdateActionMode = GetMenuUpdateActionMode();
        MissingMenuActionMode missingMenuActionMode = GetMissingMenuActionMode();
        bool promoteRepairMenu = !usesWaterLinkTargetMode &&
            IsWorkspaceModificationLocked() &&
            missingMenuActionMode != MissingMenuActionMode.None;
        bool workspaceLocked = IsWorkspaceModificationLocked();
        bool scanCapacityInvalid = IsCurrentScanCapacityInvalid();
        bool scanWriteProtected = currentScanResult?.IsWriteProtected == true;
        bool canModifyWorkspace = !workspaceUiBusy && !workspaceLocked;
        bool hasQueueItems = workspaceRows.Any(row => row.PendingItem is not null);
        bool hasWorkspaceChanges = HasWorkspaceChanges();
        bool hasSelectedQueuedItem = TryGetSelectedPendingImportItem(out _, out _);
        bool hasActiveBurnCancellation = activeBurnCancellation is not null;
        bool workspaceCapacityExceeded = !workspaceUiBusy &&
            hasWorkspaceChanges &&
            currentScanResult is not null &&
            !workspaceLocked &&
            !TryValidateWorkspaceCapacity(out _);
        bool canBurnQueue = !isAnalyzingQueueImports &&
            hasWorkspaceChanges &&
            currentScanResult is not null &&
            !string.IsNullOrWhiteSpace(GetSelectedPortName()) &&
            EnsureSelectedProgrammerSupportsCurrentCartWrite(out _) &&
            !workspaceLocked &&
            !workspaceCapacityExceeded &&
            !scanWriteProtected;
        bool canRunFragmentTest = hasActiveScan &&
            !scanCapacityInvalid &&
            GetSelectedProgrammerKind() == ProgrammerKind.WaterLink;
        string capacityExceededStatusText = T("status", "workspace_capacity_exceeded", "Current list exceeds cart capacity!");

        buttonScan.Enabled = !workspaceUiBusy &&
            !string.IsNullOrWhiteSpace(GetSelectedPortName());
        menuRefreshPorts.Enabled = !workspaceUiBusy;
        menuPendingGbaCovers.Enabled = CanEditPendingGbaCovers();
        menuScan.Enabled = buttonScan.Enabled;
        menuExit.Enabled = !workspaceUiBusy;
        menuFragmentTest.Enabled = !workspaceUiBusy && canRunFragmentTest;
        menuFragmentTest.Text = T("ui", "fragment_test", "Fragment Test");
        menuUpdateMenu.Enabled = !workspaceUiBusy &&
            !scanCapacityInvalid &&
            menuUpdateActionMode != MenuUpdateActionMode.None;
        menuUpdateMenu.Text = usesWaterLinkTargetMode
            ? T("ui", "force_rebuild_menu", "重建菜单")
            : T("ui", "update_menu", "更新菜单");
        menuReplaceMenuFile.Enabled = !workspaceUiBusy &&
            !scanCapacityInvalid &&
            menuUpdateActionMode is MenuUpdateActionMode.UpdateWaterLinkMenu or MenuUpdateActionMode.UpdateLegacyMenu;
        menuReplaceMenuFile.Text = T("ui", "replace_menu_file", "Rebuild From Menu File");
        menuRepairMenu.Enabled = !workspaceUiBusy &&
            !scanCapacityInvalid &&
            missingMenuActionMode != MissingMenuActionMode.None;
        menuUpdateBackground.Enabled = !workspaceUiBusy && CanUpdateMenuBackgroundCurrentScan(out _);
        menuEraseConfig.Enabled = !workspaceUiBusy &&
            hasActiveScan &&
            !scanCapacityInvalid &&
            EnsureSelectedProgrammerSupportsCurrentCartWrite(out _);
        menuEraseConfig.Text = T("ui", "erase_config", "Erase Config");
        menuEraseEntireNor.Enabled = !workspaceUiBusy && CanEraseEntireNorCurrentScan(out _);
        menuEraseEntireNor.Text = T("ui", "erase_entire_nor", "Erase Entire NOR");
        menuDetectMagic.Enabled = !workspaceUiBusy && hasActiveScan &&
            currentScanResult?.NoCartridgeDetected == false && !currentScanResult.IsBeggarMaxGbcMode;
        menuUnlockPpb.Enabled = !workspaceUiBusy && CanUnlockPpbCurrentScan(out _);
        menuUnlockPpb.Text = T("ui", "unlock_ppb", "Unlock PPB");
        buttonQueueAdd.Enabled = canModifyWorkspace;
        buttonQueueRemove.Enabled = canModifyWorkspace && hasSelectedQueuedItem;
        buttonQueueClear.Enabled = canModifyWorkspace && hasQueueItems;
        buttonQueueBurn.Enabled = hasActiveBurnCancellation
            ? !isBurnCancellationRequested
            : promoteRepairMenu
                ? !workspaceUiBusy
                : canModifyWorkspace && canBurnQueue;
        buttonQueueBurn.Text = hasActiveBurnCancellation
            ? T("ui", "cancel_burn", "Cancel")
            : promoteRepairMenu
                ? T("ui", "repair_menu", "Repair Menu")
                : T("ui", "queue_burn", "Start Burn");
        menuAddRom.Enabled = buttonQueueAdd.Enabled;
        menuRemoveSelectedRom.Enabled = buttonQueueRemove.Enabled;
        menuClearRomQueue.Enabled = buttonQueueClear.Enabled;
        menuStartBurn.Enabled = buttonQueueBurn.Enabled;
        menuStartBurn.Text = buttonQueueBurn.Text;
        buttonQueueAdd.Enabled=!workspaceUiBusy&&!isAnalyzingQueueImports;
        menuAddRom.Enabled=buttonQueueAdd.Enabled;
        if(HasUnreadyPlatformImports(workspaceRows)&&!CanWriteMdQueue&&!hasActiveBurnCancellation){buttonQueueBurn.Enabled=false;menuStartBurn.Enabled=false;}
        UpdateSelectionCommandStates();
        if(TryGetSelectedPendingImportItem(out var selectedImport,out _) && selectedImport?.MdRom is not null)
        {
            menuExportSelectedRom.Enabled=menuItemExportRom.Enabled=false;
            menuExportSelectedSave.Enabled=menuItemExportSave.Enabled=false;
            menuImportSelectedSave.Enabled=menuItemImportSave.Enabled=false;
            menuReplaceSelectedRom.Enabled=menuItemReplaceRom.Enabled=false;
        }
        if(CanWriteMdQueue&&!workspaceUiBusy&&!isAnalyzingQueueImports){buttonQueueBurn.Enabled=true;menuStartBurn.Enabled=true;buttonQueueBurn.Text=menuStartBurn.Text="MIX · NES / MD";}
        bool canToggleProgrammerMode = !workspaceUiBusy && !isAnalyzingQueueImports;
        UpdateProgrammerModeSelectorUi(canToggleProgrammerMode);
        // Mode selection is local navigation, not authorization to write a card.
        // New card types must not need their own pre-scan UI exception.
        comboBoxBurnProfileMode.Enabled = !workspaceUiBusy &&
            !isAnalyzingQueueImports && burnProfileModeOptions.Count > 0;
        dataGridViewRoms.Enabled = (hasCachedScan || hasQueueItems) && !scanCapacityInvalid && !workspaceUiBusy;
        groupBoxList.Enabled = hasCachedScan || burnProfileModeOptions.Count > 0;

        RefreshGbaCardModeUi();
        if (!workspaceUiBusy && !isAnalyzingQueueImports)
        {
            if (scanCapacityInvalid)
            {
                labelStatus.Text = EnsureCurrentScanCapacityValid(out string lockReason)
                    ? labelStatus.Text
                    : lockReason;
            }
            else if(currentScanResult?.IsMdMbcxContent==true&&!hasMdImports){labelStatus.Text="已识别MD02卡，GB菜单操作已禁用；可导入MD游戏重建合卡。";}
            else if(hasMdImports)
            {
                labelStatus.Text="MD游戏已加入主列表；仅保留MD队列即可写入第4槽合卡。";
            }
            else if (workspaceCapacityExceeded)
            {
                labelStatus.Text = capacityExceededStatusText;
            }
            else if (scanWriteProtected)
            {
                labelStatus.Text = EnsureCurrentScanIsWritable(out string writeProtectReason)
                    ? labelStatus.Text
                    : writeProtectReason;
            }
            else if (labelStatus.Text == capacityExceededStatusText)
            {
                labelStatus.Text = T("status", "ready", "Ready");
            }
        }
    }
}
