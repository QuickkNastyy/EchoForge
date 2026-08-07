using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Evaluation;

/// <summary>
/// A measured proportion that is honest about having no denominator.
///
/// <para>
/// A meeting with no gold decisions has no decision recall. Reporting that as 0% would make a
/// model look like it missed something that was never there, and reporting it as 100% would let a
/// corpus of empty meetings pass an acceptance gate. Both are worse than saying "not applicable",
/// so <see cref="Value"/> is null when <see cref="Denominator"/> is zero and aggregation adds the
/// counts rather than averaging the ratios.
/// </para>
/// </summary>
public sealed record Ratio
{
    public static readonly Ratio None = new() { Numerator = 0, Denominator = 0 };

    [JsonPropertyName("numerator")]
    public required int Numerator { get; init; }

    [JsonPropertyName("denominator")]
    public required int Denominator { get; init; }

    [JsonPropertyName("value")]
    public double? Value => Denominator == 0 ? null : (double)Numerator / Denominator;

    [JsonIgnore]
    public bool IsApplicable => Denominator > 0;

    public static Ratio Of(int numerator, int denominator) => new() { Numerator = numerator, Denominator = denominator };

    /// <summary>Counts add; ratios do not average. Aggregating any other way weights small meetings equally.</summary>
    public static Ratio operator +(Ratio left, Ratio right) =>
        Of(left.Numerator + right.Numerator, left.Denominator + right.Denominator);

    public override string ToString() =>
        Denominator == 0
            ? "n/a"
            : string.Create(CultureInfo.InvariantCulture, $"{Value * 100:F1}% ({Numerator}/{Denominator})");
}

/// <summary>Why one predicted item was or was not tied to a gold item, kept so a human can check.</summary>
public sealed record MatchDecision
{
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("predicted_id")]
    public string? PredictedId { get; init; }

    [JsonPropertyName("predicted_text")]
    public string? PredictedText { get; init; }

    [JsonPropertyName("gold_id")]
    public string? GoldId { get; init; }

    [JsonPropertyName("gold_text")]
    public string? GoldText { get; init; }

    /// <summary>Word-overlap score, or 1 when an exact or annotator-accepted wording matched.</summary>
    [JsonPropertyName("similarity")]
    public double Similarity { get; init; }

    [JsonPropertyName("shared_evidence")]
    public IReadOnlyList<string> SharedEvidence { get; init; } = [];

    /// <summary>matched | false_positive | false_negative</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Everything measured about one model's summary of one meeting.</summary>
public sealed record MeetingScore
{
    [JsonPropertyName("meeting_id")]
    public required string MeetingId { get; init; }

    /// <summary>False when the run produced nothing scoreable. Counted, never silently skipped.</summary>
    [JsonPropertyName("produced_summary")]
    public bool ProducedSummary { get; init; } = true;

    [JsonPropertyName("failure_reason")]
    public string? FailureReason { get; init; }

    [JsonPropertyName("decision_precision")]
    public Ratio DecisionPrecision { get; init; } = Ratio.None;

    [JsonPropertyName("decision_recall")]
    public Ratio DecisionRecall { get; init; } = Ratio.None;

    [JsonPropertyName("action_precision")]
    public Ratio ActionPrecision { get; init; } = Ratio.None;

    [JsonPropertyName("action_recall")]
    public Ratio ActionRecall { get; init; } = Ratio.None;

    [JsonPropertyName("combined_precision")]
    public Ratio CombinedPrecision { get; init; } = Ratio.None;

    [JsonPropertyName("combined_recall")]
    public Ratio CombinedRecall { get; init; } = Ratio.None;

    /// <summary>Of matched actions whose gold owner is a real name, how many were named exactly.</summary>
    [JsonPropertyName("owner_precision")]
    public Ratio OwnerPrecision { get; init; } = Ratio.None;

    [JsonPropertyName("date_precision")]
    public Ratio DatePrecision { get; init; } = Ratio.None;

    /// <summary>Predicted citations that resolve in the transcript. The gate requires 100%.</summary>
    [JsonPropertyName("evidence_validity")]
    public Ratio EvidenceValidity { get; init; } = Ratio.None;

    /// <summary>Matched items whose citations overlap the gold's. Cited *something*, but the right thing?</summary>
    [JsonPropertyName("evidence_coverage")]
    public Ratio EvidenceCoverage { get; init; } = Ratio.None;

    [JsonPropertyName("key_point_coverage")]
    public Ratio KeyPointCoverage { get; init; } = Ratio.None;

