using System.Globalization;
using System.Text.RegularExpressions;
using EchoForge.Contracts.Evaluation;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Core.Evaluation;

/// <summary>
/// Scores one summary against one meeting's gold annotation.
///
/// <para>
/// <b>No model judges the model.</b> Everything here is set arithmetic and string normalisation.
/// Using a language model to decide whether a language model was right would make the score a
/// second opinion rather than a measurement, and would make an improvement in the judge
/// indistinguishable from an improvement in the summariser.
/// </para>
///
/// <para>
/// <b>Evidence anchors every match.</b> A predicted fact can only be tied to a gold fact when the
/// two cite at least one segment in common. That single rule is what keeps matching conservative:
/// two similarly-worded statements about different moments of a meeting are two facts, and no
/// amount of textual resemblance is allowed to merge them. Wording is then compared on top of
/// that, so a match requires agreement about both <i>what</i> was said and <i>where</i>.
/// </para>
///
/// <para>
/// Every decision — matched, false positive, false negative, and the score that produced it — is
/// recorded, because a metric nobody can audit is a metric nobody should trust.
/// </para>
/// </summary>
public static class SummaryScorer
{
    /// <summary>
    /// Words that carry no evidence about which fact a sentence is about.
    ///
    /// <para>
    /// Deliberately short. A long stop-list starts deleting the words that distinguish two facts
    /// from each other, which quietly turns strict matching into fuzzy matching.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "if", "then", "than", "that", "this", "these", "those",
        "is", "are", "was", "were", "be", "been", "being", "to", "of", "in", "on", "at", "for",
        "with", "as", "by", "it", "its", "we", "will", "shall", "do", "does", "did", "so",
    };

    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Scores one prediction. <paramref name="prediction"/> null means the run produced nothing.</summary>
    public static MeetingScore Score(
        CorpusMeeting meeting,
        SummaryDocument? prediction,
        TranscriptDocument transcript,
        double matchThreshold = 0.5,
        RunMeasurements? run = null,
        string? failureReason = null)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentNullException.ThrowIfNull(transcript);

        if (prediction is null)
        {
            // Counted as a failure with no metrics rather than as a meeting of zeroes. A model
            // that crashes has not scored 0% precision; it has not been measured at all, and the
            // failure rate is where that belongs.
            return new MeetingScore
            {
                MeetingId = meeting.MeetingId,
                ProducedSummary = false,
                FailureReason = failureReason ?? "the run produced no summary",
                Run = run,
            };
        }

        HashSet<string> realSegments = new(transcript.Segments.Select(s => s.Id), StringComparer.Ordinal);
        List<MatchDecision> decisions = [];

        MatchOutcome decisionMatches = MatchItems(
            "decision",
            [.. prediction.Decisions.Select(Predicted.From)],
            [.. meeting.Gold.Decisions.Select(Gold.From)],
            matchThreshold,
            decisions);

        MatchOutcome actionMatches = MatchItems(
            "action",
            [.. prediction.ActionItems.Select(Predicted.From)],
            [.. meeting.Gold.ActionItems.Select(Gold.From)],
            matchThreshold,
            decisions);

        MatchOutcome keyPointMatches = MatchItems(
            "key_point",
            [.. prediction.KeyPoints.Select(Predicted.From)],
            [.. meeting.Gold.KeyPoints.Select(Gold.From)],
            matchThreshold,
            decisions);

        (Ratio ownerPrecision, Ratio datePrecision, int badOwners, int badDates, Ratio unknownOwner, Ratio unknownDate) =
            ScoreOwnersAndDates(prediction, meeting, actionMatches);

        return new MeetingScore
        {
            MeetingId = meeting.MeetingId,
            ProducedSummary = true,

            DecisionPrecision = Ratio.Of(decisionMatches.Matched, decisionMatches.PredictedCount),
            DecisionRecall = Ratio.Of(decisionMatches.Matched, decisionMatches.GoldCount),
            ActionPrecision = Ratio.Of(actionMatches.Matched, actionMatches.PredictedCount),
            ActionRecall = Ratio.Of(actionMatches.Matched, actionMatches.GoldCount),

            // The plan's gate is stated over actions and decisions together, so the combined
            // figure is computed from counts rather than by averaging two percentages.
            CombinedPrecision = Ratio.Of(
                decisionMatches.Matched + actionMatches.Matched,
                decisionMatches.PredictedCount + actionMatches.PredictedCount),
            CombinedRecall = Ratio.Of(
                decisionMatches.Matched + actionMatches.Matched,
                decisionMatches.GoldCount + actionMatches.GoldCount),

            OwnerPrecision = ownerPrecision,
            DatePrecision = datePrecision,
            UnsupportedExplicitOwners = badOwners,
            UnsupportedExplicitDates = badDates,
            UnknownOwnerPreserved = unknownOwner,
            UnknownDatePreserved = unknownDate,

            EvidenceValidity = ScoreEvidenceValidity(prediction, realSegments),
            EvidenceCoverage = Ratio.Of(
                decisionMatches.EvidenceAgreed + actionMatches.EvidenceAgreed,
                decisionMatches.Matched + actionMatches.Matched),

            KeyPointCoverage = Ratio.Of(keyPointMatches.Matched, keyPointMatches.GoldCount),
            ContradictionHandling = ScoreContradictions(meeting, decisionMatches),

            Decisions = decisions,
            Run = run,
        };
    }

    // -- matching ---------------------------------------------------------------------------------

    private sealed record Predicted(string Id, string Text, IReadOnlyList<string> Evidence)
    {
        public static Predicted From(SummaryItem item) =>
            new(item.Id, item.Text, [.. item.Evidence.Select(e => e.SegmentId)]);

        public static Predicted From(SummaryAction action) =>
            new(action.Id, action.Task, [.. action.Evidence.Select(e => e.SegmentId)]);
    }

    private sealed record Gold(string Id, string Text, IReadOnlyList<string> Aliases, IReadOnlyList<string> Evidence)
    {
        public static Gold From(GoldItem item) => new(item.Id, item.Text, item.Aliases, item.Evidence);

        public static Gold From(GoldAction action) => new(action.Id, action.Task, action.Aliases, action.Evidence);
    }

    private sealed record MatchOutcome(
        int Matched,
        int PredictedCount,
        int GoldCount,
        int EvidenceAgreed,
        IReadOnlyDictionary<string, string> GoldByPredicted);

    /// <summary>
    /// Ties predictions to gold, one to one, best first.
    ///
    /// <para>
    /// Greedy rather than optimal assignment, and deliberately so: a greedy pass over
    /// deterministically ordered candidates is reproducible and explainable line by line, and the
    /// cases where it differs from an optimal assignment are cases where two gold facts were
    /// nearly identical — which is an annotation problem worth seeing rather than smoothing over.
    /// </para>
    /// </summary>
    private static MatchOutcome MatchItems(
        string category,
        IReadOnlyList<Predicted> predicted,
        IReadOnlyList<Gold> gold,
        double threshold,
        List<MatchDecision> log)
    {
        List<(double Score, Predicted Predicted, Gold Gold, List<string> Shared)> candidates = [];

        foreach (Predicted candidate in predicted)
        {
            foreach (Gold target in gold)
            {
                List<string> shared =
                [
                    .. candidate.Evidence.Intersect(target.Evidence, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
                ];

                // Evidence is the anchor. No shared segment, no match — however alike the words.
                if (shared.Count == 0)
                {
                    continue;
                }

                double score = Similarity(candidate.Text, target);
                if (score >= threshold)
                {
                    candidates.Add((score, candidate, target, shared));
                }
            }
        }

        // Deterministic ordering: best score first, then by ID, so two runs over the same data
        // produce the same match log and the same numbers.
        candidates.Sort((left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            int byGold = string.CompareOrdinal(left.Gold.Id, right.Gold.Id);
            return byGold != 0 ? byGold : string.CompareOrdinal(left.Predicted.Id, right.Predicted.Id);
        });

        HashSet<string> usedPredicted = new(StringComparer.Ordinal);
        HashSet<string> usedGold = new(StringComparer.Ordinal);
        Dictionary<string, string> goldByPredicted = new(StringComparer.Ordinal);
        int evidenceAgreed = 0;

        foreach ((double score, Predicted candidate, Gold target, List<string> shared) in candidates)
        {
            if (!usedPredicted.Add(candidate.Id))
            {
                continue;
            }

            if (!usedGold.Add(target.Id))
            {
                usedPredicted.Remove(candidate.Id);
                continue;
            }

            goldByPredicted[candidate.Id] = target.Id;
            evidenceAgreed++;

            log.Add(new MatchDecision
            {
                Category = category,
                PredictedId = candidate.Id,
                PredictedText = candidate.Text,
                GoldId = target.Id,
                GoldText = target.Text,
                Similarity = score,
                SharedEvidence = shared,
                Outcome = "matched",
            });
        }

        foreach (Predicted candidate in predicted.Where(p => !usedPredicted.Contains(p.Id)))
        {
            log.Add(new MatchDecision
            {
                Category = category,
                PredictedId = candidate.Id,
                PredictedText = candidate.Text,
                Similarity = 0,
                Outcome = "false_positive",
                Reason = gold.Any(g => candidate.Evidence.Intersect(g.Evidence, StringComparer.Ordinal).Any())
                    ? "cites gold evidence but the wording did not match any gold fact closely enough"
                    : "cites no segment any gold fact cites",
            });
        }

        foreach (Gold target in gold.Where(g => !usedGold.Contains(g.Id)))
        {
            log.Add(new MatchDecision
            {
                Category = category,
                GoldId = target.Id,
                GoldText = target.Text,
                Similarity = 0,
                Outcome = "false_negative",
                Reason = "no predicted item matched this gold fact",
            });
        }

        return new MatchOutcome(goldByPredicted.Count, predicted.Count, gold.Count, evidenceAgreed, goldByPredicted);
    }

    /// <summary>
    /// How alike two statements of a fact are, once their evidence already agrees.
    ///
    /// <para>
    /// An exact normalised match, or any wording the annotator listed as accepted, scores 1. Past
    /// that it is Jaccard overlap of content words — symmetric, so a model cannot score well by
    /// padding a sentence, and strict enough that half the meaningful words have to be shared.
    /// </para>
    /// </summary>
    private static double Similarity(string predicted, Gold gold)
    {
        string normalised = Normalise(predicted);

        if (string.Equals(normalised, Normalise(gold.Text), StringComparison.Ordinal))
        {
            return 1.0;
        }

        foreach (string alias in gold.Aliases)
        {
            if (string.Equals(normalised, Normalise(alias), StringComparison.Ordinal))
            {
                return 1.0;
            }
        }

        double best = Jaccard(Words(predicted), Words(gold.Text));
        foreach (string alias in gold.Aliases)
        {
            best = Math.Max(best, Jaccard(Words(predicted), Words(alias)));
        }

        return best;
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 && right.Count == 0)
        {
            return 1.0;
        }

        if (left.Count == 0 || right.Count == 0)
        {
            return 0.0;
        }

        int shared = left.Count(right.Contains);
        return (double)shared / (left.Count + right.Count - shared);
    }

    /// <summary>Case, punctuation and spacing are noise. Word choice is not.</summary>
    public static string Normalise(string text) =>
        string.Join(' ', WordPattern.Matches(text ?? string.Empty).Select(m => m.Value.ToLowerInvariant()));

    private static HashSet<string> Words(string text) =>
    [
        .. WordPattern.Matches(text ?? string.Empty)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(word => !Noise.Contains(word))
    ];

    // -- owners, dates, evidence, contradictions ----------------------------------------------------

    private static (Ratio Owner, Ratio Date, int BadOwners, int BadDates, Ratio UnknownOwner, Ratio UnknownDate)
        ScoreOwnersAndDates(SummaryDocument prediction, CorpusMeeting meeting, MatchOutcome actions)
    {
        Dictionary<string, GoldAction> goldById = meeting.Gold.ActionItems.ToDictionary(a => a.Id, StringComparer.Ordinal);

        int ownerHits = 0, ownerTotal = 0, dateHits = 0, dateTotal = 0;
        int badOwners = 0, badDates = 0;
        int unknownOwnerKept = 0, unknownOwnerTotal = 0, unknownDateKept = 0, unknownDateTotal = 0;

        foreach (SummaryAction action in prediction.ActionItems)
        {
            bool explicitOwner = string.Equals(action.OwnerStatus, SupportStatuses.Explicit, StringComparison.Ordinal);
            bool explicitDate = string.Equals(action.DueDateStatus, SupportStatuses.Explicit, StringComparison.Ordinal);

            if (!actions.GoldByPredicted.TryGetValue(action.Id, out string? goldId) || !goldById.TryGetValue(goldId, out GoldAction? gold))
            {
                // An unmatched action is already a false positive. An unmatched action that also
                // asserts an owner has additionally invented a commitment, which the plan's gate
                // counts separately and allows none of.
                if (explicitOwner && action.Owner is not null)
                {
                    badOwners++;
                }

                if (explicitDate && action.DueDate is not null)
                {
                    badDates++;
                }

                continue;
            }

            bool goldOwnerKnown = !string.Equals(gold.OwnerStatus, SupportStatuses.Unknown, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(gold.Owner);

            if (goldOwnerKnown)
            {
                ownerTotal++;
                if (explicitOwner && string.Equals(Normalise(action.Owner ?? string.Empty), Normalise(gold.Owner!), StringComparison.Ordinal))
                {
                    ownerHits++;
                }
            }
            else
            {
                // The transcript named nobody. Keeping that unknown is the correct behaviour and
                // is measured; filling it in is the failure the certainty model exists to prevent.
                unknownOwnerTotal++;
                if (action.Owner is null)
                {
                    unknownOwnerKept++;
                }
                else if (explicitOwner)
                {
                    badOwners++;
                }
            }

            bool goldDateKnown = !string.Equals(gold.DueDateStatus, SupportStatuses.Unknown, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(gold.DueDate);

            if (goldDateKnown)
            {
                dateTotal++;
                if (explicitDate && string.Equals(action.DueDate, gold.DueDate, StringComparison.Ordinal))
                {
                    dateHits++;
                }
            }
            else
            {
                unknownDateTotal++;
                if (action.DueDate is null)
                {
                    unknownDateKept++;
                }
                else if (explicitDate)
                {
                    badDates++;
                }
            }
        }

        return (
            Ratio.Of(ownerHits, ownerTotal),
            Ratio.Of(dateHits, dateTotal),
            badOwners,
            badDates,
            Ratio.Of(unknownOwnerKept, unknownOwnerTotal),
            Ratio.Of(unknownDateKept, unknownDateTotal));
    }

    private static Ratio ScoreEvidenceValidity(SummaryDocument prediction, HashSet<string> realSegments)
    {
        int valid = 0, total = 0;

        foreach (SummaryEvidence reference in prediction.AllItems.SelectMany(i => i.Evidence)
                     .Concat(prediction.ActionItems.SelectMany(a => a.Evidence)))
        {
            total++;
            if (realSegments.Contains(reference.SegmentId) && reference.TranscriptRevision == prediction.TranscriptRevision)
            {
                valid++;
            }
        }

        return Ratio.Of(valid, total);
    }

    /// <summary>
    /// A contradiction is handled when **every** side of it survived.
    ///
    /// <para>
    /// Keeping one half of a reversal is not partial credit; it is the specific failure that makes
    /// a reader act on a decision the meeting undid.
    /// </para>
    /// </summary>
    private static Ratio ScoreContradictions(CorpusMeeting meeting, MatchOutcome decisions)
    {
        if (meeting.Gold.Contradictions.Count == 0)
        {
            return Ratio.None;
        }

        HashSet<string> matchedGold = new(decisions.GoldByPredicted.Values, StringComparer.Ordinal);
        int kept = meeting.Gold.Contradictions.Count(c => c.ItemIds.Count > 0 && c.ItemIds.All(matchedGold.Contains));

        return Ratio.Of(kept, meeting.Gold.Contradictions.Count);
    }

    // -- aggregation ---------------------------------------------------------------------------------

    /// <summary>Rolls per-meeting counts into one model's numbers. Counts add; ratios never average.</summary>
    public static ModelEvaluation Aggregate(string backend, IReadOnlyList<MeetingScore> meetings, RunMeasurements? identity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        ArgumentNullException.ThrowIfNull(meetings);

        List<MeetingScore> scored = [.. meetings.Where(m => m.ProducedSummary)];

        Ratio Sum(Func<MeetingScore, Ratio> select) =>
            scored.Aggregate(Ratio.None, (total, meeting) => total + select(meeting));

        double[] durations = [.. scored.Where(m => m.Run is not null).Select(m => m.Run!.TotalSeconds).OrderBy(s => s)];

        return new ModelEvaluation
        {
            Backend = backend,
            ModelId = identity?.ModelId ?? string.Empty,
            ModelRevision = identity?.ModelRevision ?? string.Empty,
            Meetings = meetings,
            CombinedPrecision = Sum(m => m.CombinedPrecision),
            CombinedRecall = Sum(m => m.CombinedRecall),
            OwnerPrecision = Sum(m => m.OwnerPrecision),
            DatePrecision = Sum(m => m.DatePrecision),
            EvidenceValidity = Sum(m => m.EvidenceValidity),
            EvidenceCoverage = Sum(m => m.EvidenceCoverage),
            KeyPointCoverage = Sum(m => m.KeyPointCoverage),
            ContradictionHandling = Sum(m => m.ContradictionHandling),
            UnsupportedExplicitOwners = scored.Sum(m => m.UnsupportedExplicitOwners),
            UnsupportedExplicitDates = scored.Sum(m => m.UnsupportedExplicitDates),
            FailureRate = Ratio.Of(meetings.Count(m => !m.ProducedSummary), meetings.Count),
            MedianSeconds = durations.Length == 0 ? 0 : durations[durations.Length / 2],
            TotalSeconds = scored.Sum(m => m.Run?.TotalSeconds ?? 0),
            PeakVramBytes = scored.Count == 0 ? 0 : scored.Max(m => m.Run?.PeakVramBytes ?? 0),
        };
    }

    /// <summary>Applies the plan's thresholds. Says plainly when the data cannot decide acceptance.</summary>
    public static AcceptanceVerdict Judge(ModelEvaluation evaluation, CorpusKind corpusKind)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        List<string> failures = [];

        if (evaluation.CombinedPrecision.Value is not { } precision || precision < AcceptanceVerdict.MinimumPrecision)
        {
            failures.Add(Invariant(
                $"combined action/decision precision {Describe(evaluation.CombinedPrecision)} is below the {AcceptanceVerdict.MinimumPrecision:P0} target"));
        }

        if (evaluation.CombinedRecall.Value is not { } recall || recall < AcceptanceVerdict.MinimumRecall)
        {
            failures.Add(Invariant(
                $"combined action/decision recall {Describe(evaluation.CombinedRecall)} is below the {AcceptanceVerdict.MinimumRecall:P0} target"));
        }

        if (evaluation.EvidenceValidity.Value is not { } evidence || evidence < AcceptanceVerdict.RequiredEvidenceValidity)
        {
            failures.Add(Invariant($"evidence validity {Describe(evaluation.EvidenceValidity)} is not 100%"));
        }

        if (evaluation.UnsupportedExplicitOwners > 0)
        {
            failures.Add(Invariant($"{evaluation.UnsupportedExplicitOwners} action(s) claimed an owner the transcript does not support"));
        }

        if (evaluation.UnsupportedExplicitDates > 0)
        {
            failures.Add(Invariant($"{evaluation.UnsupportedExplicitDates} action(s) claimed a due date the transcript does not support"));
        }

        bool isAcceptanceRun = corpusKind == CorpusKind.Release;
        bool passed = failures.Count == 0;

        string statement = !isAcceptanceRun
            ? corpusKind == CorpusKind.Synthetic
                ? "NOT AN ACCEPTANCE RESULT — synthetic data. These numbers say the scorer works, and nothing about any model."
                : "NOT AN ACCEPTANCE RESULT — development corpus. Acceptance is decided only by the held-out release corpus."
            : passed
                ? "Phase 3 acceptance quality gate PASSED on the held-out release corpus."
                : "Phase 3 acceptance quality gate FAILED on the held-out release corpus.";

        return new AcceptanceVerdict
        {
            Passed = passed,
            IsAcceptanceRun = isAcceptanceRun,
            Failures = failures,
            Statement = statement,
        };
    }

    private static string Describe(Ratio ratio) => ratio.ToString();

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
