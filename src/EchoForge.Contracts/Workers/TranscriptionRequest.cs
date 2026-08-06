using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Workers;

/// <summary>
/// Everything a worker needs to transcribe one session, and nothing else.
///
/// <para>
/// All times are <b>session-relative seconds</b> on the single merged timeline. The host converts
/// epoch-relative chunk offsets before sending, because a worker that had to reason about epoch
/// origins would need the QPC anchors, and those belong to the recorder.
/// </para>
///
/// <para>
/// Speaker attribution is not in this type on purpose. Microphone content is You and system
/// content is Remote by construction on both sides of the pipe; making it a parameter would make
/// it something a caller could get wrong.
/// </para>
/// </summary>
public sealed record TranscriptionRequest
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("transcript_revision")]
    public required int TranscriptRevision { get; init; }

    /// <summary>
    /// Supplied by the host so the worker owns no clock. Two runs over identical audio with the
    /// same stamp produce byte-identical transcripts, which is what makes determinism testable.
    /// </summary>
    [JsonPropertyName("created_at_utc")]
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Absolute session directory. Every chunk path is resolved inside it.</summary>
    [JsonPropertyName("session_root")]
    public required string SessionRoot { get; init; }

    /// <summary>Absolute path the worker writes the transcript to.</summary>
    [JsonPropertyName("output_path")]
    public required string OutputPath { get; init; }

    [JsonPropertyName("duration_seconds")]
    public required double DurationSeconds { get; init; }

    [JsonPropertyName("epochs")]
    public IReadOnlyList<RequestEpoch> Epochs { get; init; } = [];

    [JsonPropertyName("tracks")]
    public IReadOnlyList<RequestTrack> Tracks { get; init; } = [];

    [JsonPropertyName("options")]
    public required RequestOptions Options { get; init; }
}

/// <summary>A session-relative capture epoch. Ordered and non-overlapping.</summary>
public sealed record RequestEpoch(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("start_seconds")] double StartSeconds,
    [property: JsonPropertyName("end_seconds")] double EndSeconds);

/// <summary>One track's finalized source chunks, in index order.</summary>
public sealed record RequestTrack
{
    [JsonPropertyName("source_track")]
    public required string SourceTrack { get; init; }

    [JsonPropertyName("chunks")]
    public IReadOnlyList<RequestChunk> Chunks { get; init; } = [];
}

/// <summary>
/// One finalized, immutable source chunk. The worker opens it read-only; nothing in the pipeline
/// is permitted to rewrite a source WAV or its metadata.
/// </summary>
public sealed record RequestChunk
{
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    [JsonPropertyName("epoch")]
    public required int Epoch { get; init; }

    /// <summary>Relative to <see cref="TranscriptionRequest.SessionRoot"/>, and inside it.</summary>
    [JsonPropertyName("relative_path")]
    public required string RelativePath { get; init; }

    /// <summary>Session-relative start of this chunk's first frame.</summary>
    [JsonPropertyName("start_seconds")]
    public required double StartSeconds { get; init; }

    [JsonPropertyName("end_seconds")]
    public required double EndSeconds { get; init; }

    [JsonPropertyName("sample_rate")]
    public required int SampleRate { get; init; }

    [JsonPropertyName("channels")]
    public required int Channels { get; init; }

    [JsonPropertyName("frames")]
    public required long Frames { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
}

/// <summary>How to transcribe. The backend is named explicitly; there is no default alias.</summary>
public sealed record RequestOptions
{
    [JsonPropertyName("backend")]
    public required string Backend { get; init; }

    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    /// <summary>Null lets the backend decide. The mock backend always reports <c>und</c>.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary>Placeholder-backend segmentation window. Ignored by real recognisers.</summary>
    [JsonPropertyName("segment_seconds")]
    public double? SegmentSeconds { get; init; }

    /// <summary>
    /// Fault injection for the supervisor's own tests. The worker ignores it unless
    /// <see cref="WorkerProtocol.AllowTestModesVariable"/> is set in its environment, so a
    /// production run cannot reach any of it however this field is filled in.
    /// </summary>
    [JsonPropertyName("test_mode")]
    public string? TestMode { get; init; }

    [JsonPropertyName("test_delay_seconds")]
    public double? TestDelaySeconds { get; init; }
}

/// <summary>The fault-injection modes the worker understands, named once.</summary>
public static class WorkerTestModes
{
    public const string Normal = "normal";
    public const string Delay = "delay";
    public const string Crash = "crash";
    public const string NonzeroExit = "nonzero_exit";
    public const string InvalidJson = "invalid_json";
    public const string UnknownMessage = "unknown_message";
    public const string MalformedProgress = "malformed_progress";
    public const string MalformedResult = "malformed_result";
    public const string DuplicateResult = "duplicate_result";
    public const string OutputAfterCompletion = "output_after_completion";
    public const string Stderr = "stderr";
    public const string Hang = "hang";
}
