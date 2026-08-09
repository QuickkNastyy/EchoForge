using System.Globalization;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Inference;
using EchoForge.Contracts.Setup;

namespace EchoForge.Core.Setup;

/// <summary>
/// A recommended profile, and the reasons it was recommended.
///
/// <para>
/// The reasons are not decoration. A recommendation a user cannot interrogate is one they either
/// accept blindly or ignore, and the difference between "your GPU has enough memory" and "EchoForge
/// could not tell how much memory your GPU has, so it chose the safe option" changes what a
/// reasonable person does next.
/// </para>
/// </summary>
public sealed record ProfileRecommendation(
    string ProfileId,
    string Summary,
    IReadOnlyList<string> Reasons,
    bool IsFallback)
{
    public bool IsPlaceholder => string.Equals(ProfileId, ProcessingProfile.Mock, StringComparison.Ordinal);
}

/// <summary>
/// The independently selected ASR model and compute profile.
///
/// <para>
/// A model is an artifact/runtime identity; a compute profile is how that model is executed.
/// Keeping both values here prevents setup from inferring one from the other.
/// </para>
/// </summary>
public sealed record TranscriptionRecommendation(
    string ModelId,
    string ArtifactProfileId,
    ProfileRecommendation Compute);

/// <summary>What EchoForge suggests installing on this machine, and why.</summary>
public sealed record SetupRecommendation(
    TranscriptionRecommendation Asr,
    ProfileRecommendation Summarization,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Compatibility name for callers concerned only with compute.</summary>
    public ProfileRecommendation Transcription => Asr.Compute;

    /// <summary>True when nothing worth installing was found. Recording still works.</summary>
    public bool RecordingOnly => Transcription.IsPlaceholder;
}

/// <summary>
/// Chooses processing profiles from what the machine actually is.
///
/// <para>
/// <b>Deterministic, and not tuned to one machine.</b> It would have been easy to write
/// "if the adapter says RTX 5070 Ti then use CUDA", and it would have been right on exactly the
/// desk it was written at. The rules here are about the properties that decide whether a profile
/// runs at all: is there a working CUDA stack, how much video memory did the adapter report, how
/// much system memory and disk are there, and is the CPU fallback going to be usable rather than
/// merely possible.
/// </para>
///
/// <para>
/// <b>Unknown is not a yes.</b> When a value could not be read, the recommendation steps down to
/// the option that works without it and says which fact was missing. A confident GPU recommendation
/// made on the strength of an unreadable VRAM figure fails in the middle of somebody's first
/// meeting, which is the worst possible moment to discover it.
/// </para>
///
/// <para>
/// The comparison model is never recommended. It exists to be measured against the default, and a
/// setup screen that suggested downloading eight gigabytes of it by default would be recommending
/// a benchmark as a product.
/// </para>
/// </summary>
public static class ProfileRecommender
{
    /// <summary>
    /// Video memory the GPU speech profile needs before it is recommended.
    ///
    /// <para>
    /// large-v3-turbo in FP16 is about 1.6 GB of weights, and CTranslate2 needs working memory
    /// and activations on top. Four gigabytes is the point below which a long meeting starts
    /// failing rather than running slowly, which is a much worse experience than the CPU path.
    /// </para>
    /// </summary>
    public const long GpuSpeechVramBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Video memory required before the full Large V3 accuracy-oriented checkpoint is the
    /// recommendation. Cards below this line can still run the smaller Turbo checkpoint on the
    /// GPU; model choice and compute choice are intentionally separate decisions.
    /// </summary>
    public const long AccuracySpeechVramBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>
    /// Video memory the GPU summary profile needs.
    ///
    /// <para>
    /// Gemma 4 12B at Q4_0 is about 6.5 GB of weights, and the KV cache for a 32K context is
    /// added to it. Ten gigabytes is where the model fits with room to work; below that llama.cpp
    /// will offload layers to the CPU and the run becomes slower than the CPU profile while
    /// looking like the GPU one.
    /// </para>
    /// </summary>
    public const long GpuSummaryVramBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>System memory below which the CPU summary path is not worth recommending.</summary>
    public const long CpuSummaryMemoryBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>Free space needed for the transcription stack. Roughly twice what it downloads.</summary>
    public const long TranscriptionDiskBytes = 6L * 1024 * 1024 * 1024;

