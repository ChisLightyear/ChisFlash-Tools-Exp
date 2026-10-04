using System.Drawing;
using System.Windows.Forms;
using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private Mbc5EightMiBLayout mbc5Layout = new();
    private bool mbc5LayoutChanged;
    private readonly Dictionary<string, bool> mbc5ColumnVisibility = new();
    private ToolStripMenuItem? menuExportMbc5Image;
    private bool mbc5ProfileActive;
    private byte[]? mbc5LoadedScanImage;

    private bool IsMbc5EightMiBMode() => GetSelectedBurnProfileMode() == BurnProfileMode.Mbc5EightMiB;

    private void HandleMbc5ProfileSelection()
    {
        bool selected = IsMbc5EightMiBMode();
        if (selected != mbc5ProfileActive)
        {
            currentScanResult = null;
            lastScannedPortName = string.Empty;
        }
        mbc5ProfileActive = selected;
        if (selected && GetSelectedProgrammerKind() != ProgrammerKind.WaterLink)
        {
            bool initialized = beggarCartModeSelectionInitialized;
            beggarCartModeSelectionInitialized = false;
            try { radioButtonBeggarCartModeGb.Checked = true; radioButtonBeggarCartModeGba.Checked = false; }
            finally { beggarCartModeSelectionInitialized = initialized; }
        }
    }

    private string Mbc5Text(string key, string chinese, string english) =>
        T("mbc5", key, language.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english);

    private string Mbc5SlotName(int slot) => slot == 0
        ? Mbc5Text("menu", "菜单", "Menu")
        : Mbc5Text("game", "游戏", "Game") + " " + slot;

    private static string FormatMbc5Size(int bytes) => bytes < Mbc5EightMiBLayout.MiB
        ? $"{bytes / 1024d:0.##}K" : $"{bytes / (double)Mbc5EightMiBLayout.MiB:0.###}M";

    private void ResizeMbc5Rows()
    {
        if (!IsMbc5EightMiBMode() || dataGridViewRoms.Rows.Count != 5) return;
        int available = dataGridViewRoms.ClientSize.Height - dataGridViewRoms.ColumnHeadersHeight - SystemInformation.HorizontalScrollBarHeight - 4;
        int height = Math.Clamp(available / 5, Math.Max(22, dataGridViewRoms.Font.Height + 6), 46);
        foreach (DataGridViewRow row in dataGridViewRoms.Rows) row.Height = height;
        dataGridViewRoms.FirstDisplayedScrollingRowIndex = 0;
    }

    private string Mbc5LayoutDescription() => !mbc5Layout.HasMenu
        ? Mbc5Text("single_layout", "单卡：游戏1可放32K～8M；添加菜单后进入固定合卡", "Single ROM: Game 1 accepts 32 KiB–8 MiB; add a menu for fixed multicart")
        : mbc5Layout.IsThreeInOne ? "3in1: 1M + 2M + 4M · " + Mbc5Text("menu_reserved", "菜单另预留1M", "1 MiB reserved for menu")
        : "4in1: 1M + 2M + 2M + 2M · " + Mbc5Text("menu_reserved", "菜单另预留1M", "1 MiB reserved for menu");

    private string Mbc5ErrorMessage(Mbc5LayoutError error) => error switch
    {
        Mbc5LayoutError.RomTooLarge => Mbc5Text("too_large", "ROM实际大小或头部声明的寻址大小超过槽容量。添加菜单时，已有游戏1必须不超过1M。", "The file or its declared ROM size exceeds the slot. Adding a menu requires Game 1 to fit within 1 MiB."),
        Mbc5LayoutError.RomTooSmall => Mbc5Text("too_small", "游戏ROM至少需要32K；菜单必须包含有效GB头部。", "Game ROMs must be at least 32 KiB; a menu must contain a valid GB header."),
        Mbc5LayoutError.FourthSlotOccupied => Mbc5Text("overlap", "游戏3超过2M会占用第四槽。请先移除游戏4，或改用不超过2M的游戏3；不会自动覆盖。", "Game 3 above 2 MiB occupies Game 4's region. Remove Game 4 first or use a smaller Game 3; nothing is overwritten automatically."),
        Mbc5LayoutError.GamesRequireMenu => Mbc5Text("menu_required", "游戏2～4需要菜单。移除菜单前，请先移除这些游戏。", "Games 2–4 require a menu. Remove those games before removing the menu."),
        Mbc5LayoutError.SaveTooLarge => Mbc5Text("save_too_large", "此ROM需要的存档超过当前布局：合卡每游戏32K，单卡最多128K。", "The ROM's save requirement exceeds this layout: 32 KiB per multicart game, or 128 KiB for a single ROM."),
        _ => Mbc5Text("slot_unavailable", "此槽当前不可用，请先添加菜单或调整游戏3。", "This slot is unavailable; add a menu or adjust Game 3 first."),
    };

    private void RefreshMbc5Grid(int selectedSlot = -1)
    {
        HandleMbc5ProfileSelection();
        if (mbc5ColumnVisibility.Count == 0)
            foreach (DataGridViewColumn column in dataGridViewRoms.Columns)
                mbc5ColumnVisibility[column.Name] = column.Visible;
        foreach (string name in new[] { "Source", "Kind", "GbcDedicatedSave" })
            dataGridViewRoms.Columns[name]!.Visible = false;
        dataGridViewRoms.Columns["Order"]!.HeaderText = Mbc5Text("slot", "固定槽位", "Fixed Slot");
        dataGridViewRoms.Columns["HeaderTitle"]!.HeaderText = Mbc5Text("rom_size", "ROM大小", "ROM Size");
        dataGridViewRoms.Columns["AllocSize"]!.HeaderText = Mbc5Text("slot_capacity", "槽容量上限", "Slot Capacity");
        dataGridViewRoms.Columns["SaveType"]!.HeaderText = Mbc5Text("save_capacity", "存档空间", "Save Capacity");
        dataGridViewRoms.Columns["PatchStatus"]!.HeaderText = Mbc5Text("layout_hint", "布局说明", "Layout Hint");
        dataGridViewRoms.Columns["SpiSlot"]!.HeaderText = Mbc5Text("nor_offset", "NOR起点", "NOR Offset");
        dataGridViewRoms.Columns["SaveStatus"]!.HeaderText = Mbc5Text("slot_hint", "槽位状态", "Slot Status");
        dataGridViewRoms.Rows.Clear();
        for (int slot = 0; slot < Mbc5EightMiBLayout.SlotCount; slot++)
        {
            Mbc5SlotDefinition definition = mbc5Layout.GetSlot(slot);
            Mbc5SlotContent? content = mbc5Layout.GetContent(slot);
            string reason = definition.Enabled ? string.Empty : !mbc5Layout.HasMenu
                ? Mbc5Text("add_menu_first", "添加菜单后开放", "Available after adding a menu")
                : Mbc5Text("merged_into_game3", "已并入游戏3", "Merged into Game 3");
            string hint = slot == 3 && mbc5Layout.HasMenu
                ? Mbc5Text("game3_hint", "超过2M将占用第四槽", "Above 2 MiB occupies Game 4")
                : slot == 0 ? Mbc5Text("menu_hint", "需使用匹配的3/4in1菜单", "Use a matching 3/4in1 menu") : string.Empty;
            int rowIndex = dataGridViewRoms.Rows.Add(Mbc5SlotName(slot),
                !definition.Enabled ? Mbc5Text("disabled", "不可用", "Unavailable") : content is null ? Mbc5Text("empty", "可添加", "Empty") : content.Source == "cart" ? T("value", "row_state_cart", "On Cart") : T("value", "source_file", "File"),
                content?.Source ?? string.Empty, slot == 0 ? "Menu" : "Game", content?.Title ?? Mbc5Text("not_added", "未添加", "Not added"),
                content is null ? "—" : FormatMbc5Size(content.Data.Length),
                definition.Enabled ? "≤ " + FormatMbc5Size(definition.CapacityBytes) : "—",
                slot == 0 || !definition.Enabled ? "—" : FormatMbc5Size(definition.SaveCapacityBytes), false, hint,
                definition.Enabled ? $"0x{definition.OffsetBytes:X6}" : "—", reason);
            DataGridViewRow row = dataGridViewRoms.Rows[rowIndex];
            row.Tag = slot;
            row.ReadOnly = true;
            row.Height = 46;
            row.DefaultCellStyle.BackColor = definition.Enabled ? slot == 0 ? Color.FromArgb(230, 241, 252) : Color.White : Color.FromArgb(235, 235, 235);
            row.DefaultCellStyle.ForeColor = definition.Enabled ? Color.FromArgb(35, 45, 60) : Color.Gray;
            if (!definition.Enabled)
            {
                row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(210, 210, 210);
                row.DefaultCellStyle.SelectionForeColor = Color.FromArgb(85, 85, 85);
            }
            row.Cells["SaveStatus"].Style.Font = dataGridViewRoms.Font;
            foreach (DataGridViewCell cell in row.Cells)
                cell.ToolTipText = string.Join("\n", new[] { reason, hint, content?.Source, Mbc5LayoutDescription() }.Where(text => !string.IsNullOrWhiteSpace(text)));
        }
        groupBoxList.Text = "MBC5 8M · " + Mbc5LayoutDescription();
        int selection = selectedSlot >= 0 && selectedSlot < 5 ? selectedSlot : 1;
        dataGridViewRoms.ClearSelection();
        dataGridViewRoms.Rows[selection].Selected = true;
        dataGridViewRoms.CurrentCell = dataGridViewRoms.Rows[selection].Cells["Title"];
        ResizeMbc5Rows();
        if (menuExportMbc5Image is null)
        {
            menuExportMbc5Image = new ToolStripMenuItem();
            menuExportMbc5Image.Click += async (_, _) => await RunUiActionSafelyAsync(ExportMbc5ImageAsync);
            menuRom.DropDownItems.Add(menuExportMbc5Image);
        }
        RefreshMbc5Preview();
        UpdateMbc5ActionButtons(isBusy);
    }

    private void RestoreMbc5GridColumns()
    {
        if (mbc5ColumnVisibility.Count == 0)
            return;
        foreach ((string name, bool visible) in mbc5ColumnVisibility)
            dataGridViewRoms.Columns[name]!.Visible = visible;
        mbc5ColumnVisibility.Clear();
        buttonQueueAdd.Text = T("ui", "queue_add_rom", "Add ROM");
        buttonQueueClear.Text = T("ui", "queue_clear", "Clear Queue");
        if (menuExportMbc5Image is not null)
            menuExportMbc5Image.Visible = false;
        RefreshGridHeaders();
    }

    private int SelectedMbc5Slot() => dataGridViewRoms.SelectedRows.Count > 0 ? dataGridViewRoms.SelectedRows[0].Index : 1;

    private bool CanWriteMbc5Image() => GetSelectedProgrammerKind() == ProgrammerKind.BeggarLegacy &&
        GetSelectedBeggarCartMode() == BeggarCartMode.Gb && HasActiveScanResult() &&
        currentScanResult?.IsBeggarMaxGbcMode == true && currentScanResult.Geometry.DeviceSizeBytes == Mbc5EightMiBLayout.CapacityBytes &&
        !currentScanResult.IsWriteProtected && currentScanResult.Geometry.PrimaryCommandSetId == 2 &&
        currentScanResult.Geometry.RegionCount == 1 && currentScanResult.Geometry.BufferWriteBytes is > 0 and <= ushort.MaxValue &&
        currentScanResult.Geometry.SectorSizeBytes >= Mbc5EightMiBWriter.BankBytes &&
        (long)currentScanResult.Geometry.SectorSizeBytes * currentScanResult.Geometry.SectorCount == Mbc5EightMiBLayout.CapacityBytes;

    private void UpdateMbc5ActionButtons(bool busy)
    {
        bool idle = !busy && !isAnalyzingQueueImports;
        int selected = SelectedMbc5Slot();
        bool enabled = mbc5Layout.GetSlot(selected).Enabled;
        bool occupied = mbc5Layout.GetContent(selected) is not null;
        menuRom.Enabled = menuCartridge.Enabled = true;
        menuSave.Enabled = false;
        buttonScan.Enabled = idle && !string.IsNullOrWhiteSpace(GetSelectedPortName()) && TryGetSelectedKnownProgrammerKind(out _, out _);
        menuScan.Enabled = buttonScan.Enabled;
        buttonQueueAdd.Enabled = menuAddRom.Enabled = idle && enabled;
        buttonQueueAdd.Text = selected == 0 ? Mbc5Text("add_menu", "添加菜单", "Add Menu") : Mbc5Text("add_game", "添加游戏", "Add Game");
        buttonQueueRemove.Enabled = menuRemoveSelectedRom.Enabled = idle && occupied;
        buttonQueueClear.Enabled = menuClearRomQueue.Enabled = idle && mbc5Layout.HasContent;
        buttonQueueClear.Text = Mbc5Text("clear_slots", "清空槽位", "Clear Slots");
        buttonQueueBurn.Enabled = menuStartBurn.Enabled = activeBurnCancellation is not null ? !isBurnCancellationRequested : idle && mbc5Layout.HasContent;
        buttonQueueBurn.Text = menuStartBurn.Text = activeBurnCancellation is not null ? T("ui", "cancel_burn", "Cancel") : CanWriteMbc5Image()
            ? Mbc5Text("burn_image", "烧录完整ROM", "Burn Full ROM") : Mbc5Text("export_image", "导出完整8M ROM", "Export Full 8 MiB ROM");
        menuReplaceSelectedRom.Enabled = menuItemReplaceRom.Enabled = idle && enabled;
        menuExportSelectedRom.Enabled = menuItemExportRom.Enabled = idle && occupied;
        foreach (ToolStripMenuItem item in new[] { menuUpdateMenu, menuReplaceMenuFile, menuRepairMenu, menuUpdateBackground, menuEraseConfig, menuDetectMagic, menuUnlockPpb, menuPendingGbaCovers, menuDeleteSelectedMapping, menuItemDeleteMapping, menuExportSelectedSave, menuImportSelectedSave, menuClearSelectedSave, menuExportChipSave, menuImportChipSave, menuClearChipSave, menuItemExportSave, menuItemImportSave, menuItemClearSave, menuItemExportChipSaveContext, menuItemImportChipSaveContext, menuItemClearChipSaveContext, menuFragmentTest })
            item.Enabled = false;
        menuEraseEntireNor.Enabled = idle && CanEraseEntireNorCurrentScan(out _);
        menuRefreshPorts.Enabled = menuExit.Enabled = idle;
        if (menuFcTools is not null) menuFcTools.Visible = false;
        comboBoxBurnProfileMode.Enabled = idle && burnProfileModeOptions.Count > 0;
        checkBoxWaitcntPatch.Enabled = comboBoxGbaMenu.Enabled = comboBoxGbcMenuLanguage.Enabled = false;
        dataGridViewRoms.Enabled = groupBoxList.Enabled = !busy;
        if (menuExportMbc5Image is not null)
        {
            menuExportMbc5Image.Visible = true;
            menuExportMbc5Image.Text = Mbc5Text("export_image", "导出完整8M ROM", "Export Full 8 MiB ROM");
            menuExportMbc5Image.Enabled = idle && mbc5Layout.HasContent;
        }
        UpdateProgrammerModeSelectorUi(idle);
        labelScanMethodValue.Text = Mbc5Text("scan_method", "GB NOR固定槽位扫描", "GB NOR fixed-slot scan");
        if (idle) labelStatus.Text = Mbc5LayoutDescription();
    }

    private Task SelectMbc5FilesAsync()
    {
        int slot = SelectedMbc5Slot();
        if (isBusy || !mbc5Layout.GetSlot(slot).Enabled)
            return Task.CompletedTask;
        using var dialog = new OpenFileDialog
        {
            Title = Mbc5SlotName(slot), Filter = "GB/GBC ROM (*.gb;*.gbc)|*.gb;*.gbc", Multiselect = slot != 0,
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? AddMbc5FilesAsync(dialog.FileNames, slot) : Task.CompletedTask;
    }

    private Mbc5SlotContent ReadMbc5File(string path)
    {
        FileInfo file = new(path);
        if (file.Length < 0x150 || file.Length > Mbc5EightMiBLayout.CapacityBytes)
            throw new InvalidOperationException(Mbc5Text("invalid_rom", "文件必须是包含有效头部的GB/GBC ROM，大小不能超过8M。", "Use a GB/GBC ROM with a valid header, no larger than 8 MiB."));
        byte[] bytes = File.ReadAllBytes(path);
        var metadata = GameBoyRomHeaderService.Read(path, bytes);
        if (!metadata.HasValidNintendoLogo)
            throw new InvalidOperationException(Mbc5Text("invalid_header", "未找到有效GB ROM标志，请检查文件。", "The GB ROM logo is invalid; check the file."));
        return new(bytes, checked((int)metadata.EffectiveRomSizeBytes), metadata.EffectiveRamBankCount * 0x2000,
            Path.GetFileName(path), Path.GetFileName(path));
    }

    private async Task AddMbc5FilesAsync(IEnumerable<string> paths, int? targetSlot = null)
    {
        if (isBusy || isAnalyzingQueueImports) return;
        string[] files = paths.ToArray();
        int slot = targetSlot ?? SelectedMbc5Slot();
        if (!mbc5Layout.GetSlot(slot).Enabled)
            throw new InvalidOperationException(Mbc5ErrorMessage(Mbc5LayoutError.FourthSlotUnavailable));
        if (mbc5Layout.GetContent(slot) is not null && MessageBox.Show(this,
            Mbc5Text("replace_confirm", "替换选中槽的工作区内容？此时不会写入卡带。", "Replace this workspace slot? This does not write to the cartridge."),
            Mbc5SlotName(slot), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Mbc5EightMiBLayout proposed = mbc5Layout.Snapshot();
        SetBusy(true, Mbc5Text("loading", "正在读取ROM…", "Reading ROM…"));
        try
        {
            Mbc5SlotContent[] contents = await Task.Run(() => files.Select(ReadMbc5File).ToArray());
            foreach (Mbc5SlotContent content in contents)
            {
                if (!proposed.GetSlot(slot).Enabled)
                    throw new InvalidOperationException(Mbc5ErrorMessage(Mbc5LayoutError.FourthSlotUnavailable));
                if (!proposed.TrySet(slot, content, out Mbc5LayoutError error))
                    throw new InvalidOperationException(Mbc5ErrorMessage(error));
                if (!ReferenceEquals(content, contents[^1]))
                {
                    slot = Enumerable.Range(slot + 1, 4 - slot).FirstOrDefault(index => proposed.GetSlot(index).Enabled && proposed.GetContent(index) is null, -1);
                    if (slot < 0) throw new InvalidOperationException(Mbc5Text("no_slot", "没有足够的可用槽位，本次文件未加入。", "Not enough available slots; this batch was not added."));
                }
            }
            mbc5Layout = proposed;
            mbc5LayoutChanged = true;
        }
        finally
        {
            SetBusy(false, Mbc5LayoutDescription());
            RefreshMbc5Grid(slot);
        }
    }

    private void RemoveMbc5Slot()
    {
        if (isBusy) return;
        int slot = SelectedMbc5Slot();
        if (!mbc5Layout.TrySet(slot, null, out Mbc5LayoutError error))
        {
            MessageBox.Show(this, Mbc5ErrorMessage(error), Mbc5SlotName(slot), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        mbc5LayoutChanged = true;
        RefreshMbc5Grid(slot);
    }

    private void ClearMbc5Slots()
    {
        if (isBusy || MessageBox.Show(this, Mbc5Text("clear_confirm", "清空五个工作区槽位？不会擦除卡带。", "Clear all workspace slots? The cartridge will not be erased."),
            "MBC5 8M", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        mbc5Layout.Clear();
        mbc5LayoutChanged = true;
        RefreshMbc5Grid();
    }

    private async Task ExportMbc5ImageAsync()
    {
        if (isBusy || !mbc5Layout.HasContent) return;
        using var dialog = new SaveFileDialog { Filter = "GB ROM (*.gb)|*.gb", FileName = "MBC5_8M.gb" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        byte[] image = mbc5Layout.BuildImage();
        SetBusy(true, Mbc5Text("exporting", "正在导出完整ROM…", "Exporting full ROM…"));
        try { await File.WriteAllBytesAsync(dialog.FileName, image); }
        finally { SetBusy(false, Mbc5LayoutDescription()); }
        AppendLog(Mbc5Text("export_done", "已导出完整8M ROM，空白和预留区域填充FF；可交给其他烧录器完整烧录。", "Exported a full 8 MiB ROM, with unused areas filled with FF, for whole-image programming."));
    }

    private async Task ExportMbc5SlotAsync()
    {
        if (isBusy || mbc5Layout.GetContent(SelectedMbc5Slot()) is not { } content) return;
        using var dialog = new SaveFileDialog { Filter = "GB ROM (*.gb)|*.gb", FileName = Mbc5SlotName(SelectedMbc5Slot()) + ".gb" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await File.WriteAllBytesAsync(dialog.FileName, content.Data.ToArray());
    }

    private byte[] ReadMbc5ImageFromDevice(WaterDeviceProtocol protocol)
    {
        byte[] image = new byte[Mbc5EightMiBLayout.CapacityBytes];
        try
        {
            for (int bank = 0; bank < image.Length / Mbc5EightMiBWriter.BankBytes; bank++)
                ReadBeggarMaxSectorImage(protocol, bank * Mbc5EightMiBWriter.BankBytes, Mbc5EightMiBWriter.BankBytes).CopyTo(image, bank * Mbc5EightMiBWriter.BankBytes);
            return image;
        }
        finally
        {
            if (!TrySwitchBeggarMaxRomBank(protocol, 0)) throw new IOException("Could not restore ROM bank 0.");
        }
    }

    private void LoadMbc5ScanImage(byte[] image)
    {
        var proposed = new Mbc5EightMiBLayout();
        bool hasMenu = false;
        if (TryReadMbc5CartContent(image, 0, Mbc5EightMiBLayout.CapacityBytes, out Mbc5SlotContent? first))
            hasMenu = first!.Title.Equals("MULTICARTLDR", StringComparison.OrdinalIgnoreCase);
        if (hasMenu)
        {
            if (!proposed.TrySet(0, first, out Mbc5LayoutError menuError))
                throw new InvalidOperationException(Mbc5ErrorMessage(menuError));
            for (int slot = 1; slot < 5; slot++)
            {
                Mbc5SlotDefinition definition = proposed.GetSlot(slot);
                if (definition.Enabled && TryReadMbc5CartContent(image, definition.OffsetBytes, definition.CapacityBytes, out Mbc5SlotContent? content) &&
                    !proposed.TrySet(slot, content, out Mbc5LayoutError error))
                    throw new InvalidOperationException(Mbc5ErrorMessage(error));
            }
        }
        else if (first is not null && !proposed.TrySet(1, first, out Mbc5LayoutError error))
            throw new InvalidOperationException(Mbc5ErrorMessage(error));
        mbc5Layout = proposed;
        mbc5LayoutChanged = false;
    }

    private static bool TryReadMbc5CartContent(byte[] image, int offset, int capacity, out Mbc5SlotContent? content)
    {
        content = null;
        if (image[offset + 0x148] is not (<= 8 or 0x52 or 0x53 or 0x54)) return false;
        var metadata = GameBoyRomHeaderService.Read("cartridge.gb", image.AsSpan(offset, 0x150));
        if (!metadata.HasValidNintendoLogo) return false;
        int size = checked((int)metadata.DeclaredRomSizeBytes);
        if (size < 0x8000 || size > capacity) throw new InvalidOperationException("The cartridge header exceeds its fixed slot; back up the full ROM before editing.");
        content = new(image.AsSpan(offset, size).ToArray(), size, metadata.EffectiveRamBankCount * 0x2000, metadata.HeaderTitle, "cart");
        return true;
    }

    private async Task HandleMbc5PrimaryActionAsync()
    {
        if (activeBurnCancellation is not null) { RequestActiveBurnCancellation(); return; }
        if (isBusy || !mbc5Layout.HasContent) return;
        if (!CanWriteMbc5Image()) { await ExportMbc5ImageAsync(); return; }
        if (!mbc5LayoutChanged && MessageBox.Show(this, Mbc5Text("unchanged_confirm", "工作区没有修改，仍要完整擦写吗？", "The workspace is unchanged. Rewrite the entire ROM anyway?"), "MBC5 8M", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (MessageBox.Show(this, Mbc5Text("burn_confirm", "将全擦除8M NOR并烧录当前五槽布局，最后逐字节校验。未加入工作区的旧ROM会被清除，SRAM存档不擦除。\n\n请先备份ROM和存档，并确认卡带已安装匹配的MBC5 3/4in1固件。继续？", "Erase the entire 8 MiB NOR, write this layout, and verify every byte? Old ROMs absent from the workspace will be erased; SRAM is not erased.\n\nBack up ROMs and saves and confirm matching MBC5 3/4in1 firmware before continuing."),
            "MBC5 8M", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        byte[] image = mbc5Layout.BuildImage();
        WaterCartScanResult scan = currentScanResult!;
        string port = GetSelectedPortName()!;
        using var cancellation = new CancellationTokenSource();
        activeBurnCancellation = cancellation;
        isBurnCancellationRequested = false;
        SetBusy(true, Mbc5Text("burning", "正在完整擦写并校验…", "Erasing, writing and verifying…"));
        try
        {
            await Task.Run(() => WriteMbc5ImageOnDevice(port, scan, image, cancellation.Token));
            mbc5LayoutChanged = false;
            AppendLog(Mbc5Text("verified", "完整8M ROM写入并逐字节校验通过；游戏运行兼容性仍需实机测试。", "The full 8 MiB ROM passed byte-for-byte verification; game compatibility still requires hardware testing."));
        }
        finally
        {
            activeBurnCancellation = null;
            isBurnCancellationRequested = false;
            currentScanResult = null;
            lastScannedPortName = string.Empty;
            SetBusy(false, Mbc5Text("rescan", "操作结束，请重新扫描卡带。", "Operation ended; rescan the cartridge."));
        }
    }

    private void WriteMbc5ImageOnDevice(string port, WaterCartScanResult expected, byte[] image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var protocol = new WaterDeviceProtocol(port, ReportTransferredBytes);
        OpenProgrammerProtocol(protocol, ProgrammerKind.BeggarLegacy);
        if (!WaterCartScanner.TryScanBeggarMaxGbc(protocol, AppendLog, out WaterCartScanResult live, scanContent: false) ||
            live.Geometry.DeviceSizeBytes != Mbc5EightMiBLayout.CapacityBytes || live.RawNorIdHex != expected.RawNorIdHex ||
            live.Geometry.PrimaryCommandSetId != 2 || live.IsWriteProtected || live.Geometry.RegionCount != 1 ||
            live.Geometry.SectorSizeBytes != expected.Geometry.SectorSizeBytes || live.Geometry.SectorCount != expected.Geometry.SectorCount ||
            live.Geometry.BufferWriteBytes <= 0 || live.Geometry.BufferWriteBytes > ushort.MaxValue)
            throw new InvalidOperationException("NOR identity or geometry changed; no erase was performed. Rescan the cartridge.");
        int total = live.Geometry.SectorCount + 1024;
        BeginMeasuredProgress("MBC5 8M", total, total);
        try
        {
            Mbc5EightMiBWriter.Write(image, live.Geometry.DeviceSizeBytes, live.Geometry.SectorSizeBytes, live.Geometry.SectorCount,
                address => EraseBeggarMaxRange(protocol, address, address + live.Geometry.SectorSizeBytes - 1, live.Geometry.SectorSizeBytes, cancellationToken),
                (bank, data) =>
                {
                    if (!TrySwitchBeggarMaxRomBank(protocol, bank)) throw new IOException("ROM bank selection failed.");
                    for (int offset = 0; offset < data.Length; offset += 4096)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!protocol.TryProgramGbcRom((uint)(GetBeggarMaxBankBaseAddress(bank) + offset), data.AsSpan(offset, 4096).ToArray(), (ushort)live.Geometry.BufferWriteBytes, BeggarMaxWriteTimeoutMs))
                            throw new IOException("GB ROM programming failed.");
                    }
                },
                bank => ReadBeggarMaxSectorImage(protocol, bank * Mbc5EightMiBWriter.BankBytes, Mbc5EightMiBWriter.BankBytes, cancellationToken),
                () => { if (!protocol.TryWriteGbc(0, [0xF0], 3000) || !TrySwitchBeggarMaxRomBank(protocol, 0)) throw new IOException("Could not restore ROM bank 0."); },
                (_, _, phase) => AdvanceMeasuredProgress(1, 1, phase), cancellationToken);
            CompleteMeasuredProgress("MBC5 8M");
        }
        catch { AbortMeasuredProgress(); throw; }
    }

    private void RefreshMbc5Preview()
    {
        var bitmap = new Bitmap(144, 112);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Microsoft YaHei UI", 7.5F))
        using (var pen = new Pen(Color.White, 2F))
        {
            graphics.Clear(Color.FromArgb(245, 248, 252));
            graphics.DrawString(mbc5Layout.HasMenu ? mbc5Layout.IsThreeInOne ? "3in1 · 1+2+4" : "4in1 · 1+2+2+2" : "Single ROM · 32K–8M", font, Brushes.Black, 5, 8);
            int[] slots = !mbc5Layout.HasMenu ? [1] : mbc5Layout.IsThreeInOne ? [0, 1, 2, 3] : [0, 1, 2, 3, 4];
            float start = 8;
            foreach (int slot in slots)
            {
                int size = !mbc5Layout.HasMenu ? 8 : slot <= 1 ? 1 : slot == 3 && mbc5Layout.IsThreeInOne ? 4 : 2;
                float width = size * 16;
                using var brush = new SolidBrush(slot == 0 ? Color.LightSlateGray : mbc5Layout.GetContent(slot) is null ? Color.FromArgb(192, 213, 237) : Color.FromArgb(111, 167, 221));
                graphics.FillRectangle(brush, start, 32, width, 42);
                graphics.DrawRectangle(pen, start, 32, width, 42);
                graphics.DrawString((slot == 0 ? "M" : slot.ToString()) + "\n" + size, font, Brushes.Black, new RectangleF(start + 2, 36, width - 2, 36));
                start += width;
            }
            graphics.DrawString(Mbc5Text("preview_hint", "单位M；M为菜单\n小ROM余量不跨槽共享", "MiB; M = menu\nUnused space is not shared"), font, Brushes.Black, new RectangleF(5, 80, 136, 30));
        }
        Image? previous = pictureBoxMenuBackgroundPreview.Image;
        pictureBoxMenuBackgroundPreview.Image = bitmap;
        previous?.Dispose();
        labelMenuBackgroundPreviewPlaceholder.Visible = false;
        pictureBoxMenuBackgroundPreview.Visible = true;
        tableLayoutMenuBackgroundPreview.Visible = true;
    }
}
