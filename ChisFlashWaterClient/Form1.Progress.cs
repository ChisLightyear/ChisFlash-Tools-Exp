using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private const int LegacySpeedWindowMs = 1500;
    private const int UiProgressUpdateIntervalMs = 100;

    private ProgressBar progressBarTotal = null!;
    private StatusStrip statusStripProgress = null!;
    private ToolStripStatusLabel labelProgress = null!;
    private ToolStripStatusLabel labelProgressSpring = null!;
    private ToolStripStatusLabel labelSpeed = null!;
    private System.Windows.Forms.Timer? transferSpeedTimer;

    private Stopwatch? progressStopwatch;
    private long progressTotalBytes;
    private long progressCompletedBytes;
    private int progressTotalUnits;
    private int progressCompletedUnits;
    private string progressOperationTitle = string.Empty;
    private string progressDetailText = string.Empty;
    private long transferredBytesWindow;
    private long transferredBytesTotal;
    private long pendingProgressBytesDelta;
    private int pendingProgressUnitsDelta;
    private string? pendingProgressDetailText;
    private long lastMeasuredProgressUiUpdateTick;
    private int measuredProgressFlushScheduled;

    private void ResetMeasuredProgress(bool clearSpeedText)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<bool>(ResetMeasuredProgress), clearSpeedText);
            return;
        }

        progressStopwatch = null;
        progressTotalBytes = 0;
        progressCompletedBytes = 0;
        progressTotalUnits = 0;
        progressCompletedUnits = 0;
        progressOperationTitle = string.Empty;
        progressDetailText = string.Empty;
        Interlocked.Exchange(ref transferredBytesWindow, 0);
        Interlocked.Exchange(ref transferredBytesTotal, 0);
        Interlocked.Exchange(ref pendingProgressBytesDelta, 0);
        Interlocked.Exchange(ref pendingProgressUnitsDelta, 0);
        pendingProgressDetailText = null;
        Interlocked.Exchange(ref measuredProgressFlushScheduled, 0);
        lastMeasuredProgressUiUpdateTick = 0;

        progressBarTotal.Style = ProgressBarStyle.Continuous;
        progressBarTotal.Minimum = 0;
        progressBarTotal.Maximum = 1;
        progressBarTotal.Value = 0;
        labelProgress.Text = string.Empty;
        if (clearSpeedText)
        {
            labelSpeed.Text = string.Empty;
        }
    }

    private void BeginMeasuredProgress(string operationTitle, long totalBytes, int totalUnits)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string, long, int>(BeginMeasuredProgress), operationTitle, totalBytes, totalUnits);
            return;
        }

        progressOperationTitle = operationTitle?.Trim() ?? string.Empty;
        progressTotalBytes = Math.Max(0, totalBytes);
        progressCompletedBytes = 0;
        progressTotalUnits = Math.Max(1, totalUnits);
        progressCompletedUnits = 0;
        progressStopwatch = Stopwatch.StartNew();
        progressDetailText = string.Empty;
        Interlocked.Exchange(ref transferredBytesWindow, 0);
        Interlocked.Exchange(ref transferredBytesTotal, 0);
        Interlocked.Exchange(ref pendingProgressBytesDelta, 0);
        Interlocked.Exchange(ref pendingProgressUnitsDelta, 0);
        pendingProgressDetailText = null;
        Interlocked.Exchange(ref measuredProgressFlushScheduled, 0);
        lastMeasuredProgressUiUpdateTick = Environment.TickCount64;

        progressBarTotal.Style = ProgressBarStyle.Continuous;
        progressBarTotal.Minimum = 0;
        progressBarTotal.Maximum = GetProgressBarMaximum();
        progressBarTotal.Value = 0;

        UpdateMeasuredProgressUi();
    }

    private void BeginIndeterminateProgress(string operationTitle, string detailText)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string, string>(BeginIndeterminateProgress), operationTitle, detailText);
            return;
        }

        progressOperationTitle = operationTitle?.Trim() ?? string.Empty;
        progressTotalBytes = 0;
        progressCompletedBytes = 0;
        progressTotalUnits = 0;
        progressCompletedUnits = 0;
        progressStopwatch = Stopwatch.StartNew();
        progressDetailText = detailText?.Trim() ?? string.Empty;
        Interlocked.Exchange(ref transferredBytesWindow, 0);
        Interlocked.Exchange(ref transferredBytesTotal, 0);
        Interlocked.Exchange(ref pendingProgressBytesDelta, 0);
        Interlocked.Exchange(ref pendingProgressUnitsDelta, 0);
        pendingProgressDetailText = null;
        Interlocked.Exchange(ref measuredProgressFlushScheduled, 0);
        lastMeasuredProgressUiUpdateTick = Environment.TickCount64;

        progressBarTotal.Style = ProgressBarStyle.Marquee;
        progressBarTotal.MarqueeAnimationSpeed = 25;
        progressBarTotal.Minimum = 0;
        progressBarTotal.Maximum = 100;
        progressBarTotal.Value = 0;
        labelProgress.Text = string.IsNullOrWhiteSpace(progressDetailText)
            ? progressOperationTitle
            : progressDetailText;
    }

    private void UpdateIndeterminateProgress(string detailText)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(UpdateIndeterminateProgress), detailText);
            return;
        }

        if (progressStopwatch is null || progressBarTotal.Style != ProgressBarStyle.Marquee)
        {
            return;
        }

        progressDetailText = detailText?.Trim() ?? string.Empty;
        labelProgress.Text = string.IsNullOrWhiteSpace(progressDetailText)
            ? progressOperationTitle
            : progressDetailText;
    }

    private void AdvanceMeasuredProgress(long completedBytesDelta, int completedUnitsDelta, string? detailText = null)
    {
        if (InvokeRequired)
        {
            QueueMeasuredProgressUpdate(completedBytesDelta, completedUnitsDelta, detailText);
            return;
        }

        if (progressStopwatch is null)
        {
            return;
        }

        progressCompletedBytes = Math.Min(progressTotalBytes, progressCompletedBytes + Math.Max(0, completedBytesDelta));
        progressCompletedUnits = Math.Min(progressTotalUnits, progressCompletedUnits + Math.Max(0, completedUnitsDelta));
        UpdateMeasuredProgressUi(detailText);
    }

    private void CompleteMeasuredProgress(string? summaryTitle = null, bool appendSummaryLog = true)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string?, bool>(CompleteMeasuredProgress), summaryTitle, appendSummaryLog);
            return;
        }

        if (progressStopwatch is null)
        {
            return;
        }

        FlushPendingMeasuredProgressUpdates();
        progressStopwatch.Stop();
        progressCompletedBytes = progressTotalBytes > 0
            ? progressTotalBytes
            : progressCompletedBytes;

        progressCompletedUnits = progressTotalUnits;
        UpdateMeasuredProgressUi();

        TimeSpan elapsed = progressStopwatch.Elapsed;
        long measuredBytes = Interlocked.Read(ref transferredBytesTotal);
        if (measuredBytes <= 0)
        {
            measuredBytes = progressCompletedBytes;
        }

        if (appendSummaryLog && measuredBytes > 0)
        {
            AppendLog(Tf(
                "log",
                "progress_speed_summary",
                "Transfer complete. Elapsed: {0:F3} s, average speed: {1:F3} KiB/s",
                elapsed.TotalSeconds,
                measuredBytes / Math.Max(0.001, elapsed.TotalSeconds) / 1024.0));
        }

        progressStopwatch = null;
    }

    private void AbortMeasuredProgress()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(AbortMeasuredProgress));
            return;
        }

        FlushPendingMeasuredProgressUpdates();
        ResetMeasuredProgress(clearSpeedText: true);
    }

    private void InitializeMeasuredProgress()
    {
        if (transferSpeedTimer is null)
        {
            transferSpeedTimer = new System.Windows.Forms.Timer(components)
            {
                Enabled = true,
                Interval = LegacySpeedWindowMs,
            };
            transferSpeedTimer.Tick += TransferSpeedTimer_Tick;
        }

        ResetMeasuredProgress(clearSpeedText: true);
    }

    private void UpdateMeasuredProgressUi(string? detailText = null)
    {
        if (!string.IsNullOrWhiteSpace(detailText))
        {
            progressDetailText = detailText;
        }

        if (progressTotalUnits <= 0)
        {
            labelProgress.Text = string.Empty;
            return;
        }

        int progressValue = Math.Clamp(progressCompletedUnits, 0, progressTotalUnits);
        int progressMaximum = GetProgressBarMaximum();
        if (progressBarTotal.Maximum != progressMaximum)
        {
            progressBarTotal.Maximum = progressMaximum;
        }

        if (progressTotalBytes > 0)
        {
            int max = GetProgressBarMaximum();
            int value = (int)Math.Clamp(progressCompletedBytes, progressBarTotal.Minimum, max);
            progressBarTotal.Value = value;
            labelProgress.Text = BuildProgressStatusText();
            return;
        }

        progressBarTotal.Value = Math.Clamp(progressValue, progressBarTotal.Minimum, progressBarTotal.Maximum);
        labelProgress.Text = BuildProgressStatusText();
    }

    private void ReportTransferredBytes(int byteCount)
    {
        if (byteCount <= 0)
        {
            return;
        }

        Interlocked.Add(ref transferredBytesWindow, byteCount);
        Interlocked.Add(ref transferredBytesTotal, byteCount);
    }

    private void QueueMeasuredProgressUpdate(long completedBytesDelta, int completedUnitsDelta, string? detailText)
    {
        if (completedBytesDelta > 0)
        {
            Interlocked.Add(ref pendingProgressBytesDelta, completedBytesDelta);
        }

        if (completedUnitsDelta > 0)
        {
            Interlocked.Add(ref pendingProgressUnitsDelta, completedUnitsDelta);
        }

        if (!string.IsNullOrWhiteSpace(detailText))
        {
            pendingProgressDetailText = detailText;
        }

        long nowTick = Environment.TickCount64;
        bool dueForImmediateFlush = nowTick - Interlocked.Read(ref lastMeasuredProgressUiUpdateTick) >= UiProgressUpdateIntervalMs;
        if (dueForImmediateFlush &&
            Interlocked.Exchange(ref measuredProgressFlushScheduled, 1) == 0)
        {
            BeginInvoke(new Action(FlushPendingMeasuredProgressUpdates));
        }
    }

    private void FlushPendingMeasuredProgressUpdates()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(FlushPendingMeasuredProgressUpdates));
            return;
        }

        long bytesDelta = Interlocked.Exchange(ref pendingProgressBytesDelta, 0);
        int unitsDelta = Interlocked.Exchange(ref pendingProgressUnitsDelta, 0);
        string? detailText = pendingProgressDetailText;
        pendingProgressDetailText = null;
        Interlocked.Exchange(ref measuredProgressFlushScheduled, 0);

        if (progressStopwatch is null)
        {
            return;
        }

        if (bytesDelta <= 0 && unitsDelta <= 0 && string.IsNullOrWhiteSpace(detailText))
        {
            return;
        }

        progressCompletedBytes = Math.Min(progressTotalBytes, progressCompletedBytes + Math.Max(0, bytesDelta));
        progressCompletedUnits = Math.Min(progressTotalUnits, progressCompletedUnits + Math.Max(0, unitsDelta));
        lastMeasuredProgressUiUpdateTick = Environment.TickCount64;
        UpdateMeasuredProgressUi(detailText);

        bool hasPendingMore = Interlocked.Read(ref pendingProgressBytesDelta) > 0 ||
            Volatile.Read(ref pendingProgressUnitsDelta) > 0 ||
            !string.IsNullOrWhiteSpace(pendingProgressDetailText);
        if (hasPendingMore &&
            Interlocked.Exchange(ref measuredProgressFlushScheduled, 1) == 0)
        {
            BeginInvoke(new Action(FlushPendingMeasuredProgressUpdates));
        }
    }

    private void TransferSpeedTimer_Tick(object? sender, EventArgs e)
    {
        long bytes = Interlocked.Exchange(ref transferredBytesWindow, 0);
        if (bytes <= 0)
        {
            labelSpeed.Text = string.Empty;
            return;
        }

        double bytesPerSecond = bytes / (double)LegacySpeedWindowMs * 1000.0;
        labelSpeed.Text = FormatLegacyTransferRate(bytesPerSecond);
    }

    private int GetProgressBarMaximum()
    {
        long total = progressTotalBytes > 0 ? progressTotalBytes : progressTotalUnits;
        return (int)Math.Clamp(total, 1, int.MaxValue);
    }

    private string BuildProgressStatusText()
    {
        string progressText = string.Format(
            CultureInfo.CurrentCulture,
            "{0}/{1}",
            progressCompletedUnits,
            progressTotalUnits);

        if (!string.IsNullOrWhiteSpace(progressDetailText))
        {
            return string.Format(CultureInfo.CurrentCulture, "{0} | {1}", progressDetailText, progressText);
        }

        if (!string.IsNullOrWhiteSpace(progressOperationTitle))
        {
            return string.Format(CultureInfo.CurrentCulture, "{0} | {1}", progressOperationTitle, progressText);
        }

        return progressText;
    }

    private static int GetProgressSectorSize(NorGeometryInfo geometry) =>
        geometry.SectorSizeBytes > 0
            ? geometry.SectorSizeBytes
            : WaterWlMappingCodec.SectorSize;

    private static int CountRewriteSectorProgressUnits(byte[] sectorData) =>
        IsAllBytes(sectorData, 0xFF) ? 1 : 2;

    private static long CountRewriteSectorProgressWorkBytes(byte[] sectorData, NorGeometryInfo geometry) =>
        (long)GetProgressSectorSize(geometry) * CountRewriteSectorProgressUnits(sectorData);

    private static int CountRewriteRangeProgressUnits(byte[] rangeData, NorGeometryInfo geometry)
    {
        int sectorSize = GetProgressSectorSize(geometry);
        if (rangeData.Length == 0 || rangeData.Length % sectorSize != 0)
        {
            return 0;
        }

        int totalUnits = 0;
        for (int offset = 0; offset < rangeData.Length; offset += sectorSize)
        {
            byte[] sectorData = rangeData.AsSpan(offset, sectorSize).ToArray();
            totalUnits += CountRewriteSectorProgressUnits(sectorData);
        }

        return totalUnits;
    }

    private static long CountRewriteRangeProgressWorkBytes(byte[] rangeData, NorGeometryInfo geometry)
    {
        int sectorSize = GetProgressSectorSize(geometry);
        if (rangeData.Length == 0 || rangeData.Length % sectorSize != 0)
        {
            return 0;
        }

        long totalBytes = 0;
        for (int offset = 0; offset < rangeData.Length; offset += sectorSize)
        {
            byte[] sectorData = rangeData.AsSpan(offset, sectorSize).ToArray();
            totalBytes += CountRewriteSectorProgressWorkBytes(sectorData, geometry);
        }

        return totalBytes;
    }

    private static int CountSegmentProgressUnits(WaterNorWriteSegment segment, NorGeometryInfo geometry)
    {
        int sectorSize = GetProgressSectorSize(geometry);
        if (segment.Data.Length == 0 || segment.Data.Length % sectorSize != 0)
        {
            return 0;
        }

        int sectorCount = segment.Data.Length / sectorSize;
        return segment.EraseOnly
            ? sectorCount
            : sectorCount * 2;
    }

    private static long CountSegmentProgressWorkBytes(WaterNorWriteSegment segment, NorGeometryInfo geometry) =>
        (long)GetProgressSectorSize(geometry) * CountSegmentProgressUnits(segment, geometry);

    private string BuildProgressDetailText(string label, string phaseKey, string phaseFallback)
    {
        string phaseText = T("value", phaseKey, phaseFallback);
        return string.Format(CultureInfo.CurrentCulture, "{0} | {1}", phaseText, label);
    }

    private static string FormatLegacyTransferRate(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return string.Empty;
        }

        string[] units = ["B", "KiB", "MiB", "GiB"];
        int unitIndex = Math.Clamp((int)Math.Floor(Math.Log(bytesPerSecond, 1024)), 0, units.Length - 1);
        double scaledValue = bytesPerSecond / Math.Pow(1024, unitIndex);

        return string.Format(CultureInfo.CurrentCulture, "{0:F3} {1}/s", scaledValue, units[unitIndex]);
    }
}
