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

    /// <summary>
    /// The prepared 16 kHz mono audio, when this is a production run. Absent for the
    /// placeholder, which works from the source chunks and so stays usable before anything has
    /// been prepared.
    /// </summary>
    [JsonPropertyName("derivatives")]
    public IReadOnlyList<RequestDerivative> Derivatives { get; init; } = [];

    /// <summary>The units of work, already placed on the session timeline by the host.</summary>
    [JsonPropertyName("windows")]
    public IReadOnlyList<RequestWindow> Windows { get; init; } = [];
}

/// <summary>One track's prepared audio and the map back to the immutable source.</summary>
public sealed record RequestDerivative
{
    [JsonPropertyName("source_track")]
    public required string SourceTrack { get; init; }

    [JsonPropertyName("relative_path")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("timing_map_relative_path")]
    public required string TimingMapRelativePath { get; init; }

    [JsonPropertyName("sample_rate")]
    public required int SampleRate { get; init; }

    [JsonPropertyName("channels")]
    public required int Channels { get; init; }

    [JsonPropertyName("total_frames")]
    public required long TotalFrames { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

/// <summary>
/// One transcription window as the worker receives it.
///
/// <para>
/// The fingerprint travels with the window because the worker owns the per-window checkpoints:
/// it is what lets a resumed job reuse a finished window and refuse one whose audio has since
/// changed.
/// </para>
/// </summary>
public sealed record RequestWindow
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("source_track")]
    public required string SourceTrack { get; init; }

    [JsonPropertyName("epoch")]
    public required int Epoch { get; init; }

    [JsonPropertyName("start_frame")]
    public required long StartFrame { get; init; }

    [JsonPropertyName("end_frame")]
    public required long EndFrame { get; init; }

    [JsonPropertyName("session_start_seconds")]
    public required double SessionStartSeconds { get; init; }

    [JsonPropertyName("session_end_seconds")]
    public required double SessionEndSeconds { get; init; }

    [JsonPropertyName("overlap_before_seconds")]
    public double OverlapBeforeSeconds { get; init; }

    [JsonPropertyName("overlap_after_seconds")]
    public double OverlapAfterSeconds { get; init; }

    [JsonPropertyName("input_fingerprint")]
    public required string InputFingerprint { get; init; }
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

    [JsonPropertyName("model_id")]
    public string? ModelId { get; init; }

    [JsonPropertyName("model_revision")]
    public string? ModelRevision { get; init; }

    [JsonPropertyName("model_artifact_sha256")]
    public string? ModelArtifactSha256 { get; init; }

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

    /// <summary>
    /// The verified model directory, resolved by the registry. Never a repository id and never
    /// an alias: the worker must not go looking, and the pinning only means something if the
    /// path it is handed is the one that was checked.
    /// </summary>
    [JsonPropertyName("model_path")]
    public string? ModelPath { get; init; }

    [JsonPropertyName("compute_profile")]
    public string? ComputeProfile { get; init; }

    [JsonPropertyName("allow_cpu_fallback")]
    public bool AllowCpuFallback { get; init; } = true;

    [JsonPropertyName("beam_size")]
    public int? BeamSize { get; init; }

    [JsonPropertyName("vad_filter")]
    public bool VadFilter { get; init; } = true;

    [JsonPropertyName("vad_mode")]
    public string? VadMode { get; init; }

    [JsonPropertyName("word_timestamps")]
    public bool WordTimestamps { get; init; } = true;

    /// <summary>Seeded into the recogniser. Biases towards names and jargon; guarantees nothing.</summary>
    [JsonPropertyName("initial_prompt")]
    public string? InitialPrompt { get; init; }

    [JsonPropertyName("glossary")]
    public IReadOnlyList<string> Glossary { get; init; } = [];

    [JsonPropertyName("window_strategy")]
    public string? WindowStrategy { get; init; }

    [JsonPropertyName("window_seconds")]
    public double? WindowSeconds { get; init; }

    [JsonPropertyName("overlap_seconds")]
    public double? OverlapSeconds { get; init; }

    [JsonPropertyName("timestamp_capability")]
    public string? TimestampCapability { get; init; }

    [JsonPropertyName("timestamp_precision")]
    public string? TimestampPrecision { get; init; }
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
