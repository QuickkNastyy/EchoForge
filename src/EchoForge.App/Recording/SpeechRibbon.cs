using System.Globalization;
using System.Windows;
using System.Windows.Media;

// The application enables WinForms for the tray icon, which brings System.Drawing into scope and
// collides with the WPF drawing types. Pin every ambiguous name to its WPF meaning.
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace EchoForge.App.Recording;

/// <summary>
/// The signature two-lane speech-density ribbon, drawn directly rather than built from controls.
///
/// <para>
/// You is the amber top lane, Remote the teal bottom lane, over a time ruler that shows elapsed
/// meeting time — because this product is about a monotonic clock, so it shows the clock. Bar height
/// is how loud the track was in that slice of time; silence is flat; a track that lost its device
/// flatlines in red rather than pretending audio continued. History stays on screen and accumulates
/// toward the playhead instead of scrolling away.
/// </para>
///
/// <para>
/// It draws in a single <see cref="OnRender"/> pass from a bounded bucket snapshot — no Rectangle or
/// Border per sample — so it stays responsive over a multi-hour recording. It is presentation only:
/// it reads the canonical activity history and never writes anything.
/// </para>
/// </summary>
public sealed class SpeechRibbon : FrameworkElement
{
    // The palette is fixed by the design: amber is always You, teal always Remote, red only capture.
    //
    // Taken from the application's shared brushes rather than frozen here, so the ribbon follows a
    // theme switch. A private frozen copy would hold the colour it was born with and leave the one
    // signature element on the screen painted for the wrong palette.
    private static Brush Background => Theme.Brush("Sunken");

    private static Brush YouBrush => Theme.Brush("You");

    private static Brush RemoteBrush => Theme.Brush("Remote");

    private static Brush DeadBrush => Theme.Brush("Rec");

    private static Brush InkBrush => Theme.Brush("Ink");

    private static Brush FaintBrush => Theme.Brush("InkFaint");

    private static Pen LinePen => Theme.Pen("Line", 1);

    private static Pen PlayheadPen => Theme.Pen("Ink", 1);

    private static readonly Typeface Mono = new("Cascadia Mono, Consolas");

    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(SpeechActivityHistory), typeof(SpeechRibbon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bumped by the view model each refresh tick to ask for a redraw of the mutated history.</summary>
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(SpeechRibbon),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the playhead sits, 0 (start) to 1 (now). Defaults to the live edge.</summary>
    public static readonly DependencyProperty PlayheadFractionProperty = DependencyProperty.Register(
        nameof(PlayheadFraction), typeof(double), typeof(SpeechRibbon),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public SpeechRibbon() => ClipToBounds = true;

    public SpeechActivityHistory? History
    {
        get => (SpeechActivityHistory?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public double PlayheadFraction
    {
        get => (double)GetValue(PlayheadFractionProperty);
        set => SetValue(PlayheadFractionProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawingContext dc = drawingContext;
        double w = ActualWidth;
        double h = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        if (w < 8 || h < 20)
        {
            return;
        }

        const double rulerHeight = 15;
        const double laneGap = 5;
        double lanesTop = rulerHeight + 3;
        double laneHeight = (h - lanesTop - laneGap) / 2;
        if (laneHeight < 3)
        {
            return;
        }

        double youTop = lanesTop;
        double remoteTop = lanesTop + laneHeight + laneGap;

        IReadOnlyList<RibbonBucket> buckets = History?.Snapshot() ?? [];
        double totalSeconds = History?.TotalSeconds ?? 0;

        DrawRuler(dc, w, rulerHeight, totalSeconds);

        int n = buckets.Count;
        if (n > 0)
        {
            double barWidth = w / n;
            double gap = Math.Min(1.0, barWidth * 0.15);
            double fill = Math.Max(0.5, barWidth - gap);

            for (int i = 0; i < n; i++)
            {
                RibbonBucket b = buckets[i];
                double x = i * barWidth;
                DrawLane(dc, x, fill, youTop, laneHeight, b.You, b.YouActive, YouBrush);
                DrawLane(dc, x, fill, remoteTop, laneHeight, b.Remote, b.RemoteActive, RemoteBrush);
            }
        }

        // The dividing baseline between the lanes.
        double mid = youTop + laneHeight + (laneGap / 2);
        dc.DrawLine(LinePen, new Point(0, mid), new Point(w, mid));

        double playheadX = Math.Clamp(PlayheadFraction, 0, 1) * w;
        dc.DrawLine(PlayheadPen, new Point(playheadX, lanesTop - 2), new Point(playheadX, h));
        dc.DrawEllipse(InkBrush, null, new Point(playheadX, lanesTop - 2), 3, 3);
    }

    private static void DrawLane(DrawingContext dc, double x, double width, double laneTop, double laneHeight, float level, bool active, Brush brush)
    {
        double baseline = laneTop + laneHeight;

        if (!active)
        {
            // A lost track flatlines in red at the lane's baseline, and stays that way.
            dc.DrawRectangle(DeadBrush, null, new Rect(x, baseline - 1, width, 1.4));
            return;
        }

        double barHeight = Math.Max(1, level * laneHeight);
        dc.DrawRectangle(brush, null, new Rect(x, baseline - barHeight, width, barHeight));
    }

    private void DrawRuler(DrawingContext dc, double w, double rulerHeight, double totalSeconds)
    {
        double baselineY = rulerHeight;
        dc.DrawLine(LinePen, new Point(0, baselineY), new Point(w, baselineY));

        if (totalSeconds < 1)
        {
            return;
        }

        double step = NiceStep(totalSeconds, w);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (double t = 0; t <= totalSeconds + 0.001; t += step)
        {
            double x = (t / totalSeconds) * w;
            dc.DrawLine(LinePen, new Point(x, baselineY - 4), new Point(x, baselineY));

            FormattedText label = new(
                Format(t),
                CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                Mono,
                9.5,
                FaintBrush,
                pixelsPerDip);

            double lx = Math.Min(x + 2, w - label.Width);
            dc.DrawText(label, new Point(Math.Max(0, lx), 0));
        }
    }

    /// <summary>A round tick interval giving roughly six to ten labels across the current width.</summary>
    private static double NiceStep(double totalSeconds, double width)
    {
        double target = totalSeconds / Math.Max(4, width / 90);
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
        foreach (double s in steps)
        {
            if (s >= target)
            {
                return s;
            }
        }

        return steps[^1];
    }

    private static string Format(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes:00}:{span.Seconds:00}");
    }

}
