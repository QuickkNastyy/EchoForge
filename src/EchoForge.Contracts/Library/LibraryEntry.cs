using EchoForge.Contracts.Sessions;

namespace EchoForge.Contracts.Library;

/// <summary>
/// One meeting, as the library shows it.
///
/// <para>
/// Every field here is <b>derived</b>. This record is a projection of the session folder — the
/// journal, the snapshot, the transcript revisions and the summary revisions — and it can be
/// thrown away and rebuilt at any moment without losing anything. That is the property the whole
/// index depends on: SQLite is a cache of what the files already say, and if the two ever
/// disagree the files are right.
/// </para>
/// </summary>
public sealed record LibraryEntry
{
    public required string SessionId { get; init; }

    /// <summary>What to call it. Falls back to the date when the session has no title.</summary>
    public required string Title { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    public TimeSpan Duration { get; init; }

    public SessionState State { get; init; }

    public bool HasAudio { get; init; }

    // -- transcript ----------------------------------------------------------------------------

    public bool HasTranscript { get; init; }

    public int? SelectedTranscriptRevision { get; init; }

    /// <summary>Every activated revision that is still on disk, ascending.</summary>
    public IReadOnlyList<int> TranscriptRevisions { get; init; } = [];

    public string TranscriptBackend { get; init; } = string.Empty;

    public string TranscriptModelId { get; init; } = string.Empty;

    public string? TranscriptProfile { get; init; }

    public int SegmentCount { get; init; }

    // -- summary -------------------------------------------------------------------------------

    public bool HasSummary { get; init; }

    public int? SelectedSummaryRevision { get; init; }

    public IReadOnlyList<int> SummaryRevisions { get; init; } = [];

    public string SummaryBackend { get; init; } = string.Empty;

    public string SummaryModelId { get; init; } = string.Empty;

    public string? SummaryProfile { get; init; }

    /// <summary>Which transcript revision the selected summary was generated from.</summary>
    public int? SummarySourceTranscriptRevision { get; init; }

    /// <summary>
    /// True when the selected summary came from a transcript revision that is no longer the
    /// selected one.
    ///
    /// <para>
    /// Stale is a presentation fact, never a storage one. The summary stays exactly as it was and
    /// remains fully readable, because its evidence still resolves against its own revision.
    /// </para>
    /// </summary>
    public bool SummaryIsStale =>
        HasSummary && SelectedTranscriptRevision is { } selected &&
        SummarySourceTranscriptRevision is { } source && selected != source;

    /// <summary>False when the summary backend produced quotes rather than a summary.</summary>
    public bool SummaryProducesSummaries { get; init; }

    // -- attention -----------------------------------------------------------------------------

    /// <summary>
    /// Something a person should look at: a session that never finished, a transcript that
    /// failed, a revision file that has gone missing. Surfaced rather than hidden, because a
    /// library that silently omits a broken meeting looks like one that lost it.
    /// </summary>
    public bool NeedsAttention { get; init; }

    public string? AttentionReason { get; init; }

    /// <summary>Presentation-only remote speaker names. Never written into a transcript.</summary>
    public SpeakerAliases Aliases { get; init; } = SpeakerAliases.None;
}

/// <summary>
/// Presentation-only names for remote speakers.
///
/// <para>
/// An overlay and nothing more. The transcript revision it applies to is immutable and stays
/// immutable: renaming changes what a reader sees, never what the evidence points at, and never
/// the bytes a revision was activated under. Reverting is therefore always possible, because
/// nothing was overwritten to begin with.
/// </para>
///
/// <para>
/// <b>You is never aliasable.</b> Attribution to the microphone track is the one thing EchoForge
/// knows for certain rather than infers, and letting it be renamed would turn the only reliable
/// speaker label into another guess.
/// </para>
/// </summary>
public sealed record SpeakerAliases
{
    public static readonly SpeakerAliases None = new();

    /// <summary>Original speaker ID to the name a reader should see.</summary>
    public IReadOnlyDictionary<string, string> BySpeakerId { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool IsEmpty => BySpeakerId.Count == 0;

    /// <summary>The name to show for a speaker, which is the original unless one was chosen.</summary>
    public string Present(string speakerId, string originalName) =>
        BySpeakerId.TryGetValue(speakerId, out string? alias) && !string.IsNullOrWhiteSpace(alias)
            ? alias
            : originalName;
}
