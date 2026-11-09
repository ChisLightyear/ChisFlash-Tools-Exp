using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private int gbaSingleReplacementCapacityBytes;
    private int chisFlash10ReplacementCapacityBytes;

    private WaterCartEntry? GetChisFlash10CartGame() => HasPendingGbaWorkspaceClear() ? null : GetHeadAreaGame();

    private int GetSingleGbaReplacementCapacity(bool chisFlash10)
    {
        int limit = chisFlash10 ? chisFlash10ReplacementCapacityBytes : gbaSingleReplacementCapacityBytes;
        byte[]? pending = chisFlash10 ? chisFlash10Rom : gbaSingleRom;
        WaterCartEntry? source = chisFlash10 ? GetChisFlash10CartGame() : GetGbaSingleGame();
        if (limit <= 0 && source is not null && (source.FoundInMenuConfig || source.FoundInWlMapping))
            limit = GetCartEntryAllocatedSizeBytes(source);
        if (limit <= 0 && pending is not null) limit = pending.Length + (pending.Length & 1);
        if (limit <= 0)
            limit = currentScanResult?.Entries.FirstOrDefault(entry => !entry.IsMenu && entry.StartBucket == 0)?.AllocatedSizeBytesOverride ?? 0;
        // 只有头部时无法推断ROM长度；单卡独占可用ROM区，不把头部扫描步长当作容量。
        if (limit <= 0) limit = ChisFlash10Image.CapacityBytes;
        if (HasActiveScanResult()) limit = Math.Min(limit, currentScanResult!.Geometry.DeviceSizeBytes);
        return Math.Min(limit, ChisFlash10Image.CapacityBytes);
    }

    private void ValidateReplacementRomLength(int length, int limit, string name)
    {
        if (limit <= 0 || length <= limit) return;
        throw new InvalidOperationException(Tf("error", "prepared_rom_exceeds_allocated_space",
            "After patch preparation, {0} needs {1}, but the allocated mapping only allows {2}.",
            name, FormatMiBSize(length), FormatMiBSize(limit)));
    }

    private void EnsureReplacementRomFits(PendingRomImportItem item, int length) =>
        ValidateReplacementRomLength(length, item.ReplacementCapacityBytes, item.FileName);

    private async Task ReplaceSingleGbaRomAsync()
    {
        if (!CanReplaceGbaWorkspaceSelection()) return;
        using var dialog = new OpenFileDialog
        {
            Title = T("dialog", "replace_rom_title", "Replace ROM"),
            Filter = "GBA ROM (*.gba;*.zip)|*.gba;*.zip", Multiselect = false,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) await StageSingleGbaReplacementAsync(dialog.FileName);
    }

    private async Task StageSingleGbaReplacementAsync(string filePath)
    {
        if (!CanReplaceGbaWorkspaceSelection()) return;
        bool chisFlash10 = IsChisFlash10Mode();
        int limit = GetSingleGbaReplacementCapacity(chisFlash10);
        SetBusy(true, GbaCardText("replacement_loading", "正在读取替换ROM…", "Reading replacement ROM…"));
        try
        {
            var prepared = await Task.Run(() =>
            {
                var imported = CreatePendingRomImportItemsFromSource(filePath, false);
                if (imported.Items.Count != 1) throw new InvalidDataException("替换每次只允许选择一个游戏。");
                var item = imported.Items[0];
                byte[] bytes = File.ReadAllBytes(item.FilePath);
                if (item.ImportedPlatform is not (null or RomPlatform.Gba) || !WaterCartScanner.HasValidGbaHeader(bytes))
                    throw new InvalidDataException("替换GBA游戏需要有效的GBA ROM。");
                ValidateReplacementRomLength(checked(bytes.Length + (bytes.Length & 1)), limit, item.FileName);
                return (Bytes: bytes, Item: item, Save: ChisFlash10Image.Analyze(bytes));
            });
            void Apply()
            {
                if (chisFlash10)
                {
                    chisFlash10Rom = prepared.Bytes; chisFlash10Name = prepared.Item.FileName; chisFlash10Save = prepared.Save;
                    chisFlash10WaitcntPatch = defaultWaitcntPatchEnabled; chisFlash10ReplacementCapacityBytes = limit;
                    RefreshChisFlash10Grid();
                }
                else
                {
                    gbaSingleRom = prepared.Bytes; gbaSingleRomPath = prepared.Item.FilePath;
                    gbaSingleRomName = prepared.Item.FileName; gbaSingleRomSave = prepared.Save;
                    gbaSingleWaitcntPatch = defaultWaitcntPatchEnabled; gbaSingleReplacementCapacityBytes = limit;
                    gbaSingleGameRemoved = false; gbaSingleNeedsRelocation = false; gbaSingleGridActive = false;
                    RefreshGbaCardModeUi();
                }
            }
            if (InvokeRequired) Invoke(new Action(Apply)); else Apply();
        }
        finally
        {
            void Complete() => SetBusy(false, T("status", "ready", "Ready"));
            if (InvokeRequired) Invoke(new Action(Complete)); else Complete();
        }
    }
}
