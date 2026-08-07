using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Workers;

/// <summary>
/// One line of the protocol. Every message names its version, so a mismatch is a clean refusal
/// rather than a hopeful parse of a shape that may have changed underneath.
/// </summary>
public abstract record WorkerMessage
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; } = WorkerProtocol.Version;

    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

// ---- host to worker ----

/// <summary>
/// Sent first, before anything else. The worker must not read a job, touch the filesystem, or
/// load a backend until it has seen this, which is also what keeps the window between process
/// start and Job Object assignment inert.
/// </summary>
public sealed record HelloMessage : WorkerMessage
{
    public override string Type => WorkerProtocol.Types.Hello;

    [JsonPropertyName("host_version")]
    public required string HostVersion { get; init; }

    [JsonPropertyName("supported_protocol_versions")]
    public IReadOnlyList<int> SupportedProtocolVersions { get; init; } = WorkerProtocol.SupportedVersions;
}

/// <summary>Sent exactly once per worker lifetime. A second one is a protocol error.</summary>
public sealed record StartJobMessage : WorkerMessage
{
    public override string Type => WorkerProtocol.Types.StartJob;

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("job_kind")]
    public string JobKind { get; init; } = WorkerProtocol.TranscribeJobKind;

    /// <summary>Set for a transcription job. Exactly one of the two request fields is present.</summary>
    [JsonPropertyName("request")]
    public TranscriptionRequest? Request { get; init; }

    /// <summary>Set for a summarisation job.</summary>
    [JsonPropertyName("summary_request")]
    public Summaries.SummaryRequest? SummaryRequest { get; init; }
}

/// <summary>Idempotent. The worker stops at its next safe boundary and answers <c>cancelled</c>.</summary>
public sealed record CancelMessage : WorkerMessage
{
    public override string Type => WorkerProtocol.Types.Cancel;

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

// ---- worker to host ----

/// <summary>The only permitted reply to <c>hello</c> other than <c>error</c>.</summary>
public sealed record ReadyMessage : WorkerMessage
{
    public override string Type => WorkerProtocol.Types.Ready;

    [JsonPropertyName("worker_version")]
    public required string WorkerVersion { get; init; }

    [JsonPropertyName("python_version")]
    public string? PythonVersion { get; init; }

    [JsonPropertyName("supported_protocol_versions")]
    public IReadOnlyList<int> SupportedProtocolVersions { get; init; } = [];

    [JsonPropertyName("backends")]
    public IReadOnlyList<string> Backends { get; init; } = [];
}

/// <summary>The request validated and work began.</summary>
public sealed record StartedMessage : WorkerMessage
{
    public override string Type => WorkerProtocol.Types.Started;

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("backend")]
    public required string Backend { get; init; }

    /// <summary>
    /// False for any placeholder backend. The host carries this through to the transcript and to
    /// the user; placeholder text is never presented as recognised speech.
    /// </summary>
    [JsonPropertyName("recognizes_speech")]
    public required bool RecognizesSpeech { get; init; }
}

/// <summary>Advisory. Never moves backwards within a stage and never exceeds its total.</summary>
public sealed record ProgressMessage : WorkerMessage
{
    public override string Type => WorkerProtocol.Types.Progress;

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("completed_units")]
    public required int CompletedUnits { get; init; }

    [JsonPropertyName("total_units")]
    public required int TotalUnits { get; init; }

    public double Fraction => TotalUnits <= 0 ? 0 : Math.Clamp((double)CompletedUnits / TotalUnits, 0, 1);
}

/// <summary>Non-terminal. A job that emits warnings can still succeed.</summary>
public sealed record WarningMessage : WorkerMessage
{
    public override string Type => WorkerProtocol.Types.Warning;

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>Terminal on success. The transcript is a file; it is never inlined here.</summary>
public sealed record ResultMessage : WorkerMessage
{
    public override string Type => WorkerProtocol.Types.Result;

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("output_path")]
    public required string OutputPath { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("segment_count")]
    public required int SegmentCount { get; init; }

    [JsonPropertyName("duration_seconds")]
    public required double DurationSeconds { get; init; }
}

/// <summary>
/// Terminal on failure. <see cref="Detail"/> is a technical diagnostic for logs and is never
/// shown to the user verbatim — see <see cref="WorkerRunResult.UserMessage"/>.
/// </summary>
public sealed record ErrorMessage : WorkerMessage
{
    public override string Type => WorkerProtocol.Types.Error;

    [JsonPropertyName("job_id")]
    public string? JobId { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }
}

/// <summary>Terminal after a cancel. Sources and prior outputs are untouched.</summary>
public sealed record CancelledMessage : WorkerMessage
{
    public override string Type => WorkerProtocol.Types.Cancelled;

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }
}
