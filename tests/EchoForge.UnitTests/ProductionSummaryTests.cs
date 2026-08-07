using System.Text.Json;
using EchoForge.App;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Summaries;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Summaries;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.UnitTests;

/// <summary>
/// The production summariser's host side: what it is allowed to run, and what it refuses to.
///
/// <para>
/// Nothing here starts a language model. What is worth testing on this side is not that Gemma
/// answers well — that needs the annotated corpora — but that an unverified binary can never
/// become the thing that summarised a meeting, and that a machine without the model behaves like
/// a machine without the model rather than like a broken one.
/// </para>
/// </summary>
public sealed class ProductionSummaryTests : IDisposable
{
    private const string SessionId = "01JPRODSUMMARY";

    private readonly TempDirectory _temp = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _temp.Dispose();
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EchoForge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static ArtifactManifest Manifest()
    {
        ManifestLoadResult result = ArtifactManifestReader.Load(
            Path.Combine(RepositoryRoot(), "artifacts", "manifest.json"));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        return result.Manifest!;
    }

    private ArtifactRegistry Registry()
    {
        ArtifactRegistry registry = new(Manifest(), Path.Combine(_temp.Path, "models"));
        _disposables.Add(registry);
        return registry;
    }

    // -- what is pinned -------------------------------------------------------------------------

    [Fact]
    public void TheSummaryModelIsPinnedToAnOfficialImmutableRevision()
    {
        // There is more than one summary model in the manifest now that a bake-off candidate is
        // pinned, so this names the default rather than assuming it is the only one.
        ArtifactEntry model = Assert.Single(
            Manifest().Artifacts, a => a.ArtifactId == "summary.gemma-4-12b-it-qat-q4-0");

        Assert.Equal("summary-model", model.Kind);

        // Google's own repository, not a community re-quantization of it.
        Assert.Contains("huggingface.co/google/", model.Repository, StringComparison.Ordinal);
        Assert.Contains("google/gemma-4-12B-it-qat-q4_0-gguf", model.Url, StringComparison.Ordinal);

        // The URL must fetch the exact commit the manifest names, or the pin means nothing.
        Assert.Equal(40, model.Revision.Length);
        Assert.Contains(model.Revision, model.Url, StringComparison.Ordinal);

        Assert.Equal(6_975_879_296, model.SizeBytes);
        Assert.Equal("93567e57a8fe10b23569b9d9ec38cd005deedf71e29477c421a4b83f418a538b", model.Sha256);
        Assert.Equal("Apache-2.0", model.License);
    }

