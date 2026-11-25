using ClientText = ChisFlashWaterClient.Services.LocalizedText;
using System.Diagnostics;
namespace ChisFlashWaterClient;

public partial class Form1
{
    private WaterCartScanResult? gbaWorkspaceClearSource;
    private readonly HashSet<WaterCartEntry> removedGbaGames = [];
    private bool clearGbaGameArea;

    private bool HasPendingGbaWorkspaceClear() =>
        // 清空任务绑定扫描结果；后台判断只读取数据，不访问卡型选择控件。
        gbaWorkspaceClearSource is not null &&
        ReferenceEquals(gbaWorkspaceClearSource, currentScanResult);

    private static bool UseDirectGbaWorkspaceErase(WaterCartScanResult scan, BurnProfileMode mode) =>
        IsGbaSingleProfile(mode);

    private static bool HasGbaGamesOnCard(WaterCartScanResult? scan) => scan is { NoCartridgeDetected: false, IsBeggarMaxGbcMode: false } &&
        (scan.Entries.Any(entry => !entry.IsMenu) || scan.Games.Any(game => !game.IsMenu));

    private void StageGbaWorkspaceClear()
    {
        clearGbaGameArea = IsGbaSingleProfileSelected();
        if (clearGbaGameArea && HasGbaGamesOnCard(currentScanResult) || HasScannedGbaMulticart()) gbaWorkspaceClearSource = currentScanResult;
    }

    private string GbaWorkspaceClearConfirmation() => !clearGbaGameArea
        ? ShellText("将清除卡内菜单、游戏目录、状态和区块映射。原游戏数据及存档保留；新增游戏只擦写其占用范围。",
            "Clear the menu, directory, status and block mapping? Existing game data and saves are retained. New games overwrite their allocated ranges.")
        : GbaCardText("clear_confirm",
        "继续后将清除卡内菜单及其配置、游戏目录、映射和游戏区，再写入工作区中新加入的内容（如有）。系统区存档和独立存档芯片不参与清空。",
        "Clear the on-card menu and its settings, game list, mapping and game area, then program any newly added content? System-area saves and the separate save chip are excluded from clearing.");

    private uint[] PendingGbaWorkspaceEraseSectors(WaterCartScanResult scan, IEnumerable<WaterNorWriteSegment>? replacements = null)
    {
        var writes = replacements?.ToArray() ?? [];
        uint[] clear = HasPendingGbaWorkspaceClear() && ReferenceEquals(gbaWorkspaceClearSource, scan)
            ? GbaWorkspaceClearPlan.BuildEraseSectors(scan, writes, clearGbaGameArea) : [];
        var headers = GbaWorkspaceClearPlan.PlanMappedGameHeadErases(scan, removedGbaGames);
        return clear.Concat(headers.Where(address => !writes.Any(segment => segment.Data.Length > 0 &&
            address < (long)segment.StartAddress + segment.Data.Length && (long)address + scan.Geometry.SectorSizeBytes > segment.StartAddress)))
            .Distinct().Order().ToArray();
    }

    private string GbaMappedHeadEraseNotice(WaterCartScanResult scan)
    {
        var addresses = GbaWorkspaceClearPlan.PlanMappedGameHeadErases(scan, removedGbaGames);
        if (addresses.Length == 0) return string.Empty;
        string size = FormatKiBSize(scan.Geometry.SectorSizeBytes);
        string details = string.Join(Environment.NewLine, removedGbaGames.OrderBy(game => game.StartAddress)
            .Select(game => $"「{game.Title}」  0x{game.StartAddress:X8} · {size}"));
        return Environment.NewLine + Environment.NewLine + ShellText(
            "以下已移除游戏的启动扇区将擦除并填充 CHIS。若新 ROM 重用该扇区，会随新 ROM 一起擦写。",
            "The boot sectors below will be erased and filled with CHIS. Any sector reused by a new ROM will be erased as part of that write.") +
            Environment.NewLine + details;
    }

    private string GbaClearActionText() => clearGbaGameArea
        ? ClientText.T("client", "clear_menu_games", "Clear Menu and Games")
        : ShellText("清除合卡配置区", "Clear multicart configuration");

