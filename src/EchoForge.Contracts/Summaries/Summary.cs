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

    /// <summary>Everything that carries evidence, for validation and for the UI.</summary>
    public IEnumerable<SummaryItem> AllItems =>
        KeyPoints.Concat(Decisions).Concat(OpenQuestions).Concat(Risks).Concat(Blockers);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
