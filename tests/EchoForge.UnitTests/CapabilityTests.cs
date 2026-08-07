using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Setup;

namespace EchoForge.UnitTests;

/// <summary>
/// What EchoForge can do with part of its dependencies installed.
///
/// <para>
/// The rule under test is that setup is <b>staged, never all-or-nothing</b>. Recording works with
/// nothing downloaded at all, and it has to: a meeting is the only thing here that cannot be
/// fetched again, and refusing to capture one because a seven-gigabyte summariser has not finished
/// would be the worst trade in the product.
/// </para>
/// </summary>
public sealed class CapabilityTests : IDisposable
{
    private readonly SetupFixture _fixture = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _fixture.Dispose();
    }

    private RuntimeRegistry Build(out ArtifactRegistry artifacts, out PythonRuntimeInstaller python)
    {
        artifacts = _fixture.Registry();
        _disposables.Add(artifacts);

        python = _fixture.PythonInstaller(artifacts);
        return new RuntimeRegistry(artifacts, python, _fixture.WorkerInstaller(artifacts, python));
    }

    private void GivenAFullManifest()
    {
        _fixture.AddPythonArchive();
        _fixture.AddWheel("example");
        _fixture.WriteRequirements();

        _fixture.Add("stt.test.model", "model.bin", [1, 2, 3, 4], "speech-model", ProcessingProfile.CpuInt8);
        _fixture.Add("summary.test.model", "model.gguf", [5, 6, 7, 8], "summary-model", ProcessingProfile.SummaryCpuQ4);
        _fixture.Add("summary.test.bakeoff", "bakeoff.gguf", [9, 10], "summary-model", ProcessingProfile.SummaryBakeoff);
    }

    [Fact]
    public void RecordingIsAvailableWithNothingInstalledAtAll()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out _, out _);

        SetupSnapshot snapshot = runtimes.Snapshot(ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4);

        Assert.True(snapshot.Capability(CapabilityLevel.Recording).Available);
        Assert.False(snapshot.CanTranscribe);
        Assert.False(snapshot.CanSummarize);
    }

    [Fact]
    public async Task TranscriptionAndSummarisationBecomeAvailableIndependently()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out ArtifactRegistry artifacts, out PythonRuntimeInstaller python);

        await python.EnsureAsync();
        await artifacts.EnsureAsync("stt.test.model");

        SetupSnapshot afterSpeech = runtimes.Snapshot(ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4);

        // The speech model is ready and the summary model is not, and the two say so separately.
        Assert.True(afterSpeech.Component(RuntimeComponentId.SpeechModel).IsReady);
        Assert.False(afterSpeech.Component(RuntimeComponentId.SummaryModel).IsReady);

        await artifacts.EnsureAsync("summary.test.model");

        SetupSnapshot afterSummary = runtimes.Snapshot(ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4);
        Assert.True(afterSummary.Component(RuntimeComponentId.SummaryModel).IsReady);

        // Recording never depended on either of them.
        Assert.True(afterSummary.Capability(CapabilityLevel.Recording).Available);
    }

    [Fact]
    public void ACapabilitySaysWhichComponentsAreHoldingItUp()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out _, out _);

        CapabilityState transcription = runtimes
            .Snapshot(ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4)
            .Capability(CapabilityLevel.Transcription);

        Assert.False(transcription.Available);
        Assert.Contains(RuntimeComponentId.PythonRuntime, transcription.Blocking);
        Assert.Contains(RuntimeComponentId.SpeechModel, transcription.Blocking);
        Assert.True(transcription.BytesOutstanding > 0);
    }

    [Fact]
    public void TheComparisonModelIsNeverSomethingMissing()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out _, out _);

        SetupSnapshot snapshot = runtimes.Snapshot(ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4);
        RuntimeComponentState benchmark = snapshot.Component(RuntimeComponentId.BenchmarkModel);

        // Absent, and reported as not needed rather than not installed. A red light beside an
        // eight-gigabyte bake-off candidate is an invitation to download it for no reason.
        Assert.Equal(RuntimeComponentStatus.NotNeeded, benchmark.Status);
        Assert.False(benchmark.NeedsAction);

        // And it never counts towards what the recommended setup still has to fetch.
        Assert.DoesNotContain(
            snapshot.Capabilities.Where(c => !c.IsOptional).SelectMany(c => c.Blocking),
            id => id == RuntimeComponentId.BenchmarkModel);
    }

    [Fact]
    public void TheOutstandingTotalExcludesAnythingOptional()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out _, out _);

        SetupSnapshot snapshot = runtimes.Snapshot(ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4);

        // The headline figure is what the recommended setup still needs, and the comparison
        // model is never part of that however many bytes it happens to be.
        long expected = snapshot.Capabilities
            .Where(c => !c.IsOptional && !c.Available)
            .Sum(c => c.BytesOutstanding);

        Assert.Equal(expected, snapshot.BytesOutstanding);
        Assert.True(snapshot.BytesOutstanding > 0);

        Assert.DoesNotContain(
            snapshot.Capabilities.Where(c => !c.IsOptional),
            c => c.Blocking.Contains(RuntimeComponentId.BenchmarkModel));
    }

    [Fact]
    public void ThePlaceholderProfileNeedsNoSpeechModel()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out _, out _);

        SetupSnapshot snapshot = runtimes.Snapshot(ProcessingProfile.Mock, ProcessingProfile.SummaryCpuQ4);

        Assert.Equal(
            RuntimeComponentStatus.NotNeeded,
            snapshot.Component(RuntimeComponentId.SpeechModel).Status);
    }

    [Fact]
    public void AProfileThisBuildDoesNotHaveIsIncompatibleRatherThanMissing()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out _, out _);

        // cuda-fp16 has no artifacts in this fixture's manifest, so it is not a profile this
        // build can offer at all. That is a different statement from "not downloaded yet".
        SetupSnapshot snapshot = runtimes.Snapshot("vulkan", ProcessingProfile.SummaryCpuQ4);

        Assert.Equal(
            RuntimeComponentStatus.Incompatible,
            snapshot.Component(RuntimeComponentId.SpeechModel).Status);
    }

    [Fact]
    public async Task InstallingOneComponentInstallsOnlyThatComponent()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out ArtifactRegistry artifacts, out _);

        await runtimes.InstallAsync(
            RuntimeComponentId.SpeechModel, ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4);

        Assert.Equal(ArtifactStatus.Installed, artifacts.Status(artifacts.Find("stt.test.model")!).Status);

        // Somebody who wanted speech recognition did not get a summary model, or eight gigabytes
        // of comparison model, on the way.
        Assert.NotEqual(ArtifactStatus.Installed, artifacts.Status(artifacts.Find("summary.test.model")!).Status);
        Assert.NotEqual(ArtifactStatus.Installed, artifacts.Status(artifacts.Find("summary.test.bakeoff")!).Status);
    }

    [Fact]
    public async Task RepairingOneComponentVerifiesRatherThanRefetching()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out ArtifactRegistry artifacts, out _);

        ArtifactEntry model = artifacts.Find("stt.test.model")!;
        await artifacts.EnsureAsync(model.ArtifactId);

        int requests = _fixture.Requests[model.Url];

        // Present and correct, with the proof missing — what an artifact installed by something
        // other than this application looks like.
        File.Delete(_fixture.InstalledPath(model) + ".verified.json");
        Assert.Equal(ArtifactStatus.Invalid, artifacts.Status(model).Status);

        RuntimeComponentState repaired = await runtimes.RepairAsync(
            RuntimeComponentId.SpeechModel, ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4);

        Assert.True(repaired.IsReady);
        Assert.Equal(requests, _fixture.Requests[model.Url]);
    }

    [Fact]
    public async Task RepairingOneComponentLeavesTheOthersAlone()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out ArtifactRegistry artifacts, out _);

        await artifacts.EnsureAsync("stt.test.model");
        await artifacts.EnsureAsync("summary.test.model");

        string summaryPath = _fixture.InstalledPath(artifacts.Find("summary.test.model")!);
        DateTime written = File.GetLastWriteTimeUtc(summaryPath);

        await runtimes.RepairAsync(
            RuntimeComponentId.SpeechModel, ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4);

        Assert.True(File.Exists(summaryPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(summaryPath));
    }

    [Fact]
    public async Task ADownloadThatFailsDoesNotTakeRecordingWithIt()
    {
        GivenAFullManifest();
        RuntimeRegistry runtimes = Build(out ArtifactRegistry artifacts, out _);

        _fixture.Blocked.Add(artifacts.Find("stt.test.model")!.Url);

        await runtimes.InstallAsync(
            RuntimeComponentId.SpeechModel, ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4);

        SetupSnapshot snapshot = runtimes.Snapshot(ProcessingProfile.CpuInt8, ProcessingProfile.SummaryCpuQ4);

        Assert.False(snapshot.CanTranscribe);
        Assert.True(snapshot.Capability(CapabilityLevel.Recording).Available);
    }

    [Fact]
    public void AManifestThatCannotBeTrustedComposesNothingThatCouldDownload()
    {
        // A manifest that fails validation permits nothing rather than permitting less: the
        // recorder keeps working and no download can happen at all.
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Layout.ManifestPath)!);
        File.WriteAllText(_fixture.Layout.ManifestPath, "this is not json");

        using SetupServices? services = SetupServices.TryOpen(out IReadOnlyList<string> problems, _fixture.Layout);

        Assert.Null(services);
        Assert.NotEmpty(problems);
    }
}
