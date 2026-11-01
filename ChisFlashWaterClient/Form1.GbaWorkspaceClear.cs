namespace ChisFlashWaterClient;

public partial class Form1
{
    private WaterCartScanResult? gbaWorkspaceClearSource;

    private bool HasPendingGbaWorkspaceClear() =>
        (SupportsGbaCardMode() || IsChisFlash10Mode()) && gbaWorkspaceClearSource is not null &&
        ReferenceEquals(gbaWorkspaceClearSource, currentScanResult);

    private static bool HasGbaGamesOnCard(WaterCartScanResult? scan) => scan is { NoCartridgeDetected: false, IsBeggarMaxGbcMode: false } &&
        (scan.Entries.Any(entry => !entry.IsMenu) || scan.Games.Any(game => !game.IsMenu));

    private void StageGbaWorkspaceClear()
    {
        if (HasGbaGamesOnCard(currentScanResult)) gbaWorkspaceClearSource = currentScanResult;
    }

    private bool CanBurnGbaWorkspaceClear() => HasPendingGbaWorkspaceClear() && HasActiveScanResult() &&
        currentScanResult is { IsNorProbeOnly: false, IsWriteProtected: false } &&
        EnsureCurrentScanIsWritable(out _) && (!IsChisFlash10Mode() || CanWriteChisFlash10());

    private string GbaWorkspaceClearConfirmation() => GbaCardText("clear_confirm",
        "工作区中的卡内游戏已标记为清空。继续后将擦除卡内游戏区，再写入工作区中新加入的游戏（如有）。菜单、卡带配置和独立存档芯片保留；游戏目录及映射会按工作区更新。",
        "Erase the on-card game area, then program any newly added games? The menu, cartridge settings and separate save chip are preserved. The game list and mapping will follow the workspace.");

    private uint[] PendingGbaWorkspaceEraseSectors(WaterCartScanResult scan, IEnumerable<WaterNorWriteSegment>? replacements = null) =>
        HasPendingGbaWorkspaceClear() && ReferenceEquals(gbaWorkspaceClearSource, scan)
            ? GbaWorkspaceClearPlan.BuildEraseSectors(scan, replacements) : [];

    private void EraseGbaWorkspaceSectors(WaterDeviceProtocol protocol, ProgrammerKind kind,
        WaterCartScanResult scan, IReadOnlyList<uint> sectors, CancellationToken cancellationToken)
    {
        for (int index = 0; index < sectors.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint address = sectors[index];
            bool erased = kind == ProgrammerKind.BeggarLegacy && (IsChisFlash10Mode() || IsGbaMulticartUnavailable())
                ? TryEraseLegacyBeggarSector(protocol, address, 60000)
                : TryEraseRomSectorForProgrammer(protocol, kind, address);
            if (!erased) throw new IOException($"清空游戏区失败：0x{address:X8}。");
            AdvanceMeasuredProgress(scan.Geometry.SectorSizeBytes, 1, GbaCardText("clear_game_area", "正在清空卡内游戏", "Erasing on-card games"));
        }
    }

    private async Task BurnEmptySingleGbaWorkspaceAsync()
    {
        if (activeBurnCancellation is not null) { RequestActiveBurnCancellation(); return; }
        if (isBusy || isAnalyzingQueueImports || !CanBurnGbaWorkspaceClear()) return;
        var scan = currentScanResult!;
        uint[] sectors = PendingGbaWorkspaceEraseSectors(scan);
        if (MessageBox.Show(this, GbaWorkspaceClearConfirmation(), T("ui", "clear_workspace", "Clear Workspace"),
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        string port = GetSelectedPortName()!;
        var kind = GetRequiredSelectedProgrammerKind();
        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation; isBurnCancellationRequested = false;
        SetBusy(true, GbaCardText("clear_game_area", "正在清空卡内游戏", "Erasing on-card games"));
        bool started = false, succeeded = false;
        try
        {
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, kind);
                BeginMeasuredProgress("清空游戏区", (long)sectors.Length * scan.Geometry.SectorSizeBytes, sectors.Length);
                started = true;
                EraseGbaWorkspaceSectors(protocol, kind, scan, sectors, cancellation.Token);
                CompleteMeasuredProgress("清空游戏区");
            });
            // 尚未写入的菜单仍留在工作区，不因清空单卡游戏而被丢弃。
            byte[]? draft = gbaDraftMenu; string draftName = gbaDraftMenuName;
            var refreshed = WaterCartScanner.RebuildCachedScanResult(scan, gamesOverride: []);
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
            SetBusy(false, succeeded ? GbaCardText("clear_done", "卡内游戏已清空，菜单和配置已保留。", "On-card games cleared; menu and settings preserved.") :
                GbaCardText("clear_interrupted", "清空未完成，请重新分析卡带。", "Erase incomplete; analyze the cartridge again."));
        }
    }
}
