using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Inference;

namespace EchoForge.Core.Inference;

/// <summary>
/// The single model/capability registry used by planning, setup, worker requests, and the UI.
/// No consumer needs a switch over a model name to learn its backend or windowing rules.
/// </summary>
public static class InferenceModelRegistry
{
    public const string WhisperTurboArtifactProfile = ProcessingProfile.AsrWhisperLargeV3Turbo;
    public const string WhisperLargeV3ArtifactProfile = ProcessingProfile.AsrWhisperLargeV3;
    public const string ParakeetArtifactProfile = ProcessingProfile.AsrParakeetUnifiedEn06B;
    public const string CanaryArtifactProfile = ProcessingProfile.AsrCanaryQwen25B;
    public const string GptOssArtifactProfile = ProcessingProfile.SummaryGptOss20B;

    public static readonly AsrWindowStrategy WhisperWindowing = new(
        "whisper-long-v2", 600, 5, false,
        "Long prepared windows with word-timestamp overlap de-duplication.");

    public static readonly AsrWindowStrategy ParakeetWindowing = new(
        "parakeet-offline-v1", 300, 5, false,
        "Fixed offline NeMo windows with full coverage and native RNNT timing where supplied.");

    public static readonly AsrWindowStrategy CanaryWindowing = new(
        "canary-short-v1", 35, 5, false,
        "Fixed overlapping windows kept within Canary-Qwen's verified 40-second training range.");

