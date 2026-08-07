using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EchoForge.Contracts.Evaluation;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Core.Evaluation;

/// <summary>The verdict on a corpus. Empty problems means it may be scored against.</summary>
public sealed record CorpusVerdict(IReadOnlyList<string> Problems)
{
    public static readonly CorpusVerdict Valid = new([]);

    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Checks a corpus before anything is measured against it.
///
/// <para>
/// A benchmark is only as trustworthy as the data behind it, and the ways that data goes wrong are
/// mostly silent: a meeting quietly appearing in both the development and the held-out sets, gold
/// evidence pointing at a segment that does not exist, a written fixture filed as a real meeting.
/// None of those announce themselves in a score — they just make the score mean something other
/// than what it is quoted as meaning.
/// </para>
/// </summary>
public static class CorpusValidator
{
    /// <summary>Validates one corpus on its own.</summary>
    public static CorpusVerdict Validate(SummaryCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        List<string> problems = [];

        if (corpus.SchemaVersion != SummaryCorpus.CurrentSchemaVersion)
        {
            problems.Add(Invariant($"schema_version {corpus.SchemaVersion} is not supported"));
        }

        if (string.IsNullOrWhiteSpace(corpus.CorpusId))
        {
            problems.Add("the corpus has no id");
        }

        if (corpus.MatchThreshold is < 0 or > 1)
        {
            problems.Add(Invariant($"match_threshold {corpus.MatchThreshold} is not between 0 and 1"));
        }

        if (corpus.Meetings.Count == 0)
        {
            problems.Add("the corpus contains no meetings");
        }

        HashSet<string> meetingIds = new(StringComparer.Ordinal);

        foreach (CorpusMeeting meeting in corpus.Meetings)
        {
            string id = meeting.MeetingId;

            if (string.IsNullOrWhiteSpace(id))
            {
                problems.Add("a meeting has no id");
                continue;
            }

            if (!meetingIds.Add(id))
            {
                problems.Add(Invariant($"meeting id '{id}' appears more than once"));
            }

            // A written fixture filed as real data would make a synthetic score readable as
            // evidence about a model. Refused outright rather than flagged.
            if (meeting.Synthetic && corpus.Kind != CorpusKind.Synthetic)
            {
                problems.Add(Invariant(
                    $"meeting '{id}' is marked synthetic but sits in a {corpus.Kind.ToString().ToLowerInvariant()} corpus"));
            }

            if (!meeting.Synthetic && corpus.Kind == CorpusKind.Synthetic)
            {
                problems.Add(Invariant($"meeting '{id}' is in a synthetic corpus but is not marked synthetic"));
            }

            // Summary quality is never scored against raw recogniser output. A summariser must
            // not be marked down for a word the recogniser got wrong; that is what the separate
            // STT evaluation measures.
            if (corpus.Kind != CorpusKind.Synthetic && meeting.TranscriptFidelity != TranscriptFidelity.HumanCorrected)
            {
                problems.Add(Invariant(
                    $"meeting '{id}' is scored against a transcript nobody has corrected; summary quality may only be measured on a human-corrected transcript"));
            }

            if (string.IsNullOrWhiteSpace(meeting.TranscriptPath))
            {
                problems.Add(Invariant($"meeting '{id}' names no transcript"));
            }

            ValidateGold(id, meeting, problems);
        }

        return problems.Count == 0 ? CorpusVerdict.Valid : new CorpusVerdict(problems);
    }

