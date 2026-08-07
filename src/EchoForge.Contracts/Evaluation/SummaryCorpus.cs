using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Evaluation;

/// <summary>
/// What a corpus is for, and what may be concluded from a run against it.
///
/// <para>
/// This is the field that stops a number from being quoted as something it is not. The Phase 3
/// acceptance gate is decided by <see cref="Release"/> and by nothing else; a development score is
/// a working measurement, and a synthetic score says only that the scorer arithmetic works.
/// </para>
/// </summary>
public enum CorpusKind
{
    /// <summary>
    /// Fabricated data that exists to test the harness itself. Never evidence about a model.
    /// Kept in the same format on purpose, so the code paths under test are the real ones.
    /// </summary>
    Synthetic,

    /// <summary>
    /// Human-annotated meetings for day-to-day iteration. Prompts may be tuned against these.
    /// A development result is never an acceptance result.
    /// </summary>
    Development,

    /// <summary>
    /// Human-annotated meetings held out for the acceptance gate. Read only when a candidate is
    /// believed ready, with the criteria fixed beforehand. No prompt tuning against its contents.
    /// </summary>
    Release,
}

/// <summary>How well the transcript a meeting is scored against reflects what was actually said.</summary>
public enum TranscriptFidelity
{
    /// <summary>
    /// Straight speech-recognition output. **Summary quality is never scored against this.**
    /// Two stages fail for different reasons, and a summariser must not be penalised for a word
    /// the recogniser got wrong — that is what the separate STT evaluation is for.
    /// </summary>
    Recognised,

    /// <summary>A human has corrected the text. The only fidelity a summary may be scored on.</summary>
    HumanCorrected,
}

/// <summary>One gold fact a summary is expected to find.</summary>
public sealed record GoldItem
{
    /// <summary>Stable within its meeting. Match decisions are reported against it.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>
    /// The segments that support this fact, in the human-corrected transcript.
    ///
    /// <para>
    /// Evidence is the anchor for matching, not decoration. Two facts that cite no common segment
    /// are two facts, however similarly they are worded.
    /// </para>
    /// </summary>
    [JsonPropertyName("evidence")]
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>
    /// Wordings the annotator accepts as the same fact.
    ///
    /// <para>
    /// The escape hatch that keeps matching conservative without making it useless. A generative
    /// model will not reproduce an annotator's sentence, and the alternative to declaring accepted
    /// variants is loosening the similarity floor for everything.
    /// </para>
    /// </summary>
    [JsonPropertyName("aliases")]
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Free text for whoever has to adjudicate a disputed match later.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>One gold action item, with the owner and date rules the summary must respect.</summary>
public sealed record GoldAction
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("task")]
    public required string Task { get; init; }

    [JsonPropertyName("evidence")]
    public IReadOnlyList<string> Evidence { get; init; } = [];

    [JsonPropertyName("aliases")]
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>
    /// The owner the transcript actually gives, or null when it gives none.
    ///
    /// <para>
    /// A null owner with <c>owner_status: unknown</c> is a **deliberate** annotation, not a gap.
    /// "Someone will write it up" is a meeting declining to assign work, and a summary that
    /// produces an owner for it has invented a commitment. Scored as its own metric.
    /// </para>
    /// </summary>
    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("owner_status")]
    public string OwnerStatus { get; init; } = "unknown";

    [JsonPropertyName("due_date")]
    public string? DueDate { get; init; }

    [JsonPropertyName("due_date_status")]
    public string DueDateStatus { get; init; } = "unknown";

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>
/// A pair of gold facts that disagree, and both of which must survive.
///
/// <para>
/// A meeting that reversed a decision is the case where a tidy summary is a wrong one. Scored
/// explicitly rather than left to be noticed.
/// </para>
/// </summary>
public sealed record GoldContradiction
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>The gold item IDs that contradict each other. At least two.</summary>
    [JsonPropertyName("item_ids")]
    public IReadOnlyList<string> ItemIds { get; init; } = [];

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>Everything a summary of one meeting is expected to contain.</summary>
public sealed record GoldSummary
{
    [JsonPropertyName("decisions")]
    public IReadOnlyList<GoldItem> Decisions { get; init; } = [];

