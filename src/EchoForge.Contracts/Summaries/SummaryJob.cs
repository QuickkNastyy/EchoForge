using System.Text.Json.Serialization;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Contracts.Summaries;

/// <summary>What to ask a summariser for. The backend is named explicitly; there is no alias.</summary>
public sealed record SummaryOptions
{
    /// <summary>Defaults to the deterministic placeholder, which summarises nothing.</summary>
    public string Backend { get; init; } = "mock-summary";

    /// <summary>The production backend's name. Anything else runs the placeholder.</summary>
    public const string ProductionBackend = "gemma-4-12b";

    public const string MockBackend = "mock-summary";

    public bool IsProduction => string.Equals(Backend, ProductionBackend, StringComparison.Ordinal);

    /// <summary>
    /// Which runtime profile the production backend should use. Empty means "the best one this
    /// machine has installed". Ignored entirely by the placeholder, which needs no runtime.
    /// </summary>
    public string SummaryProfile { get; init; } = string.Empty;

    /// <summary>
    /// Fixed on purpose. A summary that changed between two runs over one transcript could not be
    /// reviewed against its own evidence, and reprocessing would stop meaning anything.
    /// </summary>
    public int Seed { get; init; } = 7;

    public string PromptVersion { get; init; } = "meeting-summary-v1";

    /// <summary>
    /// Roughly how much transcript goes into one extraction. The plan's 8K-12K token range, held
    /// as characters here because the placeholder has no tokenizer and the estimate only has to
    /// be stable, not exact.
    /// </summary>
    public int ChunkCharacters { get; init; } = 24000;

    /// <summary>
    /// Segments repeated between adjacent chunks so a decision stated across a boundary is seen
    /// whole by at least one of them.
    /// </summary>
    public int OverlapSegments { get; init; } = 3;

    /// <summary>
    /// How many extracted facts one synthesis pass considers at once.
    ///
    /// <para>
    /// A long meeting yields more facts than fit in one context, and merging them is itself an
    /// operation with a size limit — so the merge recurses. This is the width of one fold.
    /// </para>
    /// </summary>
    public int SynthesisGroupSize { get; init; } = 200;

    /// <summary>
    /// Off by default, deliberately. Guessing who owns an action is the failure mode that turns a
    /// useful draft into a misleading one, and the plan requires it to be opt-in.
    /// </summary>
    public bool InferOwners { get; init; }

    public bool InferDueDates { get; init; }

    /// <summary>The meeting's date, when known. Without it a relative date stays unknown.</summary>
    public DateOnly? MeetingDate { get; init; }

    /// <summary>Fault injection for the coordinator's own tests. Locked behind the same env var.</summary>
    public string? TestMode { get; init; }

    public double? TestDelaySeconds { get; init; }
}

/// <summary>
/// One deterministic slice of a transcript.
///
/// <para>
/// Chunks name segment IDs rather than carrying text: the worker reads the transcript file it was
/// pointed at, so the protocol keeps its rule that messages carry no meeting content. The
/// fingerprint covers everything a chunk's result depends on, so a checkpoint cannot survive a
/// change to the transcript, the boundaries, or the prompt.
/// </para>
/// </summary>
public sealed record SummaryChunk
{
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    [JsonPropertyName("first_segment_id")]
    public required string FirstSegmentId { get; init; }

    [JsonPropertyName("last_segment_id")]
    public required string LastSegmentId { get; init; }

    /// <summary>Segments at the start also present in the previous chunk.</summary>
    [JsonPropertyName("overlap_before")]
    public int OverlapBefore { get; init; }

    [JsonPropertyName("overlap_after")]
    public int OverlapAfter { get; init; }

    [JsonPropertyName("segment_count")]
    public required int SegmentCount { get; init; }

    [JsonPropertyName("estimated_characters")]
    public required int EstimatedCharacters { get; init; }

    [JsonPropertyName("input_fingerprint")]
    public required string InputFingerprint { get; init; }
}

