using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Processing;

/// <summary>
/// How a processing derivative is built.
///
/// <para>
/// <see cref="ProcessingVersion"/> is part of a derivative's identity. Change the resampler, the
/// mixdown, or the layout rules, and this must change with it: an old derivative would still have
/// the right sample rate and the right length while no longer being the file this build would
/// have produced, and nothing else would notice.
/// </para>
/// </summary>
public sealed record DerivativeOptions
{
    /// <summary>What the speech recogniser expects.</summary>
    public int SampleRate { get; init; } = 16000;

    public int Channels { get; init; } = 1;

    /// <summary>
    /// Bumped whenever the produced bytes could differ for identical input. Reusing a derivative
    /// across such a change would silently mix two pipelines' output.
    /// </summary>
    public string ProcessingVersion { get; init; } = "derivative-v1";

    public string Describe() => $"{SampleRate} Hz {(Channels == 1 ? "mono" : $"{Channels} ch")} · {ProcessingVersion}";
}

/// <summary>What one stretch of a derivative was built from.</summary>
public enum TimingSpanKind
{
    /// <summary>Audio decoded from a source chunk. Seekable back to immutable bytes.</summary>
    Source,

    /// <summary>
    /// Silence the derivative inserted because the session timeline says time passed with no
    /// audio: an epoch gap, a device stall, a pause. Nothing was recorded here, and a transcript
    /// timestamp landing inside one points at a moment that has no source at all.
    /// </summary>
    Gap,
}

/// <summary>
/// One contiguous stretch of a derivative, and where it came from.
///
/// <para>
/// Spans never straddle an epoch boundary and never straddle a chunk, so any derivative frame
/// resolves to exactly one source position or to an explicit gap. That is what lets a transcript
/// timestamp seek the immutable audio later instead of an assumed sample offset.
/// </para>
/// </summary>
public sealed record TimingSpan
{
    [JsonPropertyName("kind")]
    public required TimingSpanKind Kind { get; init; }

    /// <summary>First derivative frame of this span.</summary>
    [JsonPropertyName("derivative_frame")]
    public required long DerivativeFrame { get; init; }

    [JsonPropertyName("frames")]
    public required long Frames { get; init; }

    [JsonPropertyName("epoch")]
    public required int Epoch { get; init; }

    /// <summary>Session-relative seconds at <see cref="DerivativeFrame"/>.</summary>
    [JsonPropertyName("session_start_seconds")]
    public required double SessionStartSeconds { get; init; }

    [JsonPropertyName("session_end_seconds")]
    public required double SessionEndSeconds { get; init; }

    /// <summary>Null for a gap: there is no chunk, because nothing was captured.</summary>
    [JsonPropertyName("chunk_index")]
    public int? ChunkIndex { get; init; }

    [JsonPropertyName("chunk_relative_path")]
    public string? ChunkRelativePath { get; init; }

    /// <summary>Frame offset within the source chunk that <see cref="DerivativeFrame"/> maps to.</summary>
    [JsonPropertyName("source_frame")]
    public long? SourceFrame { get; init; }

    [JsonPropertyName("source_frames")]
    public long? SourceFrames { get; init; }

    [JsonPropertyName("source_sample_rate")]
    public int? SourceSampleRate { get; init; }

    [JsonPropertyName("source_channels")]
    public int? SourceChannels { get; init; }

    public long DerivativeEndFrame => DerivativeFrame + Frames;

    public bool Contains(long derivativeFrame) =>
        derivativeFrame >= DerivativeFrame && derivativeFrame < DerivativeEndFrame;
}

/// <summary>Where a derivative frame came from, resolved through the timing map.</summary>
public sealed record SourcePosition(
    int Epoch,
    double SessionSeconds,
    int? ChunkIndex,
    string? ChunkRelativePath,
    long? SourceFrame,
    int? SourceSampleRate,
    bool IsGap);

