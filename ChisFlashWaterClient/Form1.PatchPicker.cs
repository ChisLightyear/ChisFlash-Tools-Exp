namespace ChisFlashWaterClient;

public partial class Form1
{
    private PendingRomImportItem? singleGbaPatchItem;
    private PendingRomImportItem? chis10PatchItem;

    private PendingRomImportItem AnalyzeSinglePatchItem(byte[] bytes, string file, DetectedSaveTypeInfo save)
    {
        string target = sramPatchService.DetectPatchTarget(bytes) ?? string.Empty;
        string applied = sramPatchService.DetectAppliedPatchTarget(bytes) ?? string.Empty;
        var inspection = batterylessPatchService.Analyze(bytes);
        var item = new PendingRomImportItem
        {
            FileName = Path.GetFileName(file), FileSizeBytes = bytes.Length, SaveTypeInfo = save,
            SramPatchTargetIdentifier = target, AppliedSramPatchIdentifier = applied,
            ApplySramPatch = ShouldApplySramPatch(save, target, applied, inspection),
            PreparedBatterylessInspection = inspection
        };
        return SetSinglePatchDefaults(item);
    }

    private static PendingRomImportItem SetSinglePatchDefaults(PendingRomImportItem item)
    {
        // 单卡保持原有的原 ROM 默认值，各卡型分别记住手动勾选。
        foreach (var mode in Enum.GetValues<BurnProfileMode>())
        {
            if (item.ApplySramPatch) item.SkipSramFor.Add(mode);
            if (UsesBatterylessPatch(mode)) item.SkipBatterylessFor.Add(mode);
        }
        return item;
    }

    private async Task<byte[]> PrepareSelectedGamePatchesAsync(PendingRomImportItem item, byte[] source,
        WaterCartScanResult? scan, bool waitcnt)
    {
        var preparation = BuildPendingRomPreparation(item, source, scan, GetSelectedBurnProfileMode(), waitcnt);
        if (preparation.PatchedRomTask is not { } prepare) return source;
        SetBusy(true, ShellText("正在准备所选 ROM 补丁…", "Preparing selected ROM patches…"));
        try { return await prepare; }
        finally { SetBusy(false, T("status", "ready", "Ready")); }
    }

    private sealed class PatchPickerCell : DataGridViewTextBoxCell
    {
        protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
            DataGridViewElementStates cellState, object? value, object? formattedValue, string? errorText,
            DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue, errorText,
                cellStyle, advancedBorderStyle, paintParts);
            if (!paintParts.HasFlag(DataGridViewPaintParts.ContentForeground)) return;
            Rectangle arrow = new(cellBounds.Right - cellStyle.Padding.Right, cellBounds.Top,
                cellStyle.Padding.Right, cellBounds.Height);
            Color color = cellState.HasFlag(DataGridViewElementStates.Selected) ? cellStyle.SelectionForeColor : cellStyle.ForeColor;
            TextRenderer.DrawText(graphics, "▾", cellStyle.Font, arrow, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseClick(DataGridViewCellMouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && DataGridView?.FindForm() is Form1 form)
                form.OpenPatchPicker(this);
        }

        protected override void OnKeyDown(KeyEventArgs e, int rowIndex)
        {
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down)) &&
                DataGridView?.FindForm() is Form1 form)
            {
                e.SuppressKeyPress = true;
                form.OpenPatchPicker(this);
                return;
            }
            base.OnKeyDown(e, rowIndex);
        }
    }

    private void InstallPatchPickerCell(DataGridViewRow row)
    {
        var previous = row.Cells["PatchStatus"];
        var picker = new PatchPickerCell { Value = previous.Value };
        picker.Style.Padding = new Padding(0, 0, LogicalToDeviceUnits(18), 0);
        row.Cells[previous.ColumnIndex] = picker;
        picker.ReadOnly = true;
    }

    private void OpenPatchPicker(PatchPickerCell cell)
    {
        if (isBusy || isAnalyzingQueueImports || cell.OwningRow?.Tag is not WorkspaceListRow { PendingItem: { } item } row)
            return;
        BurnProfileMode mode = GetSelectedBurnProfileMode();
        var panel = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            FlowDirection = FlowDirection.TopDown, Padding = new Padding(4), Font = dataGridViewRoms.Font,
            BackColor = SystemColors.Window, ForeColor = SystemColors.WindowText
        };
        CheckBox AddOption(string text, bool enabled, bool selected)
        {
            var box = new CheckBox { Text = text, Enabled = enabled, Checked = selected, AutoSize = true, UseMnemonic = false };
            panel.Controls.Add(box);
            return box;
        }

        bool large = item.SramPatchTargetIdentifier.StartsWith("FLASH1M", StringComparison.OrdinalIgnoreCase);
        bool sram = item.WantsSram(mode);
        var regular = AddOption(GbaCardText("patch_sram_normal", "普通 SRAM 补丁", "Standard SRAM patch"),
            item.ApplySramPatch && !large, sram && !large);
        var banked = AddOption(GbaCardText("patch_sram_1m", "1 Mbit 专用 SRAM 补丁", "1 Mbit SRAM patch"),
            item.ApplySramPatch && large, sram && large);
        var inspection = GetBatterylessInspection(item);
        bool batterySupported = UsesBatterylessPatch(mode) && !inspection.AlreadyPatched && inspection.CanInstallPayload &&
            item.SaveTypeInfo.Kind != DetectedSaveTypeKind.None &&
            (!IsGbaConversionDraft() || item.ApplySramPatch || item.SaveTypeInfo.Kind == DetectedSaveTypeKind.Sram);
        var battery = AddOption(GbaCardText("patch_batteryless", "无电池补丁", "Batteryless patch"),
            batterySupported && !item.SkipSramFor.Contains(mode), batterySupported && item.WantsBatteryless(mode));

        void UpdateRow()
        {
            cell.Value = FormatWorkspacePatchOrMbcDisplay(row);
            if (!IsGbaSingleCardMode() && !IsChisFlash10Mode())
                cell.OwningRow.Cells["AllocSize"].Value = FormatAllocatedSizeText(GetWorkspaceRowAllocatedSizeBytes(row));
            UpdateActionButtons(isBusy);
        }
        var applicable = large ? banked : regular;
        applicable.Click += (_, _) =>
        {
            if (applicable.Checked) item.SkipSramFor.Remove(mode); else item.SkipSramFor.Add(mode);
            battery.Enabled = batterySupported && applicable.Checked;
            battery.Checked = batterySupported && item.WantsBatteryless(mode);
            UpdateRow();
        };
        battery.Click += (_, _) =>
        {
            if (battery.Checked) item.SkipBatterylessFor.Remove(mode); else item.SkipBatterylessFor.Add(mode);
            UpdateRow();
        };
        var popup = new ToolStripDropDown { Padding = Padding.Empty };
        var host = new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty };
        popup.Items.Add(host);
        components.Add(popup);
        popup.Closed += (_, _) =>
        {
            if (IsHandleCreated && !Disposing) BeginInvoke(popup.Dispose);
        };
        var bounds = dataGridViewRoms.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, true);
        popup.Show(dataGridViewRoms, new Point(bounds.Left, bounds.Bottom));
    }
}
