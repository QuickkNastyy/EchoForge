using System.Text.RegularExpressions;
using EchoForge.Contracts.Summaries;

namespace EchoForge.Core.Summaries;

public sealed record SummaryComparisonRow(
    string Section,
    string LeftText,
    string RightText,
    bool MissingFromLeft,
    bool MissingFromRight,
    double Agreement);

public sealed record SummaryComparisonResult(
    int LeftRevision,
    int RightRevision,
    int TranscriptRevision,
    SummaryModel LeftModel,
    SummaryModel RightModel,
    SummaryRunMetadata? LeftRun,
    SummaryRunMetadata? RightRun,
    IReadOnlyList<SummaryComparisonRow> Rows);

/// <summary>Aligns summary content by section, evidence overlap, and normalized wording.</summary>
public static partial class SummaryComparer
{
    public static SummaryComparisonResult Compare(SummaryDocument left, SummaryDocument right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (!string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
            || left.TranscriptRevision != right.TranscriptRevision
            || !string.Equals(left.TranscriptSha256, right.TranscriptSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("Summary comparison requires the same immutable transcript revision.", nameof(right));
        }

        List<Entry> a = Entries(left);
        List<Entry> b = Entries(right);
        List<SummaryComparisonRow> rows = [];
        HashSet<int> usedRight = [];

        for (int leftIndex = 0; leftIndex < a.Count; leftIndex++)
        {
            Entry item = a[leftIndex];
            (int index, double score) = Best(item, b, usedRight);
            if (index >= 0 && score >= 0.20)
            {
                usedRight.Add(index);
                rows.Add(new SummaryComparisonRow(item.Section, item.Text, b[index].Text, false, false, score));
            }
            else
            {
                rows.Add(new SummaryComparisonRow(item.Section, item.Text, string.Empty, false, true, 0));
            }
        }

        for (int index = 0; index < b.Count; index++)
        {
            if (!usedRight.Contains(index))
            {
                rows.Add(new SummaryComparisonRow(b[index].Section, string.Empty, b[index].Text, true, false, 0));
            }
        }

        return new SummaryComparisonResult(
            left.SummaryRevision,
            right.SummaryRevision,
            left.TranscriptRevision,
            left.Model,
            right.Model,
            left.Run,
            right.Run,
            rows);
    }

    private static List<Entry> Entries(SummaryDocument summary)
    {
        List<Entry> entries = [];
        if (summary.Narrative?.Summary.Count > 0)
        {
            entries.AddRange(summary.Narrative.Summary.Select(block => From("Overall summary", block.Text, block.Evidence)));
        }
        else if (!string.IsNullOrWhiteSpace(summary.Overview))
        {
            entries.Add(new Entry("Overall summary", summary.Overview, []));
        }

        Add(entries, "Key points", summary.KeyPoints);
        Add(entries, "Decisions", summary.Decisions);
        foreach (SummaryAction action in summary.ActionItems)
        {
            string detail = action.Task;
            if (!string.IsNullOrWhiteSpace(action.Owner)) detail += $" · owner: {action.Owner}";
            if (!string.IsNullOrWhiteSpace(action.DueDate ?? action.DueDateText)) detail += $" · due: {action.DueDate ?? action.DueDateText}";
            entries.Add(From("Action items", detail, action.Evidence));
        }
        Add(entries, "Open questions", summary.OpenQuestions);
        Add(entries, "Risks", summary.Risks);
        Add(entries, "Blockers", summary.Blockers);
        if (summary.Narrative is { } narrative)
        {
            entries.AddRange(narrative.MainTopics.Select(block => From("Main topics", block.Text, block.Evidence)));
            entries.AddRange(narrative.ImportantDetails.Select(block => From("Important details", block.Text, block.Evidence)));
            entries.AddRange(narrative.FollowUps.Select(block => From("Follow-ups", block.Text, block.Evidence)));
        }
        return entries;
    }

    private static void Add(List<Entry> destination, string section, IEnumerable<SummaryItem> source) =>
        destination.AddRange(source.Select(item => From(section, item.Text, item.Evidence)));

    private static Entry From(string section, string text, IEnumerable<SummaryEvidence> evidence) =>
        new(section, text, [.. evidence.Select(reference => reference.SegmentId)]);

    private static (int Index, double Score) Best(Entry needle, IReadOnlyList<Entry> haystack, HashSet<int> used)
    {
        int bestIndex = -1;
        double bestScore = 0;
        for (int index = 0; index < haystack.Count; index++)
        {
            if (used.Contains(index) || haystack[index].Section != needle.Section) continue;
            double evidence = Jaccard(needle.Evidence, haystack[index].Evidence);
            double words = Jaccard(Words(needle.Text), Words(haystack[index].Text));
            double score = needle.Evidence.Count > 0 && haystack[index].Evidence.Count > 0
                ? evidence * 0.7 + words * 0.3
                : words;
            if (score > bestScore)
            {
                bestIndex = index;
                bestScore = score;
            }
        }
        return (bestIndex, bestScore);
    }

    private static double Jaccard(IEnumerable<string> left, IEnumerable<string> right)
    {
        HashSet<string> a = [.. left];
        HashSet<string> b = [.. right];
        if (a.Count == 0 && b.Count == 0) return 1;
        int union = a.Union(b, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (double)a.Intersect(b, StringComparer.Ordinal).Count() / union;
    }

    private static IReadOnlyList<string> Words(string value) =>
        [.. WordPattern().Matches(value).Select(match => match.Value.ToLowerInvariant())];

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    private sealed record Entry(string Section, string Text, IReadOnlyList<string> Evidence);
}
