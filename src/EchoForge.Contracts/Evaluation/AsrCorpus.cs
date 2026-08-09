using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Evaluation;

/// <summary>
/// Optional domain terms whose spelling matters independently of aggregate word error rate.
/// Terms are supplied by the corpus author; EchoForge never guesses that a capitalized token is a
/// person's name or that a sequence of letters is an acronym.
/// </summary>
public sealed record AsrReferenceTerms
{
    [JsonPropertyName("proper_names")]
    public IReadOnlyList<string> ProperNames { get; init; } = [];

    [JsonPropertyName("acronyms")]
    public IReadOnlyList<string> Acronyms { get; init; } = [];

    [JsonPropertyName("numeric_expressions")]
    public IReadOnlyList<string> NumericExpressions { get; init; } = [];
}

/// <summary>One recording and, when available, its human-corrected transcript.</summary>
public sealed record AsrCorpusMeeting
{
    [JsonPropertyName("meeting_id")]
    public required string MeetingId { get; init; }

    /// <summary>Corpus-relative source recording/session path. The scorer never modifies it.</summary>
    [JsonPropertyName("recording_path")]
    public required string RecordingPath { get; init; }

    [JsonPropertyName("recording_sha256")]
    public string? RecordingSha256 { get; init; }

    /// <summary>Null is valid: the recording can still be used for human side-by-side review.</summary>
    [JsonPropertyName("reference_transcript_path")]
    public string? ReferenceTranscriptPath { get; init; }

    [JsonPropertyName("reference_transcript_sha256")]
    public string? ReferenceTranscriptSha256 { get; init; }

    [JsonPropertyName("terms")]
    public AsrReferenceTerms Terms { get; init; } = new();

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>A local, user-supplied ASR corpus. It has no network or inference-time dependency.</summary>
public sealed record AsrEvaluationCorpus
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("corpus_id")]
    public required string CorpusId { get; init; }

    [JsonPropertyName("kind")]
    public CorpusKind Kind { get; init; } = CorpusKind.Development;

    [JsonPropertyName("meetings")]
    public IReadOnlyList<AsrCorpusMeeting> Meetings { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>Edit-distance counts underlying WER, kept so corpora aggregate by counts, not averages.</summary>
public sealed record WordErrorCounts(
    [property: JsonPropertyName("substitutions")] int Substitutions,
    [property: JsonPropertyName("deletions")] int Deletions,
    [property: JsonPropertyName("insertions")] int Insertions,
    [property: JsonPropertyName("reference_words")] int ReferenceWords)
{
    [JsonPropertyName("wer")]
    public double? Wer => ReferenceWords == 0
        ? null
        : (double)(Substitutions + Deletions + Insertions) / ReferenceWords;
}

/// <summary>Reference-backed ASR measurements for one immutable transcript revision.</summary>
public sealed record AsrEvaluationScore
{
    [JsonPropertyName("transcript_revision")]
    public int TranscriptRevision { get; init; }

    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = string.Empty;

    [JsonPropertyName("model_revision")]
    public string ModelRevision { get; init; } = string.Empty;

    [JsonPropertyName("word_errors")]
    public WordErrorCounts WordErrors { get; init; } = new(0, 0, 0, 0);

    [JsonPropertyName("normalized_word_errors")]
    public WordErrorCounts NormalizedWordErrors { get; init; } = new(0, 0, 0, 0);

    [JsonPropertyName("short_utterance_recall")]
    public Ratio ShortUtteranceRecall { get; init; } = Ratio.None;

    [JsonPropertyName("proper_name_accuracy")]
    public Ratio ProperNameAccuracy { get; init; } = Ratio.None;

    [JsonPropertyName("acronym_accuracy")]
    public Ratio AcronymAccuracy { get; init; } = Ratio.None;

    [JsonPropertyName("numeric_accuracy")]
    public Ratio NumericAccuracy { get; init; } = Ratio.None;

    [JsonPropertyName("speech_region_recall")]
    public Ratio SpeechRegionRecall { get; init; } = Ratio.None;

    [JsonPropertyName("source_attribution_accuracy")]
    public Ratio SourceAttributionAccuracy { get; init; } = Ratio.None;

    /// <summary>Mean absolute start/end error for text-matched regions, or null when none match.</summary>
    [JsonPropertyName("mean_timestamp_error_seconds")]
    public double? MeanTimestampErrorSeconds { get; init; }
}
