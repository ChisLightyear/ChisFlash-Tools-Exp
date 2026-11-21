namespace ChisFlashWaterClient;

internal sealed class BurnSpeedLandscape : Control
{
    private const int HistoryLength = 64;
    private readonly Queue<double> samples = new(HistoryLength);
    private bool waitingForErase;
    private double peak = 1;

    public BurnSpeedLandscape()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "KiB/s";
    }

    public void SetEraseWaiting(bool waiting)
    {
        if (waiting == waitingForErase) return;
        waitingForErase = waiting;
        AccessibleDescription = waiting ? "E … KiB/s" : Text;
    }

    public void Append(double bytesPerSecond, bool firstSample, bool erase)
    {
        if (firstSample) { samples.Clear(); peak = 1; }
        if (samples.Count == HistoryLength) samples.Dequeue();
        double speed = bytesPerSecond / 1024;
        samples.Enqueue(speed);
        peak = Math.Max(peak, speed);
        Text = $"{(erase ? "E" : "W")} {speed:N1} KiB/s";
        AccessibleDescription = Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (samples.Count == 0 || ClientSize.Width < 4 || ClientSize.Height < 4) return;

        int right = ClientSize.Width - 2;
        int bottom = ClientSize.Height - 2;
        float spacing = (right - 1f) / (HistoryLength - 1);
        using var pen = new Pen(ForeColor, Math.Max(1f, DeviceDpi / 96f));
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int column = HistoryLength - samples.Count;
        PointF? previous = null;
        foreach (double speed in samples)
        {
            var point = new PointF(1 + column * spacing, bottom - (float)(speed / peak) * (bottom - 1));
            if (previous is { } start)
                e.Graphics.DrawLine(pen, start, point);
            else
                e.Graphics.DrawLine(pen, point.X - 1, point.Y, point.X, point.Y);
            previous = point;
            column++;
        }
    }
}
