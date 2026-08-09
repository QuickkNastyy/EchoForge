using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Summaries;

/// <summary>
/// How well the transcript supports a claim.
///
/// <para>
/// The three values are not a confidence scale. <see cref="Explicit"/> means the transcript says
/// so directly; <see cref="Inferred"/> means this is a reading of it and is shown separately;
/// <see cref="Unknown"/> is the honest answer when the transcript does not say. Nothing may raise
/// a value later — a synthesis pass that promoted an inference to explicit would be inventing
/// support that never existed.
/// </para>
/// </summary>
public enum SupportStatus
{
    Unknown,
    Inferred,
    Explicit,
}

/// <summary>Wire names for <see cref="SupportStatus"/>, spelled out rather than derived.</summary>
public static class SupportStatuses
{
    public const string Explicit = "explicit";
    public const string Inferred = "inferred";
    public const string Unknown = "unknown";

    public static string ToWire(SupportStatus status) => status switch
    {
        SupportStatus.Explicit => Explicit,
        SupportStatus.Inferred => Inferred,
        SupportStatus.Unknown => Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "unknown support status"),
    };

    public static bool TryParse(string? wire, out SupportStatus status)
    {
        switch (wire)
        {
            case Explicit: status = SupportStatus.Explicit; return true;
            case Inferred: status = SupportStatus.Inferred; return true;
            case Unknown: status = SupportStatus.Unknown; return true;
            default: status = SupportStatus.Unknown; return false;
        }
    }
}

/// <summary>
/// One citation into a transcript.
///
/// <para>
/// The identity is the pair <see cref="TranscriptRevision"/> + <see cref="SegmentId"/>. A bare
/// segment ID is stable only inside one revision, so it is not a durable reference, and a summary
/// always opens the exact revision it was written from rather than the newest one.
/// </para>
///
/// <para>
/// The times are derived by EchoForge from the cited segment. They are never taken from model
/// output: a model that mis-states a timestamp would send the user to the wrong audio and look
/// authoritative doing it.
/// </para>
/// </summary>
public sealed record SummaryEvidence
{
    [JsonPropertyName("transcript_revision")]
    public required int TranscriptRevision { get; init; }

    [JsonPropertyName("segment_id")]
    public required string SegmentId { get; init; }

    [JsonPropertyName("source_track")]
    public required string SourceTrack { get; init; }

    [JsonPropertyName("start_seconds")]
    public required double StartSeconds { get; init; }

    [JsonPropertyName("end_seconds")]
    public required double EndSeconds { get; init; }

    [JsonPropertyName("display_timestamp")]
    public required string DisplayTimestamp { get; init; }

    public static string Display(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}");
    }
}

/// <summary>A key point, question, risk, or blocker.</summary>
public record SummaryItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("certainty")]
    public required string Certainty { get; init; }

    /// <summary>
    /// A model's own number. Not statistically calibrated, labelled heuristic wherever it is
    /// shown, and never what decides <see cref="Certainty"/>.
    /// </summary>
    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    [JsonPropertyName("evidence")]
    public IReadOnlyList<SummaryEvidence> Evidence { get; init; } = [];

    public SupportStatus Support =>
        SupportStatuses.TryParse(Certainty, out SupportStatus status) ? status : SupportStatus.Unknown;
}

/// <summary>
/// A commitment somebody made.
///
/// <para>
/// Owner and due date each carry their own status, because a task can be perfectly explicit while
/// nobody said who would do it. Coupling them to the item's overall certainty would force a
/// choice between dropping a real action and inventing an owner for it.
/// </para>
/// </summary>
public sealed record SummaryAction
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("task")]
    public required string Task { get; init; }

    [JsonPropertyName("certainty")]
    public required string Certainty { get; init; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    [JsonPropertyName("evidence")]
    public IReadOnlyList<SummaryEvidence> Evidence { get; init; } = [];

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("owner_status")]
    public required string OwnerStatus { get; init; }

    /// <summary>Only ever set when a known meeting date made an unambiguous date resolvable.</summary>
    [JsonPropertyName("due_date")]
    public string? DueDate { get; init; }

    /// <summary>What was actually said. Kept even when no date could be resolved from it.</summary>
    [JsonPropertyName("due_date_text")]
    public string? DueDateText { get; init; }

    [JsonPropertyName("due_date_status")]
    public required string DueDateStatus { get; init; }

    /// <summary>
    /// How this commitment relates to the meeting. Null in schema-v1/v2 history, where every
    /// imperative sentence was an action item and "stop the recording" therefore was one.
    /// </summary>
    [JsonPropertyName("classification")]
    public string? Classification { get; init; }

    /// <summary>True when this is work that still exists after the call. Legacy actions count.</summary>
    public bool IsOutstandingWork =>
        Classification is null || ActionClassifications.IsOutstandingWork(Classification);

    public SupportStatus Support =>
        SupportStatuses.TryParse(Certainty, out SupportStatus status) ? status : SupportStatus.Unknown;

    public SupportStatus OwnerSupport =>
        SupportStatuses.TryParse(OwnerStatus, out SupportStatus status) ? status : SupportStatus.Unknown;

    public SupportStatus DueDateSupport =>
        SupportStatuses.TryParse(DueDateStatus, out SupportStatus status) ? status : SupportStatus.Unknown;
}