/// <summary>Everything a worker needs to summarise one transcript revision.</summary>
public sealed record SummaryRequest
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("summary_revision")]
    public required int SummaryRevision { get; init; }

    [JsonPropertyName("transcript_revision")]
    public required int TranscriptRevision { get; init; }

    [JsonPropertyName("transcript_sha256")]
    public required string TranscriptSha256 { get; init; }

    /// <summary>Absolute path of the activated transcript revision. Opened read-only.</summary>
    [JsonPropertyName("transcript_path")]
    public required string TranscriptPath { get; init; }

    [JsonPropertyName("session_root")]
    public required string SessionRoot { get; init; }

    [JsonPropertyName("output_path")]
    public required string OutputPath { get; init; }

    /// <summary>Supplied by the host so the worker owns no clock and output stays reproducible.</summary>
    [JsonPropertyName("created_at_utc")]
    public required DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("meeting_date")]
    public string? MeetingDate { get; init; }

    [JsonPropertyName("prompt_version")]
    public required string PromptVersion { get; init; }

    [JsonPropertyName("infer_owners")]
    public bool InferOwners { get; init; }

    [JsonPropertyName("infer_due_dates")]
    public bool InferDueDates { get; init; }

    [JsonPropertyName("backend")]
    public required string Backend { get; init; }

    [JsonPropertyName("chunks")]
    public IReadOnlyList<SummaryChunk> Chunks { get; init; } = [];

    [JsonPropertyName("synthesis_group_size")]
    public int SynthesisGroupSize { get; init; } = 200;

    /// <summary>
    /// The verified llama.cpp server the worker may launch, and the verified model it may load.
    ///
    /// <para>
    /// Resolved by the host, from the manifest, after the registry has hashed both. The worker
    /// never looks for a runtime: it runs what it is handed or it runs nothing, so there is no
    /// path by which an unverified binary on the machine becomes the thing that summarised a
    /// meeting.
    /// </para>
    /// </summary>
    [JsonPropertyName("llama_binary_path")]
    public string LlamaBinaryPath { get; init; } = string.Empty;

    [JsonPropertyName("model_path")]
    public string ModelPath { get; init; } = string.Empty;

    [JsonPropertyName("summary_profile")]
    public string SummaryProfile { get; init; } = string.Empty;

    [JsonPropertyName("seed")]
    public int Seed { get; init; } = 7;

    /// <summary>
    /// 0 for a first generation, 1 for the single re-ask that is allowed after a refusal.
    ///
    /// <para>
    /// The bound lives on the host, not in the worker: a worker that decided for itself how many
    /// times to try again would be the one component with no ceiling on how long it can run.
    /// </para>
    /// </summary>
    [JsonPropertyName("repair_attempt")]
    public int RepairAttempt { get; init; }

    /// <summary>
    /// Why the previous attempt was refused. Sent so the re-ask knows what went wrong; it never
    /// changes what the answer has to satisfy, which is the same validator either way.
    /// </summary>
    [JsonPropertyName("rejection_reasons")]
    public IReadOnlyList<string> RejectionReasons { get; init; } = [];

    [JsonPropertyName("test_mode")]
    public string? TestMode { get; init; }

    [JsonPropertyName("test_delay_seconds")]
    public double? TestDelaySeconds { get; init; }
}