    /// <summary>Gold contradictions where both sides survived into the summary.</summary>
    [JsonPropertyName("contradiction_handling")]
    public Ratio ContradictionHandling { get; init; } = Ratio.None;

    /// <summary>
    /// Actions claiming an explicit owner the gold does not support. The plan's gate allows none.
    /// </summary>
    [JsonPropertyName("unsupported_explicit_owners")]
    public int UnsupportedExplicitOwners { get; init; }

    [JsonPropertyName("unsupported_explicit_dates")]
    public int UnsupportedExplicitDates { get; init; }

    /// <summary>Gold unknowns the summary left unknown, instead of filling them in.</summary>
    [JsonPropertyName("unknown_owner_preserved")]
    public Ratio UnknownOwnerPreserved { get; init; } = Ratio.None;

    [JsonPropertyName("unknown_date_preserved")]
    public Ratio UnknownDatePreserved { get; init; } = Ratio.None;

    [JsonPropertyName("decisions")]
    public IReadOnlyList<MatchDecision> Decisions { get; init; } = [];

    [JsonPropertyName("run")]
    public RunMeasurements? Run { get; init; }
}

/// <summary>
/// What one production run cost, measured rather than estimated.
///
/// <para>
/// Deliberately carries no transcript text and no summary text. This is telemetry: it is written
/// to report files, quoted in comparisons, and read by people who are not the meeting's
/// participants. Numbers and identities only.
/// </para>
/// </summary>
public sealed record RunMeasurements
{
    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = string.Empty;

    [JsonPropertyName("model_revision")]
    public string ModelRevision { get; init; } = string.Empty;

    [JsonPropertyName("quantization")]
    public string Quantization { get; init; } = string.Empty;

    [JsonPropertyName("llama_version")]
    public string LlamaVersion { get; init; } = string.Empty;

    [JsonPropertyName("prompt_version")]
    public string PromptVersion { get; init; } = string.Empty;

    [JsonPropertyName("requested_context")]
    public int RequestedContext { get; init; }

    [JsonPropertyName("actual_context")]
    public int ActualContext { get; init; }

    [JsonPropertyName("requested_gpu_layers")]
    public int RequestedGpuLayers { get; init; }

    [JsonPropertyName("kv_cache_type")]
    public string KvCacheType { get; init; } = string.Empty;

    /// <summary>The rung of the ladder that actually ran. Not always the one that was asked for.</summary>
    [JsonPropertyName("runtime_tier")]
    public string RuntimeTier { get; init; } = string.Empty;

    [JsonPropertyName("fell_back")]
    public bool FellBack { get; init; }

    [JsonPropertyName("fallback_steps")]
    public IReadOnlyList<string> FallbackSteps { get; init; } = [];

    [JsonPropertyName("used_cpu_only")]
    public bool UsedCpuOnly { get; init; }

    [JsonPropertyName("oom_retries")]
    public int OomRetries { get; init; }

    [JsonPropertyName("total_seconds")]
    public double TotalSeconds { get; init; }

    [JsonPropertyName("model_load_seconds")]
    public double ModelLoadSeconds { get; init; }

    [JsonPropertyName("extraction_seconds")]
    public double ExtractionSeconds { get; init; }

    [JsonPropertyName("synthesis_seconds")]
    public double SynthesisSeconds { get; init; }

    [JsonPropertyName("repair_seconds")]
    public double RepairSeconds { get; init; }

    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; init; }

    [JsonPropertyName("prompt_tokens_per_second")]
    public double PromptTokensPerSecond { get; init; }

    [JsonPropertyName("generation_tokens_per_second")]
    public double GenerationTokensPerSecond { get; init; }

    /// <summary>Best available measurement, in bytes. Zero when nothing could measure it.</summary>
    [JsonPropertyName("peak_vram_bytes")]
    public long PeakVramBytes { get; init; }

    [JsonPropertyName("vram_source")]
    public string VramSource { get; init; } = string.Empty;

    [JsonPropertyName("repair_attempts")]
    public int RepairAttempts { get; init; }

    [JsonPropertyName("synthesis_levels")]
    public int SynthesisLevels { get; init; }

    [JsonPropertyName("chunks")]
    public int Chunks { get; init; }
}

/// <summary>One model measured across one corpus.</summary>
public sealed record ModelEvaluation
{
    [JsonPropertyName("backend")]
    public required string Backend { get; init; }

    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = string.Empty;

    [JsonPropertyName("model_revision")]
    public string ModelRevision { get; init; } = string.Empty;

    [JsonPropertyName("meetings")]
    public IReadOnlyList<MeetingScore> Meetings { get; init; } = [];

