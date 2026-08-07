using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Playback;

/// <summary>
/// Which interleaved channel each source track occupies in the playback derivative.
///
/// <para>
/// Named once and shared by the builder, the mixer and the transport, so the assignment cannot
/// drift apart between the code that writes the file and the code that plays it.
/// </para>
/// </summary>
public static class PlaybackChannels
{
    /// <summary>Microphone. Always You, by construction of the recorder.</summary>
    public const int You = 0;

    /// <summary>System audio. Everyone who was not in the room.</summary>
    public const int Remote = 1;
}

/// <summary>
/// How the aligned playback derivative is built.
///
/// <para>
/// <see cref="ProcessingVersion"/> is part of the derivative's identity, exactly as it is for the
/// transcription derivative. Change the layout, the resampler, or the channel assignment, and an
/// old file would still have the right length and the right rate while no longer being the file
/// this build would have produced — and nothing else would notice.
/// </para>
/// </summary>
public sealed record PlaybackOptions
{
    /// <summary>
    /// Playback rate.
    ///
    /// <para>
    /// 24 kHz, which is a deliberate middle. It is well above anything speech needs and comfortably
    /// above the 16 kHz the recogniser works at, while a three-hour meeting still costs about a
    /// gigabyte rather than two — and the raw session it was built from already costs several.
    /// </para>
    /// </summary>
    public int SampleRate { get; init; } = 24000;

    /// <summary>
    /// Always two, and deliberately not settable.
    ///
    /// <para>
    /// One channel per source track: microphone in channel 0, system in channel 1. The mix a
    /// listener hears is applied on the way to the device, not baked into the file, so muting a
    /// track or changing its level costs nothing and never invalidates the derivative. It also
    /// means the derivative never loses track identity — a mixed-down file could not tell anyone
    /// afterwards which half of it was You.
    /// </para>
    /// </summary>
    public int Channels { get; } = 2;

    /// <summary>Bumped whenever the produced bytes could differ for identical input.</summary>
    public string ProcessingVersion { get; init; } = "playback-v1";

    public string Describe() =>
        $"{SampleRate} Hz · 2 ch (You / Remote) · {ProcessingVersion}";
}

/// <summary>One source track's place in the playback derivative.</summary>
public sealed record PlaybackTrack
{
    [JsonPropertyName("source_track")]
    public required string SourceTrack { get; init; }

    /// <summary>Which interleaved channel this track occupies.</summary>
    [JsonPropertyName("channel")]
    public required int Channel { get; init; }

    /// <summary>
    /// The map from this channel back to the immutable chunks it was built from, in exactly the
    /// same shape the transcription derivative uses.
    /// </summary>
    [JsonPropertyName("timing_map_relative_path")]
    public required string TimingMapRelativePath { get; init; }

    [JsonPropertyName("timing_map_sha256")]
    public required string TimingMapSha256 { get; init; }

    /// <summary>False when the session recorded nothing on this track at all.</summary>
    [JsonPropertyName("has_audio")]
    public required bool HasAudio { get; init; }
}

/// <summary>
/// A built playback derivative, recorded so it can be reused only when everything it depended on
/// is unchanged.
/// </summary>
public sealed record PlaybackDerivativeRecord
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("relative_path")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("size_bytes")]
    public required long SizeBytes { get; init; }

    [JsonPropertyName("sample_rate")]
    public required int SampleRate { get; init; }

    [JsonPropertyName("channels")]
    public required int Channels { get; init; }

    [JsonPropertyName("total_frames")]
    public required long TotalFrames { get; init; }

    [JsonPropertyName("tracks")]
    public IReadOnlyList<PlaybackTrack> Tracks { get; init; } = [];

    /// <summary>Identity of the source audio. A different manifest means a different derivative.</summary>
    [JsonPropertyName("source_manifest_sha256")]
    public required string SourceManifestSha256 { get; init; }

    [JsonPropertyName("processing_version")]
    public required string ProcessingVersion { get; init; }

    [JsonPropertyName("created_utc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    public double DurationSeconds => SampleRate <= 0 ? 0 : (double)TotalFrames / SampleRate;

    public PlaybackTrack? For(string sourceTrack) =>
        Tracks.FirstOrDefault(t => string.Equals(t.SourceTrack, sourceTrack, StringComparison.Ordinal));

    /// <summary>
    /// Whether this record may be reused for a given request.
    ///
    /// <para>
    /// Every part of the identity has to match: the audio it came from, the settings it was built
    /// with, and the code version that built it. Matching on any subset would eventually hand a
    /// listener a file of the right length holding somebody else's meeting.
    /// </para>
    /// </summary>
    public bool Matches(string sourceManifestSha256, PlaybackOptions options)
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