/// <summary>
/// The mapping between a derivative and the immutable audio it was built from.
///
/// <para>
/// Persisted beside the derivative and hashed with it. Without this a transcript timestamp could
/// only be turned back into a source position by re-deriving the arithmetic that produced it,
/// which is exactly the assumption the architecture forbids.
/// </para>
/// </summary>
public sealed record TimingMap
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("source_track")]
    public required string SourceTrack { get; init; }

    [JsonPropertyName("sample_rate")]
    public required int SampleRate { get; init; }

    [JsonPropertyName("channels")]
    public required int Channels { get; init; }

    [JsonPropertyName("total_frames")]
    public required long TotalFrames { get; init; }

    [JsonPropertyName("processing_version")]
    public required string ProcessingVersion { get; init; }

    [JsonPropertyName("source_manifest_sha256")]
    public required string SourceManifestSha256 { get; init; }

    [JsonPropertyName("spans")]
    public IReadOnlyList<TimingSpan> Spans { get; init; } = [];

    public double DurationSeconds => SampleRate <= 0 ? 0 : (double)TotalFrames / SampleRate;

    /// <summary>Session-relative seconds for a derivative frame, by definition of the layout.</summary>
    public double SessionSecondsAt(long derivativeFrame) =>
        SampleRate <= 0 ? 0 : (double)derivativeFrame / SampleRate;

    /// <summary>The derivative frame a session time lands on.</summary>
    public long FrameAt(double sessionSeconds) =>
        (long)Math.Round(Math.Max(0, sessionSeconds) * SampleRate, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Resolves a derivative frame back to immutable audio, or reports that it lands in a gap.
    /// Returns null only for a frame outside the derivative entirely.
    /// </summary>
    public SourcePosition? Resolve(long derivativeFrame)
    {
        foreach (TimingSpan span in Spans)
        {
            if (!span.Contains(derivativeFrame))
            {
                continue;
            }

            double seconds = SessionSecondsAt(derivativeFrame);

            if (span.Kind == TimingSpanKind.Gap)
            {
                return new SourcePosition(span.Epoch, seconds, null, null, null, null, IsGap: true);
            }

            long into = derivativeFrame - span.DerivativeFrame;
            long sourceFrame = span.SourceFrame!.Value +
                (span.SourceSampleRate!.Value == SampleRate
                    ? into
                    : (long)((double)into * span.SourceSampleRate.Value / SampleRate));

            return new SourcePosition(
                span.Epoch,
                seconds,
                span.ChunkIndex,
                span.ChunkRelativePath,
                sourceFrame,
                span.SourceSampleRate,
                IsGap: false);
        }

        return null;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

/// <summary>
/// A built derivative, recorded so it can be reused only when everything it depended on is
/// unchanged.
/// </summary>
public sealed record DerivativeRecord
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("source_track")]
    public required string SourceTrack { get; init; }

    [JsonPropertyName("relative_path")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("timing_map_relative_path")]
    public required string TimingMapRelativePath { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("timing_map_sha256")]
    public required string TimingMapSha256 { get; init; }

    [JsonPropertyName("size_bytes")]
    public required long SizeBytes { get; init; }

    [JsonPropertyName("sample_rate")]
    public required int SampleRate { get; init; }

    [JsonPropertyName("channels")]
    public required int Channels { get; init; }

    [JsonPropertyName("total_frames")]
    public required long TotalFrames { get; init; }

    /// <summary>Identity of the source audio. A different manifest means a different derivative.</summary>
    [JsonPropertyName("source_manifest_sha256")]
    public required string SourceManifestSha256 { get; init; }

    [JsonPropertyName("processing_version")]
    public required string ProcessingVersion { get; init; }

    [JsonPropertyName("created_utc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    public double DurationSeconds => SampleRate <= 0 ? 0 : (double)TotalFrames / SampleRate;

    /// <summary>
    /// Whether this record may be reused for a given request.
    ///
    /// <para>
    /// Every part of the identity has to match: the audio it came from, the settings it was built
    /// with, and the code version that built it. Matching on any subset would eventually hand a
    /// caller a file that is the right shape and the wrong content.
    /// </para>
    /// </summary>
    public bool Matches(string sourceManifestSha256, DerivativeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.Equals(SourceManifestSha256, sourceManifestSha256, StringComparison.Ordinal)
            && string.Equals(ProcessingVersion, options.ProcessingVersion, StringComparison.Ordinal)
            && SampleRate == options.SampleRate
            && Channels == options.Channels;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Both tracks' derivatives for one session.</summary>
public sealed record DerivativeSet(IReadOnlyList<DerivativeRecord> Derivatives)
{
    public DerivativeRecord? For(string sourceTrack) =>
        Derivatives.FirstOrDefault(d => string.Equals(d.SourceTrack, sourceTrack, StringComparison.Ordinal));
}