    private static void ValidateGold(string meetingId, CorpusMeeting meeting, List<string> problems)
    {
        HashSet<string> itemIds = new(StringComparer.Ordinal);

        foreach (GoldItem item in meeting.Gold.AllItems)
        {
            if (!itemIds.Add(item.Id))
            {
                problems.Add(Invariant($"{meetingId}: gold id '{item.Id}' appears more than once"));
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                problems.Add(Invariant($"{meetingId}: gold item '{item.Id}' has no text"));
            }
        }

        foreach (GoldAction action in meeting.Gold.ActionItems)
        {
            if (!itemIds.Add(action.Id))
            {
                problems.Add(Invariant($"{meetingId}: gold id '{action.Id}' appears more than once"));
            }

            if (string.IsNullOrWhiteSpace(action.Task))
            {
                problems.Add(Invariant($"{meetingId}: gold action '{action.Id}' has no task"));
            }

            // Evidence anchors every match, so a gold decision or action with none can never be
            // matched by anything and would count as a permanent, unfixable miss.
            if (action.Evidence.Count == 0)
            {
                problems.Add(Invariant($"{meetingId}: gold action '{action.Id}' cites no evidence"));
            }

            ValidateSupport(meetingId, action.Id, "owner", action.Owner, action.OwnerStatus, problems);
            ValidateSupport(meetingId, action.Id, "due date", action.DueDate, action.DueDateStatus, problems);

            if (action.DueDate is { } due &&
                !DateOnly.TryParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                problems.Add(Invariant($"{meetingId}: gold action '{action.Id}' has a due date that is not an ISO calendar date"));
            }
        }

        foreach (GoldItem decision in meeting.Gold.Decisions.Where(d => d.Evidence.Count == 0))
        {
            problems.Add(Invariant($"{meetingId}: gold decision '{decision.Id}' cites no evidence"));
        }

        foreach (GoldContradiction contradiction in meeting.Gold.Contradictions)
        {
            if (contradiction.ItemIds.Count < 2)
            {
                problems.Add(Invariant($"{meetingId}: contradiction '{contradiction.Id}' names fewer than two facts"));
            }

            foreach (string member in contradiction.ItemIds.Where(m => !itemIds.Contains(m)))
            {
                problems.Add(Invariant($"{meetingId}: contradiction '{contradiction.Id}' names '{member}', which is not a gold fact"));
            }
        }
    }

    /// <summary>The same owner/date invariants the summary validator enforces, applied to the gold.</summary>
    private static void ValidateSupport(
        string meetingId,
        string actionId,
        string what,
        string? value,
        string status,
        List<string> problems)
    {
        if (!SupportStatuses.TryParse(status, out SupportStatus parsed))
        {
            problems.Add(Invariant($"{meetingId}: gold action '{actionId}' has an unknown {what} status '{status}'"));
            return;
        }

        // There is no such thing as an unknown owner with a name — in a summary or in the gold
        // it is scored against. An annotation that broke this would score a correct refusal wrong.
        if (parsed == SupportStatus.Unknown && !string.IsNullOrWhiteSpace(value))
        {
            problems.Add(Invariant($"{meetingId}: gold action '{actionId}' has an unknown {what} but names one anyway"));
        }

        if (parsed != SupportStatus.Unknown && string.IsNullOrWhiteSpace(value))
        {
            problems.Add(Invariant($"{meetingId}: gold action '{actionId}' claims a {status} {what} but gives none"));
        }
    }

    /// <summary>
    /// Checks that the held-out set really is held out.
    ///
    /// <para>
    /// Matched on meeting id <b>and</b> on transcript digest, because the failure this prevents is
    /// not usually somebody copying a file — it is the same meeting arriving twice under two names
    /// after being re-exported. A release score computed over a meeting the prompts were tuned
    /// against is not a held-out score, and nothing downstream can detect that afterwards.
    /// </para>
    /// </summary>
    public static CorpusVerdict ValidateSeparation(SummaryCorpus development, SummaryCorpus release)
    {
        ArgumentNullException.ThrowIfNull(development);
        ArgumentNullException.ThrowIfNull(release);

        List<string> problems = [];

        foreach (string shared in development.Meetings.Select(m => m.MeetingId)
                     .Intersect(release.Meetings.Select(m => m.MeetingId), StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            problems.Add(Invariant($"meeting '{shared}' is in both the development and the release corpus"));
        }

        Dictionary<string, string> releaseDigests = new(StringComparer.Ordinal);
        foreach (CorpusMeeting meeting in release.Meetings.Where(m => !string.IsNullOrWhiteSpace(m.TranscriptSha256)))
        {
            releaseDigests[meeting.TranscriptSha256!] = meeting.MeetingId;
        }

        foreach (CorpusMeeting meeting in development.Meetings.Where(m => !string.IsNullOrWhiteSpace(m.TranscriptSha256)))
        {
            if (releaseDigests.TryGetValue(meeting.TranscriptSha256!, out string? releaseId))
            {
                problems.Add(Invariant(
                    $"development meeting '{meeting.MeetingId}' and release meeting '{releaseId}' are the same transcript under two names"));
            }
        }

        return problems.Count == 0 ? CorpusVerdict.Valid : new CorpusVerdict(problems);
    }

    /// <summary>
    /// Checks a meeting's gold against the transcript it will actually be scored on.
    ///
    /// <para>
    /// Gold evidence naming a segment the transcript does not contain makes a fact unmatchable,
    /// which shows up as a model failing to find something no model could have found.
    /// </para>
    /// </summary>
    public static CorpusVerdict ValidateAgainstTranscript(CorpusMeeting meeting, TranscriptDocument transcript)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentNullException.ThrowIfNull(transcript);

        List<string> problems = [];
        HashSet<string> segments = new(transcript.Segments.Select(s => s.Id), StringComparer.Ordinal);

        void Check(string id, IReadOnlyList<string> evidence)
        {
            foreach (string segment in evidence.Where(e => !segments.Contains(e)))
            {
                problems.Add(Invariant($"{meeting.MeetingId}: gold '{id}' cites '{segment}', which is not in that transcript"));
            }
        }

        foreach (GoldItem item in meeting.Gold.AllItems)
        {
            Check(item.Id, item.Evidence);
        }

        foreach (GoldAction action in meeting.Gold.ActionItems)
        {
            Check(action.Id, action.Evidence);
        }

        return problems.Count == 0 ? CorpusVerdict.Valid : new CorpusVerdict(problems);
    }

