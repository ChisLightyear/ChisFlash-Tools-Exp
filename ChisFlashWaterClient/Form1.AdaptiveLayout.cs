using System.Drawing;
using System.Windows.Forms;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private bool updatingAdaptiveLayout;
    private TableLayoutPanel? gbaMenuOptions;
    private TableLayoutPanel? gbcMenuOptions;
    private readonly Dictionary<DataGridViewColumn, int> originalColumnMinimums = [];

    private static TableLayoutPanel CreateToolbarPair(Label caption, ComboBox selector)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Anchor = AnchorStyles.None,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        caption.Anchor = AnchorStyles.Left;
        selector.Anchor = AnchorStyles.Left;
        caption.Margin = new Padding(6, 4, 4, 4);
        selector.Margin = new Padding(4);
        panel.Controls.Add(caption, 0, 0);
        panel.Controls.Add(selector, 1, 0);
        return panel;
    }

    private void UpdateAdaptiveLayout()
    {
        if (Disposing || IsDisposed || updatingAdaptiveLayout || rootLayout is null || workspaceLayout is null) return;
        updatingAdaptiveLayout = true;
        try
        {
            foreach (var entry in new[]
            {
                (Selector: comboBoxLanguage, Width: 128),
                (Selector: comboBoxBurnProfileMode, Width: 220),
                (Selector: comboBoxGbaMenu, Width: 132),
                (Selector: comboBoxGbcMenuLanguage, Width: 104),
            })
            {
                if (entry.Selector is null || entry.Selector.IsDisposed) continue;
                entry.Selector.AutoSize = true;
                int textWidth = entry.Selector.Items.Cast<object>()
                    .Select(item => TextRenderer.MeasureText(entry.Selector.GetItemText(item), entry.Selector.Font, Size.Empty, TextFormatFlags.SingleLine).Width)
                    .DefaultIfEmpty(0).Max();
                int selectedWidth = TextRenderer.MeasureText(entry.Selector.Text, entry.Selector.Font, Size.Empty, TextFormatFlags.SingleLine).Width;
                int selectorWidth = Math.Max(LogicalToDeviceUnits(entry.Width),
                    selectedWidth + LogicalToDeviceUnits(24) + SystemInformation.VerticalScrollBarWidth);
                entry.Selector.MinimumSize = new Size(selectorWidth, 0);
                entry.Selector.Width = selectorWidth;
                entry.Selector.DropDownWidth = Math.Max(entry.Selector.Width, textWidth + LogicalToDeviceUnits(24));
                entry.Selector.Parent?.PerformLayout(entry.Selector, nameof(Control.PreferredSize));
                entry.Selector.Parent?.Parent?.PerformLayout();
            }
            comboBoxPorts.Parent?.PerformLayout(comboBoxPorts, nameof(Control.PreferredSize));
            foreach (DataGridView grid in EnumerateGrids(rootLayout))
            {
                grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
                grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    originalColumnMinimums.TryAdd(column, column.MinimumWidth);
                    int width = column.GetPreferredWidth(DataGridViewAutoSizeColumnMode.ColumnHeader, true);
                    if (column.Name is "Order" or "State" or "Source" or "Kind")
                        width = Math.Max(width, column.GetPreferredWidth(DataGridViewAutoSizeColumnMode.AllCells, true));
                    if (column.Name == "Title" && ReferenceEquals(grid, dataGridViewRoms) && SupportsGbaCardMode())
                    {
                        // 操作提示完整显示；游戏的长文件名仍按原有方式省略。
                        string[] hints = [GbaCardText("select_menu_hint", "双击选择菜单", "Double-click to select a menu"),
                            GbaCardText("add_game_hint", "点击添加游戏", "Click to add a game"),
                            GbaCardText("menu_required", "添加菜单后开放", "Available after adding a menu")];
                        width = Math.Max(width, hints.Max(hint => TextRenderer.MeasureText(hint, grid.Font, Size.Empty, TextFormatFlags.SingleLine).Width)
                            + grid.DefaultCellStyle.Padding.Horizontal + LogicalToDeviceUnits(8));
                    }
                    column.MinimumWidth = Math.Max(originalColumnMinimums[column], width);
                }
            }
            UpdateMenuBackgroundPreviewLayout();
        }
        finally { updatingAdaptiveLayout = false; }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateAdaptiveLayout();
        if (IsHandleCreated && !Disposing && !IsDisposed) BeginInvoke(new Action(UpdateAdaptiveLayout));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateAdaptiveLayout();
        if (IsHandleCreated && !Disposing && !IsDisposed) BeginInvoke(new Action(UpdateAdaptiveLayout));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateAdaptiveLayout();
    }

    private static IEnumerable<DataGridView> EnumerateGrids(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is DataGridView grid) yield return grid;
            else foreach (DataGridView nested in EnumerateGrids(child)) yield return nested;
        }
    }
}
