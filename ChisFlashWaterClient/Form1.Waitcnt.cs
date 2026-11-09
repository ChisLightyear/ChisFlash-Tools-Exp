using ChisFlashWaterClient.Services;
using GbaMultiMenuBuilder.Services;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private bool defaultWaitcntPatchEnabled;
    private bool gbaSingleWaitcntPatch;
    private bool chisFlash10WaitcntPatch;
    private bool refreshingWaitcntPatch;

    private bool IsWaitcntGbaWorkspace() => GetSelectedBeggarCartMode() == BeggarCartMode.Gba &&
        !IsFcWorkspaceActive && !IsGbcWorkspaceMode() && !IsMbc5FixedSlotMode() &&
        GetSelectedBurnProfileMode() != BurnProfileMode.FcMbcx;

    private bool? GetRowWaitcntPatch(DataGridViewRow row)
    {
        if (!IsWaitcntGbaWorkspace()) return null;
        if (IsChisFlash10Mode()) return chisFlash10Rom is not null && row.Index == 0 ? chisFlash10WaitcntPatch : null;
        if (row.Tag is not WorkspaceListRow workspaceRow) return null;
        if (IsGbaSingleCardMode()) return gbaSingleRom is not null ? gbaSingleWaitcntPatch : null;
        return workspaceRow.PendingItem is { IsLoadingPlaceholder: false, ImportedPlatform: null or RomPlatform.Gba, MdRom: null, FcRom: null } item
            ? item.ApplyWaitcntPatch : null;
    }

    private void SetRowWaitcntPatch(DataGridViewRow row, bool enabled)
    {
        if (GetRowWaitcntPatch(row) is null) return;
        if (IsChisFlash10Mode()) chisFlash10WaitcntPatch = enabled;
        else if (IsGbaSingleCardMode()) gbaSingleWaitcntPatch = enabled;
        else ((WorkspaceListRow)row.Tag!).PendingItem!.ApplyWaitcntPatch = enabled;
    }

    private void ToggleRowWaitcntPatch(int index)
    {
        if (isBusy || isAnalyzingQueueImports || index < 0 || index >= dataGridViewRoms.Rows.Count) return;
        var row = dataGridViewRoms.Rows[index];
        if (GetRowWaitcntPatch(row) is not bool enabled) return;
        SetRowWaitcntPatch(row, !enabled);
        RefreshWaitcntPatchUi();
    }

    private void ToggleAllWaitcntPatches()
    {
        if (isBusy || isAnalyzingQueueImports || !IsWaitcntGbaWorkspace()) return;
        bool enabled = checkBoxWaitcntPatch.CheckState != CheckState.Checked;
        defaultWaitcntPatchEnabled = enabled;
        ChisFlashWaterClient.Services.AppConfigService.SaveWaitcntPatchEnabled(enabled);
        foreach (DataGridViewRow row in dataGridViewRoms.Rows) SetRowWaitcntPatch(row, enabled);
        RefreshWaitcntPatchUi();
    }

    private void RefreshWaitcntPatchUi()
    {
        if (refreshingWaitcntPatch || dataGridViewRoms is null || checkBoxWaitcntPatch is null ||
            dataGridViewRoms.Columns["WaitcntPatch"] is not { } column) return;
        refreshingWaitcntPatch = true;
        try
        {
            bool visible = IsWaitcntGbaWorkspace();
            column.Visible = visible;
            checkBoxWaitcntPatch.Visible = visible;
            checkBoxWaitcntPatch.Enabled = visible && !isBusy && !isAnalyzingQueueImports;
            menuReplaceSelectedRom.Visible = menuItemReplaceRom.Visible = true;
            var choices = new List<bool>();
            foreach (DataGridViewRow row in dataGridViewRoms.Rows)
            {
                bool? enabled = GetRowWaitcntPatch(row);
                DataGridViewCell cell = row.Cells[column.Index];
                if (enabled is bool value)
                {
                    if (cell is not DataGridViewCheckBoxCell)
                        row.Cells[column.Index] = cell = new DataGridViewCheckBoxCell();
                    cell.Value = value;
                    cell.ToolTipText = T("ui", "waitcnt_row_hint", "Apply WAITCNT patch to this pending game when burned.");
                    if (IsGbaSingleCardMode() || IsChisFlash10Mode())
                        row.Cells["PatchStatus"].Value = value ? T("ui", "waitcnt_patch", "WAITCNT Patch") :
                            GbaCardText("original_rom", "原文件，不打补丁", "Original ROM; no patches");
                    else if (row.Tag is WorkspaceListRow workspaceRow)
                        row.Cells["PatchStatus"].Value = FormatWorkspacePatchOrMbcDisplay(workspaceRow);
                    choices.Add(value);
                }
                else
                {
                    if (cell is not DataGridViewTextBoxCell)
                        row.Cells[column.Index] = cell = new DataGridViewTextBoxCell();
                    cell.Value = "—";
                    cell.ToolTipText = T("ui", "waitcnt_pending_only", "Only pending games can be selected. On-card games are unchanged.");
                }
                cell.ReadOnly = true;
                cell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }
            checkBoxWaitcntPatch.CheckState = choices.Count == 0
                ? defaultWaitcntPatchEnabled ? CheckState.Checked : CheckState.Unchecked
                : choices.All(value => value) ? CheckState.Checked
                : choices.All(value => !value) ? CheckState.Unchecked : CheckState.Indeterminate;
        }
        finally { refreshingWaitcntPatch = false; }
    }

    private byte[] PrepareGbaRomWaitcnt(byte[] source, bool enabled)
    {
        byte[] result = source.ToArray();
        if (enabled) WaitcntPatchService.Apply(result);
        return result;
    }

    private string PrepareGbaConversionWaitcnt(PendingRomImportItem item, string staging, int index)
    {
        if (!item.ApplyWaitcntPatch) return item.FilePath;
        string path = Path.Combine(staging, $"game-{index:D3}.gba");
        File.WriteAllBytes(path, PrepareGbaRomWaitcnt(File.ReadAllBytes(item.FilePath), true));
        string save = Path.ChangeExtension(item.FilePath, ".sav");
        if (File.Exists(save)) File.Copy(save, Path.ChangeExtension(path, ".sav"));
        return path;
    }
}
