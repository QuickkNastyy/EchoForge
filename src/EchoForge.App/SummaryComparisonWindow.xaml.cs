using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using EchoForge.Contracts.Summaries;
using EchoForge.Core.Summaries;

namespace EchoForge.App;

public sealed record SummaryComparisonDisplayRow(
    string Section,
    string LeftText,
    string RightText,
    string Agreement,
    bool IsMissing);

public sealed class SummaryComparisonWindowViewModel
{
    public SummaryComparisonWindowViewModel(SummaryComparisonResult comparison)
    {
        LeftHeading = $"Revision {comparison.LeftRevision}: {comparison.LeftModel.ModelId}";
        RightHeading = $"Revision {comparison.RightRevision}: {comparison.RightModel.ModelId}";
        LeftDetails = Details(comparison.LeftModel.Runtime, comparison.LeftModel.ContextTokens, comparison.LeftRun);
        RightDetails = Details(comparison.RightModel.Runtime, comparison.RightModel.ContextTokens, comparison.RightRun);
        Rows =
        [
            .. comparison.Rows.Select(row => new SummaryComparisonDisplayRow(
                row.Section,
                string.IsNullOrWhiteSpace(row.LeftText) ? "— absent —" : row.LeftText,
                string.IsNullOrWhiteSpace(row.RightText) ? "— absent —" : row.RightText,
                row.MissingFromLeft || row.MissingFromRight
                    ? "missing item"
                    : string.Create(CultureInfo.InvariantCulture, $"agreement {row.Agreement:P0}"),
                row.MissingFromLeft || row.MissingFromRight))
        ];
    }

    public string LeftHeading { get; }
    public string RightHeading { get; }
    public string LeftDetails { get; }
    public string RightDetails { get; }
    public ObservableCollection<SummaryComparisonDisplayRow> Rows { get; }

    private static string Details(string runtime, int context, SummaryRunMetadata? run)
    {
        if (run is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{runtime} · {context:N0} context");
        }

        string vram = run.PeakVramBytes > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" · peak VRAM {run.PeakVramBytes / (1024.0 * 1024 * 1024):F1} GB")
            : string.Empty;
        string fallback = run.FellBack
            ? " · fallback: " + (run.FallbackSteps.Count > 0
                ? string.Join("; ", run.FallbackSteps)
                : "runtime tier reduced")
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{runtime} · requested {run.RequestedContext:N0} / actual {run.ActualContext:N0} context · "
            + $"{run.TotalSeconds:F1}s total{vram}{fallback}");
    }
}

public partial class SummaryComparisonWindow : Window
{
    public SummaryComparisonWindow(SummaryComparisonResult comparison)
    {
        InitializeComponent();
        DataContext = new SummaryComparisonWindowViewModel(comparison);
    }
}
