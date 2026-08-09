using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Inference;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Inference;
using EchoForge.Core.Processing;
using EchoForge.Core.Summaries;
using EchoForge.Core.Transcripts;

namespace EchoForge.UnitTests;

public sealed class ModelRegistryTests
{
    [Fact]
    public void RegistrySeparatesModelBackendComputeVadAndWindowCapabilities()
    {
        Assert.Equal(
            [
                AsrModelIds.Mock,
                AsrModelIds.WhisperLargeV3Turbo,
                AsrModelIds.WhisperLargeV3,
                AsrModelIds.ParakeetUnifiedEn06B,
                AsrModelIds.CanaryQwen25B,
            ],
            InferenceModelRegistry.AsrModels.Select(model => model.Id));

        AsrModelDefinition full = InferenceModelRegistry.GetAsr(AsrModelIds.WhisperLargeV3);
        Assert.Equal(AsrBackendIds.FasterWhisper, full.BackendId);
        Assert.Contains(ComputeProfileIds.CudaFloat16, full.SupportedComputeProfiles);
        Assert.Contains(VadMode.Off, full.SupportedVadModes);
        Assert.Equal(AsrTimestampPrecision.WordNative, full.TimestampPrecision);
        Assert.Equal(ProcessingProfile.AsrWhisperLargeV3, full.ArtifactProfileId);
        Assert.DoesNotContain("main", full.ArtifactIdentity, StringComparison.OrdinalIgnoreCase);

        AsrModelDefinition canary = InferenceModelRegistry.GetAsr(AsrModelIds.CanaryQwen25B);
        Assert.Equal(AsrBackendIds.Nemo, canary.BackendId);
        Assert.Equal(ModelMaturity.Experimental, canary.Maturity);
        Assert.Equal(AsrTimestampCapability.Segment, canary.TimestampCapability);
        Assert.Equal(AsrTimestampPrecision.WindowApproximate, canary.TimestampPrecision);
        Assert.InRange(canary.WindowStrategy.WindowSeconds, 30, 40);
        Assert.Equal(40, canary.MaximumWindowSeconds);
        Assert.True(canary.WindowStrategy.WindowSeconds <= canary.MaximumWindowSeconds);
        Assert.False(canary.WindowStrategy.PreferSpeechAwareBoundaries);
        Assert.False(canary.SupportsGlossaryPrompt);

        AsrModelDefinition parakeet = InferenceModelRegistry.GetAsr(AsrModelIds.ParakeetUnifiedEn06B);
        Assert.Equal(["en"], parakeet.Languages);
        Assert.Contains(ComputeProfileIds.CudaBFloat16, parakeet.SupportedComputeProfiles);
        Assert.DoesNotContain(ComputeProfileIds.CpuInt8, parakeet.SupportedComputeProfiles);
    }

