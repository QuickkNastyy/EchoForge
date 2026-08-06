using EchoForge.App;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Exports;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.UnitTests;

/// <summary>
/// Production preparation, wired into the coordinator beside the placeholder path rather than
/// replacing it.
///
/// <para>
/// The point of this pass is that everything expensive can be done, proven, and re-run before any
/// recogniser exists. These check that it happens in the right order, refuses for the right
/// reasons, and never claims to have transcribed anything.
/// </para>
/// </summary>
public sealed class ProcessingPreparationTests : IDisposable
{
    private const string SessionId = "01JPREPARE";

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _sessions;
    private readonly FileTranscriptionStore _transcripts;
    private readonly SwitchableGate _gate = new();
    private readonly List<IDisposable> _disposables = [];

    public ProcessingPreparationTests()
    {
        _sessions = new FileSessionStore(_temp.Path);
        _transcripts = new FileTranscriptionStore(_sessions);
    }

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _temp.Dispose();
    }

    private static byte[] Payload(int length = 32 * 1024, byte seed = 5)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 17 + seed) & 0xFF);
        }

        return bytes;
    }

    private static ArtifactEntry Entry(string url, byte[] content, string id) => new()
    {
        ArtifactId = id,
        Kind = "speech-model",
        Repository = "https://example.invalid/model",
        Url = url,
        Revision = "0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf",
        FileName = $"{id}.bin",
        SizeBytes = content.Length,
        Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)),
        License = "MIT",
        LicenseFile = "third_party/licenses/ctranslate2-4.8.1-LICENSE.txt",
        RuntimeVersion = "test",
        Profiles = ["cpu-int8"],
        VerifiedUtc = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero),
    };

    private ArtifactRegistry Registry(params ArtifactEntry[] entries)
    {
        ArtifactRegistry registry = new(
            new ArtifactManifest { Artifacts = entries }, Path.Combine(_temp.Path, "models"))
        {
            TransferTimeout = TimeSpan.FromSeconds(30),
        };

        _disposables.Add(registry);
        return registry;
    }

    private TranscriptionCoordinator Coordinator(ArtifactRegistry registry)
    {
        TranscriptionCoordinator coordinator = new(
            _sessions,
            _transcripts,
            new WorkerSupervisor(WorkerTestEnvironment.Options()),
            _gate,
            preparation: new ProcessingPreparation(_sessions, registry, new DerivativeBuilder(_sessions)));

        _disposables.Add(coordinator);
        return coordinator;
    }

    // -- artifacts gate preparation -----------------------------------------------------------

    [Fact]
    public async Task PreparationStopsWhenTheModelsAreNotInstalled()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        using LoopbackHttpServer server = new(Payload());
        TranscriptionCoordinator coordinator = Coordinator(Registry(Entry(server.Url, Payload(), "stt.test")));

        PreparationResult result = await coordinator.PrepareAsync(SessionId, ProcessingProfile.CpuInt8);

        Assert.Equal(PreparationStage.ArtifactsMissing, result.Stage);
        Assert.Equal("artifacts_missing", result.FailureCode);
        Assert.Contains("downloaded", result.Message, StringComparison.Ordinal);

        // Nothing was fetched, because nothing asked for it.
        Assert.Equal(0, server.Requests);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task PreparationInstallsTheModelsThenPreparesAudioAndPlansWindows()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId, seconds: 3.0);
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        ArtifactRegistry registry = Registry(Entry(server.Url, content, "stt.test"));
        TranscriptionCoordinator coordinator = Coordinator(registry);

        List<PreparationStage> stages = [];
        coordinator.PreparationProgress += (_, e) => { lock (stages) { stages.Add(e.Stage); } };

        PreparationResult result = await coordinator.PrepareAsync(
            SessionId, ProcessingProfile.CpuInt8, installMissing: true);

        Assert.Equal(PreparationStage.Ready, result.Stage);
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Derivatives);

        // The order matters: nothing is prepared against models that are not there yet.
        lock (stages)
        {
            Assert.Contains(PreparationStage.CheckingArtifacts, stages);
            Assert.Contains(PreparationStage.Downloading, stages);
            Assert.Contains(PreparationStage.PreparingAudio, stages);
            Assert.Contains(PreparationStage.PlanningWindows, stages);
            Assert.True(
                stages.IndexOf(PreparationStage.Downloading) < stages.IndexOf(PreparationStage.PreparingAudio),
                "audio was prepared before the models arrived");
        }

        Assert.True(registry.IsProfileReady(registry.Profile(ProcessingProfile.CpuInt8)!));

        // Both tracks were converted, and the windows describe them.
        Assert.Equal(2, result.Derivatives!.Derivatives.Count);
        Assert.All(result.Derivatives.Derivatives, d => Assert.Equal(16000, d.SampleRate));
        Assert.NotEmpty(result.Plan!.Windows);
        Assert.Contains(result.Plan.Windows, w => w.SourceTrack == "microphone");
        Assert.Contains(result.Plan.Windows, w => w.SourceTrack == "system");
    }

    [Fact]
    public async Task PreparationSaysPlainlyThatRecognitionIsNotImplemented()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        TranscriptionCoordinator coordinator = Coordinator(Registry(Entry(server.Url, content, "stt.test")));

        PreparationResult result = await coordinator.PrepareAsync(
            SessionId, ProcessingProfile.CpuInt8, installMissing: true);

        Assert.Contains("not implemented", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThePlanIsWrittenDownAndItsFinishedWindowsSurviveAReRun()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        TranscriptionCoordinator coordinator = Coordinator(Registry(Entry(server.Url, content, "stt.test")));

        PreparationResult first = await coordinator.PrepareAsync(SessionId, ProcessingProfile.CpuInt8, true);
        Assert.Equal(PreparationStage.Ready, first.Stage);

        string planPath = ProcessingPreparation.PlanPath(_sessions.Resolve(SessionId), new WindowPlanOptions());
        Assert.True(File.Exists(planPath));

        // Mark one window done, exactly as an inference pass eventually would.
        WindowPlan stored = System.Text.Json.JsonSerializer.Deserialize<WindowPlan>(
            await File.ReadAllBytesAsync(planPath), WindowPlan.Json)!;

        WindowPlan withProgress = stored with
        {
            Checkpoints =
            [
                stored.Checkpoints[0] with { State = WindowCheckpointState.Succeeded },
                .. stored.Checkpoints.Skip(1),
            ],
        };

        await File.WriteAllBytesAsync(
            planPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(withProgress, WindowPlan.Json));

        PreparationResult second = await coordinator.PrepareAsync(SessionId, ProcessingProfile.CpuInt8, true);

        Assert.Equal(PreparationStage.Ready, second.Stage);
        Assert.Equal(
            WindowCheckpointState.Succeeded,
            second.Plan!.CheckpointFor(stored.Windows[0].Id)!.State);
    }

    // -- recording still wins ---------------------------------------------------------------------

    [Fact]
    public async Task PreparationIsRefusedWhileCaptureIsLive()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        TranscriptionCoordinator coordinator = Coordinator(Registry(Entry(server.Url, content, "stt.test")));

        _gate.IsCaptureActive = true;

        PreparationResult result = await coordinator.PrepareAsync(SessionId, ProcessingProfile.CpuInt8, true);

        Assert.Equal(PreparationStage.Blocked, result.Stage);
        Assert.Equal("recording_active", result.FailureCode);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task CancellingPreparationLeavesTheRecordingUntouched()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId, seconds: 3.0);
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content) { ResponseDelay = TimeSpan.FromSeconds(20) };
        TranscriptionCoordinator coordinator = Coordinator(Registry(Entry(server.Url, content, "stt.test")));

        DirectorySnapshot before = DirectorySnapshot.Capture(_sessions.Resolve(SessionId).TracksRoot);

        using CancellationTokenSource cancellation = new();
        Task<PreparationResult> run = coordinator.PrepareAsync(
            SessionId, ProcessingProfile.CpuInt8, installMissing: true, cancellation.Token);

        await Task.Delay(400);
        await cancellation.CancelAsync();

        PreparationResult result = await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.Stage is PreparationStage.Cancelled or PreparationStage.Failed);
        Assert.Null(result.Plan);

        DirectorySnapshot after = DirectorySnapshot.Capture(_sessions.Resolve(SessionId).TracksRoot);
        Assert.True(after.Matches(before), after.Describe());
    }

    [Fact]
    public async Task AnUnverifiedRecordingIsRefusedBeforeAnythingIsDownloaded()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);

        string chunk = Path.Combine(
            _sessions.Resolve(SessionId).Root, "tracks", "microphone", "chunks", "000001.wav");
        byte[] bytes = await File.ReadAllBytesAsync(chunk);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(chunk, bytes);

        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        TranscriptionCoordinator coordinator = Coordinator(Registry(Entry(server.Url, content, "stt.test")));

        PreparationResult result = await coordinator.PrepareAsync(SessionId, ProcessingProfile.CpuInt8, true);

        Assert.Equal(PreparationStage.Failed, result.Stage);
        Assert.Contains("no longer matches", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.Requests);
    }

    // -- the placeholder path is untouched -----------------------------------------------------------

    [WorkerFact]
    public async Task ThePlaceholderBackendStillWorksExactlyAsBefore()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        TranscriptionCoordinator coordinator = Coordinator(Registry(Entry(server.Url, content, "stt.test")));

        TranscriptionRunResult result = await coordinator
            .Request(SessionId, new TranscriptionOptions { Backend = WorkerProtocol.MockBackend })
            .Completion;

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, coordinator.StateFor(SessionId).SelectedRevision);
        Assert.Contains("does not recognise speech", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstallationWithoutAManifestKeepsTheRecorderAndThePlaceholderWorking()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);

        TranscriptionCoordinator coordinator = new(
            _sessions, _transcripts, new WorkerSupervisor(WorkerTestEnvironment.Options()), _gate);
        _disposables.Add(coordinator);

        Assert.False(coordinator.SupportsProductionProfiles);

        PreparationResult result = await coordinator.PrepareAsync(SessionId, ProcessingProfile.CpuInt8);

        Assert.Equal(PreparationStage.Failed, result.Stage);
        Assert.Equal("preparation_unavailable", result.FailureCode);
    }

    // -- the surface ---------------------------------------------------------------------------------

    [Fact]
    public void TheWindowShowsProductionReadinessSeparatelyFromTranscription()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        TranscriptionCoordinator coordinator = Coordinator(Registry(Entry(server.Url, content, "stt.test")));

        TranscriptionViewModel viewModel = new(coordinator, new NoPrompt());
        _disposables.Add(viewModel);

        viewModel.UpdateHost(SessionId, sessionSettled: true, recordingActive: false, hostReady: true, shuttingDown: false);

        Assert.True(viewModel.SupportsProduction);
        Assert.True(viewModel.CanPrepare);
        Assert.True(viewModel.PrepareProductionCommand.CanExecute(null));

        // Still a placeholder build: the two statements are independent and both are shown.
        Assert.True(viewModel.IsPlaceholderBackend);
        Assert.False(viewModel.HasTranscript);
    }

    [Fact]
    public void PreparationIsUnavailableWhileRecordingOrShuttingDown()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        TranscriptionCoordinator coordinator = Coordinator(Registry(Entry(server.Url, content, "stt.test")));

        TranscriptionViewModel viewModel = new(coordinator, new NoPrompt());
        _disposables.Add(viewModel);

        viewModel.UpdateHost(SessionId, sessionSettled: false, recordingActive: true, hostReady: true, shuttingDown: false);
        Assert.False(viewModel.CanPrepare);

        viewModel.UpdateHost(SessionId, sessionSettled: true, recordingActive: false, hostReady: true, shuttingDown: true);
        Assert.False(viewModel.CanPrepare);
    }

    private sealed class SwitchableGate : ICaptureActivityGate
    {
        public bool IsCaptureActive { get; set; }
    }

    private sealed class NoPrompt : IExportDestinationPrompt
    {
        public ExportDestination? Ask(string suggestedFileName, TranscriptExportFormat format) => null;
    }
}
