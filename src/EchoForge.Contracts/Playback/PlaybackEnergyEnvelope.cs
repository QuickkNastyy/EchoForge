using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Playback;

/// <summary>
/// A bounded, derived picture of per-track audio energy across a playback derivative.
///
/// <para>
/// It is a cache, never source data. <see cref="DerivativeSha256"/> binds it to the exact aligned
/// playback WAV it was measured from, so changing the source recording or playback processing
/// invalidates the envelope automatically. Values are normalized to 0..1 independently per lane.
/// </para>
/// </summary>
public sealed record PlaybackEnergyEnvelope
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultBuckets = 64;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("derivative_sha256")]
    public required string DerivativeSha256 { get; init; }

    [JsonPropertyName("processing_version")]
    public required string ProcessingVersion { get; init; }

    [JsonPropertyName("buckets")]
    public required int Buckets { get; init; }

    [JsonPropertyName("you")]
    public required float[] You { get; init; }

    [JsonPropertyName("remote")]
    public required float[] Remote { get; init; }

    [JsonIgnore]
    public bool HasData => You.Any(v => v > 0) || Remote.Any(v => v > 0);

    public bool Matches(PlaybackDerivativeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return SchemaVersion == CurrentSchemaVersion
            && Buckets > 0
            && You.Length == Buckets
            && Remote.Length == Buckets
            && You.All(ValidLevel)
            && Remote.All(ValidLevel)
            && string.Equals(DerivativeSha256, record.Sha256, StringComparison.Ordinal)
            && string.Equals(ProcessingVersion, record.ProcessingVersion, StringComparison.Ordinal);
    }

    private static bool ValidLevel(float level) => float.IsFinite(level) && level is >= 0 and <= 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };
}
