using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Audio;

/// <summary>
/// Durable per-chunk metadata written next to the audio itself.
///
/// <para>
/// This exists so a chunk is discoverable without the journal. The writer emits it for the
/// active chunk on every flush and for a finalized chunk immediately after the atomic rename,
/// which closes the crash window between "the WAV is complete on disk" and "the journal knows
/// about it". Recovery reconciles the two and trusts whichever is more complete, never
/// discarding audio because a journal line was lost.
/// </para>
/// </summary>
public sealed record ChunkRecord
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("track")]
    public required string Track { get; init; }

    [JsonPropertyName("index")]
    public required int Index { get; init; }

    [JsonPropertyName("epoch")]
    public required int Epoch { get; init; }

    [JsonPropertyName("sample_rate")]
    public required int SampleRate { get; init; }

    [JsonPropertyName("channels")]
    public required int Channels { get; init; }

    [JsonPropertyName("bits_per_sample")]
    public int BitsPerSample { get; init; } = 16;

    /// <summary>Frames placed on the timeline for this chunk, including inserted silence.</summary>
    [JsonPropertyName("frames")]
    public required long Frames { get; init; }

    /// <summary>Offset of this chunk's first frame within its epoch, in seconds.</summary>
    [JsonPropertyName("start_seconds")]
    public required double StartSeconds { get; init; }

    /// <summary>The epoch's QPC origin, so session-relative time can be derived after a crash.</summary>
    [JsonPropertyName("epoch_qpc")]
    public required long EpochQpc { get; init; }

    /// <summary>Path relative to the session root.</summary>
    [JsonPropertyName("relative_path")]
    public required string RelativePath { get; init; }

    /// <summary>Set only once the chunk is finalized and hashed.</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("finalized")]
    public bool Finalized { get; init; }

    [JsonPropertyName("discontinuities")]
    public IReadOnlyList<DiscontinuityRecord> Discontinuities { get; init; } = [];

    public CaptureFormat Format() => new(SampleRate, Channels, BitsPerSample);

    public double EndSeconds => SampleRate == 0 ? StartSeconds : StartSeconds + ((double)Frames / SampleRate);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>A discontinuity in a form that survives being written to disk.</summary>
public sealed record DiscontinuityRecord
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("at_frame")]
    public required long AtFrame { get; init; }

    [JsonPropertyName("frames")]
    public required long Frames { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    public static DiscontinuityRecord From(CaptureDiscontinuity source) => new()
    {
        Kind = source.Kind.ToString(),
        AtFrame = source.AtDevicePosition,
        Frames = source.FrameCount,
        Detail = source.Detail,
    };

    public CaptureDiscontinuity ToDiscontinuity() => new(
        Enum.TryParse(Kind, out DiscontinuityKind kind) ? kind : DiscontinuityKind.TimestampError,
        AtFrame,
        Frames,
        Detail ?? string.Empty);
}
