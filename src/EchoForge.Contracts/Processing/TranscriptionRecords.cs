using System.Text.Json.Serialization;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Contracts.Processing;

/// <summary>
/// One activated transcript revision.
///
/// <para>
/// A revision is immutable once activated. Re-running transcription produces a new one; it never
/// edits an old one, because a summary generated from revision 2 must still be able to open the
/// exact text it was written from.
/// </para>
///
/// <para>
/// <see cref="SourceManifestSha256"/> is the identity of the audio the revision came from, and
/// <see cref="TranscriptSha256"/> is the identity of the revision itself. Together they make both
/// "the audio changed underneath this transcript" and "this file is not the one we activated"
/// detectable rather than assumed.
/// </para>
/// </summary>
public sealed record TranscriptRevisionRecord
{
    [JsonPropertyName("revision")]
    public required int Revision { get; init; }

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("created_utc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Path relative to the session root, forward-slashed.</summary>
    [JsonPropertyName("relative_path")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("transcript_sha256")]
    public required string TranscriptSha256 { get; init; }

    [JsonPropertyName("source_manifest_sha256")]
    public required string SourceManifestSha256 { get; init; }

    [JsonPropertyName("segment_count")]
    public int SegmentCount { get; init; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; init; }

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = string.Empty;

    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    [JsonPropertyName("worker_version")]
    public string WorkerVersion { get; init; } = string.Empty;

    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; }

    /// <summary>
    /// False when a placeholder backend produced this revision. Carried all the way to the UI:
    /// the user must never be shown placeholder text as if it were a record of what was said.
    /// </summary>
    [JsonPropertyName("recognizes_speech")]
    public bool RecognizesSpeech { get; init; }

    /// <summary>False once the file behind this revision is missing. Not selectable in that state.</summary>
    [JsonIgnore]
    public bool FileExists { get; init; } = true;
}

/// <summary>
/// The in-flight or most recent transcription attempt.
///
/// <para>
/// Progress lives here rather than in the journal. A journal line per progress update would turn
/// the recovery ledger into a log, and progress is worth nothing after a restart anyway.
/// </para>
/// </summary>
public sealed record TranscriptionJobRecord
{
    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("revision")]
    public required int Revision { get; init; }

    [JsonPropertyName("state")]
    public required ProcessingStageState State { get; init; }

    [JsonPropertyName("queued_utc")]
    public DateTimeOffset? QueuedUtc { get; init; }

    [JsonPropertyName("started_utc")]
    public DateTimeOffset? StartedUtc { get; init; }

    [JsonPropertyName("completed_utc")]
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>The worker stage this job had reached, as a wire name.</summary>
    [JsonPropertyName("stage")]
    public string Stage { get; init; } = "handshake";

    [JsonPropertyName("completed_units")]
    public int CompletedUnits { get; init; }

    [JsonPropertyName("total_units")]
    public int TotalUnits { get; init; }

    [JsonPropertyName("source_manifest_sha256")]
    public string? SourceManifestSha256 { get; init; }

    [JsonPropertyName("backend")]
    public string? Backend { get; init; }

    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    /// <summary>A stable machine-readable reason. Never worker text.</summary>
    [JsonPropertyName("failure_code")]
    public string? FailureCode { get; init; }

    /// <summary>
    /// A sentence safe to show the user. Generated from the outcome, so it cannot contain a
    /// path, a session identifier, or anything the worker printed.
    /// </summary>
    [JsonPropertyName("failure_summary")]
    public string? FailureSummary { get; init; }

    public double Fraction => TotalUnits <= 0 ? 0 : Math.Clamp((double)CompletedUnits / TotalUnits, 0, 1);
}

/// <summary>
/// Everything known about one session's transcription: the stage, which revision is selected,
/// and every revision that survives.
/// </summary>
public sealed record TranscriptionState
{
    public static readonly TranscriptionState Empty = new();

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("state")]
    public ProcessingStageState Stage { get; init; } = ProcessingStageState.NotRequested;

    /// <summary>
    /// The revision the app reads, exports, and will attach summaries to. A failed or cancelled
    /// attempt never changes it: an attempt that produced nothing cannot retract one that did.
    /// </summary>
    [JsonPropertyName("selected_revision")]
    public int? SelectedRevision { get; init; }

    [JsonPropertyName("revisions")]
    public IReadOnlyList<TranscriptRevisionRecord> Revisions { get; init; } = [];

    [JsonPropertyName("current_job")]
    public TranscriptionJobRecord? CurrentJob { get; init; }

    public TranscriptRevisionRecord? Selected =>
        SelectedRevision is { } revision ? Revisions.FirstOrDefault(r => r.Revision == revision) : null;

    public bool HasTranscript => Selected is not null;

    /// <summary>The highest revision number ever allocated, activated or not.</summary>
    [JsonPropertyName("highest_allocated_revision")]
    public int HighestAllocatedRevision { get; init; }
}

/// <summary>What to ask the worker for. Kept small; the backend is named explicitly.</summary>
public sealed record TranscriptionOptions
{
    /// <summary>
    /// Defaults to the deterministic placeholder. There is no alias that silently resolves to
    /// whatever model happens to be installed.
    /// </summary>
    public string Backend { get; init; } = "mock";

    public string? Profile { get; init; }

    public string? Language { get; init; }

    /// <summary>Placeholder-backend segmentation window. Ignored by a real recogniser.</summary>
    public double? SegmentSeconds { get; init; }

    /// <summary>Fault injection, for the coordinator's own tests only.</summary>
    public string? TestMode { get; init; }

    public double? TestDelaySeconds { get; init; }

    /// <summary>
    /// Which compute profile to ask for. The worker may climb down from it — and records that
    /// it did — but never changes it silently.
    /// </summary>
    public string ComputeProfile { get; init; } = "cpu-int8";

    /// <summary>Words the recogniser would otherwise mis-hear: names, jargon, acronyms.</summary>
    public IReadOnlyList<string> Glossary { get; init; } = [];

    public string? InitialPrompt { get; init; }

    /// <summary>Conservative voice-activity filtering, from the model inside the pinned wheel.</summary>
    public bool VadFilter { get; init; } = true;

    public bool WordTimestamps { get; init; } = true;

    public int? BeamSize { get; init; }
}