    private void EraseGbaWorkspaceSectors(WaterDeviceProtocol protocol, ProgrammerKind kind,
        WaterCartScanResult scan, IReadOnlyList<uint> sectors, bool directLegacyErase, CancellationToken cancellationToken,
        string? operationTitle = null, bool markFirstSector = false)
    {
        if (sectors.Count == 0) return;
        string progressTitle = operationTitle ?? GbaClearActionText();
        BeginMeasuredProgressPhase(sectors.Count, true, progressTitle);
        var markedSectors = removedGbaGames.Select(game => game.StartAddress).ToHashSet();
        if (markFirstSector) markedSectors.Add(0);
        byte[]? markerData = null;
        int selectedBucket = -1;
        try
        {
            for (int index = 0; index < sectors.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint address = sectors[index];
                bool mark = markedSectors.Contains(address);
                bool useDirectAddress = directLegacyErase && (!mark || address == 0);
                long eraseStart = StartLandscapeErase();
                bool erased = kind == ProgrammerKind.BeggarLegacy && useDirectAddress
                    ? TryEraseLegacyBeggarSector(protocol, address, Timeout.Infinite)
                    : TryEraseRomSectorForProgrammer(protocol, kind, address, ref selectedBucket);
                if (!erased) throw new IOException(operationTitle is null
                    ? ClientText.Tf("client", "clear_failed_address", "Failed to clear the menu and games at 0x{0:X8}.", address)
                    : ShellText($"启动头清除失败，NOR 地址 0x{address:X8}。", $"Failed to clear the boot header at NOR address 0x{address:X8}."));
                protocol.RomTiming.FinishErase(eraseStart);
                FinishLandscapeErase(eraseStart, scan.Geometry.SectorSizeBytes);
                if (mark)
                {
                    markerData ??= GbaWorkspaceClearPlan.BuildRemovedGameSector(scan.Geometry.SectorSizeBytes);
                    long writeStart = Stopwatch.GetTimestamp();
                    // 当前扇区写完标记再响应取消，避免首段全 FF 被下次烧录当成空白。
                    ProgramNorSectorChunks(protocol, kind, address, markerData, GetEffectiveBufferWriteBytes(scan.Geometry),
                        "CHIS", 0, false, CancellationToken.None, ref selectedBucket, useDirectAddress);
                    protocol.RomTiming.FinishProgram(writeStart, markerData.Length, markerData.Length);
                }
                AdvanceMeasuredProgress(scan.Geometry.SectorSizeBytes, 1, progressTitle);
            }
        }
        catch (OperationCanceledException)
        {
            RestoreGbaBatchBank(protocol, selectedBucket, reportFailure: false);
            throw;
        }

        RestoreGbaBatchBank(protocol, selectedBucket);
    }