    /// <summary>Free space needed for the summary stack on top of that.</summary>
    public const long SummaryDiskBytes = 20L * 1024 * 1024 * 1024;

    public static SetupRecommendation Recommend(HardwareSnapshot hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        List<string> warnings = [];

        if (!hardware.HasMicrophone)
        {
            warnings.Add("No microphone was found, so your own voice would not be recorded.");
        }

        if (!hardware.HasLoopback)
        {
            warnings.Add("No playback device was found, so the other side of a call could not be captured.");
        }

        if (hardware.AvailableDiskBytes is { } free && free < TranscriptionDiskBytes)
        {
            warnings.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"There is {Describe(free)} free on {hardware.DataVolume ?? "this drive"}. The speech model alone needs about {Describe(TranscriptionDiskBytes)}."));
        }

        foreach (string missing in hardware.Unavailable)
        {
            warnings.Add("EchoForge could not read this machine's " + missing + ".");
        }

        return new SetupRecommendation(
            RecommendTranscription(hardware),
            RecommendSummarization(hardware),
            warnings);
    }

    // -- speech ---------------------------------------------------------------------------------

    private static TranscriptionRecommendation RecommendTranscription(HardwareSnapshot hardware)
    {
        List<string> reasons = [];
        GpuInfo? nvidia = hardware.PrimaryNvidia;

        if (nvidia is not null && hardware.Cuda == CudaAvailability.Available)
        {
            if (nvidia.DedicatedMemoryBytes is { } vram)
            {
                if (vram >= GpuSpeechVramBytes)
                {
                    reasons.Add($"{nvidia.Model} reports {Describe(vram)} of video memory.");
                    reasons.Add("CTranslate2 can see a usable CUDA device on this machine.");

                    bool accuracyModel = vram >= AccuracySpeechVramBytes;
                    if (accuracyModel)
                    {
                        reasons.Add("The full Whisper Large V3 checkpoint fits the accuracy-oriented GPU recommendation.");
                    }
                    else
                    {
                        reasons.Add(string.Create(
                            CultureInfo.CurrentCulture,
                            $"The Turbo checkpoint is recommended below {Describe(AccuracySpeechVramBytes)} of video memory."));
                    }

                    return Whisper(
                        accuracyModel ? AsrModelIds.WhisperLargeV3 : AsrModelIds.WhisperLargeV3Turbo,
                        accuracyModel ? ProcessingProfile.AsrWhisperLargeV3 : ProcessingProfile.AsrWhisperLargeV3Turbo,
                        new ProfileRecommendation(
                            ProcessingProfile.CudaFp16,
                            accuracyModel
                                ? "Whisper Large V3 in the accuracy-oriented CUDA FP16 configuration."
                                : "Whisper Large V3 Turbo on your NVIDIA GPU.",
                            reasons,
                            IsFallback: false));
                }

                reasons.Add(string.Create(
                    CultureInfo.CurrentCulture,
                    $"{nvidia.Model} reports {Describe(vram)}, below the {Describe(GpuSpeechVramBytes)} the GPU profile needs."));

                return CpuTranscription(hardware, reasons);
            }

            // A working CUDA stack and an adapter that will not say how much memory it has. The
            // lower-memory GPU profile is the honest middle: it uses the GPU without assuming
            // there is room for the larger one.
            reasons.Add($"{nvidia.Model} did not report how much video memory it has.");
            reasons.Add("CTranslate2 can see a usable CUDA device, so the lower-memory GPU profile is the safe choice.");

            return Whisper(
                AsrModelIds.WhisperLargeV3Turbo,
                ProcessingProfile.AsrWhisperLargeV3Turbo,
                new ProfileRecommendation(
                    ProcessingProfile.CudaInt8Float16,
                    "Whisper Large V3 Turbo on your NVIDIA GPU, in the lower-memory mode.",
                    reasons,
                    IsFallback: true));
        }

        if (nvidia is not null)
        {
            reasons.Add(hardware.Cuda switch
            {
                CudaAvailability.AdapterWithoutRuntime =>
                    $"{nvidia.Model} is present, but EchoForge has not been able to confirm CUDA works yet.",
                _ => $"{nvidia.Model} is present, but its CUDA support could not be established.",
            });
        }
        else if (hardware.Gpus.Count > 0)
        {
            reasons.Add("No NVIDIA adapter was found. The pinned GPU profiles are CUDA-only.");
        }
        else
        {
            reasons.Add("EchoForge could not read this machine's graphics adapters.");
        }

        return CpuTranscription(hardware, reasons);
    }

    private static TranscriptionRecommendation CpuTranscription(HardwareSnapshot hardware, List<string> reasons)
    {
        if (hardware.HasAvx2 == false)
        {
            reasons.Add("This processor does not report AVX2, so the CPU profile would be very slow.");

            return Whisper(
                AsrModelIds.Mock,
                ProcessingProfile.Mock,
                new ProfileRecommendation(
                    ProcessingProfile.Mock,
                    "Recording only. Speech recognition is not recommended on this machine.",
                    reasons,
                    IsFallback: true));
        }

        reasons.Add(hardware.HasAvx2 == true
            ? string.Create(CultureInfo.CurrentCulture, $"This processor reports AVX2 and {hardware.LogicalCores} logical cores.")
            : "This processor's instruction set could not be read, so the CPU profile is the safe choice.");

        return Whisper(
            AsrModelIds.WhisperLargeV3Turbo,
            ProcessingProfile.AsrWhisperLargeV3Turbo,
            new ProfileRecommendation(
                ProcessingProfile.CpuInt8,
                "Whisper Large V3 Turbo on the processor. It works everywhere, and it is slow.",
                reasons,
                IsFallback: true));
    }

    private static TranscriptionRecommendation Whisper(
        string modelId,
        string artifactProfileId,
        ProfileRecommendation compute) => new(modelId, artifactProfileId, compute);

    // -- summaries ------------------------------------------------------------------------------

    private static ProfileRecommendation RecommendSummarization(HardwareSnapshot hardware)
    {
        List<string> reasons = [];
        GpuInfo? nvidia = hardware.PrimaryNvidia;

        if (hardware.AvailableDiskBytes is { } free && free < SummaryDiskBytes)
        {
            reasons.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"There is {Describe(free)} free, and the summary stack needs about {Describe(SummaryDiskBytes)}."));

            return new ProfileRecommendation(
                ProcessingProfile.Mock,
                "Local summaries are not recommended until there is more free space.",
                reasons,
                IsFallback: true);
        }

        if (nvidia?.DedicatedMemoryBytes is { } vram && vram >= GpuSummaryVramBytes)
        {
            reasons.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"{nvidia.Model} reports {Describe(vram)}, enough for the 12B model at Q4 with a 32K context."));

            if (hardware.Cuda != CudaAvailability.Available)
            {
                reasons.Add("CUDA has not been confirmed yet; llama.cpp will fall back to the processor if it cannot start on the GPU.");
            }

            return new ProfileRecommendation(
                ProcessingProfile.SummaryCudaQ4,
                "Local summaries on your NVIDIA GPU.",
                reasons,
                IsFallback: false);
        }

        if (nvidia is not null)
        {
            reasons.Add(nvidia.DedicatedMemoryBytes is { } small
                ? string.Create(
                    CultureInfo.CurrentCulture,
                    $"{nvidia.Model} reports {Describe(small)}, below the {Describe(GpuSummaryVramBytes)} the GPU summary profile needs.")
                : $"{nvidia.Model} did not report how much video memory it has.");
        }
        else
        {
            reasons.Add("No NVIDIA adapter was found.");
        }

        if (hardware.TotalMemoryBytes is { } memory && memory < CpuSummaryMemoryBytes)
        {
            reasons.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"This machine has {Describe(memory)} of memory, below the {Describe(CpuSummaryMemoryBytes)} the processor profile needs."));

            return new ProfileRecommendation(
                ProcessingProfile.Mock,
                "Local summaries are not recommended on this machine.",
                reasons,
                IsFallback: true);
        }

        reasons.Add(hardware.TotalMemoryBytes is { } total
            ? string.Create(CultureInfo.CurrentCulture, $"This machine has {Describe(total)} of memory, which the processor profile can use.")
            : "This machine's memory could not be read, so the processor profile is the safe choice.");

        return new ProfileRecommendation(
            ProcessingProfile.SummaryCpuQ4,
            "Local summaries on the processor. It works everywhere, and it is slow enough to say so.",
            reasons,
            IsFallback: true);
    }

    private static string Describe(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):F1} GB"),
        >= 1024L * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):F0} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes} bytes"),
    };
}