    private static readonly IReadOnlyList<AsrModelDefinition> AsrDefinitions =
    [
        new()
        {
            Id = AsrModelIds.Mock,
            DisplayName = "Deterministic placeholder",
            BackendId = AsrBackendIds.Mock,
            Runtime = "python-stdlib",
            ModelRevision = "mock-v1",
            ArtifactProfileId = ProcessingProfile.Mock,
            ArtifactIdentity = "none",
            Languages = ["und"],
            TimestampCapability = AsrTimestampCapability.Segment,
            TimestampPrecision = AsrTimestampPrecision.WindowApproximate,
            MaximumWindowSeconds = 3,
            WindowStrategy = WhisperWindowing,
            SupportedComputeProfiles = [ComputeProfileIds.CpuInt8],
            SupportedVadModes = [VadMode.Off],
            Maturity = ModelMaturity.Experimental,
            License = "MIT",
            Provenance = "EchoForge deterministic test backend; it performs no speech recognition.",
            ShortDescription = "Testing only; does not recognize speech.",
            SupportsCpu = true,
        },
        new()
        {
            Id = AsrModelIds.WhisperLargeV3Turbo,
            DisplayName = "Whisper Large V3 Turbo",
            BackendId = AsrBackendIds.FasterWhisper,
            Runtime = "faster-whisper 1.2.1 / CTranslate2 4.8.1",
            ModelRevision = "0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf",
            ArtifactProfileId = WhisperTurboArtifactProfile,
            ArtifactIdentity = "dropbox-dash/faster-whisper-large-v3-turbo@0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf",
            Languages = ["multilingual"],
            SupportsLanguageAutoDetection = true,
            TimestampCapability = AsrTimestampCapability.Word,
            TimestampPrecision = AsrTimestampPrecision.WordNative,
            MaximumWindowSeconds = 600,
            WindowStrategy = WhisperWindowing,
            SupportedComputeProfiles =
                [ComputeProfileIds.CudaFloat16, ComputeProfileIds.CudaInt8Float16, ComputeProfileIds.CpuInt8],
            SupportedVadModes = [VadMode.Accuracy, VadMode.Balanced, VadMode.Fast, VadMode.Off],
            Maturity = ModelMaturity.Production,
            License = "MIT model conversion; Apache-2.0 upstream Whisper weights",
            Provenance = "Pinned CTranslate2 conversion already verified in artifacts/manifest.json.",
            ShortDescription = "Fast, multilingual, and compatible with existing EchoForge revisions.",
            SupportsGlossaryPrompt = true,
            SupportsCpu = true,
        },
        new()
        {
            Id = AsrModelIds.WhisperLargeV3,
            DisplayName = "Whisper Large V3",
            BackendId = AsrBackendIds.FasterWhisper,
            Runtime = "faster-whisper 1.2.1 / CTranslate2 4.8.1",
            ModelRevision = "edaa852ec7e145841d8ffdb056a99866b5f0a478",
            ArtifactProfileId = WhisperLargeV3ArtifactProfile,
            ArtifactIdentity = "Systran/faster-whisper-large-v3@edaa852ec7e145841d8ffdb056a99866b5f0a478",
            Languages = ["multilingual"],
            SupportsLanguageAutoDetection = true,
            TimestampCapability = AsrTimestampCapability.Word,
            TimestampPrecision = AsrTimestampPrecision.WordNative,
            MaximumWindowSeconds = 600,
            WindowStrategy = WhisperWindowing,
            SupportedComputeProfiles =
                [ComputeProfileIds.CudaFloat16, ComputeProfileIds.CudaInt8Float16, ComputeProfileIds.CpuInt8],
            SupportedVadModes = [VadMode.Accuracy, VadMode.Balanced, VadMode.Fast, VadMode.Off],
            Maturity = ModelMaturity.Production,
            License = "MIT model conversion; Apache-2.0 upstream Whisper weights",
            Provenance = "Pinned Systran CTranslate2 float16 conversion of openai/whisper-large-v3.",
            ShortDescription = "Accuracy-oriented multilingual Whisper option; slower than Turbo.",
            SupportsGlossaryPrompt = true,
            SupportsCpu = true,
        },
        new()
        {
            Id = AsrModelIds.ParakeetUnifiedEn06B,
            DisplayName = "NVIDIA Parakeet Unified EN 0.6B",
            BackendId = AsrBackendIds.Nemo,
            Runtime = "NVIDIA NeMo 2.7.3 / PyTorch",
            ModelRevision = "fe53cd885760c96b6a5f51a0bfd362cb4584a98b",
            ArtifactProfileId = ParakeetArtifactProfile,
            ArtifactIdentity = "nvidia/parakeet-unified-en-0.6b@fe53cd885760c96b6a5f51a0bfd362cb4584a98b",
            Languages = ["en"],
            TimestampCapability = AsrTimestampCapability.Word,
            TimestampPrecision = AsrTimestampPrecision.WordNative,
            MaximumWindowSeconds = 300,
            WindowStrategy = ParakeetWindowing,
            SupportedComputeProfiles = [ComputeProfileIds.CudaFloat16, ComputeProfileIds.CudaBFloat16],
            SupportedVadModes = [VadMode.Accuracy, VadMode.Off],
            Maturity = ModelMaturity.Experimental,
            License = "NVIDIA Open Model License",
            Provenance = "Pinned official NVIDIA .nemo artifact; upstream card identifies NeMo 2.7.3.",
            ShortDescription = "English offline/streaming RNNT model; isolated NeMo runtime required.",
        },
        new()
        {
            Id = AsrModelIds.CanaryQwen25B,
            DisplayName = "NVIDIA Canary-Qwen 2.5B",
            BackendId = AsrBackendIds.Nemo,
            Runtime = "NVIDIA NeMo 2.7.3 / PyTorch",
            ModelRevision = "b1469e1bba1cfe140205529c79c434ca47180960",
            ArtifactProfileId = CanaryArtifactProfile,
            ArtifactIdentity = "nvidia/canary-qwen-2.5b@b1469e1bba1cfe140205529c79c434ca47180960",
            Languages = ["en"],
            TimestampCapability = AsrTimestampCapability.Segment,
            TimestampPrecision = AsrTimestampPrecision.WindowApproximate,
            MaximumWindowSeconds = 40,
            WindowStrategy = CanaryWindowing,
            SupportedComputeProfiles = [ComputeProfileIds.CudaFloat16, ComputeProfileIds.CudaBFloat16],
            SupportedVadModes = [VadMode.Accuracy, VadMode.Off],
            Maturity = ModelMaturity.Experimental,
            License = "CC-BY-4.0",
            Provenance = "Pinned official NVIDIA safetensors checkpoint; the model card limits training audio to 40 seconds.",
            ShortDescription = "English experimental accuracy model; short-window NeMo path and no fabricated word times.",
        },
    ];

