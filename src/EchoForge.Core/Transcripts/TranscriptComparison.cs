using System.Text.RegularExpressions;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Core.Transcripts;

public enum TranscriptDifferenceKind
{
    Match,
    PunctuationOnly,
    TextDifference,
    MissingFromLeft,
    MissingFromRight,
}

/// <summary>One time-aligned comparison row. Missing speech is deliberately a distinct result.</summary>
public sealed record TranscriptComparisonRow(
    string SourceTrack,
    double StartSeconds,
    double EndSeconds,
    string LeftText,
    string RightText,
    IReadOnlyList<string> LeftSegmentIds,
    IReadOnlyList<string> RightSegmentIds,
    TranscriptDifferenceKind Difference,
    double WordAgreement)
{
    public bool IsMissingRegion => Difference is TranscriptDifferenceKind.MissingFromLeft
        or TranscriptDifferenceKind.MissingFromRight;
}

public sealed record TranscriptComparisonMetrics(
    int Characters,
    int Words,
    int Segments,
    double RepresentedSpeechSeconds,
    double TimelineCoverage,
    int RegionsMissingFromThisRevision);

/// <summary>
/// Comparison of two immutable revisions made from the same source audio. Metrics describe
/// coverage and disagreement; they intentionally do not declare a winner based on text volume.
/// </summary>
public sealed record TranscriptComparisonResult(
    int LeftRevision,
    int RightRevision,
    TranscriptModel LeftModel,
    TranscriptModel RightModel,
    TranscriptionRunMetadata? LeftRun,
    TranscriptionRunMetadata? RightRun,
    TranscriptComparisonMetrics LeftMetrics,
    TranscriptComparisonMetrics RightMetrics,
    IReadOnlyList<TranscriptComparisonRow> Rows);

public static partial class TranscriptComparer
{
    private const double ClusterGapSeconds = 0.75;
    private const double MaximumClusterSeconds = 30;
    private const double GapCost = 0.7;

    public static TranscriptComparisonResult Compare(TranscriptDocument left, TranscriptDocument right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Transcript revisions must belong to the same session.", nameof(right));
        }

        if (!string.IsNullOrWhiteSpace(left.SourceManifestSha256)
            && !string.IsNullOrWhiteSpace(right.SourceManifestSha256)
            && !string.Equals(left.SourceManifestSha256, right.SourceManifestSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("Transcript revisions must identify the same source audio.", nameof(right));
        }

        List<TranscriptComparisonRow> rows = [];
        foreach (string track in new[] { TranscriptSpeakers.MicrophoneTrack, TranscriptSpeakers.SystemTrack })
        {
            rows.AddRange(CompareTrack(
                track,
                left.Segments.Where(segment => segment.SourceTrack == track),
                right.Segments.Where(segment => segment.SourceTrack == track)));
        }

        rows.Sort((a, b) =>
        {
            int time = a.StartSeconds.CompareTo(b.StartSeconds);
            return time != 0 ? time : string.CompareOrdinal(a.SourceTrack, b.SourceTrack);
        });

        return new TranscriptComparisonResult(
            left.TranscriptRevision,
            right.TranscriptRevision,
            left.Model,
            right.Model,
            left.Run,
            right.Run,
            Metrics(left, rows.Count(row => row.Difference == TranscriptDifferenceKind.MissingFromLeft)),
            Metrics(right, rows.Count(row => row.Difference == TranscriptDifferenceKind.MissingFromRight)),
            rows);
    }

