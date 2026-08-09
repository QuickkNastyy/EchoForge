using System.Globalization;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Library;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.Infrastructure.Library;

/// <summary>One meeting's indexable text, alongside the entry that describes it.</summary>
public sealed record LibraryDocument(
    LibraryEntry Entry,
    TranscriptDocument? Transcript,
    SummaryDocument? Summary);

/// <summary>
/// Reads session folders and produces what the library shows.
///
/// <para>
/// <b>This is the authority.</b> Everything the index later serves is computed here, from the
/// journal, the projections and the revision files — so the database can be deleted at any moment
/// and rebuilt from what is still on disk. Nothing in the library is stored only in SQLite, and a
/// disagreement between the two is always resolved by re-reading the files.
/// </para>
///
/// <para>
/// A session that cannot be read is <b>included and marked</b> rather than skipped. A library
/// that silently omits a broken meeting is indistinguishable from one that lost it, and the
/// person who needs to know is exactly the person looking at the list.
/// </para>
/// </summary>
public sealed class LibraryProjection(
    ISessionStore sessions,
    ITranscriptionStore transcripts,
    FileSummaryStore summaries,
    FileSpeakerAliasStore aliases,
    FileMeetingTitleStore? titles = null)
{
    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly ITranscriptionStore _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
    private readonly FileSummaryStore _summaries = summaries ?? throw new ArgumentNullException(nameof(summaries));
    private readonly FileSpeakerAliasStore _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
    private readonly FileMeetingTitleStore? _titles = titles;

    /// <summary>Every session on disk, newest first.</summary>
    public IReadOnlyList<string> SessionIds() => _sessions.EnumerateSessions();

    /// <summary>Builds one meeting's entry and loads the text worth searching.</summary>
    public LibraryDocument? Build(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        SessionSnapshot? snapshot;
        try
        {
            snapshot = _sessions.ReadSnapshot(sessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            snapshot = null;
        }

        if (snapshot is null)
        {
            // A folder with no readable snapshot is not a meeting yet. It is left out rather
            // than shown as an empty row, because there is nothing to open.
            return null;
        }

        SpeakerAliases overlay = _aliases.Read(sessionId);

        TranscriptionState transcription = SafeRead(() => _transcripts.Read(sessionId), TranscriptionState.Empty);
        SummaryState summaryState = SafeRead(() => _summaries.Read(sessionId), SummaryState.Empty);

        TranscriptDocument? transcript = null;
        if (transcription.SelectedRevision is { } transcriptRevision)
        {
            transcript = SafeRead(() => _transcripts.ReadTranscript(sessionId, transcriptRevision), null);
        }

        SummaryDocument? summary = null;
        if (summaryState.SelectedRevision is { } summaryRevision)
        {
            summary = SafeRead(() => _summaries.ReadSummary(sessionId, summaryRevision), null);
        }

        (bool needsAttention, string? reason) = Attention(snapshot, transcription, summaryState, transcript, summary);

        LibraryEntry entry = new()
        {
            SessionId = sessionId,
            Title = Title(snapshot, _titles?.Read(sessionId)),
            CreatedUtc = snapshot.CreatedUtc,
            StartedUtc = snapshot.StartedUtc,
            Duration = snapshot.Duration,
            State = snapshot.State,
            HasAudio = snapshot.HasAudio,

            HasTranscript = transcription.HasTranscript,
            SelectedTranscriptRevision = transcription.SelectedRevision,
            TranscriptRevisions = [.. transcription.Revisions.Where(r => r.FileExists).Select(r => r.Revision).Order()],
            TranscriptBackend = transcription.Selected?.Backend ?? string.Empty,
            TranscriptModelId = transcription.Selected?.ModelId ?? string.Empty,
            TranscriptProfile = transcription.Selected?.Profile,
            SegmentCount = transcription.Selected?.SegmentCount ?? 0,

            HasSummary = summaryState.HasSummary,
            SelectedSummaryRevision = summaryState.SelectedRevision,
            SummaryRevisions = [.. summaryState.Revisions.Where(r => r.FileExists).Select(r => r.Revision).Order()],
            SummaryBackend = summaryState.Selected?.Backend ?? string.Empty,
            SummaryModelId = summaryState.Selected?.ModelId ?? string.Empty,
            SummaryProfile = summaryState.Selected?.Runtime,
            SummarySourceTranscriptRevision = summaryState.Selected?.TranscriptRevision,
            SummaryProducesSummaries = summaryState.Selected?.ProducesSummaries ?? false,

            NeedsAttention = needsAttention,
            AttentionReason = reason,
            Aliases = overlay,
        };

        return new LibraryDocument(entry, transcript, summary);
    }

    /// <summary>
    /// A title a person recognises. The session's own if it has one, otherwise the date.
    ///
    /// <para>
    /// Never the session ID alone. A list of ULIDs is a list nobody can read, and the date is the
    /// thing people actually remember a meeting by.
    /// </para>
    /// </summary>
    private static string Title(SessionSnapshot snapshot, string? displayTitle)
    {
        if (!string.IsNullOrWhiteSpace(displayTitle))
        {
            return displayTitle!;
        }

        // Legacy snapshots may already have a title. New user renames live outside session.json so
        // recovery remains free to rebuild the snapshot from the journal.
        if (!string.IsNullOrWhiteSpace(snapshot.Title))
        {
            return snapshot.Title!;
        }

        DateTimeOffset when = (snapshot.StartedUtc ?? snapshot.CreatedUtc).ToLocalTime();
        return when.ToString("ddd, MMM d, yyyy · h:mm tt", CultureInfo.CurrentCulture);
    }

    /// <summary>What, if anything, a person should look at.</summary>
    private static (bool NeedsAttention, string? Reason) Attention(
        SessionSnapshot snapshot,
        TranscriptionState transcription,
        SummaryState summaries,
        TranscriptDocument? transcript,
        SummaryDocument? summary)
    {
        if (snapshot.State is SessionState.NeedsAttention or SessionState.Failed)
        {
            return (true, "This recording did not finish cleanly.");
        }

        if (snapshot.State is SessionState.Recording or SessionState.Paused or SessionState.Finalizing)
        {
            return (true, "This recording was interrupted and never closed.");
        }

        // A selected revision whose file will not load is the case the index must never paper
        // over: the journal says it was activated, and the bytes are not there.
        if (transcription.SelectedRevision is not null && transcript is null)
        {
            return (true, "The selected transcript version could not be read.");
        }

        if (summaries.SelectedRevision is not null && summary is null)
        {
            return (true, "The selected summary version could not be read.");
        }

        // A revision the journal says was activated whose file is no longer there. The stores
        // quietly fall back to whatever still exists, which is the right behaviour for them and
        // the wrong thing for a library to stay silent about: without this, a meeting that lost
        // its only transcript is indistinguishable from one that was never transcribed.
        if (transcription.Revisions.Any(r => !r.FileExists))
        {
            return (true, "A transcript version recorded in this session's history is missing from disk.");
        }

        if (summaries.Revisions.Any(r => !r.FileExists))
        {
            return (true, "A summary version recorded in this session's history is missing from disk.");
        }

        if (transcription.Stage == ProcessingStageState.Failed && !transcription.HasTranscript)
        {
            return (true, "Transcribing failed and there is no transcript yet.");
        }

        if (snapshot.State == SessionState.Recorded && !snapshot.HasAudio)
        {
            return (true, "This session has no recorded audio.");
        }

        return (false, null);
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return fallback;
        }
    }
}
