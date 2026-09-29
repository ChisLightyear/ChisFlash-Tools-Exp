using System.Drawing;
using System.Windows.Forms;

namespace GbaMultiMenuBuilder.Fc;

// Keep the padded text for accessibility, and align the separator by pixels
// because the normal UI font is proportional (GBA, GBC and NES differ in width).
public sealed class ModeAlignedComboBox : ComboBox
{
    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding |
        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

    public ModeAlignedComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = Math.Max(18, Font.Height + 4);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = Math.Max(18, Font.Height + 4);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        e.DrawBackground();
        object? item = e.Index >= 0 && e.Index < Items.Count ? Items[e.Index] : SelectedItem;
        string caption = GetItemText(item) ?? string.Empty;
        var font = e.Font ?? Font;
        Color color = Enabled ? e.ForeColor : SystemColors.GrayText;
        var bounds = new Rectangle(e.Bounds.X + 2, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 4), e.Bounds.Height);
        int separator = caption.IndexOf('|');
        if (separator < 0)
        {
            TextRenderer.DrawText(e.Graphics, caption, font, bounds, color, TextFlags | TextFormatFlags.EndEllipsis);
        }
        else
        {
            int WidthOf(string value) => TextRenderer.MeasureText(e.Graphics, value, font,
                new Size(int.MaxValue, int.MaxValue), TextFlags).Width;
            int prefixWidth = Math.Max(Math.Max(WidthOf("GBA |"), WidthOf("GBC |")), WidthOf("NES |")) - WidthOf("|");
            var left = new Rectangle(bounds.X, bounds.Y, Math.Min(prefixWidth, bounds.Width), bounds.Height);
            var right = new Rectangle(bounds.X + prefixWidth, bounds.Y, Math.Max(0, bounds.Width - prefixWidth), bounds.Height);
            TextRenderer.DrawText(e.Graphics, caption[..separator].TrimEnd(), font, left, color, TextFlags);
            TextRenderer.DrawText(e.Graphics, caption[separator..], font, right, color, TextFlags | TextFormatFlags.EndEllipsis);
        }
        e.DrawFocusRectangle();
    }
}
