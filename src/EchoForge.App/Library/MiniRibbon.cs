using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

using EchoForge.App;

namespace EchoForge.App.Library;

/// <summary>
/// Draws a <see cref="ConversationShape"/> as a two-lane You/Remote ribbon, in one render pass.
///
/// <para>
/// The same control serves the tiny sparkline on a library row and the full timeline in the recording
/// detail — the difference is just size, and whether a ruler and a playhead are shown. It draws a
/// bounded number of bars from a bounded shape, so a row list of any length and a meeting of any
/// length both stay cheap; there is no control per segment and nothing per audio sample.
/// </para>
/// </summary>
public sealed class MiniRibbon : FrameworkElement
{
    // The shared application brushes, so the row sparklines and the detail timeline follow a
    // theme switch instead of holding the palette they were first drawn in.
    private static Brush YouBrush => Theme.Brush("You");

    private static Brush RemoteBrush => Theme.Brush("Remote");

    private static Brush FaintBrush => Theme.Brush("InkFaint");

    private static Pen LinePen => Theme.Pen("Line", 1);

    private static Pen PlayheadPen => Theme.Pen("Ink", 1);

    private static Brush InkBrush => Theme.Brush("Ink");

    private static readonly Typeface Mono = new("Cascadia Mono, Consolas");

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(ConversationShape), typeof(MiniRibbon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Playhead position 0..1, or negative to hide it (the library-row use).</summary>
    public static readonly DependencyProperty PlayheadFractionProperty = DependencyProperty.Register(
        nameof(PlayheadFraction), typeof(double), typeof(MiniRibbon),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Where the selected claim's evidence sits, 0..1, or negative for none.
    ///
    /// <para>
    /// Drawn in the focus colour rather than in a track colour, because it marks <em>what you asked
    /// about</em> and not who was speaking — the two would otherwise compete for the same meaning.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty EvidenceFractionProperty = DependencyProperty.Register(
        nameof(EvidenceFraction), typeof(double), typeof(MiniRibbon),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowRulerProperty = DependencyProperty.Register(
        nameof(ShowRuler), typeof(bool), typeof(MiniRibbon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DurationSecondsProperty = DependencyProperty.Register(
        nameof(DurationSeconds), typeof(double), typeof(MiniRibbon),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The recording this ribbon draws. When set together with an inherited <see cref="ProviderProperty"/>,
    /// the ribbon loads its own shape asynchronously — which is how a virtualized library row gets its
    /// sparkline without the list reading every transcript up front.
    /// </summary>
    public static readonly DependencyProperty SessionIdProperty = DependencyProperty.Register(
        nameof(SessionId), typeof(string), typeof(MiniRibbon),
        new FrameworkPropertyMetadata(null, OnKeyChanged));

    /// <summary>Inheritable: set once on a parent, and every row's ribbon can load its shape.</summary>
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.RegisterAttached(
        "Provider", typeof(Func<string, Task<ConversationShape>>), typeof(MiniRibbon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnKeyChanged));

    public static void SetProvider(DependencyObject element, Func<string, Task<ConversationShape>>? value) =>
        element.SetValue(ProviderProperty, value);

    public static Func<string, Task<ConversationShape>>? GetProvider(DependencyObject element) =>
        (Func<string, Task<ConversationShape>>?)element.GetValue(ProviderProperty);

    public MiniRibbon() => ClipToBounds = true;

    public ConversationShape? Shape
    {
        get => (ConversationShape?)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    public string? SessionId
    {
        get => (string?)GetValue(SessionIdProperty);
        set => SetValue(SessionIdProperty, value);
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MiniRibbon ribbon)
        {
            ribbon.LoadShape();
        }
    }

    private async void LoadShape()
    {
        Func<string, Task<ConversationShape>>? provider = GetProvider(this);
        string? id = SessionId;
        if (provider is null || string.IsNullOrEmpty(id))
        {
            return;
        }

        try
        {
            ConversationShape shape = await provider(id).ConfigureAwait(true);
            // The recycled row may have moved to a different meeting while we loaded; only apply if
            // this ribbon still wants this session.
            if (string.Equals(SessionId, id, StringComparison.Ordinal))
            {
                Shape = shape;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // A row without a readable transcript simply stays quiet.
        }
    }

    public double PlayheadFraction
    {
        get => (double)GetValue(PlayheadFractionProperty);
        set => SetValue(PlayheadFractionProperty, value);
    }

    public double EvidenceFraction
    {
        get => (double)GetValue(EvidenceFractionProperty);
        set => SetValue(EvidenceFractionProperty, value);
    }

    public bool ShowRuler
    {
        get => (bool)GetValue(ShowRulerProperty);
        set => SetValue(ShowRulerProperty, value);
    }

    public double DurationSeconds
    {
        get => (double)GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawingContext dc = drawingContext;
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 2 || h < 2)
        {
            return;
        }

        double top = 0;
        if (ShowRuler && h > 22)
        {
            top = 15;
            DrawRuler(dc, w, top);
        }

        double laneGap = h > 40 ? 4 : 1;
        double laneHeight = (h - top - laneGap) / 2;
        if (laneHeight < 1)
        {
            return;
        }

        ConversationShape shape = Shape ?? ConversationShape.Empty;

        if (!shape.HasData)
        {
            // Nothing to draw is not the same as nothing to show. A recording with no transcript
            // yet gets two quiet baselines, so the well reads as empty rather than as a hole where
            // a control failed to render.
            Pen quiet = Theme.Pen("Line", 1);
            dc.DrawLine(quiet, new Point(0, top + laneHeight), new Point(w, top + laneHeight));
            dc.DrawLine(quiet, new Point(0, top + (laneHeight * 2) + laneGap), new Point(w, top + (laneHeight * 2) + laneGap));
            return;
        }

        DrawLane(dc, shape.You, top, laneHeight, YouBrush, w);
        DrawLane(dc, shape.Remote, top + laneHeight + laneGap, laneHeight, RemoteBrush, w);

        // The evidence marker sits under the playhead, so a claim being followed while the audio
        // plays past it stays legible as two separate facts.
        if (EvidenceFraction >= 0)
        {
            double x = Math.Clamp(EvidenceFraction, 0, 1) * w;
            Brush focus = Theme.Brush("Focus");
            dc.DrawRectangle(focus, null, new Rect(x - 1, top, 2, h - top));

            StreamGeometry flag = new();
            using (StreamGeometryContext leg = flag.Open())
            {
                leg.BeginFigure(new Point(x - 4, top), isFilled: true, isClosed: true);
                leg.LineTo(new Point(x + 4, top), isStroked: false, isSmoothJoin: false);
                leg.LineTo(new Point(x, top + 5), isStroked: false, isSmoothJoin: false);
            }

            flag.Freeze();
            dc.DrawGeometry(focus, null, flag);
        }

        if (PlayheadFraction >= 0)
        {
            double x = Math.Clamp(PlayheadFraction, 0, 1) * w;
            dc.DrawLine(PlayheadPen, new Point(x, top), new Point(x, h));
            dc.DrawEllipse(InkBrush, null, new Point(x, top), 2.5, 2.5);
        }
    }

    private static void DrawLane(DrawingContext dc, float[] lane, double laneTop, double laneHeight, Brush brush, double w)
    {
        int n = lane.Length;
        if (n == 0)
        {
            return;
        }

        double barWidth = w / n;
        double gap = Math.Min(1.0, barWidth * 0.12);
        double fill = Math.Max(0.5, barWidth - gap);
        double baseline = laneTop + laneHeight;

        for (int i = 0; i < n; i++)
        {
            double level = lane[i];
            if (level <= 0)
            {
                continue;
            }

            double barHeight = Math.Max(1, level * laneHeight);
            dc.DrawRectangle(brush, null, new Rect(i * barWidth, baseline - barHeight, fill, barHeight));
        }
    }

    private void DrawRuler(DrawingContext dc, double w, double baselineY)
    {
        dc.DrawLine(LinePen, new Point(0, baselineY), new Point(w, baselineY));

        double total = DurationSeconds;
        if (total < 1)
        {
            return;
        }

        double step = NiceStep(total, w);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (double t = 0; t <= total + 0.001; t += step)
        {
            double x = (t / total) * w;
            dc.DrawLine(LinePen, new Point(x, baselineY - 4), new Point(x, baselineY));

            FormattedText label = new(
                Format(t), CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                Mono, 9.5, FaintBrush, dpi);
            dc.DrawText(label, new Point(Math.Max(0, Math.Min(x + 2, w - label.Width)), 0));
        }
    }

    private static double NiceStep(double total, double width)
    {
        double target = total / Math.Max(4, width / 90);
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