    private static List<TranscriptComparisonRow> CompareTrack(
        string track,
        IEnumerable<TranscriptSegment> leftSegments,
        IEnumerable<TranscriptSegment> rightSegments)
    {
        List<TaggedSegment> ordered =
        [
            .. leftSegments.Select(segment => new TaggedSegment(segment, IsLeft: true)),
            .. rightSegments.Select(segment => new TaggedSegment(segment, IsLeft: false)),
        ];
        ordered.Sort((a, b) =>
        {
            int start = a.Segment.StartSeconds.CompareTo(b.Segment.StartSeconds);
            return start != 0 ? start : a.Segment.EndSeconds.CompareTo(b.Segment.EndSeconds);
        });

        List<TranscriptComparisonRow> rows = [];
        List<TaggedSegment> cluster = [];
        double clusterEnd = 0;
        double clusterStart = 0;

        foreach (TaggedSegment tagged in ordered)
        {
            if (cluster.Count > 0
                && (tagged.Segment.StartSeconds > clusterEnd + ClusterGapSeconds
                    || tagged.Segment.StartSeconds > clusterStart + MaximumClusterSeconds))
            {
                rows.AddRange(BuildRows(track, cluster));
                cluster.Clear();
            }

            if (cluster.Count == 0)
            {
                clusterStart = tagged.Segment.StartSeconds;
            }

            cluster.Add(tagged);
            clusterEnd = cluster.Count == 1
                ? tagged.Segment.EndSeconds
                : Math.Max(clusterEnd, tagged.Segment.EndSeconds);
        }

        if (cluster.Count > 0)
        {
            rows.AddRange(BuildRows(track, cluster));
        }

        return rows;
    }

    /// <summary>
    /// Aligns segments inside one local time cluster. A single long segment is allowed to align
    /// with several short ones (segmentation is not missing speech); otherwise sequence alignment
    /// exposes an unmatched short response instead of hiding it inside one large text-difference
    /// row for an entire continuously spoken passage.
    /// </summary>
    private static List<TranscriptComparisonRow> BuildRows(
        string track,
        IReadOnlyList<TaggedSegment> cluster)
    {
        List<TranscriptSegment> left =
            [.. cluster.Where(item => item.IsLeft).Select(item => item.Segment).OrderBy(item => item.StartSeconds)];
        List<TranscriptSegment> right =
            [.. cluster.Where(item => !item.IsLeft).Select(item => item.Segment).OrderBy(item => item.StartSeconds)];

        if (left.Count == 0 || right.Count == 0 || left.Count == 1 || right.Count == 1)
        {
            return [BuildRow(track, left, right)];
        }

        AlignmentCell[,] cells = new AlignmentCell[left.Count + 1, right.Count + 1];
        for (int row = 1; row <= left.Count; row++)
        {
            cells[row, 0] = new AlignmentCell(row * GapCost, AlignmentOperation.LeftOnly);
        }

        for (int column = 1; column <= right.Count; column++)
        {
            cells[0, column] = new AlignmentCell(column * GapCost, AlignmentOperation.RightOnly);
        }

        for (int row = 1; row <= left.Count; row++)
        {
            for (int column = 1; column <= right.Count; column++)
            {
                double paired = cells[row - 1, column - 1].Cost + PairCost(left[row - 1], right[column - 1]);
                double leftOnly = cells[row - 1, column].Cost + GapCost;
                double rightOnly = cells[row, column - 1].Cost + GapCost;

                cells[row, column] = paired <= leftOnly && paired <= rightOnly
                    ? new AlignmentCell(paired, AlignmentOperation.Pair)
                    : leftOnly <= rightOnly
                        ? new AlignmentCell(leftOnly, AlignmentOperation.LeftOnly)
                        : new AlignmentCell(rightOnly, AlignmentOperation.RightOnly);
            }
        }

        List<TranscriptComparisonRow> rows = [];
        int leftIndex = left.Count;
        int rightIndex = right.Count;
        while (leftIndex > 0 || rightIndex > 0)
        {
            AlignmentOperation operation = cells[leftIndex, rightIndex].Operation;
            if (operation == AlignmentOperation.Pair)
            {
                rows.Add(BuildRow(track, [left[--leftIndex]], [right[--rightIndex]]));
            }
            else if (operation == AlignmentOperation.LeftOnly)
            {
                rows.Add(BuildRow(track, [left[--leftIndex]], []));
            }
            else
            {
                rows.Add(BuildRow(track, [], [right[--rightIndex]]));
            }
        }

        rows.Reverse();
        return rows;
    }

    private static double PairCost(TranscriptSegment left, TranscriptSegment right)
    {
        double temporal = TemporalAgreement(left, right);
        double words = Agreement(left.Text, right.Text);
        return 1.4 - ((0.5 * temporal) + (0.9 * words));
    }

