using System.Drawing;
using System.Windows.Forms;

namespace ChisFlashWaterClient;

internal sealed class SingleLineLabel : Label
{
    private TextFormatFlags TextFlags
    {
        get
        {
            var flags = TextFormatFlags.SingleLine;
            flags |= TextAlign switch
            {
                ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter,
                ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
                _ => TextFormatFlags.Left,
            };
            flags |= TextAlign switch
            {
                ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight => TextFormatFlags.VerticalCenter,
                ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => TextFormatFlags.Bottom,
                _ => TextFormatFlags.Top,
            };
            if (AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;
            if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
            return flags;
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size textSize = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(
            Math.Max(MinimumSize.Width, textSize.Width + Padding.Horizontal),
            Math.Max(MinimumSize.Height, textSize.Height + Padding.Vertical));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle bounds = new(Padding.Left, Padding.Top,
            Math.Max(0, ClientSize.Width - Padding.Horizontal),
            Math.Max(0, ClientSize.Height - Padding.Vertical));
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds,
            Enabled ? ForeColor : SystemColors.GrayText, TextFlags);
    }
}