    [JsonPropertyName("combined_precision")]
    public Ratio CombinedPrecision { get; init; } = Ratio.None;

    [JsonPropertyName("combined_recall")]
    public Ratio CombinedRecall { get; init; } = Ratio.None;

    [JsonPropertyName("owner_precision")]
    public Ratio OwnerPrecision { get; init; } = Ratio.None;

    [JsonPropertyName("date_precision")]
    public Ratio DatePrecision { get; init; } = Ratio.None;

    [JsonPropertyName("evidence_validity")]
    public Ratio EvidenceValidity { get; init; } = Ratio.None;

    [JsonPropertyName("evidence_coverage")]
    public Ratio EvidenceCoverage { get; init; } = Ratio.None;

    [JsonPropertyName("key_point_coverage")]
    public Ratio KeyPointCoverage { get; init; } = Ratio.None;

    [JsonPropertyName("contradiction_handling")]
    public Ratio ContradictionHandling { get; init; } = Ratio.None;

    [JsonPropertyName("unsupported_explicit_owners")]
    public int UnsupportedExplicitOwners { get; init; }

    [JsonPropertyName("unsupported_explicit_dates")]
    public int UnsupportedExplicitDates { get; init; }

    /// <summary>Meetings that produced nothing scoreable, over meetings attempted.</summary>
    [JsonPropertyName("failure_rate")]
    public Ratio FailureRate { get; init; } = Ratio.None;

    [JsonPropertyName("median_seconds")]
    public double MedianSeconds { get; init; }

    [JsonPropertyName("total_seconds")]
    public double TotalSeconds { get; init; }

    [JsonPropertyName("peak_vram_bytes")]
    public long PeakVramBytes { get; init; }
}

/// <summary>Whether a set of numbers clears the plan's acceptance targets.</summary>
public sealed record AcceptanceVerdict
{
    /// <summary>≥95% precision for emitted actions and decisions.</summary>
    public const double MinimumPrecision = 0.95;

    /// <summary>≥85% recall on the annotated set.</summary>
    public const double MinimumRecall = 0.85;

    /// <summary>100% valid evidence references. Not a target that rounds.</summary>
    public const double RequiredEvidenceValidity = 1.0;

    [JsonPropertyName("passed")]
    public required bool Passed { get; init; }

    /// <summary>
    /// False whenever the numbers did not come from the held-out release corpus.
    ///
    /// <para>
    /// A development or synthetic run can satisfy every threshold and still not be an acceptance
    /// result, and this is the field that says so rather than leaving it to a caption.
    /// </para>
    /// </summary>
    [JsonPropertyName("is_acceptance_run")]
    public required bool IsAcceptanceRun { get; init; }

    [JsonPropertyName("failures")]
    public IReadOnlyList<string> Failures { get; init; } = [];

    [JsonPropertyName("statement")]
    public required string Statement { get; init; }
}

/// <summary>One complete evaluation, machine-readable.</summary>
public sealed record EvaluationReport
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("corpus_id")]
    public required string CorpusId { get; init; }

    [JsonPropertyName("corpus_kind")]
    public required CorpusKind CorpusKind { get; init; }

    [JsonPropertyName("corpus_sha256")]
    public string CorpusSha256 { get; init; } = string.Empty;

    [JsonPropertyName("generated_utc")]
    public DateTimeOffset GeneratedUtc { get; init; }

    [JsonPropertyName("prompt_versions")]
    public IReadOnlyList<string> PromptVersions { get; init; } = [];

    [JsonPropertyName("models")]
    public IReadOnlyList<ModelEvaluation> Models { get; init; } = [];

    [JsonPropertyName("acceptance")]
    public AcceptanceVerdict? Acceptance { get; init; }

    [JsonPropertyName("bakeoff")]
    public BakeoffVerdict? Bakeoff { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

/// <summary>Whether the comparison model has earned the default, under the preregistered rule.</summary>
public sealed record BakeoffVerdict
{
    [JsonPropertyName("incumbent")]
    public required string Incumbent { get; init; }

    [JsonPropertyName("challenger")]
    public required string Challenger { get; init; }

    [JsonPropertyName("incumbent_composite")]
    public double? IncumbentComposite { get; init; }

    [JsonPropertyName("challenger_composite")]
    public double? ChallengerComposite { get; init; }

    [JsonPropertyName("margin_points")]
    public double? MarginPoints { get; init; }

    [JsonPropertyName("should_switch_default")]
    public required bool ShouldSwitchDefault { get; init; }

    [JsonPropertyName("reasons")]
    public IReadOnlyList<string> Reasons { get; init; } = [];
}
