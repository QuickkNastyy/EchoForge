using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Core.Setup;

namespace EchoForge.UnitTests;

/// <summary>
/// What EchoForge suggests installing, and why.
///
/// <para>
/// The rule these tests hold is that <b>unknown is never treated as yes</b>. It would have been
/// easy to write a recommender that assumed a missing VRAM figure meant "probably enough" and
/// recommended the GPU profile: it would look right on every machine that reports its memory, and
/// on the ones that do not it would fail in the middle of somebody's first meeting, which is the
/// worst possible moment to discover it.
/// </para>
///
/// <para>
/// Nothing here is tuned to a particular card. The rules are about the properties that decide
/// whether a profile runs — a working CUDA stack, video memory, system memory, disk, AVX2 — so a
/// machine nobody has ever seen gets a defensible answer.
/// </para>
/// </summary>
public sealed class RecommendationTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void AWorkingCudaStackWithPlentyOfMemoryGetsTheGpuProfile()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(Machines.WithNvidia(16 * Gb));

        Assert.Equal(ProcessingProfile.CudaFp16, recommendation.Transcription.ProfileId);
        Assert.False(recommendation.Transcription.IsFallback);
        Assert.Equal(ProcessingProfile.SummaryCudaQ4, recommendation.Summarization.ProfileId);
        Assert.False(recommendation.RecordingOnly);
    }

    [Fact]
    public void AnAdapterThatWillNotSayHowMuchMemoryItHasGetsTheLowerMemoryProfile()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(Machines.WithNvidia(vram: null));

        // Uses the GPU, because CUDA demonstrably works, but does not assume there is room for
        // the larger profile.
        Assert.Equal(ProcessingProfile.CudaInt8Float16, recommendation.Transcription.ProfileId);
        Assert.True(recommendation.Transcription.IsFallback);
        Assert.Contains(
            recommendation.Transcription.Reasons,
            reason => reason.Contains("did not report", StringComparison.Ordinal));

        // And summaries step off the GPU entirely: the 12B model needs a figure to fit inside.
        Assert.Equal(ProcessingProfile.SummaryCpuQ4, recommendation.Summarization.ProfileId);
    }

    [Fact]
    public void AnNvidiaCardWithoutAConfirmedCudaStackDoesNotGetTheGpuProfile()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(
            Machines.WithNvidia(16 * Gb, CudaAvailability.AdapterWithoutRuntime));

        // The two are different facts. A driver too old for the pinned CTranslate2 enumerates
        // perfectly and then runs nothing.
        Assert.Equal(ProcessingProfile.CpuInt8, recommendation.Transcription.ProfileId);
        Assert.Contains(
            recommendation.Transcription.Reasons,
            reason => reason.Contains("CUDA", StringComparison.Ordinal));
    }

    [Fact]
    public void ASmallGpuFallsBackToTheProcessorAndSaysWhy()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(Machines.WithNvidia(2 * Gb));

        Assert.Equal(ProcessingProfile.CpuInt8, recommendation.Transcription.ProfileId);
        Assert.True(recommendation.Transcription.IsFallback);
        Assert.Contains(
            recommendation.Transcription.Reasons,
            reason => reason.Contains("below", StringComparison.Ordinal));
    }

    [Fact]
    public void AMachineWithNoNvidiaAdapterGetsTheProcessorProfiles()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(Machines.WithoutGpu());

        Assert.Equal(ProcessingProfile.CpuInt8, recommendation.Transcription.ProfileId);
        Assert.Equal(ProcessingProfile.SummaryCpuQ4, recommendation.Summarization.ProfileId);
        Assert.Contains(
            recommendation.Transcription.Reasons,
            reason => reason.Contains("NVIDIA", StringComparison.Ordinal));
    }

    [Fact]
    public void AProcessorWithoutAvx2IsToldRecordingOnlyRatherThanPromisedSomethingUnusable()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(
            Machines.Base(avx2: false) with { Gpus = [], Cuda = CudaAvailability.Unknown });

        Assert.Equal(ProcessingProfile.Mock, recommendation.Transcription.ProfileId);
        Assert.True(recommendation.RecordingOnly);
        Assert.Contains(
            recommendation.Transcription.Reasons,
            reason => reason.Contains("AVX2", StringComparison.Ordinal));
    }

    [Fact]
    public void AProcessorWhoseInstructionSetCouldNotBeReadStillGetsTheCpuProfile()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(
            Machines.Base(avx2: null) with { Gpus = [], Cuda = CudaAvailability.Unknown });

        // Unknown is not "no". Refusing to recommend anything because a CPUID leaf was missing
        // would be worse than recommending the option that works everywhere.
        Assert.Equal(ProcessingProfile.CpuInt8, recommendation.Transcription.ProfileId);
        Assert.Contains(
            recommendation.Transcription.Reasons,
            reason => reason.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void NotEnoughMemoryMeansLocalSummariesAreNotRecommended()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(
            Machines.WithoutGpu(memory: 8 * Gb));

        Assert.Equal(ProcessingProfile.Mock, recommendation.Summarization.ProfileId);
        Assert.Contains(
            recommendation.Summarization.Reasons,
            reason => reason.Contains("memory", StringComparison.Ordinal));

        // Transcription is a separate decision and is unaffected.
        Assert.Equal(ProcessingProfile.CpuInt8, recommendation.Transcription.ProfileId);
    }

    [Fact]
    public void NotEnoughDiskSpaceIsSaidBeforeAnythingIsRecommended()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(
            Machines.WithNvidia(16 * Gb, disk: 3 * Gb));

        Assert.Contains(recommendation.Warnings, warning => warning.Contains("free", StringComparison.Ordinal));
        Assert.Equal(ProcessingProfile.Mock, recommendation.Summarization.ProfileId);
    }

    [Fact]
    public void MissingAudioDevicesAreWarnedAboutRatherThanIgnored()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(
            Machines.WithNvidia(16 * Gb) with { InputDevices = [], OutputDevices = [] });

        Assert.Contains(recommendation.Warnings, w => w.Contains("microphone", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(recommendation.Warnings, w => w.Contains("playback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhateverCouldNotBeReadIsSaidOutLoud()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(
            Machines.Base() with { Unavailable = ["graphics adapters", "system memory"] });

        Assert.Contains(recommendation.Warnings, w => w.Contains("graphics adapters", StringComparison.Ordinal));
        Assert.Contains(recommendation.Warnings, w => w.Contains("system memory", StringComparison.Ordinal));
    }

    [Fact]
    public void TheComparisonModelIsNeverRecommended()
    {
        // On every machine shape, including the most capable one there is.
        foreach (HardwareSnapshot machine in (HardwareSnapshot[])
        [
            Machines.WithNvidia(48 * Gb),
            Machines.WithNvidia(16 * Gb),
            Machines.WithNvidia(null),
            Machines.WithoutGpu(),
            Machines.Base(),
            HardwareSnapshot.Unknown,
        ])
        {
            SetupRecommendation recommendation = ProfileRecommender.Recommend(machine);

            Assert.NotEqual(ProcessingProfile.SummaryBakeoff, recommendation.Transcription.ProfileId);
            Assert.NotEqual(ProcessingProfile.SummaryBakeoff, recommendation.Summarization.ProfileId);
        }
    }

    [Fact]
    public void AMachineThatSaidNothingAtAllStillProducesAnExplainedAnswer()
    {
        SetupRecommendation recommendation = ProfileRecommender.Recommend(HardwareSnapshot.Unknown);

        // Never an exception, never an empty recommendation. Hardware detection failing must not
        // be able to stop somebody using the application.
        Assert.NotEmpty(recommendation.Transcription.Reasons);
        Assert.NotEmpty(recommendation.Summarization.Reasons);
        Assert.True(recommendation.Transcription.IsFallback);
    }

    [Fact]
    public void EveryRecommendationExplainsItself()
    {
        foreach (HardwareSnapshot machine in (HardwareSnapshot[])
        [
            Machines.WithNvidia(16 * Gb),
            Machines.WithNvidia(2 * Gb),
            Machines.WithNvidia(null, CudaAvailability.AdapterWithoutRuntime),
            Machines.WithoutGpu(),
            Machines.WithoutGpu(memory: 4 * Gb),
            Machines.Base(avx2: false),
        ])
        {
            SetupRecommendation recommendation = ProfileRecommender.Recommend(machine);

            Assert.NotEmpty(recommendation.Transcription.Reasons);
            Assert.NotEmpty(recommendation.Summarization.Reasons);
            Assert.All(recommendation.Transcription.Reasons, r => Assert.False(string.IsNullOrWhiteSpace(r)));
            Assert.All(recommendation.Summarization.Reasons, r => Assert.False(string.IsNullOrWhiteSpace(r)));
        }
    }

    [Fact]
    public void TheSameMachineAlwaysGetsTheSameAnswer()
    {
        HardwareSnapshot machine = Machines.WithNvidia(16 * Gb);

        SetupRecommendation first = ProfileRecommender.Recommend(machine);
        SetupRecommendation second = ProfileRecommender.Recommend(machine);

        Assert.Equal(first.Transcription.ProfileId, second.Transcription.ProfileId);
        Assert.Equal(first.Summarization.ProfileId, second.Summarization.ProfileId);
        Assert.Equal(first.Transcription.Reasons, second.Transcription.Reasons);
    }
}