    private static readonly IReadOnlyList<SummaryModelDefinition> SummaryDefinitions =
    [
        new()
        {
            Id = SummaryModelIds.Gemma4TwelveB,
            DisplayName = "Gemma 4 12B Instruct QAT Q4_0",
            BackendId = "gemma-4-12b",
            Runtime = "llama.cpp b10298",
            ModelRevision = "29d097773436b69ff9feafd636ab4cf873786537",
            ArtifactProfileId = ProcessingProfile.SummaryCudaQ4,
            ArtifactIdentity = "google/gemma-4-12B-it-qat-q4_0-gguf@29d097773436b69ff9feafd636ab4cf873786537",
            Maturity = ModelMaturity.Production,
            License = "Apache-2.0",
            Provenance = "Pinned official Google GGUF already verified in artifacts/manifest.json.",
            ShortDescription = "Existing production evidence extractor and narrative summarizer.",
            RecommendedContextTokens = 32768,
        },
        new()
        {
            Id = SummaryModelIds.GptOss20B,
            DisplayName = "gpt-oss-20b MXFP4",
            BackendId = "gpt-oss-20b",
            Runtime = "llama.cpp b10298",
            ModelRevision = "ef9b12f2ff56c69cf32153a02784e7a3c88bf524",
            ArtifactProfileId = GptOssArtifactProfile,
            ArtifactIdentity = "ggml-org/gpt-oss-20b-GGUF@ef9b12f2ff56c69cf32153a02784e7a3c88bf524",
            Maturity = ModelMaturity.Experimental,
            License = "Apache-2.0",
            Provenance = "Pinned ggml-org automatic GGUF conversion of the official OpenAI MXFP4 weights.",
            ShortDescription = "Optional local comparison model using the native Harmony chat template.",
            RecommendedContextTokens = 16384,
            UsesHarmonyTemplate = true,
        },
        new()
        {
            Id = SummaryModelIds.Ministral3FourteenB,
            DisplayName = "Ministral 3 14B Instruct Q4_K_M",
            BackendId = "ministral-3-14b",
            Runtime = "llama.cpp b10298",
            ModelRevision = "74fac473c43357d7fb2671713608183cc72496d0",
            ArtifactProfileId = ProcessingProfile.SummaryBakeoff,
            ArtifactIdentity = "mistralai/Ministral-3-14B-Instruct-2512-GGUF@74fac473c43357d7fb2671713608183cc72496d0",
            Maturity = ModelMaturity.Experimental,
            License = "Apache-2.0",
            Provenance = "Pinned official Mistral comparison GGUF already verified in artifacts/manifest.json.",
            ShortDescription = "Optional benchmark model; never selected automatically.",
            RecommendedContextTokens = 32768,
        },
    ];

    public static IReadOnlyList<AsrModelDefinition> AsrModels => AsrDefinitions;

    public static IReadOnlyList<SummaryModelDefinition> SummaryModels => SummaryDefinitions;

    public static AsrModelDefinition GetAsr(string id) =>
        TryGetAsr(id) ?? throw new ArgumentException($"Unknown ASR model '{id}'.", nameof(id));

    public static AsrModelDefinition? TryGetAsr(string? id) => AsrDefinitions.FirstOrDefault(
        model => string.Equals(model.Id, id, StringComparison.Ordinal));

    public static SummaryModelDefinition GetSummary(string id) =>
        TryGetSummary(id) ?? throw new ArgumentException($"Unknown summary model '{id}'.", nameof(id));

    public static SummaryModelDefinition? TryGetSummary(string? id) => SummaryDefinitions.FirstOrDefault(
        model => string.Equals(model.Id, id, StringComparison.Ordinal));

    /// <summary>Legacy requests did not carry model identity; map them without changing history.</summary>
    public static AsrModelDefinition ResolveLegacyAsr(string backend, string? modelId = null) =>
        TryGetAsr(modelId) ?? (backend switch
        {
            AsrBackendIds.FasterWhisper => GetAsr(AsrModelIds.WhisperLargeV3Turbo),
            AsrBackendIds.Nemo => GetAsr(AsrModelIds.ParakeetUnifiedEn06B),
            _ => GetAsr(AsrModelIds.Mock),
        });
}
