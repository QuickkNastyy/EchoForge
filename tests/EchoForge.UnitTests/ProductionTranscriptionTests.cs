using System.Security.Cryptography;
using EchoForge.App;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Exports;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.UnitTests;

/// <summary>
/// The host side of production transcription: staging a verified model, refusing to run
/// without one, and handing the worker windows rather than chunks.
/// </summary>
public sealed class ProductionTranscriptionTests : IDisposable
{
    private const string SessionId = "01JPRODUCTION";

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _sessions;
    private readonly FileTranscriptionStore _transcripts;
    private readonly SwitchableGate _gate = new();
    private readonly List<IDisposable> _disposables = [];

    public ProductionTranscriptionTests()
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

    private static byte[] Payload(int length, byte seed) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)((i * 13 + seed) & 0xFF))];

    private static ArtifactEntry ModelFile(string id, string fileName, byte[] content) => new()
    {
        ArtifactId = id,
        Kind = "speech-model",
        Repository = "https://example.invalid/model",
        Url = "https://example.invalid/model/" + fileName,
        Revision = "0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf",
        FileName = fileName,
        SizeBytes = content.Length,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
        License = "MIT",
        LicenseFile = "third_party/licenses/onnxruntime-1.28.0-LICENSE.txt",
        RuntimeVersion = "test",
        Profiles = ["cpu-int8"],
        VerifiedUtc = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero),
    };

    /// <summary>A registry holding a model whose files are already installed and verified.</summary>
    private ArtifactRegistry RegistryWithInstalledModel(out ArtifactEntry[] entries)
    {
        byte[] weights = Payload(2048, 1);
        byte[] config = Payload(64, 2);
        byte[] tokenizer = Payload(128, 3);

        entries =
        [
            ModelFile("stt.test.model", "model.bin", weights),
            ModelFile("stt.test.config", "config.json", config),
            ModelFile("stt.test.tokenizer", "tokenizer.json", tokenizer),
        ];

        ArtifactRegistry registry = new(
            new ArtifactManifest { Artifacts = entries }, Path.Combine(_temp.Path, "models"));
        _disposables.Add(registry);

        byte[][] payloads = [weights, config, tokenizer];
        for (int i = 0; i < entries.Length; i++)
        {
            Directory.CreateDirectory(registry.InstallDirectory(entries[i]));
            File.WriteAllBytes(registry.InstallPath(entries[i]), payloads[i]);
        }

        return registry;
    }

    private static async Task VerifyAllAsync(ArtifactRegistry registry, IEnumerable<ArtifactEntry> entries)
    {
        foreach (ArtifactEntry entry in entries)
        {
            Assert.Equal(ArtifactStatus.Installed, (await registry.VerifyInstalledAsync(entry.ArtifactId)).Status);
        }
    }

    private TranscriptionCoordinator Coordinator(ArtifactRegistry registry) =>
        Track(new TranscriptionCoordinator(
            _sessions,
            _transcripts,
            new WorkerSupervisor(WorkerTestEnvironment.Options()),
            _gate,
            preparation: new ProcessingPreparation(_sessions, registry, new DerivativeBuilder(_sessions))));

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    // -- staging the model ---------------------------------------------------------------------

    [Fact]
    public async Task VerifiedModelFilesAreAssembledIntoOneDirectoryTheRecogniserCanLoad()
    {
        ArtifactRegistry registry = RegistryWithInstalledModel(out ArtifactEntry[] entries);
        await VerifyAllAsync(registry, entries);

        string? staged = registry.TryStageModelDirectory(ProcessingProfile.CpuInt8);

        Assert.NotNull(staged);
        foreach (ArtifactEntry entry in entries)
        {
            string path = Path.Combine(staged!, entry.FileName);
            Assert.True(File.Exists(path), entry.FileName);
            Assert.Equal(entry.SizeBytes, new FileInfo(path).Length);
        }
    }

    [Fact]
    public async Task StagingIsSkippedWhenAlreadyDoneRatherThanRecopyingGigabytes()
    {
        ArtifactRegistry registry = RegistryWithInstalledModel(out ArtifactEntry[] entries);
        await VerifyAllAsync(registry, entries);

        string staged = registry.TryStageModelDirectory(ProcessingProfile.CpuInt8)!;
        string weights = Path.Combine(staged, "model.bin");
        DateTime written = File.GetLastWriteTimeUtc(weights);

        await Task.Delay(50);
        registry.TryStageModelDirectory(ProcessingProfile.CpuInt8);

        Assert.Equal(written, File.GetLastWriteTimeUtc(weights));
    }

    [Fact]
    public void AModelThatIsNotFullyInstalledIsNeverStaged()
    {
        ArtifactRegistry registry = RegistryWithInstalledModel(out ArtifactEntry[] entries);

        // Present but never verified, which is not the same as installed.
        Assert.Equal(ArtifactStatus.Invalid, registry.Status(entries[0]).Status);

        // A half-assembled directory is worse than none: the library would load it and fail
        // in a way nobody can act on.
        Assert.Null(registry.TryStageModelDirectory(ProcessingProfile.CpuInt8));
    }

    [Fact]
    public async Task AMissingFileStopsStagingEvenWhenTheOthersAreVerified()
    {
        ArtifactRegistry registry = RegistryWithInstalledModel(out ArtifactEntry[] entries);
        await VerifyAllAsync(registry, entries);

        File.Delete(registry.InstallPath(entries[2]));

        Assert.Null(registry.TryStageModelDirectory(ProcessingProfile.CpuInt8));
    }

    // -- refusing to run without one -------------------------------------------------------------

    [Fact]
    public async Task AProductionRunIsRefusedWhenTheModelIsNotInstalled()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        RegistryWithInstalledModel(out ArtifactEntry[] entries);

        // A registry that knows the model but has none of it on disk.
        ArtifactRegistry empty = Track(new ArtifactRegistry(
            new ArtifactManifest { Artifacts = entries }, Path.Combine(_temp.Path, "elsewhere")));

        TranscriptionRunResult result = await Coordinator(empty)
            .Request(SessionId, new TranscriptionOptions { Backend = "faster-whisper" })
            .Completion;

        Assert.Equal(ProcessingStageState.Failed, result.State);
        Assert.Equal("artifacts_missing", result.FailureCode);

        // Transcribing is not the moment to start a download nobody asked for, so the refusal
        // says what is needed rather than quietly fetching it.
        Assert.Contains("downloaded before it can run", result.Message, StringComparison.Ordinal);
        Assert.Empty(_transcripts.Read(SessionId).Revisions);
    }

    [WorkerFact]
    public async Task ThePlaceholderStillRunsWithNoProductionArtifactsAtAll()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        RegistryWithInstalledModel(out ArtifactEntry[] entries);

        ArtifactRegistry empty = Track(new ArtifactRegistry(
            new ArtifactManifest { Artifacts = entries }, Path.Combine(_temp.Path, "elsewhere")));

        TranscriptionRunResult result = await Coordinator(empty)
            .Request(SessionId, new TranscriptionOptions { Backend = WorkerProtocol.MockBackend })
            .Completion;

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, _transcripts.Read(SessionId).SelectedRevision);
    }

    // -- the surface ------------------------------------------------------------------------------

    [Fact]
    public void TheBackendSelectorAlwaysSaysWhetherItRecognisesSpeech()
    {
        ArtifactRegistry registry = RegistryWithInstalledModel(out _);
        TranscriptionViewModel viewModel = Track(new TranscriptionViewModel(Coordinator(registry), new NoPrompt()));

        Assert.Equal(WorkerProtocol.MockBackend, viewModel.SelectedBackend.Id);
        Assert.False(viewModel.SelectedBackend.RecognizesSpeech);
        Assert.False(viewModel.IsProductionSelected);

        foreach (BackendOption option in viewModel.Backends)
        {
            Assert.Contains(
                option.RecognizesSpeech ? "real speech recognition" : "no speech recognition",
                option.Label,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The Ready screen used to lead with "this build understands nothing" on every machine,
    /// because the picker always started on the placeholder. It starts on real recognition
    /// wherever real recognition is actually installed, and only there.
    /// </summary>
    [Fact]
    public async Task RealSpeechRecognitionIsTheDefaultOnceItsModelsAreUsable()
    {
        ArtifactRegistry registry = RegistryWithInstalledModel(out ArtifactEntry[] entries);

        // Bytes on disk that have never been verified are not usable yet, and defaulting to a
        // recogniser whose first click would fail is worse than defaulting to the placeholder.
        TranscriptionViewModel unverified = Track(new TranscriptionViewModel(Coordinator(registry), new NoPrompt()));

        Assert.False(unverified.ProductionInstalled);
        Assert.Equal(WorkerProtocol.MockBackend, unverified.SelectedBackend.Id);
        Assert.True(unverified.IsPlaceholderBackend);

        await VerifyAllAsync(registry, entries);

        TranscriptionViewModel verified = Track(new TranscriptionViewModel(Coordinator(registry), new NoPrompt()));

        Assert.True(verified.ProductionInstalled);
        Assert.True(verified.SelectedBackend.RecognizesSpeech);
        Assert.True(verified.IsProductionSelected);

        // And with nothing transcribed yet, the screen says nothing about a placeholder, because
        // the placeholder is not what the next run would use.
        Assert.False(verified.IsPlaceholderBackend);

        // Choosing the placeholder deliberately still warns, which is the case the notice is for.
        verified.SelectedBackend = verified.Backends.First(b => !b.RecognizesSpeech);
        Assert.True(verified.IsPlaceholderBackend);
    }

    [Fact]
    public void ProfileAndLanguageControlsOnlyApplyToARealRecogniser()
    {
        ArtifactRegistry registry = RegistryWithInstalledModel(out _);
        TranscriptionViewModel viewModel = Track(new TranscriptionViewModel(Coordinator(registry), new NoPrompt()));

        Assert.False(viewModel.IsProductionSelected);

        viewModel.SelectedBackend = viewModel.Backends.First(b => b.RecognizesSpeech);

        Assert.True(viewModel.IsProductionSelected);
        Assert.Equal(ProcessingProfile.CpuInt8, viewModel.SelectedComputeProfile.Id);

        // Automatic is the default: a wrongly forced language is worse than a detection that
        // occasionally hesitates.
        Assert.Null(viewModel.SelectedLanguage.Code);
        Assert.Contains(viewModel.ComputeProfiles, p => p.Id == ProcessingProfile.CudaFp16);
    }

    [Fact]
    public void TheGlossaryIsSplitIntoTermsRatherThanSentAsOneString()
    {
        ArtifactRegistry registry = RegistryWithInstalledModel(out _);
        TranscriptionViewModel viewModel = Track(new TranscriptionViewModel(Coordinator(registry), new NoPrompt()));

        viewModel.Glossary = " EchoForge , WASAPI ,, CTranslate2 ";

        string[] terms =
            [.. viewModel.Glossary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        Assert.Equal(["EchoForge", "WASAPI", "CTranslate2"], terms);
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
