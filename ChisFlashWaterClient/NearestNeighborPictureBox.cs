using System.Drawing.Drawing2D;

namespace ChisFlashWaterClient;

public sealed class NearestNeighborPictureBox : PictureBox
{
    protected override void OnPaint(PaintEventArgs pe)
    {
        if (Image is null || SizeMode != PictureBoxSizeMode.Zoom)
        {
            base.OnPaint(pe);
            return;
        }

        Rectangle destination = CalculateZoomRectangle(ClientRectangle, Image.Size);
        pe.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        pe.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        pe.Graphics.SmoothingMode = SmoothingMode.None;
        pe.Graphics.CompositingQuality = CompositingQuality.HighSpeed;
        pe.Graphics.DrawImage(
            Image,
            destination,
            0,
            0,
            Image.Width,
            Image.Height,
            GraphicsUnit.Pixel);
    }

    private static Rectangle CalculateZoomRectangle(Rectangle bounds, Size imageSize)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || imageSize.Width <= 0 || imageSize.Height <= 0)
        {
            return Rectangle.Empty;
        }

        double scale = Math.Min(
            bounds.Width / (double)imageSize.Width,
            bounds.Height / (double)imageSize.Height);
        int width = Math.Max(1, (int)Math.Round(imageSize.Width * scale));
        int height = Math.Max(1, (int)Math.Round(imageSize.Height * scale));
        return new Rectangle(
            bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2,
            width,
            height);
    }
}