    [JsonPropertyName("action_items")]
    public IReadOnlyList<GoldAction> ActionItems { get; init; } = [];

    [JsonPropertyName("key_points")]
    public IReadOnlyList<GoldItem> KeyPoints { get; init; } = [];

    [JsonPropertyName("open_questions")]
    public IReadOnlyList<GoldItem> OpenQuestions { get; init; } = [];

    [JsonPropertyName("risks")]
    public IReadOnlyList<GoldItem> Risks { get; init; } = [];

    [JsonPropertyName("blockers")]
    public IReadOnlyList<GoldItem> Blockers { get; init; } = [];

    [JsonPropertyName("contradictions")]
    public IReadOnlyList<GoldContradiction> Contradictions { get; init; } = [];

    public IEnumerable<GoldItem> AllItems =>
        Decisions.Concat(KeyPoints).Concat(OpenQuestions).Concat(Risks).Concat(Blockers);
}

/// <summary>One annotated meeting.</summary>
public sealed record CorpusMeeting
{
    /// <summary>Stable and unique across every corpus. How overlap is detected.</summary>
    [JsonPropertyName("meeting_id")]
    public required string MeetingId { get; init; }

    /// <summary>Null when genuinely unknown, which is what makes a relative date unresolvable.</summary>
    [JsonPropertyName("meeting_date")]
    public string? MeetingDate { get; init; }

    /// <summary>Corpus-relative path to the canonical transcript this meeting is scored against.</summary>
    [JsonPropertyName("transcript_path")]
    public required string TranscriptPath { get; init; }

    /// <summary>
    /// Digest of that transcript. Half of the corpus identity, and what makes two corpora
    /// sharing a meeting detectable even if somebody renamed the meeting.
    /// </summary>
    [JsonPropertyName("transcript_sha256")]
    public string? TranscriptSha256 { get; init; }

    [JsonPropertyName("transcript_fidelity")]
    public TranscriptFidelity TranscriptFidelity { get; init; } = TranscriptFidelity.Recognised;

    /// <summary>
    /// True for a meeting that was written rather than recorded. Checked, not trusted: a
    /// development or release corpus containing one is rejected outright.
    /// </summary>
    [JsonPropertyName("synthetic")]
    public bool Synthetic { get; init; }

    [JsonPropertyName("annotator")]
    public string? Annotator { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("gold")]
    public GoldSummary Gold { get; init; } = new();
}

/// <summary>One corpus file: what it is for, and the meetings in it.</summary>
public sealed record SummaryCorpus
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("corpus_id")]
    public required string CorpusId { get; init; }

    /// <summary>What may be concluded from a run against this. See <see cref="CorpusKind"/>.</summary>
    [JsonPropertyName("kind")]
    public required CorpusKind Kind { get; init; }

    [JsonPropertyName("created_utc")]
    public DateTimeOffset? CreatedUtc { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    /// <summary>
    /// How similar a predicted wording has to be before it counts as the same fact, once the
    /// evidence already agrees. Declared per corpus so the number that decides a match is part of
    /// the annotated data rather than buried in the scorer.
    /// </summary>
    [JsonPropertyName("match_threshold")]
    public double MatchThreshold { get; init; } = 0.5;

    [JsonPropertyName("meetings")]
    public IReadOnlyList<CorpusMeeting> Meetings { get; init; } = [];

    /// <summary>True only for the held-out set. The acceptance gate is decided by this and nothing else.</summary>
    [JsonIgnore]
    public bool IsAcceptanceData => Kind == CorpusKind.Release;

    /// <summary>True when nothing here is evidence about a model's behaviour on real meetings.</summary>
    [JsonIgnore]
    public bool IsSynthetic => Kind == CorpusKind.Synthetic;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