/// <summary>What produced a summary.</summary>
/// <param name="ProducesSummaries">
/// False for any placeholder backend, and surfaced rather than hidden. Placeholder prose is not a
/// summary of anything that was said.
/// </param>
public sealed record SummaryModel(
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("context_tokens")] int ContextTokens,
    [property: JsonPropertyName("thinking")] bool Thinking,
    [property: JsonPropertyName("produces_summaries")] bool ProducesSummaries,
    [property: JsonPropertyName("worker_version")] string WorkerVersion);

/// <summary>
/// How the extracted facts were folded together.
///
/// <para>
/// A meeting long enough to exceed one pass is merged hierarchically, and the shape of that fold
/// is recorded rather than left implicit: a reader who wonders why two similar decisions both
/// survived is owed the answer that they were never considered side by side.
/// </para>
/// </summary>
/// <param name="ReachedLevelCap">
/// True when the fold stopped at its backstop. It stops holding everything it had — nothing is
/// dropped to make the result fit — so this is a note about effort, not about completeness.
/// </param>
public sealed record SummarySynthesis(
    [property: JsonPropertyName("levels")] int Levels,
    [property: JsonPropertyName("groups")] int Groups,
    [property: JsonPropertyName("merged_items")] int MergedItems,
    [property: JsonPropertyName("reached_level_cap")] bool ReachedLevelCap);

/// <summary>One prose claim whose support remains expandable in the UI.</summary>
public sealed record SummaryNarrativeBlock
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>Validated structured fact IDs this prose restates.</summary>
    [JsonPropertyName("supporting_item_ids")]
    public IReadOnlyList<string> SupportingItemIds { get; init; } = [];

    [JsonPropertyName("evidence")]
    public IReadOnlyList<SummaryEvidence> Evidence { get; init; } = [];
}

/// <summary>
/// Why a plan step says what it says.
///
/// <para>
/// The three are not a confidence scale either. <see cref="Explicit"/> is something the meeting
/// stated; <see cref="GroundedInference"/> is a relationship the meeting implied strongly enough
/// to act on — "do A before B" when the meeting said B needs A — and <see cref="Recommendation"/>
/// is EchoForge ordering work the meeting left unordered. Keeping them apart is what lets the
/// brief be useful about sequencing without presenting sequencing as something somebody said.
/// </para>
/// </summary>
public enum PlanBasis
{
    Recommendation,
    GroundedInference,
    Explicit,
}

public static class PlanBases
{
    public const string Explicit = "explicit";
    public const string GroundedInference = "grounded_inference";
    public const string Recommendation = "recommendation";

    public static bool TryParse(string? wire, out PlanBasis basis)
    {
        switch (wire)
        {
            case Explicit: basis = PlanBasis.Explicit; return true;
            case GroundedInference: basis = PlanBasis.GroundedInference; return true;
            case Recommendation: basis = PlanBasis.Recommendation; return true;
            default: basis = PlanBasis.Recommendation; return false;
        }
    }
}

/// <summary>Who a plan step belongs to. Never guessed from a name.</summary>
public static class PlanAudiences
{
    /// <summary>The person holding the microphone — the "You" track.</summary>
    public const string You = "you";

    /// <summary>Somebody else who was named as doing the work.</summary>
    public const string Others = "others";

    /// <summary>Real work nobody was assigned. The common case, and not a gap to fill.</summary>
    public const string Unassigned = "unassigned";

    public static bool IsKnown(string? value) =>
        value is You or Others or Unassigned;
}

/// <summary>When a plan step wants attention, taken from the meeting rather than from convention.</summary>
public static class PlanTimings
{
    public const string Immediate = "immediate";
    public const string Next = "next";
    public const string Later = "later";