    private async Task BurnGbaHeadRemovalAsync()
    {
        if (isBusy || isAnalyzingQueueImports) return;
        if (!EnsureSelectedPortHasActiveScan(out string reason) || !EnsureCurrentScanIsWritable(out reason))
            throw new InvalidOperationException(reason);

        var scan = currentScanResult!;
        var (address, sectorBytes) = GbaWorkspaceClearPlan.PlanHeadErase(scan);
        var kind = GetRequiredSelectedProgrammerKind();
        if (kind is not (ProgrammerKind.WaterLink or ProgrammerKind.BeggarLegacy))
            throw new InvalidOperationException(ShellText("当前烧录器不支持 GBA 首扇区擦除。", "This programmer does not support erasing the first GBA NOR sector."));
        bool directLegacyErase = UseDirectGbaWorkspaceErase(scan, GetSelectedBurnProfileMode());
        var game = scan.Games.First(game => game.StartBucket == 0 && !game.IsMenu);
        string action = ShellText("清除启动头", "Clear boot header");
        string size = FormatKiBSize(sectorBytes);
        string port = GetSelectedPortName()!;
        var erase = new TaskDialogButton(action);
        var confirmation = new TaskDialogPage
        {
            Caption = action,
            Heading = ShellText($"清除「{game.Title}」的启动头？", $"Clear the boot header for {game.Title}?"),
            Text = ShellText($"这会擦除 NOR 的第一个扇区（{size}，从 0x{address:X8} 开始），再填充重复的 CHIS。这个游戏将无法从卡带启动，重新扫描也不会识别到它。其余 ROM 数据仍留在卡上。",
                $"This erases the first NOR sector ({size}, starting at 0x{address:X8}) and fills it with repeated CHIS bytes. The game will no longer boot or appear in a new scan. The rest of the ROM data remains on the cartridge."),
            Icon = TaskDialogIcon.Warning,
            DefaultButton = TaskDialogButton.Cancel,
            Buttons = { TaskDialogButton.Cancel, erase }
        };
        if (TaskDialog.ShowDialog(this, confirmation) != erase) return;

        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation;
        isBurnCancellationRequested = false;
        SetBusy(true, action);
        bool started = false, succeeded = false;
        try
        {
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, kind);
                BeginMeasuredProgress(action, sectorBytes, 1);
                started = true;
                EraseGbaWorkspaceSectors(protocol, kind, scan, [address], directLegacyErase, cancellation.Token, action, markFirstSector: true);
                CompleteGbaRomProgress(protocol);
            });
            ApplyScanResult(GbaWorkspaceClearPlan.BuildHeadRemovedScan(scan), false);
            succeeded = true;
        }
        finally
        {
            activeBurnCancellation = null;
            isBurnCancellationRequested = false;
            if (!succeeded)
            {
                AbortMeasuredProgress();
                if (started) { currentScanResult = null; lastScannedPortName = string.Empty; }
            }
            SetBusy(false, succeeded ? ShellText("游戏启动头已清除", "Game boot header cleared") :
                ShellText("启动头清除未完成，请重新分析卡带。", "Boot header erase incomplete. Analyze the cartridge again."));
        }
    }

    private async Task BurnEmptySingleGbaWorkspaceAsync()
    {
        if (activeBurnCancellation is not null) { RequestActiveBurnCancellation(); return; }
        if (isBusy || isAnalyzingQueueImports || !HasPendingGbaWorkspaceClear()) return;
        if (!EnsureSelectedPortHasActiveScan(out string scanReason)) throw new InvalidOperationException(scanReason);
        if (!EnsureCurrentScanIsWritable(out string writeReason)) throw new InvalidOperationException(writeReason);

        if (IsChisFlash10Mode() && !CanWriteChisFlash10()) throw new InvalidOperationException(ClientText.T("client", "chis10_erase_unsupported", "The current device or cartridge does not support erasing the 1.0V game area."));
        var scan = currentScanResult!;
        uint[] sectors = PendingGbaWorkspaceEraseSectors(scan);
        if (MessageBox.Show(this, GbaWorkspaceClearConfirmation() + GbaMappedHeadEraseNotice(scan), T("ui", "clear_workspace", "Clear Workspace"),
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        string port = GetSelectedPortName()!;
        var kind = GetRequiredSelectedProgrammerKind();
        bool directLegacyErase = UseDirectGbaWorkspaceErase(scan, GetSelectedBurnProfileMode());
        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation; isBurnCancellationRequested = false;
        SetBusy(true, GbaClearActionText());
        bool started = false, succeeded = false;
        try
        {
            await Task.Run(() =>
            {
                using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
                OpenProgrammerProtocol(protocol, kind);
                BeginMeasuredProgress(GbaClearActionText(), (long)sectors.Length * scan.Geometry.SectorSizeBytes, sectors.Length);
                started = true;
                EraseGbaWorkspaceSectors(protocol, kind, scan, sectors, directLegacyErase, cancellation.Token);
                CompleteGbaRomProgress(protocol);
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
            SetBusy(false, succeeded ? GbaClearActionText() + ShellText("完成", " complete") :
                GbaCardText("clear_interrupted", "清空未完成，请重新分析卡带。", "Erase incomplete; analyze the cartridge again."));
        }
    }
}