    private static double TemporalAgreement(TranscriptSegment left, TranscriptSegment right)
    {
        double intersection = Math.Max(
            0,
            Math.Min(left.EndSeconds, right.EndSeconds) - Math.Max(left.StartSeconds, right.StartSeconds));
        double union = Math.Max(left.EndSeconds, right.EndSeconds) - Math.Min(left.StartSeconds, right.StartSeconds);
        return union <= 0 ? 0 : intersection / union;
    }

    private static TranscriptComparisonRow BuildRow(
        string track,
        IReadOnlyList<TranscriptSegment> left,
        IReadOnlyList<TranscriptSegment> right)
    {
        IReadOnlyList<TranscriptSegment> all = [.. left.Concat(right)];

        string leftText = Join(left);
        string rightText = Join(right);
        TranscriptDifferenceKind difference = Difference(leftText, rightText);

        return new TranscriptComparisonRow(
            track,
            all.Min(item => item.StartSeconds),
            all.Max(item => item.EndSeconds),
            leftText,
            rightText,
            [.. left.Select(segment => segment.Id)],
            [.. right.Select(segment => segment.Id)],
            difference,
            Agreement(leftText, rightText));
    }

    private static TranscriptComparisonMetrics Metrics(TranscriptDocument document, int missing)
    {
        int characters = document.Segments.Sum(segment => segment.Text.Length);
        int words = document.Segments.Sum(segment => Words(segment.Text).Count);
        double represented = document.Segments
            .GroupBy(segment => segment.SourceTrack, StringComparer.Ordinal)
            .Sum(group => UnionDuration(group));
        double denominator = document.DurationSeconds * 2.0;

        return new TranscriptComparisonMetrics(
            characters,
            words,
            document.Segments.Count,
            represented,
            denominator <= 0 ? 0 : Math.Clamp(represented / denominator, 0, 1),
            missing);
    }

    private static double UnionDuration(IEnumerable<TranscriptSegment> source)
    {
        double total = 0;
        double? start = null;
        double end = 0;
        foreach (TranscriptSegment segment in source.OrderBy(segment => segment.StartSeconds))
        {
            if (start is null)
            {
                start = segment.StartSeconds;
                end = segment.EndSeconds;
            }
            else if (segment.StartSeconds <= end)
            {
                end = Math.Max(end, segment.EndSeconds);
            }
            else
            {
                total += Math.Max(0, end - start.Value);
                start = segment.StartSeconds;
                end = segment.EndSeconds;
            }
        }

        return start is null ? 0 : total + Math.Max(0, end - start.Value);
    }

    private static TranscriptDifferenceKind Difference(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return string.IsNullOrWhiteSpace(right)
                ? TranscriptDifferenceKind.Match
                : TranscriptDifferenceKind.MissingFromLeft;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return TranscriptDifferenceKind.MissingFromRight;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return TranscriptDifferenceKind.Match;
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal)
            ? TranscriptDifferenceKind.PunctuationOnly
            : TranscriptDifferenceKind.TextDifference;
    }

    private static double Agreement(string left, string right)
    {
        HashSet<string> a = [.. Words(left)];
        HashSet<string> b = [.. Words(right)];
        if (a.Count == 0 && b.Count == 0)
        {
            return 1;
        }

        int union = a.Union(b, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (double)a.Intersect(b, StringComparer.Ordinal).Count() / union;
    }

    private static string Join(IEnumerable<TranscriptSegment> segments) =>
        string.Join(" ", segments.OrderBy(segment => segment.StartSeconds).Select(segment => segment.Text.Trim()));

    private static IReadOnlyList<string> Words(string text) =>
        [.. WordPattern().Matches(text).Select(match => match.Value.ToLowerInvariant())];

    private static string Normalize(string text) => string.Join(' ', Words(text));

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    private sealed record TaggedSegment(TranscriptSegment Segment, bool IsLeft);

    private readonly record struct AlignmentCell(double Cost, AlignmentOperation Operation);

    private enum AlignmentOperation
    {
        Pair,
        LeftOnly,
        RightOnly,
    }
}
