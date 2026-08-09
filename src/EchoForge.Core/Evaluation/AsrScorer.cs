using System.Text.RegularExpressions;
using EchoForge.Contracts.Evaluation;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Core.Evaluation;

/// <summary>
/// Reference-backed ASR scoring. It compares immutable documents only; running models and managing
/// corpora remain orchestration concerns. No metric is produced without a human reference.
/// </summary>
public static partial class AsrScorer
{
    private const double TimeToleranceSeconds = 1.0;

    public static AsrEvaluationScore Score(
        TranscriptDocument reference,
        TranscriptDocument hypothesis,
        AsrReferenceTerms? terms = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);

        if (!string.Equals(reference.SessionId, hypothesis.SessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Reference and hypothesis must describe the same session.", nameof(hypothesis));
        }

        terms ??= new AsrReferenceTerms();
        IReadOnlyList<string> referenceExact = ExactWords(Join(reference));
        IReadOnlyList<string> hypothesisExact = ExactWords(Join(hypothesis));
        IReadOnlyList<string> referenceNormalized = NormalizedWords(Join(reference));
        IReadOnlyList<string> hypothesisNormalized = NormalizedWords(Join(hypothesis));

        List<SegmentMatch> matches = MatchSegments(reference.Segments, hypothesis.Segments);
        HashSet<string> emittedReferenceIds =
            [.. matches.Where(match => match.Hypothesis is not null).Select(match => match.Reference.Id)];
        IReadOnlyList<TranscriptSegment> shortUtterances =
            [.. reference.Segments.Where(IsShortUtterance)];

        List<double> timestampErrors =
        [
            .. matches
                .Where(match => match.Hypothesis is not null && match.TextAgreement >= 0.5)
                .SelectMany(match => new[]
                {
                    Math.Abs(match.Reference.StartSeconds - match.Hypothesis!.StartSeconds),
                    Math.Abs(match.Reference.EndSeconds - match.Hypothesis!.EndSeconds),
                }),
        ];

        return new AsrEvaluationScore
        {
            TranscriptRevision = hypothesis.TranscriptRevision,
            ModelId = hypothesis.Model.ModelId,
            ModelRevision = hypothesis.Model.Revision,
            WordErrors = EditDistance(referenceExact, hypothesisExact),
            NormalizedWordErrors = EditDistance(referenceNormalized, hypothesisNormalized),
            ShortUtteranceRecall = Ratio.Of(
                shortUtterances.Count(segment => emittedReferenceIds.Contains(segment.Id)),
                shortUtterances.Count),
            ProperNameAccuracy = TermAccuracy(terms.ProperNames, referenceNormalized, hypothesisNormalized),
            AcronymAccuracy = TermAccuracy(terms.Acronyms, referenceNormalized, hypothesisNormalized),
            NumericAccuracy = TermAccuracy(terms.NumericExpressions, referenceNormalized, hypothesisNormalized),
            SpeechRegionRecall = Ratio.Of(emittedReferenceIds.Count, reference.Segments.Count),
            SourceAttributionAccuracy = Ratio.Of(
                matches.Count(match => match.Hypothesis is not null
                    && string.Equals(match.Reference.SourceTrack, match.Hypothesis.SourceTrack, StringComparison.Ordinal)),
                reference.Segments.Count),
            MeanTimestampErrorSeconds = timestampErrors.Count == 0 ? null : timestampErrors.Average(),
        };
    }

