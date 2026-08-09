using EchoForge.Contracts.Transcripts;

namespace EchoForge.Contracts.Processing;

/// <summary>
/// One attempt at producing a transcript. The revision number is allocated up front and never
/// reused, so an abandoned staging file can always be traced back to the attempt that wrote it.
/// </summary>
public sealed record TranscriptionAttempt(
    string SessionId,
    string JobId,
    int Revision,
    string StagingPath,
    string SourceManifestSha256);

/// <summary>What a completed attempt is asking to have activated.</summary>
public sealed record ActivationRequest(
    TranscriptionAttempt Attempt,
    string ExpectedSha256,
    TranscriptDocument Transcript,
    int ProtocolVersion,
    string? Profile,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// The result of trying to activate. A refusal names its reason and leaves the previously
/// selected revision exactly where it was.
/// </summary>
public sealed record ActivationOutcome(TranscriptRevisionRecord? Revision, string? Refusal)
{
    public bool Activated => Revision is not null;

    public static ActivationOutcome Refuse(string reason) => new(null, reason);
}

/// <summary>
/// Durable storage for transcription: revisions, which one is selected, and the state of the
/// attempt in flight.
///
/// <para>
/// The journal is the authority, exactly as it is for recording. A transcript file that no
/// <c>transcription_activated</c> event vouches for is not a revision, and a revision whose file
/// has gone is not selectable. Neither half is sufficient alone.
/// </para>
/// </summary>
public interface ITranscriptionStore
{
    /// <summary>
    /// Reconstructs everything known about a session's transcription from the journal, then
    /// checks it against the files that are actually present.
    /// </summary>
    TranscriptionState Read(string sessionId);

    /// <summary>
    /// Allocates the next revision number and records the attempt as queued.
    ///
    /// <para>
    /// This happens before any work starts, so a crash cannot cause the next attempt to reuse the
    /// number and overwrite a staging file it did not write.
    /// </para>
    /// </summary>
    TranscriptionAttempt BeginAttempt(
        string sessionId,
        string jobId,
        string sourceManifestSha256,
        TranscriptionOptions options,
        DateTimeOffset now);

    void MarkStarted(TranscriptionAttempt attempt, TranscriptionOptions options, DateTimeOffset now);

    /// <summary>Updates the projection only. Progress is never journalled.</summary>
    void RecordProgress(TranscriptionAttempt attempt, string stage, int completedUnits, int totalUnits);

    /// <summary>
    /// Verifies the staged bytes, moves them into place atomically, and only then journals the
    /// activation. A failure at any point leaves the previous revision selected.
    /// </summary>
    ActivationOutcome Activate(ActivationRequest request, DateTimeOffset now);

    void MarkFailed(TranscriptionAttempt attempt, string failureCode, string failureSummary, DateTimeOffset now);

    void MarkCancelled(TranscriptionAttempt attempt, DateTimeOffset now);

    /// <summary>Chooses an existing activated revision as the selected one.</summary>
    bool SelectRevision(string sessionId, int revision, DateTimeOffset now);

    /// <summary>
    /// Removes staged transcripts that no activation vouches for. Called at startup: a staging
    /// file is the visible remains of an attempt that died, and keeping it would eventually make
    /// an unverified file look like a revision.
    /// </summary>
    int DiscardOrphanStaging(string sessionId, DateTimeOffset now);

    /// <summary>
    /// Reads an activated revision, verifying the bytes against the digest recorded when it was
    /// activated. Returns null if it is missing or does not match.
    /// </summary>
    TranscriptDocument? ReadTranscript(string sessionId, int revision);

    /// <summary>The absolute path of an activated revision, whether or not it currently exists.</summary>
    string RevisionPath(string sessionId, int revision);
}