    public static bool IsKnown(string? value) => value is Immediate or Next or Later;

    public static int Rank(string? value) => value switch
    {
        Immediate => 0,
        Next => 1,
        Later => 2,
        _ => 3,
    };
}

/// <summary>
/// How an extracted commitment relates to the meeting that produced it.
///
/// <para>
/// This is the distinction that separates a meeting assistant from a transcript indexer. "Stop the
/// recording" and "Jordan will grant Alex access" are both imperative sentences somebody said; only
/// one of them is work that still exists once the call ends. Only
/// <see cref="PostMeetingCommitment"/> and <see cref="InferredNextStep"/> ever reach the action
/// plan; the rest are kept, classified, and shown somewhere they cannot be mistaken for a task.
/// </para>
/// </summary>
public static class ActionClassifications
{
    /// <summary>Work that outlives the meeting. The only kind the plan is built from.</summary>
    public const string PostMeetingCommitment = "post_meeting_commitment";

    /// <summary>An instruction about the meeting itself: stop the recording, share your screen.</summary>
    public const string EphemeralInstruction = "ephemeral_instruction";

    /// <summary>Asked for and done while everybody watched. Nothing remains.</summary>
    public const string CompletedInMeeting = "completed_in_meeting";

    /// <summary>Something the meeting wanted eventually. Backlog, not this week.</summary>
    public const string FutureIdea = "future_idea";

    /// <summary>Nobody said it outright, but the meeting clearly leaves it to be done.</summary>
    public const string InferredNextStep = "inferred_next_step";

    public static bool IsKnown(string? value) =>
        value is PostMeetingCommitment or EphemeralInstruction or CompletedInMeeting
            or FutureIdea or InferredNextStep;

    /// <summary>True for the two classes that describe work still outstanding.</summary>
    public static bool IsOutstandingWork(string? value) =>
        value is PostMeetingCommitment or InferredNextStep;
}

/// <summary>
/// One numbered step of the action plan.
///
/// <para>
/// A step is prose a person can act on, plus the machinery that keeps it honest: what it depends
/// on, whether the ordering was stated or reasoned, and the same evidence every other claim
/// carries. Owner and due date keep their own statuses, for the same reason they do on
/// <see cref="SummaryAction"/> — a task can be perfectly real while nobody agreed to own it.
/// </para>
/// </summary>
public sealed record MeetingPlanStep
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>1-based position in the plan. The order is the product.</summary>
    [JsonPropertyName("order")]
    public required int Order { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Why it matters, and what happens around it. Empty when the meeting said only what.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;

    [JsonPropertyName("audience")]
    public string Audience { get; init; } = PlanAudiences.Unassigned;

    [JsonPropertyName("timing")]
    public string Timing { get; init; } = PlanTimings.Next;

    [JsonPropertyName("basis")]
    public string Basis { get; init; } = PlanBases.Explicit;

    /// <summary>What must happen first, in the meeting's own words. Null when nothing does.</summary>
    [JsonPropertyName("depends_on")]
    public string? DependsOn { get; init; }

    /// <summary>Other step IDs in this same plan that have to come first.</summary>
    [JsonPropertyName("depends_on_step_ids")]
    public IReadOnlyList<string> DependsOnStepIds { get; init; } = [];

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("owner_status")]
    public string OwnerStatus { get; init; } = SupportStatuses.Unknown;

    [JsonPropertyName("due_date")]
    public string? DueDate { get; init; }

    [JsonPropertyName("due_date_text")]
    public string? DueDateText { get; init; }

    [JsonPropertyName("due_date_status")]
    public string DueDateStatus { get; init; } = SupportStatuses.Unknown;

    [JsonPropertyName("supporting_item_ids")]
    public IReadOnlyList<string> SupportingItemIds { get; init; } = [];

    [JsonPropertyName("evidence")]
    public IReadOnlyList<SummaryEvidence> Evidence { get; init; } = [];

    public PlanBasis Support => PlanBases.TryParse(Basis, out PlanBasis basis) ? basis : PlanBasis.Recommendation;

    public SupportStatus OwnerSupport =>
        SupportStatuses.TryParse(OwnerStatus, out SupportStatus status) ? status : SupportStatus.Unknown;

    public SupportStatus DueDateSupport =>
        SupportStatuses.TryParse(DueDateStatus, out SupportStatus status) ? status : SupportStatus.Unknown;
}