    [Fact]
    public void SummaryRegistryKeepsProductionAndComparisonModelsDistinct()
    {
        Assert.Equal(3, InferenceModelRegistry.SummaryModels.Count);
        SummaryModelDefinition gemma = InferenceModelRegistry.GetSummary(SummaryModelIds.Gemma4TwelveB);
        SummaryModelDefinition gpt = InferenceModelRegistry.GetSummary(SummaryModelIds.GptOss20B);

        Assert.Equal(ModelMaturity.Production, gemma.Maturity);
        Assert.Equal("Apache-2.0", gemma.License);
        Assert.Equal(ModelMaturity.Experimental, gpt.Maturity);
        Assert.True(gpt.UsesHarmonyTemplate);
        Assert.Equal(16384, gpt.RecommendedContextTokens);
        Assert.Contains("ef9b12", gpt.ArtifactIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownModelsAreRefusedInsteadOfFallingThroughToAnotherBackend()
    {
        Assert.Throws<ArgumentException>(() => InferenceModelRegistry.GetAsr("whisper-ish"));
        Assert.Throws<ArgumentException>(() => InferenceModelRegistry.GetSummary("some-gguf"));
    }
}

public sealed class ModelSpecificWindowPlannerTests
{
    [Theory]
    [InlineData(AsrModelIds.WhisperLargeV3, 1400)]
    [InlineData(AsrModelIds.ParakeetUnifiedEn06B, 721)]
    [InlineData(AsrModelIds.CanaryQwen25B, 91)]
    public void EveryModelStrategyCoversTheEpochWithoutLosingItsTail(string modelId, double duration)
    {
        AsrWindowStrategy strategy = InferenceModelRegistry.GetAsr(modelId).WindowStrategy;
        WindowPlan plan = TranscriptionWindowPlanner.Plan(
            Request(duration),
            Derivatives(duration),
            ProcessingProfile.CudaFp16,
            new WindowPlanOptions
            {
                WindowSeconds = strategy.WindowSeconds,
                OverlapSeconds = strategy.OverlapSeconds,
                StrategyId = strategy.Id,
                PlanningVersion = strategy.Id + "-test",
            });

        Assert.NotEmpty(plan.Windows);
        Assert.Equal(strategy.Id, plan.StrategyId);
        Assert.Equal(0, plan.Windows[0].SessionStartSeconds);
        Assert.Equal(duration, plan.Windows[^1].SessionEndSeconds, 6);
        Assert.All(plan.Windows, window =>
        {
            Assert.True(window.DurationSeconds > 0);
            Assert.True(window.DurationSeconds <= strategy.WindowSeconds + 1e-9);
        });

        for (int index = 1; index < plan.Windows.Count; index++)
        {
            Assert.Equal(
                strategy.OverlapSeconds,
                plan.Windows[index - 1].SessionEndSeconds - plan.Windows[index].SessionStartSeconds,
                6);
        }
    }

    [Fact]
    public void CanaryWindowsNeverExceedTheVerifiedFortySecondBound()
    {
        AsrWindowStrategy strategy = InferenceModelRegistry.CanaryWindowing;
        WindowPlan plan = TranscriptionWindowPlanner.Plan(
            Request(3600),
            Derivatives(3600),
            ProcessingProfile.CudaFp16,
            new WindowPlanOptions
            {
                WindowSeconds = strategy.WindowSeconds,
                OverlapSeconds = strategy.OverlapSeconds,
                StrategyId = strategy.Id,
            });

        Assert.All(plan.Windows, window => Assert.InRange(window.DurationSeconds, 0.000001, 40));
    }

    private static TranscriptionRequest Request(double duration) => new()
    {
        SessionId = "01JMODELWINDOW",
        TranscriptRevision = 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        SessionRoot = @"C:\sessions\01JMODELWINDOW",
        OutputPath = @"C:\sessions\01JMODELWINDOW\out.json",
        DurationSeconds = duration,
        Epochs = [new RequestEpoch(1, 0, duration)],
        Tracks =
        [
            new RequestTrack
            {
                SourceTrack = TranscriptSpeakers.SystemTrack,
                Chunks =
                [
                    new RequestChunk
                    {
                        Index = 1,
                        Epoch = 1,
                        RelativePath = "tracks/system/chunks/000001.wav",
                        StartSeconds = 0,
                        EndSeconds = duration,
                        SampleRate = 16000,
                        Channels = 1,
                        Frames = (long)(duration * 16000),
                        Sha256 = new string('a', 64),
                    },
                ],
            },
        ],
        Options = new RequestOptions { Backend = AsrBackendIds.Mock },
    };

    private static DerivativeSet Derivatives(double duration) => new(
    [
        new DerivativeRecord
        {
            SourceTrack = TranscriptSpeakers.SystemTrack,
            RelativePath = "derived/audio/system.wav",
            TimingMapRelativePath = "derived/audio/system.timing.json",
            Sha256 = new string('b', 64),
            TimingMapSha256 = new string('c', 64),
            SizeBytes = 44 + (long)(duration * 32000),
            SampleRate = 16000,
            Channels = 1,
            TotalFrames = (long)(duration * 16000),
            SourceManifestSha256 = new string('d', 64),
            ProcessingVersion = "derivative-v1",
            CreatedUtc = DateTimeOffset.UnixEpoch,
        },
    ]);
}

public sealed class RevisionComparisonTests
{
    [Fact]
    public void TranscriptComparisonTreatsMissingSpeechAsMoreThanPunctuation()
    {
        TranscriptDocument left = Transcript(1,
            Segment("a", 10, 11, "Yeah."),
            Segment("b", 30, 31, "Tuesday"));
        TranscriptDocument right = Transcript(2,
            Segment("c", 10, 11, "Yeah"));

        TranscriptComparisonResult comparison = TranscriptComparer.Compare(left, right);

        Assert.Contains(comparison.Rows, row => row.Difference == TranscriptDifferenceKind.PunctuationOnly);
        TranscriptComparisonRow missing = Assert.Single(
            comparison.Rows, row => row.Difference == TranscriptDifferenceKind.MissingFromRight);
        Assert.True(missing.IsMissingRegion);
        Assert.Equal("Tuesday", missing.LeftText);
        Assert.Equal(1, comparison.RightMetrics.RegionsMissingFromThisRevision);
        Assert.Equal(2, comparison.LeftMetrics.Words);
    }

    [Fact]
    public void TranscriptComparisonRejectsDifferentSourceAudio()
    {
        TranscriptDocument left = Transcript(1, Segment("a", 0, 1, "hello"));
        TranscriptDocument right = Transcript(2, Segment("b", 0, 1, "hello")) with
        {
            SourceManifestSha256 = new string('e', 64),
        };

        Assert.Throws<ArgumentException>(() => TranscriptComparer.Compare(left, right));
    }

    [Fact]
    public void TranscriptComparisonFindsAMissedReplyInsideContinuousConversation()
    {
        TranscriptDocument left = Transcript(1,
            Segment("a", 0, 1, "Start"),
            Segment("b", 1, 2, "No"),
            Segment("c", 2, 3, "Continue"));
        TranscriptDocument right = Transcript(2,
            Segment("d", 0, 1, "Start"),
            Segment("e", 2, 3, "Continue"));

        TranscriptComparisonResult comparison = TranscriptComparer.Compare(left, right);

        TranscriptComparisonRow missing = Assert.Single(
            comparison.Rows,
            row => row.Difference == TranscriptDifferenceKind.MissingFromRight);
        Assert.Equal("No", missing.LeftText);
    }

    [Fact]
    public void SummaryComparisonRequiresTheExactSameTranscriptRevisionAndDigest()
    {
        TranscriptDocument transcript = Transcript(1, Segment("a", 0, 2, "We will ship Tuesday."));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "a")!;
        SummaryDocument left = Summary(1, transcript, citation, "Ship Tuesday", SummaryModelIds.Gemma4TwelveB);
        SummaryDocument right = Summary(2, transcript, citation, "Ship on Tuesday", SummaryModelIds.GptOss20B);

        SummaryComparisonResult result = SummaryComparer.Compare(left, right);

        Assert.Equal(transcript.TranscriptRevision, result.TranscriptRevision);
        Assert.Contains(result.Rows, row => row.Section == "Key points" && !row.MissingFromLeft && !row.MissingFromRight);

        Assert.Throws<ArgumentException>(() => SummaryComparer.Compare(
            left,
            right with { TranscriptRevision = 2 }));
        Assert.Throws<ArgumentException>(() => SummaryComparer.Compare(
            left,
            right with { TranscriptSha256 = new string('f', 64) }));
    }

    [Fact]
    public void GroundedNarrativeAcceptsValidatedFactsAndRejectsUnknownSupport()
    {
        TranscriptDocument transcript = Transcript(1, Segment("a", 0, 2, "We will ship Tuesday."));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "a")!;
        SummaryDocument summary = Summary(1, transcript, citation, "Ship Tuesday", SummaryModelIds.Gemma4TwelveB);

        Assert.True(SummaryValidator.Validate(summary, transcript).IsValid);

        SummaryNarrative invalid = summary.Narrative! with
        {
            Summary =
            [
                summary.Narrative!.Summary[0] with { SupportingItemIds = ["invented-fact"] },
            ],
        };
        SummaryVerdict verdict = SummaryValidator.Validate(summary with { Narrative = invalid }, transcript);
        Assert.False(verdict.IsValid);
        Assert.Contains(verdict.Problems, problem => problem.Contains("unknown fact", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoricalSchemaOneSummaryStillValidatesWithoutNarrative()
    {
        TranscriptDocument transcript = Transcript(1, Segment("a", 0, 2, "We will ship Tuesday."));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "a")!;
        SummaryDocument historical = Summary(1, transcript, citation, "Ship Tuesday", SummaryModelIds.Gemma4TwelveB) with
        {
            SchemaVersion = 1,
            Narrative = null,
        };

        Assert.True(SummaryValidator.Validate(historical, transcript).IsValid);
    }

    private static TranscriptDocument Transcript(int revision, params TranscriptSegment[] segments) => new()
    {
        SchemaVersion = 2,
        SessionId = "01JCOMPARE",
        TranscriptRevision = revision,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        SourceManifestSha256 = new string('d', 64),
        DurationSeconds = 60,
        Model = new TranscriptModel(
            "test", AsrBackendIds.FasterWhisper, AsrModelIds.WhisperLargeV3,
            "pinned", ComputeProfileIds.CudaFloat16, true, "test"),
        Epochs = [new TranscriptEpoch(1, 0, 60)],
        Speakers = [new TranscriptSpeaker(TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName, TranscriptSpeakers.SystemTrack)],
        Languages = [new TranscriptLanguage(TranscriptSpeakers.SystemTrack, "en", 1)],
        Segments = segments,
    };

    private static TranscriptSegment Segment(string id, double start, double end, string text) => new()
    {
        Id = id,
        Epoch = 1,
        StartSeconds = start,
        EndSeconds = end,
        SpeakerId = TranscriptSpeakers.RemoteId,
        SpeakerName = TranscriptSpeakers.RemoteName,
        SourceTrack = TranscriptSpeakers.SystemTrack,
        Text = text,
        Language = "en",
    };

    private static SummaryDocument Summary(
        int revision,
        TranscriptDocument transcript,
        SummaryEvidence citation,
        string text,
        string modelId)
    {
        SummaryItem fact = new()
        {
            Id = "fact-1",
            Text = text,
            Certainty = SupportStatuses.Explicit,
            Evidence = [citation],
        };
        SummaryNarrativeBlock prose = new()
        {
            Id = "narrative-1",
            Text = "The team committed to shipping Tuesday.",
            SupportingItemIds = [fact.Id],
            Evidence = [citation],
        };

        return new SummaryDocument
        {
            SchemaVersion = 2,
            SessionId = transcript.SessionId,
            SummaryRevision = revision,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            TranscriptRevision = transcript.TranscriptRevision,
            TranscriptSha256 = new string('a', 64),
            PromptVersion = "extract-v1+synthesize-v1+narrative-v1",
            Model = new SummaryModel("llama.cpp", "local", modelId, "pinned", 16384, false, true, "test"),
            Title = "Meeting",
            Overview = prose.Text,
            Narrative = new SummaryNarrative { Summary = [prose] },
            KeyPoints = [fact],
        };
    }
}