    /// <summary>
    /// A stable digest of everything that would change a score.
    ///
    /// <para>
    /// Covers the gold facts and the corpus identity, and deliberately not the notes: an annotator
    /// clarifying why they judged something should not invalidate a completed evaluation, and a
    /// changed fact must.
    /// </para>
    /// </summary>
    public static string Fingerprint(SummaryCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        StringBuilder builder = new();
        builder.Append(corpus.CorpusId).Append('');
        builder.Append(corpus.Kind.ToString()).Append('');
        builder.Append(corpus.MatchThreshold.ToString("R", CultureInfo.InvariantCulture)).Append('');

        foreach (CorpusMeeting meeting in corpus.Meetings.OrderBy(m => m.MeetingId, StringComparer.Ordinal))
        {
            builder.Append(meeting.MeetingId).Append('');
            builder.Append(meeting.MeetingDate ?? string.Empty).Append('');
            builder.Append(meeting.TranscriptSha256 ?? string.Empty).Append('');

            foreach (GoldItem item in meeting.Gold.AllItems.OrderBy(i => i.Id, StringComparer.Ordinal))
            {
                builder.Append(item.Id).Append('=').Append(item.Text).Append('|');
                builder.AppendJoin(',', item.Evidence.OrderBy(e => e, StringComparer.Ordinal));
                builder.Append('|');
                builder.AppendJoin(',', item.Aliases);
                builder.Append('');
            }

            foreach (GoldAction action in meeting.Gold.ActionItems.OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                builder.Append(action.Id).Append('=').Append(action.Task).Append('|');
                builder.AppendJoin(',', action.Evidence.OrderBy(e => e, StringComparer.Ordinal));
                builder.Append('|').Append(action.Owner ?? string.Empty).Append('/').Append(action.OwnerStatus);
                builder.Append('|').Append(action.DueDate ?? string.Empty).Append('/').Append(action.DueDateStatus);
                builder.Append('|');
                builder.AppendJoin(',', action.Aliases);
                builder.Append('');
            }

            foreach (GoldContradiction contradiction in meeting.Gold.Contradictions.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                builder.Append(contradiction.Id).Append('=');
                builder.AppendJoin(',', contradiction.ItemIds.OrderBy(i => i, StringComparer.Ordinal));
                builder.Append('');
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Reads a corpus file. A corpus that will not parse permits nothing.</summary>
    public static SummaryCorpus? TryLoad(string path, out IReadOnlyList<string> problems)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            SummaryCorpus? corpus = JsonSerializer.Deserialize<SummaryCorpus>(stream, SummaryCorpus.Json);

            if (corpus is null)
            {
                problems = ["the corpus file was empty"];
                return null;
            }

            CorpusVerdict verdict = Validate(corpus);
            problems = verdict.Problems;
            return verdict.IsValid ? corpus : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problems = [$"the corpus could not be read ({ex.GetType().Name}): {ex.Message}"];
            return null;
        }
    }

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
