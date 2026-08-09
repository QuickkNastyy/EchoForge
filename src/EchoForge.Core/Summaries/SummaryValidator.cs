using System.Globalization;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Core.Summaries;

/// <summary>The verdict on a summary. Empty problems means it may be activated.</summary>
public sealed record SummaryVerdict(IReadOnlyList<string> Problems)
{
    public static readonly SummaryVerdict Valid = new([]);

    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Checks a summary against the transcript it claims to come from.
///
/// <para>
/// <b>The validator is the final authority, not the model.</b> A language model asked for
/// structured output will produce structured output whether or not the transcript supports it:
/// confident prose, a plausible owner, a segment ID that does not exist. Everything here exists
/// because the generating step cannot be trusted to police itself, and because a summary that
/// activates with one bad citation teaches the user that the citations are decorative.
/// </para>
///
/// <para>
/// It refuses rather than repairs. Dropping the unsupported half of a claim and keeping the rest
/// would produce a summary nobody wrote and nobody reviewed.
/// </para>
/// </summary>
public static class SummaryValidator
{
    private const double Tolerance = 1e-6;

    /// <summary>
    /// One re-ask, and only one.
    ///
    /// <para>
    /// A model that produced an unsupported answer will often produce a supported one when told
    /// what was wrong with it, and asking once is worth it. Asking repeatedly is how a generator
    /// eventually stumbles onto output that satisfies the checks without satisfying the
    /// transcript, and how a job acquires an unbounded running time. A failed repair fails the
    /// attempt; it never relaxes what the answer had to be.
    /// </para>
    /// </summary>
    public const int MaxRepairAttempts = 1;

    public static SummaryVerdict Validate(SummaryDocument summary, TranscriptDocument transcript)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(transcript);

        List<string> problems = [];

        if (summary.SchemaVersion is not (1 or 2 or 3))
        {
            problems.Add(Invariant($"schema_version {summary.SchemaVersion} is not supported"));
        }

        if (!string.Equals(summary.SessionId, transcript.SessionId, StringComparison.Ordinal))
        {
            problems.Add("the summary names a different session from the transcript");
        }

        if (summary.TranscriptRevision != transcript.TranscriptRevision)
        {
            problems.Add(Invariant(
                $"the summary was written from revision {summary.TranscriptRevision} but was validated against {transcript.TranscriptRevision}"));
        }

        if (summary.SummaryRevision < 1)
        {
            problems.Add("summary_revision must be at least 1");
        }

        if (string.IsNullOrWhiteSpace(summary.PromptVersion))
        {
            problems.Add("no prompt version was recorded");
        }

        // The bound is checked here as well as enforced by the coordinator, because a document
        // claiming a third attempt is evidence that something re-asked without being asked to.
        if (summary.RepairAttempt is < 0 or > MaxRepairAttempts)
        {
            problems.Add(Invariant(
                $"the summary reports repair attempt {summary.RepairAttempt}, and at most {MaxRepairAttempts} is allowed"));
        }

        if (summary.Synthesis is { } synthesis)
        {
            if (synthesis.Levels < 1)
            {
                problems.Add(Invariant($"the synthesis records {synthesis.Levels} passes, and every summary is folded at least once"));
            }

            // Every pass folds at least one group, so fewer groups than passes did not happen.
            if (synthesis.Groups < synthesis.Levels)
            {
                problems.Add(Invariant(
                    $"the synthesis records {synthesis.Groups} groups across {synthesis.Levels} passes"));
            }

            if (synthesis.MergedItems < 0)
            {
                problems.Add("the synthesis reports a negative number of merged items");
            }
        }

        // The allow-list. Anything cited that is not in here does not exist.
        Dictionary<string, TranscriptSegment> allowed = new(StringComparer.Ordinal);
        foreach (TranscriptSegment segment in transcript.Segments)
        {
            allowed[segment.Id] = segment;
        }

        HashSet<string> ids = new(StringComparer.Ordinal);

        foreach (SummaryItem item in summary.AllItems)
        {
            ValidateItem(item, allowed, summary, ids, problems);
        }

        foreach (SummaryAction action in summary.ActionItems)
        {
            ValidateAction(action, allowed, summary, ids, problems);
        }

        // A classification the host does not recognise would be silently treated as outstanding
        // work, which is the exact failure this field exists to prevent.
        foreach (SummaryAction action in summary.ActionItems)
        {
            if (action.Classification is { } classification && !ActionClassifications.IsKnown(classification))
            {
                problems.Add(Invariant($"action '{action.Id}' has an unknown classification '{classification}'"));
            }
        }

