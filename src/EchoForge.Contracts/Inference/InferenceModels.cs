using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Inference;

/// <summary>Stable identities used in persisted revisions and on the worker protocol.</summary>
public static class AsrModelIds
{
    public const string Mock = "mock-asr";
    public const string WhisperLargeV3Turbo = "whisper-large-v3-turbo";
    public const string WhisperLargeV3 = "whisper-large-v3";
    public const string ParakeetUnifiedEn06B = "parakeet-unified-en-0.6b";
    public const string CanaryQwen25B = "canary-qwen-2.5b";
}

public static class AsrBackendIds
{
    public const string Mock = "mock";
    public const string FasterWhisper = "faster-whisper";
    public const string Nemo = "nemo";
}

public static class SummaryModelIds
{
    public const string Mock = "mock-summary";
    public const string Gemma4TwelveB = "gemma-4-12b-it-qat-q4_0";
    public const string GptOss20B = "gpt-oss-20b-mxfp4";
    public const string Ministral3FourteenB = "ministral-3-14b-instruct-q4-k-m";
}

public static class ComputeProfileIds
{
    public const string CpuInt8 = "cpu-int8";
    public const string CudaInt8Float16 = "cuda-int8-float16";
    public const string CudaFloat16 = "cuda-fp16";
    public const string CudaBFloat16 = "cuda-bf16";
}

/// <summary>
/// A named, persisted VAD policy. It is separate from model and compute identity so changing one
/// never silently changes either of the others.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<VadMode>))]
public enum VadMode
{
    Accuracy,
    Balanced,
    Fast,
    Off,
}

[JsonConverter(typeof(JsonStringEnumConverter<AsrTimestampCapability>))]
public enum AsrTimestampCapability
{
    None,
    Segment,
    Word,
}

[JsonConverter(typeof(JsonStringEnumConverter<AsrTimestampPrecision>))]
public enum AsrTimestampPrecision
{
    None,
    WindowApproximate,
    SegmentNative,
    WordNative,
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelMaturity>))]
public enum ModelMaturity
{
    Production,
    Experimental,
}

/// <summary>One backend-owned way of covering a recording without gaps.</summary>
public sealed record AsrWindowStrategy(
    string Id,
    double WindowSeconds,
    double OverlapSeconds,
    bool PreferSpeechAwareBoundaries,
    string Description);

/// <summary>
/// Static capabilities and immutable provenance for one ASR model. Installation and machine
/// usability are deliberately evaluated separately because both can change without changing the
/// model's identity.
/// </summary>
public sealed record AsrModelDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string BackendId { get; init; }
    public required string Runtime { get; init; }
    public required string ModelRevision { get; init; }
    public required string ArtifactProfileId { get; init; }
    public required string ArtifactIdentity { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public bool SupportsLanguageAutoDetection { get; init; }
    public required AsrTimestampCapability TimestampCapability { get; init; }
    public required AsrTimestampPrecision TimestampPrecision { get; init; }
    public int ExpectedSampleRate { get; init; } = 16000;
    public double MaximumWindowSeconds { get; init; } = 600;
    public required AsrWindowStrategy WindowStrategy { get; init; }
    public required IReadOnlyList<string> SupportedComputeProfiles { get; init; }
    public required IReadOnlyList<VadMode> SupportedVadModes { get; init; }
    public required ModelMaturity Maturity { get; init; }
    public required string License { get; init; }
    public required string Provenance { get; init; }
    public required string ShortDescription { get; init; }
    public bool SupportsGlossaryPrompt { get; init; }
    public bool SupportsCpu { get; init; }
}

/// <summary>Dynamic status layered over an immutable model definition.</summary>
public sealed record AsrModelAvailability(
    AsrModelDefinition Model,
    bool Installed,
    bool Usable,
    string? UnavailableReason = null);

public sealed record SummaryModelDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string BackendId { get; init; }
    public required string Runtime { get; init; }
    public required string ModelRevision { get; init; }
    public required string ArtifactProfileId { get; init; }
    public required string ArtifactIdentity { get; init; }
    public required ModelMaturity Maturity { get; init; }
    public required string License { get; init; }
    public required string Provenance { get; init; }
    public required string ShortDescription { get; init; }
    public required int RecommendedContextTokens { get; init; }
    public bool UsesHarmonyTemplate { get; init; }
}
