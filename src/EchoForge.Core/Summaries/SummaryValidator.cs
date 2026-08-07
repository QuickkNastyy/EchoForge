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

        if (summary.SchemaVersion != 1)
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

        // Decisions are what people act on afterwards, so an uncited one is never acceptable
        // however certain the model claimed to be.
        foreach (SummaryItem decision in summary.Decisions)
        {
            if (decision.Evidence.Count == 0)
            {
                problems.Add(Invariant($"decision '{decision.Id}' cites nothing"));
            }
        }

        return problems.Count == 0 ? SummaryVerdict.Valid : new SummaryVerdict(problems);
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