/// <summary>
/// The document a person actually reads after a meeting.
///
/// <para>
/// Sections are omitted rather than rendered empty. A brief that prints "Risks: none" for every
/// meeting teaches the reader to skim past the one meeting where it says something, and a section
/// per schema property is how a summary ends up saying the same thing four ways.
/// </para>
/// </summary>
public sealed record MeetingBrief
{
    /// <summary>What the meeting was about, what happened, what changed. Never a count of objects.</summary>
    [JsonPropertyName("summary")]
    public IReadOnlyList<SummaryNarrativeBlock> Summary { get; init; } = [];

    /// <summary>The ordered plan. Split by audience for display; ordered as one sequence.</summary>
    [JsonPropertyName("action_plan")]
    public IReadOnlyList<MeetingPlanStep> ActionPlan { get; init; } = [];

    [JsonPropertyName("decisions")]
    public IReadOnlyList<SummaryNarrativeBlock> Decisions { get; init; } = [];

    [JsonPropertyName("blockers")]
    public IReadOnlyList<SummaryNarrativeBlock> Blockers { get; init; } = [];

    [JsonPropertyName("important_context")]
    public IReadOnlyList<SummaryNarrativeBlock> ImportantContext { get; init; } = [];

    [JsonPropertyName("follow_ups")]
    public IReadOnlyList<SummaryNarrativeBlock> FollowUps { get; init; } = [];

    [JsonPropertyName("open_questions")]
    public IReadOnlyList<SummaryNarrativeBlock> OpenQuestions { get; init; } = [];

    /// <summary>Discussed, wanted, not now. The section that keeps the plan short enough to obey.</summary>
    [JsonPropertyName("backlog")]
    public IReadOnlyList<SummaryNarrativeBlock> Backlog { get; init; } = [];

    [JsonPropertyName("risks")]
    public IReadOnlyList<SummaryNarrativeBlock> Risks { get; init; } = [];

    public IEnumerable<SummaryNarrativeBlock> AllBlocks =>
        Summary.Concat(Decisions).Concat(Blockers).Concat(ImportantContext)
            .Concat(FollowUps).Concat(OpenQuestions).Concat(Backlog).Concat(Risks);

    /// <summary>Steps the reader owns, or that nobody claimed. The "what do I do now" answer.</summary>
    public IEnumerable<MeetingPlanStep> YourSteps =>
        ActionPlan.Where(step => step.Audience is not PlanAudiences.Others);

    public IEnumerable<MeetingPlanStep> OtherPeoplesSteps =>
        ActionPlan.Where(step => step.Audience is PlanAudiences.Others);
}

/// <summary>A human-readable synthesis layered over, and restricted to, validated facts.</summary>
public sealed record SummaryNarrative
{
    [JsonPropertyName("summary")]
    public IReadOnlyList<SummaryNarrativeBlock> Summary { get; init; } = [];

    [JsonPropertyName("main_topics")]
    public IReadOnlyList<SummaryNarrativeBlock> MainTopics { get; init; } = [];

    [JsonPropertyName("important_details")]
    public IReadOnlyList<SummaryNarrativeBlock> ImportantDetails { get; init; } = [];

    [JsonPropertyName("follow_ups")]
    public IReadOnlyList<SummaryNarrativeBlock> FollowUps { get; init; } = [];

    public IEnumerable<SummaryNarrativeBlock> AllBlocks =>
        Summary.Concat(MainTopics).Concat(ImportantDetails).Concat(FollowUps);
}

/// <summary>Content-free performance and fallback telemetry for one local summary run.</summary>
public sealed record SummaryRunMetadata
{
    [JsonPropertyName("requested_context")]
    public int RequestedContext { get; init; }

    [JsonPropertyName("actual_context")]
    public int ActualContext { get; init; }

    [JsonPropertyName("runtime_tier")]
    public string RuntimeTier { get; init; } = string.Empty;

    [JsonPropertyName("model_load_seconds")]
    public double ModelLoadSeconds { get; init; }

    [JsonPropertyName("extraction_seconds")]
    public double ExtractionSeconds { get; init; }

    [JsonPropertyName("synthesis_seconds")]
    public double SynthesisSeconds { get; init; }

    [JsonPropertyName("narrative_seconds")]
    public double NarrativeSeconds { get; init; }

    [JsonPropertyName("total_seconds")]
    public double TotalSeconds { get; init; }

    [JsonPropertyName("generation_tokens_per_second")]
    public double GenerationTokensPerSecond { get; init; }

    [JsonPropertyName("prompt_tokens_per_second")]
    public double PromptTokensPerSecond { get; init; }

