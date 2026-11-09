namespace ChisFlashWaterClient;

public partial class Form1
{
    private WaterCartScanResult? gbaWorkspaceClearSource;

    private bool HasPendingGbaWorkspaceClear() =>
        // 清空任务绑定扫描结果；后台判断只读取数据，不访问卡型选择控件。
        gbaWorkspaceClearSource is not null &&
        ReferenceEquals(gbaWorkspaceClearSource, currentScanResult);

    private static bool UseDirectGbaWorkspaceErase(WaterCartScanResult scan, BurnProfileMode mode) =>
        IsGbaSingleProfile(mode) || IsSingleGameOnlyGbaScan(scan);

    private static bool HasGbaGamesOnCard(WaterCartScanResult? scan) => scan is { NoCartridgeDetected: false, IsBeggarMaxGbcMode: false } &&
        (scan.Entries.Any(entry => !entry.IsMenu) || scan.Games.Any(game => !game.IsMenu));

    private void StageGbaWorkspaceClear()
    {
        if (HasGbaGamesOnCard(currentScanResult) || currentScanResult?.MenuConfig.IsDetected == true ||
            currentScanResult?.Games.Any(game => game.IsMenu) == true) gbaWorkspaceClearSource = currentScanResult;
    }

    private string GbaWorkspaceClearConfirmation() => GbaCardText("clear_confirm",
        "继续后将清除卡内菜单及其配置、游戏目录、映射和游戏区，再写入工作区中新加入的内容（如有）。系统区存档和独立存档芯片不参与清空。",
        "Clear the on-card menu and its settings, game list, mapping and game area, then program any newly added content? System-area saves and the separate save chip are excluded from clearing.");

    private uint[] PendingGbaWorkspaceEraseSectors(WaterCartScanResult scan, IEnumerable<WaterNorWriteSegment>? replacements = null) =>
        HasPendingGbaWorkspaceClear() && ReferenceEquals(gbaWorkspaceClearSource, scan)
            ? GbaWorkspaceClearPlan.BuildEraseSectors(scan, replacements) : [];

    private void EraseGbaWorkspaceSectors(WaterDeviceProtocol protocol, ProgrammerKind kind,
        WaterCartScanResult scan, IReadOnlyList<uint> sectors, bool directLegacyErase, CancellationToken cancellationToken)
    {
        for (int index = 0; index < sectors.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint address = sectors[index];
            bool erased = kind == ProgrammerKind.BeggarLegacy && directLegacyErase
                ? TryEraseLegacyBeggarSector(protocol, address, 60000)
                : TryEraseRomSectorForProgrammer(protocol, kind, address);
            if (!erased) throw new IOException($"清空菜单和游戏失败：0x{address:X8}。");
            AdvanceMeasuredProgress(scan.Geometry.SectorSizeBytes, 1, GbaCardText("clear_game_area", "正在清空卡内菜单和游戏", "Clearing on-card menu and games"));
        }
    }

    private async Task BurnEmptySingleGbaWorkspaceAsync()
    {
        if (activeBurnCancellation is not null) { RequestActiveBurnCancellation(); return; }
        if (isBusy || isAnalyzingQueueImports || !HasPendingGbaWorkspaceClear()) return;
        if (!EnsureSelectedPortHasActiveScan(out string scanReason)) throw new InvalidOperationException(scanReason);
        if (!EnsureCurrentScanIsWritable(out string writeReason)) throw new InvalidOperationException(writeReason);
        if (currentScanResult!.IsNorProbeOnly) throw new InvalidOperationException("请先分析卡带内容，再清空卡内游戏。");
        if (IsChisFlash10Mode() && !CanWriteChisFlash10()) throw new InvalidOperationException("当前设备或卡带规格不支持1.0V游戏区擦除。");
        var scan = currentScanResult!;
        uint[] sectors = PendingGbaWorkspaceEraseSectors(scan);
        if (MessageBox.Show(this, GbaWorkspaceClearConfirmation(), T("ui", "clear_workspace", "Clear Workspace"),
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        string port = GetSelectedPortName()!;
        var kind = GetRequiredSelectedProgrammerKind();
        bool directLegacyErase = UseDirectGbaWorkspaceErase(scan, GetSelectedBurnProfileMode());
        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation; isBurnCancellationRequested = false;
        SetBusy(true, GbaCardText("clear_game_area", "正在清空卡内菜单和游戏", "Clearing on-card menu and games"));
        bool started = false, succeeded = false;
        try
        {
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, kind);
                BeginMeasuredProgress("清空菜单和游戏", (long)sectors.Length * scan.Geometry.SectorSizeBytes, sectors.Length);
                started = true;
                EraseGbaWorkspaceSectors(protocol, kind, scan, sectors, directLegacyErase, cancellation.Token);
                CompleteMeasuredProgress("清空菜单和游戏");
            });
            // 清空后重新添加的菜单继续作为草稿，旧菜单及目录不再出现在扫描缓存中。
            byte[]? draft = gbaDraftMenu; string draftName = gbaDraftMenuName;
            var refreshed = GbaWorkspaceClearPlan.BuildClearedScanResult(scan);
            pendingImportItems.Clear(); workspaceRows.Clear();
            ApplyScanResult(refreshed, false);
            if (draft is not null) { gbaDraftMenu = draft; gbaDraftMenuName = draftName; RefreshWorkspaceGrid(); UpdateMenuBackgroundPreview(refreshed); }
            succeeded = true;
        }
        finally
        {
            activeBurnCancellation = null; isBurnCancellationRequested = false;
            if (!succeeded)
            {
                AbortMeasuredProgress();
                if (started) { currentScanResult = null; lastScannedPortName = string.Empty; }
            }
            SetBusy(false, succeeded ? GbaCardText("clear_done", "卡内菜单和游戏已清空。", "On-card menu and games cleared.") :
                GbaCardText("clear_interrupted", "清空未完成，请重新分析卡带。", "Erase incomplete; analyze the cartridge again."));
        }
    }
}
