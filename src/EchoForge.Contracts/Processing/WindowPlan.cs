using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Processing;

/// <summary>How transcription work is divided up.</summary>
public sealed record WindowPlanOptions
{
    /// <summary>
    /// Ten minutes, from the architecture plan. Long enough that a recogniser keeps useful
    /// context, short enough that a failure costs one window rather than a meeting.
    /// </summary>
    public double WindowSeconds { get; init; } = 600;

    /// <summary>
    /// Five seconds of shared audio between adjacent windows, so a sentence spoken across a
    /// boundary is heard whole by at least one of them. Source chunk boundaries fall every sixty
    /// seconds and mean nothing to speech; windows are cut here instead, and only inside an epoch.
    /// </summary>
    public double OverlapSeconds { get; init; } = 5;

    /// <summary>Bumped when the planning rules change, so old checkpoints stop matching.</summary>
    public string PlanningVersion { get; init; } = "windows-v1";

    /// <summary>Backend/model-owned strategy identity persisted separately from its version.</summary>
    public string StrategyId { get; init; } = "whisper-long-v1";
}

/// <summary>Where one unit of transcription work has got to.</summary>
public enum WindowCheckpointState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// One unit of transcription work: a stretch of one track's derivative, inside one epoch.
///
/// <para>
/// Tracks are never combined before transcription. Mixing them would destroy the only
/// deterministic speaker signal EchoForge has — that the microphone is You and the endpoint is
/// everyone else — in exchange for nothing.
/// </para>
/// </summary>
public sealed record TranscriptionWindow
{
    /// <summary>Stable for identical inputs, so a plan can be recomputed and still line up.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("source_track")]
    public required string SourceTrack { get; init; }

    [JsonPropertyName("epoch")]
    public required int Epoch { get; init; }

    [JsonPropertyName("derivative_relative_path")]
    public required string DerivativeRelativePath { get; init; }

    [JsonPropertyName("derivative_sha256")]
    public required string DerivativeSha256 { get; init; }

    [JsonPropertyName("start_frame")]
    public required long StartFrame { get; init; }

    [JsonPropertyName("end_frame")]
    public required long EndFrame { get; init; }

    [JsonPropertyName("session_start_seconds")]
    public required double SessionStartSeconds { get; init; }

    [JsonPropertyName("session_end_seconds")]
    public required double SessionEndSeconds { get; init; }

    /// <summary>
    /// Audio at the start of this window that the previous one also heard. Recorded so the
    /// inference pass can de-duplicate the overlap later; nothing de-duplicates it yet.
    /// </summary>
    [JsonPropertyName("overlap_before_seconds")]
    public double OverlapBeforeSeconds { get; init; }

    [JsonPropertyName("overlap_after_seconds")]
    public double OverlapAfterSeconds { get; init; }

    /// <summary>
    /// Everything this window's result depends on, in one digest. A checkpoint may be reused only
    /// when this matches, which is what stops a stale result from surviving a change of audio,
    /// profile, or planning rules.
    /// </summary>
    [JsonPropertyName("input_fingerprint")]
    public required string InputFingerprint { get; init; }

    public long Frames => EndFrame - StartFrame;

    public double DurationSeconds => SessionEndSeconds - SessionStartSeconds;
}

/// <summary>A window plus what happened to it.</summary>
public sealed record WindowCheckpoint
{
    [JsonPropertyName("window_id")]
    public required string WindowId { get; init; }

    [JsonPropertyName("state")]
    public required WindowCheckpointState State { get; init; }

    /// <summary>The fingerprint the result was produced against, not the current one.</summary>
    [JsonPropertyName("input_fingerprint")]
    public required string InputFingerprint { get; init; }

    [JsonPropertyName("completed_utc")]
    public DateTimeOffset? CompletedUtc { get; init; }

    [JsonPropertyName("failure_code")]
    public string? FailureCode { get; init; }

    /// <summary>Where a finished window's intermediate result was written, once one exists.</summary>
    [JsonPropertyName("result_relative_path")]
    public string? ResultRelativePath { get; init; }

    public bool IsReusableFor(TranscriptionWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return State == WindowCheckpointState.Succeeded
            && string.Equals(InputFingerprint, window.InputFingerprint, StringComparison.Ordinal);
    }
}

/// <summary>
/// The complete division of one session into transcription work, and the state of each part.
///
/// <para>
/// Deterministic: the same session, derivatives, and options always produce the same plan, with
/// the same window IDs in the same order. That is what lets a re-run pick up where a failed one
/// stopped instead of starting again.
/// </para>
/// </summary>
public sealed record WindowPlan
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("source_manifest_sha256")]
    public required string SourceManifestSha256 { get; init; }

    [JsonPropertyName("processing_profile")]
    public required string ProcessingProfile { get; init; }

    [JsonPropertyName("planning_version")]
    public required string PlanningVersion { get; init; }

    [JsonPropertyName("strategy_id")]
    public string StrategyId { get; init; } = "whisper-long-v1";

    [JsonPropertyName("window_seconds")]
    public required double WindowSeconds { get; init; }

    [JsonPropertyName("overlap_seconds")]
    public required double OverlapSeconds { get; init; }

    [JsonPropertyName("windows")]
    public IReadOnlyList<TranscriptionWindow> Windows { get; init; } = [];

    [JsonPropertyName("checkpoints")]
    public IReadOnlyList<WindowCheckpoint> Checkpoints { get; init; } = [];

    public IEnumerable<TranscriptionWindow> For(string sourceTrack) =>
        Windows.Where(w => string.Equals(w.SourceTrack, sourceTrack, StringComparison.Ordinal));

    public WindowCheckpoint? CheckpointFor(string windowId) =>
        Checkpoints.FirstOrDefault(c => string.Equals(c.WindowId, windowId, StringComparison.Ordinal));

    /// <summary>Windows that still need running, in order.</summary>
    public IReadOnlyList<TranscriptionWindow> Outstanding =>
    [
        .. Windows.Where(w => CheckpointFor(w.Id) is not { } checkpoint || !checkpoint.IsReusableFor(w))
    ];

    public bool IsComplete => Outstanding.Count == 0;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
