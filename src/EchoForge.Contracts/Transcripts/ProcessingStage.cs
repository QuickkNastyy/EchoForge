namespace EchoForge.Contracts.Transcripts;

/// <summary>
/// Where one processing stage has reached.
///
/// <para>
/// This is deliberately a second, independent state machine. <c>SessionState</c> describes what
/// the recorder is doing; this describes what transcription is doing. Compressing the two into
/// one enum is exactly the fragility the plan forbids: a session can be <c>Recorded</c> while
/// transcription is <c>Running</c>, <c>Failed</c>, and <c>Running</c> again, all without the
/// recording state meaning anything different.
/// </para>
/// </summary>
public enum ProcessingStageState
{
    /// <summary>Nobody has asked for it. The default, and not a failure.</summary>
    NotRequested,

    /// <summary>Asked for, but waiting. Recording has priority over every processing job.</summary>
    Queued,

    Running,
    Succeeded,

    /// <summary>Only this stage failed. Audio and every previously activated revision survive.</summary>
    Failed,

    Cancelled,
}

/// <summary>
/// The transcription stage of one session: its state, which input it ran against, and which
/// output revision it produced.
///
/// <para>
/// The input digest is kept beside the revision so a stale result is detectable rather than
/// assumed fresh. A run that fails or is cancelled leaves <see cref="Revision"/> pointing at the
/// last revision that actually succeeded, because a failed run must never retract a good one.
/// </para>
/// </summary>
public sealed record TranscriptionStage(
    ProcessingStageState State = ProcessingStageState.NotRequested,
    int? Revision = null,
    string? InputManifestSha256 = null,
    string? FailureCode = null,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null)
{
    public static readonly TranscriptionStage NotRequested = new();

    public bool IsTerminal => State
        is ProcessingStageState.Succeeded
        or ProcessingStageState.Failed
        or ProcessingStageState.Cancelled
        or ProcessingStageState.NotRequested;

    /// <summary>True while a worker may be alive for this stage.</summary>
    public bool IsActive => State is ProcessingStageState.Queued or ProcessingStageState.Running;

    public TranscriptionStage Queue() => this with
    {
        State = ProcessingStageState.Queued,
        FailureCode = null,
    };

    public TranscriptionStage Run(DateTimeOffset startedUtc, string? inputManifestSha256) => this with
    {
        State = ProcessingStageState.Running,
        StartedUtc = startedUtc,
        CompletedUtc = null,
        FailureCode = null,
        InputManifestSha256 = inputManifestSha256,
    };

    public TranscriptionStage Succeed(int revision, DateTimeOffset completedUtc) => this with
    {
        State = ProcessingStageState.Succeeded,
        Revision = revision,
        CompletedUtc = completedUtc,
        FailureCode = null,
    };

    /// <summary>A failure records why, and leaves the last good revision selected.</summary>
    public TranscriptionStage Fail(string failureCode, DateTimeOffset completedUtc) => this with
    {
        State = ProcessingStageState.Failed,
        CompletedUtc = completedUtc,
        FailureCode = failureCode,
    };

    public TranscriptionStage Cancel(DateTimeOffset completedUtc) => this with
    {
        State = ProcessingStageState.Cancelled,
        CompletedUtc = completedUtc,
        FailureCode = null,
    };
}