    private static List<SegmentMatch> MatchSegments(
        IReadOnlyList<TranscriptSegment> reference,
        IReadOnlyList<TranscriptSegment> hypothesis)
    {
        HashSet<string> used = new(StringComparer.Ordinal);
        List<SegmentMatch> matches = [];

        foreach (TranscriptSegment expected in reference.OrderBy(segment => segment.StartSeconds))
        {
            TranscriptSegment? best = null;
            double bestScore = 0;
            foreach (TranscriptSegment candidate in hypothesis.Where(segment => !used.Contains(segment.Id)))
            {
                if (!NearInTime(expected, candidate))
                {
                    continue;
                }

                double agreement = WordAgreement(expected.Text, candidate.Text);
                double temporal = TemporalAgreement(expected, candidate);
                double score = (agreement * 0.75) + (temporal * 0.25);
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            // A region with emitted text counts for region recall even when the words are wrong,
            // but an unrelated nearby segment should not be paired merely because time overlaps.
            if (best is not null && bestScore >= 0.15)
            {
                used.Add(best.Id);
                matches.Add(new SegmentMatch(expected, best, WordAgreement(expected.Text, best.Text)));
            }
            else
            {
                matches.Add(new SegmentMatch(expected, null, 0));
            }
        }

        return matches;
    }

    private static bool IsShortUtterance(TranscriptSegment segment) =>
        NormalizedWords(segment.Text).Count <= 4 && segment.EndSeconds - segment.StartSeconds <= 4.0;

    private static bool NearInTime(TranscriptSegment left, TranscriptSegment right) =>
        left.StartSeconds <= right.EndSeconds + TimeToleranceSeconds
        && right.StartSeconds <= left.EndSeconds + TimeToleranceSeconds;

    private static double TemporalAgreement(TranscriptSegment left, TranscriptSegment right)
    {
        double intersection = Math.Max(0,
            Math.Min(left.EndSeconds, right.EndSeconds) - Math.Max(left.StartSeconds, right.StartSeconds));
        double union = Math.Max(left.EndSeconds, right.EndSeconds) - Math.Min(left.StartSeconds, right.StartSeconds);
        return union <= 0 ? 0 : intersection / union;
    }

    private static double WordAgreement(string left, string right)
    {
        HashSet<string> a = [.. NormalizedWords(left)];
        HashSet<string> b = [.. NormalizedWords(right)];
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        int union = a.Union(b, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (double)a.Intersect(b, StringComparer.Ordinal).Count() / union;
    }

    private static Ratio TermAccuracy(
        IReadOnlyList<string> terms,
        IReadOnlyList<string> reference,
        IReadOnlyList<string> hypothesis)
    {
        List<IReadOnlyList<string>> applicable =
        [
            .. terms
                .Select(NormalizedWords)
                .Where(term => term.Count > 0 && Contains(reference, term)),
        ];

        return Ratio.Of(applicable.Count(term => Contains(hypothesis, term)), applicable.Count);
    }

    private static bool Contains(IReadOnlyList<string> words, IReadOnlyList<string> phrase)
    {
        for (int start = 0; start + phrase.Count <= words.Count; start++)
        {
            bool same = true;
            for (int offset = 0; offset < phrase.Count; offset++)
            {
                if (!string.Equals(words[start + offset], phrase[offset], StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return true;
            }
        }

        return false;
    }

    private static WordErrorCounts EditDistance(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        EditCounts[,] matrix = new EditCounts[reference.Count + 1, hypothesis.Count + 1];
        for (int row = 1; row <= reference.Count; row++)
        {
            matrix[row, 0] = matrix[row - 1, 0] with { Deletions = matrix[row - 1, 0].Deletions + 1 };
        }

        for (int column = 1; column <= hypothesis.Count; column++)
        {
            matrix[0, column] = matrix[0, column - 1] with { Insertions = matrix[0, column - 1].Insertions + 1 };
        }

        for (int row = 1; row <= reference.Count; row++)
        {
            for (int column = 1; column <= hypothesis.Count; column++)
            {
                if (string.Equals(reference[row - 1], hypothesis[column - 1], StringComparison.Ordinal))
                {
                    matrix[row, column] = matrix[row - 1, column - 1];
                    continue;
                }

                EditCounts substitution = matrix[row - 1, column - 1] with
                {
                    Substitutions = matrix[row - 1, column - 1].Substitutions + 1,
                };
                EditCounts deletion = matrix[row - 1, column] with
                {
                    Deletions = matrix[row - 1, column].Deletions + 1,
                };
                EditCounts insertion = matrix[row, column - 1] with
                {
                    Insertions = matrix[row, column - 1].Insertions + 1,
                };
                matrix[row, column] = new[] { substitution, deletion, insertion }
                    .OrderBy(counts => counts.Total)
                    .ThenBy(counts => counts.Substitutions)
                    .ThenBy(counts => counts.Deletions)
                    .First();
            }
        }

        EditCounts result = matrix[reference.Count, hypothesis.Count];
        return new WordErrorCounts(
            result.Substitutions,
            result.Deletions,
            result.Insertions,
            reference.Count);
    }

    private static string Join(TranscriptDocument document) => string.Join(
        ' ',
        document.Segments.OrderBy(segment => segment.StartSeconds).Select(segment => segment.Text));

    private static IReadOnlyList<string> ExactWords(string text) =>
        [.. WhitespacePattern().Split(text.Trim()).Where(word => word.Length > 0)];

    private static IReadOnlyList<string> NormalizedWords(string text) =>
        [.. WordPattern().Matches(text).Select(match => match.Value.ToLowerInvariant())];

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    private sealed record SegmentMatch(
        TranscriptSegment Reference,
        TranscriptSegment? Hypothesis,
        double TextAgreement);

    private readonly record struct EditCounts(int Substitutions, int Deletions, int Insertions)
    {
        public int Total => Substitutions + Deletions + Insertions;
    }
}
