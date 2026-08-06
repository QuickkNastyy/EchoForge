namespace EchoForge.Contracts.Workers;

/// <summary>
/// The constants of the NDJSON worker protocol, defined once so the host, the worker, and
/// <c>schemas/worker-protocol.schema.json</c> cannot quietly disagree.
/// </summary>
public static class WorkerProtocol
{
    /// <summary>The version this build speaks. A worker announcing anything else is refused.</summary>
    public const int Version = 1;

    /// <summary>
    /// Versions this host can talk to. It is a set rather than a single number so a future host
    /// can accept an older worker deliberately, instead of by accident.
    /// </summary>
    public static readonly IReadOnlyList<int> SupportedVersions = [Version];

    public static bool IsSupported(int protocolVersion) => SupportedVersions.Contains(protocolVersion);

    /// <summary>Message type names. Host-to-worker first, then worker-to-host.</summary>
    public static class Types
    {
        public const string Hello = "hello";
        public const string StartJob = "start_job";
        public const string Cancel = "cancel";

        public const string Ready = "ready";
        public const string Started = "started";
        public const string Progress = "progress";
        public const string Warning = "warning";
        public const string Result = "result";
        public const string Error = "error";
        public const string Cancelled = "cancelled";
    }

    /// <summary>The only job kind Phase 2 defines.</summary>
    public const string TranscribeJobKind = "transcribe";

    /// <summary>
    /// The deterministic placeholder backend. It performs no speech recognition of any kind and
    /// says so in every transcript it writes.
    /// </summary>
    public const string MockBackend = "mock";

    /// <summary>
    /// Set to <c>1</c> in the child environment to unlock fault injection. Absent in production,
    /// so a real run cannot reach a test mode however the request is filled in.
    /// </summary>
    public const string AllowTestModesVariable = "ECHOFORGE_WORKER_ALLOW_TEST_MODES";
}

/// <summary>
/// Where a job had reached. Separate from the recording state machine and from
/// <see cref="Transcripts.ProcessingStageState"/>: this names the step inside one worker run, so
/// a failure can say which part of the pipeline it happened in.
/// </summary>
public enum WorkerStage
{
    Handshake,
    Accepting,
    Preparing,
    ReadingAudio,
    TranscribingMicrophone,
    TranscribingSystem,
    Merging,
    Validating,
    WritingOutput,
    Finished,
}

/// <summary>Wire names for <see cref="WorkerStage"/>, kept explicit rather than derived.</summary>
public static class WorkerStages
{
    public static string ToWire(WorkerStage stage) => stage switch
    {
        WorkerStage.Handshake => "handshake",
        WorkerStage.Accepting => "accepting",
        WorkerStage.Preparing => "preparing",
        WorkerStage.ReadingAudio => "reading_audio",
        WorkerStage.TranscribingMicrophone => "transcribing_microphone",
        WorkerStage.TranscribingSystem => "transcribing_system",
        WorkerStage.Merging => "merging",
        WorkerStage.Validating => "validating",
        WorkerStage.WritingOutput => "writing_output",
        WorkerStage.Finished => "finished",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "unknown worker stage"),
    };

    public static bool TryParse(string? wire, out WorkerStage stage)
    {
        switch (wire)
        {
            case "handshake": stage = WorkerStage.Handshake; return true;
            case "accepting": stage = WorkerStage.Accepting; return true;
            case "preparing": stage = WorkerStage.Preparing; return true;
            case "reading_audio": stage = WorkerStage.ReadingAudio; return true;
            case "transcribing_microphone": stage = WorkerStage.TranscribingMicrophone; return true;
            case "transcribing_system": stage = WorkerStage.TranscribingSystem; return true;
            case "merging": stage = WorkerStage.Merging; return true;
            case "validating": stage = WorkerStage.Validating; return true;
            case "writing_output": stage = WorkerStage.WritingOutput; return true;
            case "finished": stage = WorkerStage.Finished; return true;
            default: stage = WorkerStage.Handshake; return false;
        }
    }
}

/// <summary>
/// The class of a worker-reported failure. Timeout is deliberately absent: a timeout is something
/// the host observes about a silent child, never something the child claims about itself.
/// </summary>
public static class WorkerErrorCodes
{
    public const string UnsupportedProtocolVersion = "unsupported_protocol_version";
    public const string ProtocolError = "protocol_error";
    public const string InvalidRequest = "invalid_request";
    public const string InputMissing = "input_missing";
    public const string InputInvalid = "input_invalid";
    public const string AudioUnreadable = "audio_unreadable";
    public const string BackendUnavailable = "backend_unavailable";
    public const string BackendFailed = "backend_failed";
    public const string OutputWriteFailed = "output_write_failed";
    public const string InternalError = "internal_error";

    public static readonly IReadOnlyList<string> All =
    [
        UnsupportedProtocolVersion,
        ProtocolError,
        InvalidRequest,
        InputMissing,
        InputInvalid,
        AudioUnreadable,
        BackendUnavailable,
        BackendFailed,
        OutputWriteFailed,
        InternalError,
    ];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}