/// <summary>One activated summary revision, recorded so it can be explained and reused.</summary>
public sealed record SummaryRevisionRecord
{
    [JsonPropertyName("revision")]
    public required int Revision { get; init; }

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("created_utc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    [JsonPropertyName("relative_path")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("summary_sha256")]
    public required string SummarySha256 { get; init; }

    /// <summary>Which transcript this was written from. Half of every evidence identity.</summary>
    [JsonPropertyName("transcript_revision")]
    public required int TranscriptRevision { get; init; }

    [JsonPropertyName("transcript_sha256")]
    public required string TranscriptSha256 { get; init; }

    [JsonPropertyName("prompt_version")]
    public string PromptVersion { get; init; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = string.Empty;

    [JsonPropertyName("worker_version")]
    public string WorkerVersion { get; init; } = string.Empty;

    /// <summary>False for a placeholder. Carried to the UI so it can say so.</summary>
    [JsonPropertyName("produces_summaries")]
    public bool ProducesSummaries { get; init; }

    [JsonPropertyName("decision_count")]
    public int DecisionCount { get; init; }

    [JsonPropertyName("action_count")]
    public int ActionCount { get; init; }

    /// <summary>
    /// True once every citation resolved in the transcript revision it names. A summary that
    /// failed this was never activated, so on disk this is always true - it is recorded because
    /// the fact is worth being able to show, not because it varies.
    /// </summary>
    [JsonPropertyName("evidence_validated")]
    public bool EvidenceValidated { get; init; }

    /// <summary>
    /// 1 when this revision came from the one bounded re-ask rather than the first generation.
    /// Recorded because "the first answer was refused" is worth being able to see afterwards.
    /// </summary>
    [JsonPropertyName("repair_attempt")]
    public int RepairAttempt { get; init; }

    /// <summary>How many synthesis passes the facts were folded through. 1 for a short meeting.</summary>
    [JsonPropertyName("synthesis_levels")]
    public int SynthesisLevels { get; init; }

    /// <summary>
    /// The runtime profile that actually ran, which is not always the one that was asked for.
    /// Recorded so a summary produced at a reduced context can be told apart from one produced at
    /// the full context later, without guessing from the chunk count.
    /// </summary>
    [JsonPropertyName("runtime")]
    public string Runtime { get; init; } = string.Empty;

    [JsonPropertyName("context_tokens")]
    public int ContextTokens { get; init; }

    public bool WasRepaired => RepairAttempt > 0;

    [JsonIgnore]
    public bool FileExists { get; init; } = true;

    /// <summary>
    /// A summary is stale when the session's selected transcript is no longer the one it was
    /// written from. Stale is visible, never silent, and the summary stays fully readable: its
    /// evidence still resolves, because it still points at its own revision.
    /// </summary>
    public bool IsStaleAgainst(int? selectedTranscriptRevision) =>
        selectedTranscriptRevision is { } selected && selected != TranscriptRevision;
}

/// <summary>Everything known about one session's summarisation.</summary>
public sealed record SummaryState
{
    public static readonly SummaryState Empty = new();

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("state")]
    public ProcessingStageState Stage { get; init; } = ProcessingStageState.NotRequested;

    [JsonPropertyName("selected_revision")]
    public int? SelectedRevision { get; init; }

    [JsonPropertyName("revisions")]
    public IReadOnlyList<SummaryRevisionRecord> Revisions { get; init; } = [];

    [JsonPropertyName("current_job")]
    public SummaryJobRecord? CurrentJob { get; init; }

    [JsonPropertyName("highest_allocated_revision")]
    public int HighestAllocatedRevision { get; init; }

    public SummaryRevisionRecord? Selected =>
        SelectedRevision is { } revision ? Revisions.FirstOrDefault(r => r.Revision == revision) : null;

    public bool HasSummary => Selected is not null;
}

/// <summary>The in-flight or most recent summarisation attempt. Progress lives here, never in the journal.</summary>
public sealed record SummaryJobRecord
{
    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("revision")]
    public required int Revision { get; init; }

    [JsonPropertyName("state")]
    public required ProcessingStageState State { get; init; }

    [JsonPropertyName("transcript_revision")]
    public int TranscriptRevision { get; init; }

    [JsonPropertyName("queued_utc")]
    public DateTimeOffset? QueuedUtc { get; init; }

    [JsonPropertyName("started_utc")]
    public DateTimeOffset? StartedUtc { get; init; }

    [JsonPropertyName("completed_utc")]
    public DateTimeOffset? CompletedUtc { get; init; }

    [JsonPropertyName("stage")]
    public string Stage { get; init; } = "handshake";

    [JsonPropertyName("completed_units")]
    public int CompletedUnits { get; init; }

    [JsonPropertyName("total_units")]
    public int TotalUnits { get; init; }

    [JsonPropertyName("failure_code")]
    public string? FailureCode { get; init; }

    /// <summary>Safe for the user by construction: chosen from the outcome, never worker text.</summary>
    [JsonPropertyName("failure_summary")]
    public string? FailureSummary { get; init; }

    public double Fraction => TotalUnits <= 0 ? 0 : Math.Clamp((double)CompletedUnits / TotalUnits, 0, 1);
}
