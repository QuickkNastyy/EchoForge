using System.Globalization;

namespace EchoForge.Contracts.Workers;

/// <summary>
/// How a worker run ended.
///
/// <para>
/// The classes are kept apart because the host must react to them differently. A protocol error
/// means the worker build and the host build disagree and retrying will not help. A worker
/// failure may be worth retrying. A timeout is the host's own verdict on a silent child. A
/// cancellation is a user decision and not a fault at all. Collapsing these into "failed" is how
/// a version mismatch ends up looking like a flaky model.
/// </para>
/// </summary>
public enum WorkerOutcome
{
    Succeeded,

    /// <summary>The worker reported a structured error.</summary>
    Failed,

    /// <summary>The user cancelled, and the worker acknowledged.</summary>
    Cancelled,

    /// <summary>The host gave up waiting. The child tree was terminated.</summary>
    TimedOut,

    /// <summary>The worker said something this host cannot make sense of.</summary>
    ProtocolError,

    /// <summary>The child died, or exited without a terminal message.</summary>
    Crashed,

    /// <summary>The worker could not be launched at all.</summary>
    LaunchFailed,

    /// <summary>
    /// Refused before launch because capture is live. Recording always has priority; this is a
    /// clean answer, not an error.
    /// </summary>
    Busy,
}

/// <summary>A structured failure, as reported by the worker or as diagnosed by the host.</summary>
/// <param name="Detail">
/// Technical diagnostic for the log. It may name paths, exception types, and exit codes, so it is
/// never rendered into user-facing text.
/// </param>
public sealed record WorkerError(
    string Code,
    WorkerStage Stage,
    string? Detail = null,
    bool Retryable = false);

/// <summary>
/// What the worker said about itself during the handshake.
///
/// <para>
/// This is the honest answer to "what actually ran": the worker build, its interpreter, and the
/// backends it can offer. It is deliberately reported by the worker rather than inferred by the
/// host, because the host cannot know which Python finally resolved or which backends that build
/// was compiled with.
/// </para>
/// </summary>
public sealed record WorkerEnvironment(
    string WorkerVersion,
    string? PythonVersion,
    int ProtocolVersion,
    IReadOnlyList<string> Backends)
{
    public string Summary => string.Join(
        " · ",
        new[]
        {
            $"worker {WorkerVersion}",
            PythonVersion is null ? null : $"Python {PythonVersion}",
            $"protocol {ProtocolVersion.ToString(CultureInfo.InvariantCulture)}",
            Backends.Count == 0 ? null : $"backends: {string.Join(", ", Backends)}",
        }.Where(part => part is not null));
}

/// <summary>Where a completed transcription put its output.</summary>
public sealed record TranscriptionOutput(
    string OutputPath,
    string Sha256,
    int SegmentCount,
    double DurationSeconds);

/// <summary>
/// The complete story of one worker run.
///
/// <para>
/// <see cref="UserMessage"/> is generated here from the outcome and the error code alone. It
/// deliberately cannot contain worker text, file paths, or stderr: those may carry meeting
/// content, and an error dialog is not a place private material may leak into.
/// </para>
/// </summary>
public sealed record WorkerRunResult
{
    public required WorkerOutcome Outcome { get; init; }

    public TranscriptionOutput? Output { get; init; }

    public WorkerError? Error { get; init; }

    /// <summary>Process exit code, when the process ran and exited.</summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// The tail of the child's stderr, bounded. Diagnostics only: it goes to the log, never to
    /// the user, and never into a transcript.
    /// </summary>
    public string StandardErrorTail { get; init; } = string.Empty;

    /// <summary>
    /// What the worker reported about itself, when the handshake got that far. Null means the
    /// child never identified itself, which is itself worth showing rather than inventing.
    /// </summary>
    public WorkerEnvironment? Environment { get; init; }

    /// <summary>Non-terminal warnings the worker reported along the way.</summary>
    public IReadOnlyList<WarningMessage> Warnings { get; init; } = [];

    /// <summary>
    /// Things the worker said that it should not have, after it had already finished.
    ///
    /// <para>
    /// A second terminal message, or anything at all after one, is a protocol violation. It
    /// is recorded here rather than retracting the outcome: the transcript on disk has
    /// already been written and its digest verified, and discarding verified work because
    /// the child kept talking afterwards would be the worse failure. A non-empty list
    /// belongs in the log and means the worker build is suspect.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ProtocolViolations { get; init; } = [];

    public bool Succeeded => Outcome == WorkerOutcome.Succeeded;

    /// <summary>
    /// A safe sentence for the user. Built only from the outcome and a known error code, so it is
    /// structurally incapable of quoting private material.
    /// </summary>
    public string UserMessage => Outcome switch
    {
        WorkerOutcome.Succeeded => "Transcription finished.",
        WorkerOutcome.Cancelled => "Transcription was cancelled. Your recording and any earlier transcript are unchanged.",
        WorkerOutcome.Busy => "Transcription is waiting because a recording is in progress. Recording always has priority.",
        WorkerOutcome.TimedOut => "Transcription took longer than the time limit and was stopped. Your recording is unchanged.",
        WorkerOutcome.LaunchFailed => "The transcription worker could not be started. Check that the app's Python runtime is installed.",
        WorkerOutcome.Crashed => "The transcription worker stopped unexpectedly. Your recording is unchanged and you can try again.",
        WorkerOutcome.ProtocolError => "The transcription worker does not match this version of EchoForge. Reinstalling should repair it.",
        WorkerOutcome.Failed => FailureMessage(Error?.Code),
        _ => "Transcription did not finish.",
    };

    private static string FailureMessage(string? code) => code switch
    {
        WorkerErrorCodes.UnsupportedProtocolVersion =>
            "The transcription worker does not match this version of EchoForge. Reinstalling should repair it.",
        WorkerErrorCodes.InvalidRequest =>
            "EchoForge could not describe this session to the transcription worker. This is a bug; the recording is unaffected.",
        WorkerErrorCodes.InputMissing =>
            "Some recorded audio for this session could not be found. Nothing has been changed or deleted.",
        WorkerErrorCodes.InputInvalid or WorkerErrorCodes.AudioUnreadable =>
            "Some recorded audio for this session could not be read. The files are left exactly as they are.",
        WorkerErrorCodes.BackendUnavailable =>
            "The selected transcription model is not available. Choose another profile or complete setup first.",
        WorkerErrorCodes.BackendFailed =>
            "Transcription failed while processing the audio. Your recording is unchanged and you can try again.",
        WorkerErrorCodes.OutputWriteFailed =>
            "The transcript could not be saved. Check free disk space; your recording is unchanged.",
        _ => "Transcription failed. Your recording is unchanged and you can try again.",
    };

    public static WorkerRunResult Busy() => new() { Outcome = WorkerOutcome.Busy };

    public static WorkerRunResult Protocol(string detail, WorkerStage stage, string stderrTail = "", int? exitCode = null) => new()
    {
        Outcome = WorkerOutcome.ProtocolError,
        Error = new WorkerError(WorkerErrorCodes.ProtocolError, stage, detail),
        StandardErrorTail = stderrTail,
        ExitCode = exitCode,
    };

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Outcome}{(Error is null ? string.Empty : $" ({Error.Code} at {Error.Stage})")}");
}
