using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Transcripts;

namespace EchoForge.App;

public sealed record TranscriptComparisonDisplayRow(
    string Speaker,
    string Timestamp,
    string LeftText,
    string RightText,
    string DifferenceLabel,
    bool IsMissing);

public sealed class TranscriptComparisonWindowViewModel
{
    public TranscriptComparisonWindowViewModel(TranscriptComparisonResult comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        LeftHeading = Heading(comparison.LeftRevision, comparison.LeftModel);
        RightHeading = Heading(comparison.RightRevision, comparison.RightModel);
        LeftDetails = Details(comparison.LeftModel, comparison.LeftRun);
        RightDetails = Details(comparison.RightModel, comparison.RightRun);
        LeftMetrics = Metrics(comparison.LeftMetrics);
        RightMetrics = Metrics(comparison.RightMetrics);
        Rows =
        [
            .. comparison.Rows.Select(row => new TranscriptComparisonDisplayRow(
                row.SourceTrack == TranscriptSpeakers.MicrophoneTrack ? "You" : "Remote",
                Display(row.StartSeconds),
                string.IsNullOrWhiteSpace(row.LeftText) ? "— no transcript in this region —" : row.LeftText,
                string.IsNullOrWhiteSpace(row.RightText) ? "— no transcript in this region —" : row.RightText,
                Difference(row.Difference, row.WordAgreement),
                row.IsMissingRegion))
        ];
    }

    public string LeftHeading { get; }
    public string RightHeading { get; }
    public string LeftDetails { get; }
    public string RightDetails { get; }
    public string LeftMetrics { get; }
    public string RightMetrics { get; }
    public ObservableCollection<TranscriptComparisonDisplayRow> Rows { get; }

    private static string Heading(int revision, TranscriptModel model) =>
        $"Revision {revision}: {model.ModelId}";

    private static string Details(TranscriptModel model, TranscriptionRunMetadata? run)
    {
        string runtime = model.BackendRuntimeVersion ?? model.Runtime;
        if (run is null)
        {
            return $"{model.Backend} · actual {model.ComputeType} · {runtime}";
        }

        string vram = run.PeakVramBytes is { } bytes && bytes > 0
            ? string.Create(CultureInfo.InvariantCulture, $" · peak VRAM {bytes / (1024.0 * 1024 * 1024):F1} GB")
            : string.Empty;
        string elapsed = run.ProcessingSeconds is { } seconds
            ? string.Create(CultureInfo.InvariantCulture, $" · {seconds:F1}s processing")
            : string.Empty;
        string warnings = run.Warnings.Count == 0
            ? string.Empty
            : " · warnings: " + string.Join("; ", run.Warnings);
        return $"{model.Backend} · requested {run.RequestedComputeProfile} / actual {run.ActualComputeProfile} · "
               + $"{run.VadMode} VAD · {run.WindowStrategy}{elapsed}{vram}{warnings} · {runtime}";
    }

    private static string Metrics(TranscriptComparisonMetrics value) => string.Create(
        CultureInfo.InvariantCulture,
        $"{value.Words:N0} words · {value.Segments:N0} segments · {value.RepresentedSpeechSeconds:F1}s represented · " +
        $"{value.TimelineCoverage:P1} two-track coverage · {value.RegionsMissingFromThisRevision} missing regions");

    private static string Difference(TranscriptDifferenceKind difference, double agreement) => difference switch
    {
        TranscriptDifferenceKind.Match => "match",
        TranscriptDifferenceKind.PunctuationOnly => "punctuation only",
        TranscriptDifferenceKind.MissingFromLeft => "missing on left",
        TranscriptDifferenceKind.MissingFromRight => "missing on right",
        _ => string.Create(CultureInfo.InvariantCulture, $"word agreement {agreement:P0}"),
    };

    private static string Display(double seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}");
    }
}

public partial class TranscriptComparisonWindow : Window
{
    public TranscriptComparisonWindow(TranscriptComparisonResult comparison)
    {
        InitializeComponent();
        DataContext = new TranscriptComparisonWindowViewModel(comparison);
    }
}