    [JsonPropertyName("peak_vram_bytes")]
    public long PeakVramBytes { get; init; }

    [JsonPropertyName("fell_back")]
    public bool FellBack { get; init; }

    [JsonPropertyName("fallback_steps")]
    public IReadOnlyList<string> FallbackSteps { get; init; } = [];

    [JsonPropertyName("oom_retries")]
    public int OomRetries { get; init; }
}

/// <summary>
/// One immutable summary revision.
///
/// <para>
/// Mirrors <c>schemas/summary.schema.json</c>, which stays authoritative. Regenerating produces a
/// new revision; an existing one is never edited, because a summary that changed underneath a
/// reader would make its own citations unverifiable.
/// </para>
/// </summary>
public sealed record SummaryDocument
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("summary_revision")]
    public required int SummaryRevision { get; init; }

    [JsonPropertyName("created_at_utc")]
    public required DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("transcript_revision")]
    public required int TranscriptRevision { get; init; }

    [JsonPropertyName("transcript_sha256")]
    public required string TranscriptSha256 { get; init; }

    /// <summary>Null when unknown, which is what makes a relative date unresolvable.</summary>
    [JsonPropertyName("meeting_date")]
    public string? MeetingDate { get; init; }

    [JsonPropertyName("prompt_version")]
    public required string PromptVersion { get; init; }

    [JsonPropertyName("model")]
    public required SummaryModel Model { get; init; }

    [JsonPropertyName("run")]
    public SummaryRunMetadata? Run { get; init; }

    /// <summary>
    /// 0 when this was generated first time, 1 when it came from the one bounded re-ask.
    ///
    /// <para>
    /// Anything above 1 means the bound was not honoured, which the validator refuses. A repair
    /// is a second chance at answering, never a second standard to be judged by.
    /// </para>
    /// </summary>
    [JsonPropertyName("repair_attempt")]
    public int RepairAttempt { get; init; }

    /// <summary>Null for documents written before the fold was recorded.</summary>
    [JsonPropertyName("synthesis")]
    public SummarySynthesis? Synthesis { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("overview")]
    public string Overview { get; init; } = string.Empty;

    /// <summary>Null for schema-v1 history and for the explicitly labelled placeholder.</summary>
    [JsonPropertyName("narrative")]
    public SummaryNarrative? Narrative { get; init; }

    /// <summary>
    /// The meeting brief. Null for every document written before schema v3 and for the
    /// placeholder, both of which stay fully readable through <see cref="Narrative"/>.
    /// </summary>
    [JsonPropertyName("brief")]
    public MeetingBrief? Brief { get; init; }

    [JsonPropertyName("key_points")]
    public IReadOnlyList<SummaryItem> KeyPoints { get; init; } = [];

    [JsonPropertyName("decisions")]
    public IReadOnlyList<SummaryItem> Decisions { get; init; } = [];

    [JsonPropertyName("action_items")]
    public IReadOnlyList<SummaryAction> ActionItems { get; init; } = [];

    [JsonPropertyName("open_questions")]
    public IReadOnlyList<SummaryItem> OpenQuestions { get; init; } = [];

    [JsonPropertyName("risks")]
    public IReadOnlyList<SummaryItem> Risks { get; init; } = [];

    [JsonPropertyName("blockers")]
    public IReadOnlyList<SummaryItem> Blockers { get; init; } = [];

    /// <summary>
    /// Wanted eventually rather than now. Empty in schema-v1/v2 history, where a speculative
    /// aside and an assignment for tomorrow both landed in the same list.
    /// </summary>
    [JsonPropertyName("future_ideas")]
    public IReadOnlyList<SummaryItem> FutureIdeas { get; init; } = [];

    /// <summary>Worth remembering, not itself work. Empty in schema-v1/v2 history.</summary>
    [JsonPropertyName("important_context")]
    public IReadOnlyList<SummaryItem> ImportantContext { get; init; } = [];

    /// <summary>"A has to happen before B", as the meeting stated it. Empty in v1/v2 history.</summary>
    [JsonPropertyName("dependencies")]
    public IReadOnlyList<SummaryItem> Dependencies { get; init; } = [];

    /// <summary>Everything that carries evidence, for validation and for the UI.</summary>
    public IEnumerable<SummaryItem> AllItems =>
        KeyPoints.Concat(Decisions).Concat(OpenQuestions).Concat(Risks).Concat(Blockers)
            .Concat(FutureIdeas).Concat(ImportantContext).Concat(Dependencies);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