    [Fact]
    public void TheVisionProjectorIsDeliberatelyNotPinned()
    {
        // The same repository ships an mmproj file. EchoForge summarises text, and loading a
        // vision tower would spend VRAM on a capability it never uses.
        Assert.DoesNotContain(Manifest().Artifacts, a => a.FileName.Contains("mmproj", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCudaRuntimeIsTheBlackwellCapableBuild()
    {
        ArtifactEntry cuda = Manifest().Artifacts.Single(a => a.ArtifactId == "summary.llama-cpp-cuda");

        // The target GPU is Blackwell, which CUDA 12.4 has no kernels for.
        Assert.Contains("cuda-13.3", cuda.FileName, StringComparison.Ordinal);
        Assert.Contains(cuda.Revision, cuda.Url, StringComparison.Ordinal);
        Assert.Equal("MIT", cuda.License);
    }

    [Fact]
    public void EverySummaryArtifactRetainsALicenceFileThatExists()
    {
        string root = RepositoryRoot();

        foreach (ArtifactEntry entry in Manifest().Artifacts.Where(a => a.ArtifactId.StartsWith("summary.", StringComparison.Ordinal)))
        {
            Assert.True(
                File.Exists(Path.Combine(root, entry.LicenseFile.Replace('/', Path.DirectorySeparatorChar))),
                $"{entry.ArtifactId} names a licence file that is not in the repository: {entry.LicenseFile}");
        }
    }

    [Fact]
    public void BothSummaryProfilesResolveAndNeedTheModel()
    {
        ArtifactRegistry registry = Registry();

        foreach (string id in ProcessingProfile.SummaryProfiles)
        {
            ProcessingProfile profile = registry.Profile(id)
                ?? throw new InvalidOperationException($"{id} is not derivable from the manifest");

            Assert.Contains(profile.Artifacts, a => a.Kind == "summary-model");
            Assert.Contains(profile.Artifacts, a => a.FileName.EndsWith(".zip", StringComparison.Ordinal));
        }
    }

    // -- nothing unverified ever runs ------------------------------------------------------------

    [Fact]
    public void AProfileWithNothingDownloadedResolvesToNoRuntimeAtAll()
    {
        LlamaRuntimeStager stager = new(Registry());

        Assert.Null(stager.TryResolve(ProcessingProfile.SummaryCudaQ4));

        SummaryRuntimeStatus status = stager.Status(ProcessingProfile.SummaryCudaQ4);
        Assert.False(status.Ready);
        Assert.True(status.AnythingToDownload);
        Assert.True(status.BytesRequired > 6_000_000_000);
    }

    [Fact]
    public void AnUnverifiedBinarySittingInTheStagingDirectoryIsNotAccepted()
    {
        ArtifactRegistry registry = Registry();
        LlamaRuntimeStager stager = new(registry);

        // Somebody drops a llama-server.exe in the right place by hand. Nothing about it was
        // hashed against the manifest, so it is an unknown runtime rather than a degraded one.
        string staged = stager.StagingRoot(ProcessingProfile.SummaryCudaQ4);
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, LlamaRuntimeStager.ServerBinaryName), "not really llama.cpp");

        Assert.Null(stager.TryResolve(ProcessingProfile.SummaryCudaQ4));
    }

    [Fact]
    public async Task AProductionRunIsRefusedWhenTheModelIsNotInstalled()
    {
        SummaryCoordinator coordinator = Coordinator(out FileSessionStore sessions, out FileTranscriptionStore transcripts);
        PlantTranscript(sessions, transcripts);

        SummaryRunResult result = await coordinator.SummarizeAsync(
            SessionId, new SummaryOptions { Backend = SummaryOptions.ProductionBackend });

        Assert.Equal("summary_model_missing", result.FailureCode);
        Assert.Contains("not installed", result.Message, StringComparison.Ordinal);

        // Refused before an attempt existed, so there is no failed revision for a run that never
        // had anything to run with.
        Assert.Empty(new FileSummaryStore(sessions).Read(SessionId).Revisions);
    }

    [Fact]
    public async Task ThePlaceholderStillWorksOnAMachineWithNoSummaryModel()
    {
        SummaryCoordinator coordinator = Coordinator(out FileSessionStore sessions, out FileTranscriptionStore transcripts);
        WorkerTestEnvironment.CreateRecordedSession(sessions, SessionId);
        PlantTranscript(sessions, transcripts);

        SummaryRunResult result = await coordinator.SummarizeAsync(SessionId, new SummaryOptions());

        // A missing summary model must never make the rest of EchoForge worse.
        Assert.True(result.Succeeded, result.Message);
        Assert.False(coordinator.ProductionAvailable);
    }

    // -- input identity ---------------------------------------------------------------------------

    [Fact]
    public void ChangingTheBackendChangesEveryChunkFingerprint()
    {
        TranscriptDocument transcript = Transcript();

        IReadOnlyList<SummaryChunk> placeholder = TranscriptChunker.Plan(transcript, new SummaryOptions());
        IReadOnlyList<SummaryChunk> production = TranscriptChunker.Plan(
            transcript, new SummaryOptions { Backend = SummaryOptions.ProductionBackend });

        // The same transcript through two different models is two different extractions, and a
        // checkpoint from one must never be reused for the other. The tokenizer lives inside the
        // pinned GGUF, so naming the backend names the tokenizer too.
        Assert.Equal(placeholder.Count, production.Count);
        for (int i = 0; i < placeholder.Count; i++)
        {
            Assert.NotEqual(placeholder[i].InputFingerprint, production[i].InputFingerprint);
        }
    }

    [Fact]
    public void ChangingTheRuntimeProfileOrTheSeedChangesEveryFingerprint()
    {
        TranscriptDocument transcript = Transcript();
        SummaryOptions baseline = new() { Backend = SummaryOptions.ProductionBackend };

        string[] original = [.. TranscriptChunker.Plan(transcript, baseline).Select(c => c.InputFingerprint)];

        string[] cpu = [.. TranscriptChunker
            .Plan(transcript, baseline with { SummaryProfile = ProcessingProfile.SummaryCpuQ4 })
            .Select(c => c.InputFingerprint)];

        string[] reseeded = [.. TranscriptChunker
            .Plan(transcript, baseline with { Seed = 11 })
            .Select(c => c.InputFingerprint)];

        Assert.All(original.Zip(cpu), pair => Assert.NotEqual(pair.First, pair.Second));
        Assert.All(original.Zip(reseeded), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public void TheSameProductionOptionsProduceTheSameFingerprints()
    {
        TranscriptDocument transcript = Transcript();
        SummaryOptions options = new() { Backend = SummaryOptions.ProductionBackend, Seed = 7 };

        Assert.Equal(
            TranscriptChunker.Plan(transcript, options).Select(c => c.InputFingerprint),
            TranscriptChunker.Plan(transcript, options).Select(c => c.InputFingerprint));
    }

    // -- the surface -------------------------------------------------------------------------------

    [Fact]
    public void ThePanelOffersThePlaceholderWhenNoModelIsInstalled()
    {
        SummaryCoordinator coordinator = Coordinator(out FileSessionStore sessions, out FileTranscriptionStore transcripts);
        PlantTranscript(sessions, transcripts);

        SummaryViewModel viewModel = new(coordinator);
        _disposables.Add(viewModel);
        viewModel.UpdateHost(SessionId, 1, false, true, false);

        Assert.False(viewModel.ProductionAvailable);
        Assert.False(viewModel.UseProductionModel);
        Assert.Contains("placeholder", viewModel.BackendText, StringComparison.OrdinalIgnoreCase);

        // The download is offered, with its real size, rather than the feature simply being absent.
        Assert.True(viewModel.CanInstallModel);
        Assert.Contains("GB", viewModel.ModelStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModelCannotBeDownloadedWhileRecording()
    {
        SummaryCoordinator coordinator = Coordinator(out FileSessionStore sessions, out FileTranscriptionStore transcripts);
        PlantTranscript(sessions, transcripts);

        SummaryViewModel viewModel = new(coordinator);
        _disposables.Add(viewModel);

        viewModel.UpdateHost(SessionId, 1, recordingActive: true, hostReady: true, shuttingDown: false);

        // Recording outranks everything, including a seven-gigabyte download.
        Assert.False(viewModel.CanInstallModel);
        Assert.False(viewModel.CanGenerate);
    }

    [Fact]
    public void AskingForTheLocalModelWhenItIsAbsentFallsBackToThePlaceholderRatherThanFailing()
    {
        SummaryCoordinator coordinator = Coordinator(out FileSessionStore sessions, out FileTranscriptionStore transcripts);
        PlantTranscript(sessions, transcripts);

        SummaryViewModel viewModel = new(coordinator);
        _disposables.Add(viewModel);
        viewModel.UpdateHost(SessionId, 1, false, true, false);

        viewModel.UseProductionModel = true;

        // The checkbox can be set, but what it would actually run is still the placeholder, and
        // the panel says so rather than letting the click fail later.
        Assert.Contains("placeholder", viewModel.BackendText, StringComparison.OrdinalIgnoreCase);
    }

    // -- fixtures -----------------------------------------------------------------------------------

    private SummaryCoordinator Coordinator(out FileSessionStore sessions, out FileTranscriptionStore transcripts)
    {
        sessions = new FileSessionStore(_temp.Path);
        transcripts = new FileTranscriptionStore(sessions);
        sessions.Create(SessionId);

        SummaryCoordinator coordinator = new(
            sessions,
            new FileSummaryStore(sessions),
            transcripts,
            new WorkerSupervisor(WorkerTestEnvironment.Options()),
            runtime: new LlamaRuntimeStager(Registry()));

        _disposables.Add(coordinator);
        return coordinator;
    }

    private static TranscriptDocument Transcript() => new()
    {
        SessionId = SessionId,
        TranscriptRevision = 1,
        CreatedAtUtc = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero),
        SourceManifestSha256 = new string('a', 64),
        DurationSeconds = 3600,
        Model = new TranscriptModel("echoforge-mock", "mock", "mock-v1", "mock-v1", "none", false, "0.1.0"),
        Epochs = [new TranscriptEpoch(1, 0, 3600)],
        Speakers =
        [
            new TranscriptSpeaker(TranscriptSpeakers.YouId, TranscriptSpeakers.YouName, TranscriptSpeakers.MicrophoneTrack),
            new TranscriptSpeaker(TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName, TranscriptSpeakers.SystemTrack),
        ],
        Languages = [new TranscriptLanguage(TranscriptSpeakers.MicrophoneTrack, "en", null)],
        Segments =
        [
            .. Enumerable.Range(1, 12).Select(i =>
            {
                (string speakerId, string speakerName) = TranscriptSpeakers.For(TranscriptSpeakers.MicrophoneTrack);
                return new TranscriptSegment
                {
                    Id = $"segment-{i:D6}",
                    Epoch = 1,
                    StartSeconds = i * 10,
                    EndSeconds = (i * 10) + 5,
                    SpeakerId = speakerId,
                    SpeakerName = speakerName,
                    SourceTrack = TranscriptSpeakers.MicrophoneTrack,
                    Text = $"We will ship item {i.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    Confidence = null,
                    Language = "en",
                    Words = [],
                };
            })
        ],
    };

    private static void PlantTranscript(FileSessionStore sessions, FileTranscriptionStore transcripts)
    {
        TranscriptionAttempt attempt = transcripts.BeginAttempt(
            SessionId, "job-transcript", new string('a', 64), new TranscriptionOptions(), DateTimeOffset.UtcNow);

        TranscriptDocument transcript = Transcript() with { TranscriptRevision = attempt.Revision };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(transcript, TranscriptDocument.Json);

        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
        File.WriteAllBytes(attempt.StagingPath, payload);

        ActivationOutcome outcome = transcripts.Activate(
            new ActivationRequest(
                attempt,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)),
                transcript,
                1,
                null),
            DateTimeOffset.UtcNow);

        Assert.True(outcome.Activated, outcome.Refusal);
    }
}
