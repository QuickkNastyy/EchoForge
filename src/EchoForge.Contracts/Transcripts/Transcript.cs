using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Transcripts;

/// <summary>
/// One immutable transcript revision. The shape here mirrors
/// <c>schemas/transcript.schema.json</c>, which stays authoritative: these types exist so C# can
/// read and write the file without hand-rolling JSON, not so they can drift from it.
///
/// <para>
/// Segment IDs are stable only inside one revision, so a durable reference to a segment is the
/// pair <see cref="TranscriptRevision"/> + segment ID. Nothing downstream may store a bare ID.
/// </para>
/// </summary>
public sealed record TranscriptDocument
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("transcript_revision")]
    public required int TranscriptRevision { get; init; }

    [JsonPropertyName("created_at_utc")]
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Digest over the ordered source-chunk identities this revision was produced from. It is
    /// what makes "the audio underneath this transcript changed" detectable rather than assumed.
    /// </summary>
    [JsonPropertyName("source_manifest_sha256")]
    public string? SourceManifestSha256 { get; init; }

    [JsonPropertyName("duration_seconds")]
    public required double DurationSeconds { get; init; }

    [JsonPropertyName("model")]
    public required TranscriptModel Model { get; init; }

    [JsonPropertyName("epochs")]
    public IReadOnlyList<TranscriptEpoch> Epochs { get; init; } = [];

    [JsonPropertyName("speakers")]
    public IReadOnlyList<TranscriptSpeaker> Speakers { get; init; } = [];

    [JsonPropertyName("languages")]
    public IReadOnlyList<TranscriptLanguage> Languages { get; init; } = [];

    [JsonPropertyName("segments")]
    public IReadOnlyList<TranscriptSegment> Segments { get; init; } = [];

    /// <summary>
    /// Reading and writing use the same options so a round trip is byte-stable. Nulls are written
    /// explicitly because the schema requires the keys to be present: an absent
    /// <c>confidence</c> and a null one mean different things.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>What produced a transcript, recorded so a revision can be explained later.</summary>
/// <param name="RecognizesSpeech">
/// False for any placeholder backend. A false value must be surfaced to the user, never hidden:
/// placeholder text is not a transcript of anything that was said.
/// </param>
public sealed record TranscriptModel(
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("compute_type")] string ComputeType,
    [property: JsonPropertyName("recognizes_speech")] bool RecognizesSpeech,
    [property: JsonPropertyName("worker_version")] string WorkerVersion);

/// <summary>
/// A capture epoch copied into the transcript so segment bounds are checkable without opening the
/// session snapshot. Times are session-relative seconds.
/// </summary>
public sealed record TranscriptEpoch(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("start_seconds")] double StartSeconds,
    [property: JsonPropertyName("end_seconds")] double EndSeconds)
{
    public bool Contains(double startSeconds, double endSeconds, double tolerance = 1e-6) =>
        startSeconds >= StartSeconds - tolerance && endSeconds <= EndSeconds + tolerance;
}

/// <summary>
/// A speaker. Fixed for the MVP: the microphone is You and the system track is Remote. Remote is
/// a track, not a person, and no identity is claimed for anyone on it.
/// </summary>
public sealed record TranscriptSpeaker(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source_track")] string SourceTrack);

/// <summary>
/// The language reported for one track. <c>und</c> means undetermined, which is what a
/// placeholder backend always reports; it is not a detection result.
/// </summary>
public sealed record TranscriptLanguage(
    [property: JsonPropertyName("source_track")] string SourceTrack,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("probability")] double? Probability);

/// <summary>One contiguous stretch of one speaker on one track.</summary>
public sealed record TranscriptSegment
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("epoch")]
    public required int Epoch { get; init; }

    [JsonPropertyName("start_seconds")]
    public required double StartSeconds { get; init; }

    [JsonPropertyName("end_seconds")]
    public required double EndSeconds { get; init; }

    [JsonPropertyName("speaker_id")]
    public required string SpeakerId { get; init; }

    [JsonPropertyName("speaker_name")]
    public required string SpeakerName { get; init; }

    [JsonPropertyName("source_track")]
    public required string SourceTrack { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>
    /// Null unless the runtime exposes a defined score. An average log probability is not a
    /// calibrated confidence and must not be written here under that name.
    /// </summary>
    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    [JsonPropertyName("language")]
    public required string Language { get; init; }

    [JsonPropertyName("words")]
    public IReadOnlyList<TranscriptWord> Words { get; init; } = [];

    /// <summary>
    /// Segments on the <em>other</em> track whose time range intersects this one. Cross-track
    /// overlap is recorded as information; it is never treated as proof of duplication, because
    /// headset sidetone and a genuine interruption look identical from here.
    /// </summary>
    [JsonPropertyName("overlaps_segment_ids")]
    public IReadOnlyList<string> OverlapsSegmentIds { get; init; } = [];
}

/// <summary>One word with its own timing. Always contained by its segment.</summary>
public sealed record TranscriptWord(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("start_seconds")] double StartSeconds,
    [property: JsonPropertyName("end_seconds")] double EndSeconds,
    [property: JsonPropertyName("probability")] double? Probability);

/// <summary>
/// The two speaker identities the MVP recognises, in one place so no caller invents a third.
/// </summary>
public static class TranscriptSpeakers
{
    public const string YouId = "speaker-you";
    public const string YouName = "You";
    public const string RemoteId = "speaker-remote";
    public const string RemoteName = "Remote";

    public const string MicrophoneTrack = "microphone";
    public const string SystemTrack = "system";

    /// <summary>Undetermined. Not a detection result and never presented as one.</summary>
    public const string UndeterminedLanguage = "und";

    /// <summary>
    /// Attribution follows from the track and from nothing else. It is not a request parameter,
    /// not a model output, and not something a backend may override.
    /// </summary>
    public static (string Id, string Name) For(string sourceTrack) => sourceTrack switch
    {
        MicrophoneTrack => (YouId, YouName),
        SystemTrack => (RemoteId, RemoteName),
        _ => throw new ArgumentOutOfRangeException(nameof(sourceTrack), sourceTrack, "unknown source track"),
    };
}