        // Decisions are what people act on afterwards, so an uncited one is never acceptable
        // however certain the model claimed to be.
        foreach (SummaryItem decision in summary.Decisions)
        {
            if (decision.Evidence.Count == 0)
            {
                problems.Add(Invariant($"decision '{decision.Id}' cites nothing"));
            }
        }

        ValidateNarrative(summary, allowed, ids, problems);
        ValidateBrief(summary, allowed, ids, problems);

        return problems.Count == 0 ? SummaryVerdict.Valid : new SummaryVerdict(problems);
    }

    /// <summary>
    /// Holds the brief to the same rule as the narrative, plus the ones only a plan has.
    ///
    /// <para>
    /// A brief is allowed to reason further than the narrative was: it may say what to do first and
    /// why, because it read the meeting rather than a bag of sentences. What it may not do is cite
    /// something that was never said. Every block and every step still names validated facts, and
    /// still cites only the segments those facts cite, so "do X first because Y blocks it" is
    /// always one click from the two moments where Y and X were discussed.
    /// </para>
    /// </summary>
    private static void ValidateBrief(
        SummaryDocument summary,
        Dictionary<string, TranscriptSegment> allowed,
        HashSet<string> structuredIds,
        List<string> problems)
    {
        if (summary.Brief is null)
        {
            // Only a v3 model document is required to carry one. Everything older is complete
            // without it and is never rewritten to acquire one.
            if (summary.SchemaVersion >= 3 && summary.Model.ProducesSummaries)
            {
                problems.Add("a schema-v3 model summary has no meeting brief");
            }
            return;
        }

        if (!summary.Model.ProducesSummaries)
        {
            problems.Add("a placeholder document must not claim a generated meeting brief");
            return;
        }

        if (summary.Brief.Summary.Count == 0)
        {
            problems.Add("the meeting brief has no summary");
        }

        Dictionary<string, HashSet<string>> evidenceByItem = EvidenceByItem(summary);
        HashSet<string> briefIds = new(StringComparer.Ordinal);

        foreach (SummaryNarrativeBlock block in summary.Brief.AllBlocks)
        {
            ValidateGroundedBlock(
                block.Id, block.Text, block.SupportingItemIds, block.Evidence,
                allowed, structuredIds, evidenceByItem, summary, briefIds, problems);
        }

        HashSet<string> stepIds = new(StringComparer.Ordinal);
        foreach (MeetingPlanStep step in summary.Brief.ActionPlan)
        {
            stepIds.Add(step.Id);
        }

        int expectedOrder = 1;
        foreach (MeetingPlanStep step in summary.Brief.ActionPlan)
        {
            ValidateGroundedBlock(
                step.Id, step.Title, step.SupportingItemIds, step.Evidence,
                allowed, structuredIds, evidenceByItem, summary, briefIds, problems);

            // The plan is one sequence. Two steps both called "3" would leave the reader to guess
            // which comes first, which is the one thing the plan exists to answer.
            if (step.Order != expectedOrder)
            {
                problems.Add(Invariant(
                    $"plan step '{step.Id}' is numbered {step.Order} where {expectedOrder} was expected"));
            }
            expectedOrder++;

            if (!PlanAudiences.IsKnown(step.Audience))
            {
                problems.Add(Invariant($"plan step '{step.Id}' has an unknown audience '{step.Audience}'"));
            }

            if (!PlanTimings.IsKnown(step.Timing))
            {
                problems.Add(Invariant($"plan step '{step.Id}' has an unknown timing '{step.Timing}'"));
            }

            if (!PlanBases.TryParse(step.Basis, out _))
            {
                problems.Add(Invariant($"plan step '{step.Id}' has an unknown basis '{step.Basis}'"));
            }

            if (!SupportStatuses.TryParse(step.OwnerStatus, out SupportStatus owner))
            {
                problems.Add(Invariant($"plan step '{step.Id}' has an unknown owner status '{step.OwnerStatus}'"));
            }
            else if (owner == SupportStatus.Unknown && step.Owner is not null)
            {
                problems.Add(Invariant($"plan step '{step.Id}' has an unknown owner but names one anyway"));
            }

            // Addressing work to somebody else without saying who is worse than not splitting the
            // list at all: it tells the reader this is not theirs and gives them nobody to ask.
            if (string.Equals(step.Audience, PlanAudiences.Others, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(step.Owner))
            {
                problems.Add(Invariant($"plan step '{step.Id}' is somebody else's work but names nobody"));
            }

            if (!SupportStatuses.TryParse(step.DueDateStatus, out SupportStatus due))
            {
                problems.Add(Invariant($"plan step '{step.Id}' has an unknown due-date status '{step.DueDateStatus}'"));
            }
            else
            {
                if (due == SupportStatus.Unknown && step.DueDate is not null)
                {
                    problems.Add(Invariant($"plan step '{step.Id}' has an unknown due date but names one anyway"));
                }

                if (step.DueDate is { } date &&
                    !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    problems.Add(Invariant($"plan step '{step.Id}' has a due date that is not an ISO calendar date"));
                }

                if (due != SupportStatus.Unknown && step.DueDate is not null && summary.MeetingDate is null)
                {
                    problems.Add(Invariant(
                        $"plan step '{step.Id}' resolves a due date, but the meeting date is unknown so nothing could be resolved against it"));
                }
            }

            foreach (string dependency in step.DependsOnStepIds)
            {
                if (!stepIds.Contains(dependency))
                {
                    problems.Add(Invariant($"plan step '{step.Id}' depends on '{dependency}', which is not in this plan"));
                }
                else if (string.Equals(dependency, step.Id, StringComparison.Ordinal))
                {
                    problems.Add(Invariant($"plan step '{step.Id}' depends on itself"));
                }
            }
        }
    }

    private static Dictionary<string, HashSet<string>> EvidenceByItem(SummaryDocument summary)
    {
        Dictionary<string, HashSet<string>> evidenceByItem = new(StringComparer.Ordinal);
        foreach (SummaryItem item in summary.AllItems)
        {
            evidenceByItem[item.Id] = [.. item.Evidence.Select(reference => reference.SegmentId)];
        }
        foreach (SummaryAction action in summary.ActionItems)
        {
            evidenceByItem[action.Id] = [.. action.Evidence.Select(reference => reference.SegmentId)];
        }
        return evidenceByItem;
    }

    /// <summary>
    /// The shared rule for anything the final pass wrote: real prose, named facts that exist, and
    /// citations that come from those facts and nowhere else.
    /// </summary>
    private static void ValidateGroundedBlock(
        string id,
        string text,
        IReadOnlyList<string> supportingItemIds,
        IReadOnlyList<SummaryEvidence> evidence,
        Dictionary<string, TranscriptSegment> allowed,
        HashSet<string> structuredIds,
        Dictionary<string, HashSet<string>> evidenceByItem,
        SummaryDocument summary,
        HashSet<string> seenIds,
        List<string> problems)
    {
        if (!seenIds.Add(id))
        {
            problems.Add(Invariant($"brief entry id '{id}' appears more than once"));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            problems.Add(Invariant($"brief entry '{id}' has no prose"));
        }

        if (supportingItemIds.Count == 0)
        {
            problems.Add(Invariant($"brief entry '{id}' names no validated facts"));
        }

        HashSet<string> permitted = new(StringComparer.Ordinal);
        foreach (string itemId in supportingItemIds.Distinct(StringComparer.Ordinal))
        {
            if (!structuredIds.Contains(itemId) || !evidenceByItem.TryGetValue(itemId, out HashSet<string>? itemEvidence))
            {
                problems.Add(Invariant($"brief entry '{id}' refers to unknown fact '{itemId}'"));
                continue;
            }
            permitted.UnionWith(itemEvidence);
        }

        if (evidence.Count == 0)
        {
            problems.Add(Invariant($"brief entry '{id}' cites nothing"));
        }

        foreach (SummaryEvidence reference in evidence)
        {
            if (!permitted.Contains(reference.SegmentId))
            {
                problems.Add(Invariant(
                    $"brief entry '{id}' cites '{reference.SegmentId}', which does not support its named facts"));
            }
        }

        ValidateEvidence(id, evidence, allowed, summary, problems);
    }

    private static void ValidateNarrative(
        SummaryDocument summary,
        Dictionary<string, TranscriptSegment> allowed,
        HashSet<string> structuredIds,
        List<string> problems)
    {
        if (summary.SchemaVersion == 1)
        {
            return;
        }

        if (!summary.Model.ProducesSummaries)
        {
            if (summary.Narrative is not null)
            {
                problems.Add("a placeholder document must not claim a generated narrative");
            }
            return;
        }

        if (summary.Narrative is null || summary.Narrative.Summary.Count == 0)
        {
            // From v3 the brief is the final pass and the narrative is history. Demanding both
            // would make every new summary carry a second, near-identical document purely to
            // satisfy a check written when only one existed.
            if (summary.SchemaVersion < 3)
            {
                problems.Add("a schema-v2 model summary has no final narrative");
            }
            return;
        }

        Dictionary<string, HashSet<string>> evidenceByItem = EvidenceByItem(summary);

        HashSet<string> narrativeIds = new(StringComparer.Ordinal);
        foreach (SummaryNarrativeBlock block in summary.Narrative.AllBlocks)
        {
            if (!narrativeIds.Add(block.Id))
            {
                problems.Add(Invariant($"narrative block id '{block.Id}' appears more than once"));
            }
            if (string.IsNullOrWhiteSpace(block.Text))
            {
                problems.Add(Invariant($"narrative block '{block.Id}' has no prose"));
            }
            if (block.SupportingItemIds.Count == 0)
            {
                problems.Add(Invariant($"narrative block '{block.Id}' names no validated facts"));
            }

            HashSet<string> permittedEvidence = new(StringComparer.Ordinal);
            foreach (string itemId in block.SupportingItemIds.Distinct(StringComparer.Ordinal))
            {
                if (!structuredIds.Contains(itemId) || !evidenceByItem.TryGetValue(itemId, out HashSet<string>? itemEvidence))
                {
                    problems.Add(Invariant($"narrative block '{block.Id}' refers to unknown fact '{itemId}'"));
                    continue;
                }
                permittedEvidence.UnionWith(itemEvidence);
            }

            if (block.Evidence.Count == 0)
            {
                problems.Add(Invariant($"narrative block '{block.Id}' cites nothing"));
            }
            foreach (SummaryEvidence reference in block.Evidence)
            {
                if (!permittedEvidence.Contains(reference.SegmentId))
                {
                    problems.Add(Invariant(
                        $"narrative block '{block.Id}' cites '{reference.SegmentId}', which does not support its named facts"));
                }
            }
            ValidateEvidence(block.Id, block.Evidence, allowed, summary, problems);
        }
    }

    private static void ValidateItem(
        SummaryItem item,
        Dictionary<string, TranscriptSegment> allowed,
        SummaryDocument summary,
        HashSet<string> ids,
        List<string> problems)
    {
        if (!ids.Add(item.Id))
        {
            problems.Add(Invariant($"item id '{item.Id}' appears more than once"));
        }

        if (string.IsNullOrWhiteSpace(item.Text))
        {
            problems.Add(Invariant($"item '{item.Id}' has no text"));
        }

        if (!SupportStatuses.TryParse(item.Certainty, out SupportStatus support))
        {
            problems.Add(Invariant($"item '{item.Id}' has an unknown certainty '{item.Certainty}'"));
            return;
        }

        // Explicit means the transcript says so. Without a citation there is nothing that says so.
        if (support == SupportStatus.Explicit && item.Evidence.Count == 0)
        {
            problems.Add(Invariant($"item '{item.Id}' is explicit but cites nothing"));
        }

        if (item.Confidence is < 0 or > 1)
        {
            problems.Add(Invariant($"item '{item.Id}' has a confidence outside 0..1"));
        }

        ValidateEvidence(item.Id, item.Evidence, allowed, summary, problems);
    }

    private static void ValidateAction(
        SummaryAction action,
        Dictionary<string, TranscriptSegment> allowed,
        SummaryDocument summary,
        HashSet<string> ids,
        List<string> problems)
    {
        if (!ids.Add(action.Id))
        {
            problems.Add(Invariant($"item id '{action.Id}' appears more than once"));
        }

        if (string.IsNullOrWhiteSpace(action.Task))
        {
            problems.Add(Invariant($"action '{action.Id}' has no task"));
        }

        if (action.Evidence.Count == 0)
        {
            problems.Add(Invariant($"action '{action.Id}' cites nothing"));
        }

        if (!SupportStatuses.TryParse(action.Certainty, out _))
        {
            problems.Add(Invariant($"action '{action.Id}' has an unknown certainty '{action.Certainty}'"));
        }

        if (!SupportStatuses.TryParse(action.OwnerStatus, out SupportStatus owner))
        {
            problems.Add(Invariant($"action '{action.Id}' has an unknown owner status '{action.OwnerStatus}'"));
        }
        else
        {
            // There is no such thing as an unknown owner with a name.
            if (owner == SupportStatus.Unknown && action.Owner is not null)
            {
                problems.Add(Invariant($"action '{action.Id}' has an unknown owner but names one anyway"));
            }

            if (owner != SupportStatus.Unknown && string.IsNullOrWhiteSpace(action.Owner))
            {
                problems.Add(Invariant($"action '{action.Id}' claims a {action.OwnerStatus} owner but names nobody"));
            }
        }

        if (!SupportStatuses.TryParse(action.DueDateStatus, out SupportStatus due))
        {
            problems.Add(Invariant($"action '{action.Id}' has an unknown due-date status '{action.DueDateStatus}'"));
        }
        else
        {
            if (due == SupportStatus.Unknown && action.DueDate is not null)
            {
                problems.Add(Invariant($"action '{action.Id}' has an unknown due date but names one anyway"));
            }

            if (due != SupportStatus.Unknown && string.IsNullOrWhiteSpace(action.DueDate))
            {
                problems.Add(Invariant($"action '{action.Id}' claims a {action.DueDateStatus} due date but gives none"));
            }

            if (action.DueDate is { } date && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                problems.Add(Invariant($"action '{action.Id}' has a due date that is not an ISO calendar date"));
            }

            // A date can only be resolved against a meeting date. Without one, "Friday" names no
            // particular day, and emitting a calendar date for it would be a guess wearing the
            // clothes of a fact.
            if (due != SupportStatus.Unknown && action.DueDate is not null && summary.MeetingDate is null)
            {
                problems.Add(Invariant(
                    $"action '{action.Id}' resolves a due date, but the meeting date is unknown so nothing could be resolved against it"));
            }
        }

        if (action.Confidence is < 0 or > 1)
        {
            problems.Add(Invariant($"action '{action.Id}' has a confidence outside 0..1"));
        }

        ValidateEvidence(action.Id, action.Evidence, allowed, summary, problems);
    }

    /// <summary>
    /// Every citation must resolve, in the exact revision it names, with times taken from the
    /// segment rather than from the model.
    /// </summary>
    private static void ValidateEvidence(
        string itemId,
        IReadOnlyList<SummaryEvidence> evidence,
        Dictionary<string, TranscriptSegment> allowed,
        SummaryDocument summary,
        List<string> problems)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (SummaryEvidence reference in evidence)
        {
            if (reference.TranscriptRevision != summary.TranscriptRevision)
            {
                problems.Add(Invariant(
                    $"'{itemId}' cites revision {reference.TranscriptRevision}, but this summary was written from {summary.TranscriptRevision}"));
                continue;
            }

            if (!allowed.TryGetValue(reference.SegmentId, out TranscriptSegment? segment))
            {
                problems.Add(Invariant($"'{itemId}' cites '{reference.SegmentId}', which is not in that transcript revision"));
                continue;
            }

            if (!seen.Add(reference.SegmentId))
            {
                problems.Add(Invariant($"'{itemId}' cites '{reference.SegmentId}' twice"));
            }

            if (!string.Equals(reference.SourceTrack, segment.SourceTrack, StringComparison.Ordinal))
            {
                problems.Add(Invariant($"'{itemId}' cites '{reference.SegmentId}' as the wrong track"));
            }

            // Derived, not trusted. A model that mis-states a timestamp would send the reader to
            // the wrong audio and look authoritative doing it.
            if (Math.Abs(reference.StartSeconds - segment.StartSeconds) > Tolerance ||
                Math.Abs(reference.EndSeconds - segment.EndSeconds) > Tolerance)
            {
                problems.Add(Invariant($"'{itemId}' cites times for '{reference.SegmentId}' that are not the segment's own"));
            }

            string expected = SummaryEvidence.Display(segment.StartSeconds);
            if (!string.Equals(reference.DisplayTimestamp, expected, StringComparison.Ordinal))
            {
                problems.Add(Invariant($"'{itemId}' shows a timestamp for '{reference.SegmentId}' that is not derived from it"));
            }
        }
    }

    /// <summary>
    /// Builds a citation from the transcript itself, which is the only supported way to make one.
    ///
    /// <para>
    /// Callers give a segment ID; everything else comes from the segment. There is deliberately no
    /// path that lets a caller supply the times.
    /// </para>
    /// </summary>
    public static SummaryEvidence? Cite(TranscriptDocument transcript, string segmentId)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        TranscriptSegment? segment = transcript.Segments.FirstOrDefault(s => s.Id == segmentId);
        if (segment is null)
        {
            return null;
        }

        return new SummaryEvidence
        {
            TranscriptRevision = transcript.TranscriptRevision,
            SegmentId = segment.Id,
            SourceTrack = segment.SourceTrack,
            StartSeconds = segment.StartSeconds,
            EndSeconds = segment.EndSeconds,
            DisplayTimestamp = SummaryEvidence.Display(segment.StartSeconds),
        };
    }

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
