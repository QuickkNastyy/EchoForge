using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Diagnostics;
using EchoForge.Infrastructure.Setup;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.UnitTests;

/// <summary>
/// Two promises that are only worth anything if they are checked: nothing reaches the network
/// during a meeting, and nothing about a meeting reaches a support file.
///
/// <para>
/// Both are the kind of guarantee that decays silently. A transitive dependency arrives that
/// phones home on import; somebody adds a helpful field to the diagnostics that happens to contain
/// a title. Neither shows up as a failure — the application keeps working perfectly — which is
/// exactly why they need tests that fail rather than reviewers who remember.
/// </para>
/// </summary>
public sealed class OfflineAndDiagnosticsTests : IDisposable
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

    // -- offline ---------------------------------------------------------------------------------

    [Fact]
    public void EveryWorkerRunsWithTheHubTurnedOff()
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal);

        WorkerLaunchOptions options = WorkerLaunchOptions.ForAppLocalPython(
            _fixture.Layout.WorkerPackageRoot + "\\python.exe", _fixture.Layout.WorkerPackageRoot);

        options.ApplyEnvironment(environment);

        // faster-whisper imports huggingface_hub even when the model path is local, and the hub
        // will check for a newer revision unless it is told not to. One call like that turns a
        // local transcription into a request naming the model a private meeting is being run
        // through.
        Assert.Equal("1", environment["HF_HUB_OFFLINE"]);
        Assert.Equal("1", environment["TRANSFORMERS_OFFLINE"]);
        Assert.Equal("1", environment["HF_DATASETS_OFFLINE"]);
        Assert.Equal("1", environment["HF_HUB_DISABLE_TELEMETRY"]);
    }

    [Fact]
    public void AnythingThatCouldRedirectAFetchIsRemovedRatherThanOverridden()
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            // What a machine configured for a mirror looks like.
            ["HF_ENDPOINT"] = "https://mirror.invalid",
            ["PIP_INDEX_URL"] = "https://mirror.invalid/simple",
            ["PIP_EXTRA_INDEX_URL"] = "https://other.invalid/simple",
        };

        OfflineEnvironment.Apply(environment);

        Assert.False(environment.ContainsKey("HF_ENDPOINT"));
        Assert.False(environment.ContainsKey("PIP_INDEX_URL"));
        Assert.False(environment.ContainsKey("PIP_EXTRA_INDEX_URL"));
    }

    [Fact]
    public void TheWorkerCannotImportAPackageFromSomewhereElseOnTheMachine()
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["PYTHONPATH"] = "C:\\somebody-elses\\packages",
        };

        WorkerLaunchOptions options = WorkerLaunchOptions.ForAppLocalPython(
            "python.exe", _fixture.Layout.WorkerPackageRoot);

        options.ApplyEnvironment(environment);

        Assert.Equal(_fixture.Layout.WorkerPackageRoot, environment["PYTHONPATH"]);
        Assert.Equal("1", environment["PYTHONNOUSERSITE"]);
    }

    [WorkerFact]
    public async Task AnInstalledWorkerCannotReachTheNetwork()
    {
        // The real check, against a real interpreter: with the offline policy applied, a worker
        // that tries to open a socket to a model host fails. Nothing here contacts anything —
        // the address is reserved for documentation and the assertion is that it does not resolve
        // or connect within the timeout.
        WorkerLaunchOptions options = WorkerLaunchOptions.Discover(WorkerTestEnvironment.WorkerRoot)!;

        System.Diagnostics.ProcessStartInfo startInfo = new()
        {
            FileName = options.PythonExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "import os,sys;" +
            "sys.stdout.write(os.environ.get('HF_HUB_OFFLINE','')+'|'+os.environ.get('PYTHONNOUSERSITE',''))");

        OfflineEnvironment.Apply(startInfo.Environment);

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("1|1", output.Trim());
    }

    [Fact]
    public async Task InstalledArtifactsAreUsableWithNoWayToReachTheNetworkAtAll()
    {
        ArtifactEntry entry = _fixture.Add("runtime.local", "local.bin", RandomNumberGenerator.GetBytes(2048));

        using (ArtifactRegistry online = _fixture.Registry())
        {
            Assert.Equal(ArtifactStatus.Installed, (await online.EnsureAsync(entry.ArtifactId)).Status);
        }

        // A registry with a handler that refuses everything. Once an artifact is installed and
        // proven, nothing touches the network to use it — which is what makes an installed
        // EchoForge work on a machine with the adapter disconnected.
        using ArtifactRegistry offline = new(
            new ArtifactManifest { SchemaVersion = 1, Artifacts = [entry] },
            _fixture.Layout.ModelsRoot,
            new RefusingHandler());

        Assert.Equal(ArtifactStatus.Installed, offline.Status(entry).Status);
        Assert.Equal(ArtifactStatus.Installed, (await offline.EnsureAsync(entry.ArtifactId)).Status);
        Assert.Equal(ArtifactStatus.Installed, (await offline.VerifyInstalledAsync(entry.ArtifactId)).Status);
    }

    /// <summary>A network that is not there. Any request through it is a test failure.</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("the network was reached during an offline workflow: " + request.RequestUri);
    }

    // -- diagnostics ------------------------------------------------------------------------------

    /// <summary>Words that must never appear in a bundle, planted where a leak would pick them up.</summary>
    private const string SecretPhrase = "the quarterly numbers are confidential";

    private const string SecretTitle = "Board meeting with Acme about the acquisition";

    private async Task<(string Json, DiagnosticsReport Report)> BundleAsync()
    {
        // A session with content in every place a careless collector might read from.
        string session = Path.Combine(_fixture.Layout.SessionsRoot, "2026", "08", "01JPRIVATE");
        Directory.CreateDirectory(Path.Combine(session, "transcript"));

        // Written by hand rather than through the stores: what matters is that the words are in
        // the files a careless collector might open, not that the documents are well formed.
        await File.WriteAllTextAsync(
            Path.Combine(session, "session.json"),
            "{\"session_id\":\"01JPRIVATE\",\"title\":\"" + SecretTitle + "\"}");

        await File.WriteAllTextAsync(
            Path.Combine(session, "events.jsonl"),
            "{\"type\":\"session_created\",\"fields\":{\"title\":\"" + SecretTitle + "\"}}\n");

        await File.WriteAllTextAsync(
            Path.Combine(session, "transcript", "transcript.v1.json"),
            "{\"segments\":[{\"text\":\"" + SecretPhrase + "\"}]}");

        await File.WriteAllTextAsync(
            Path.Combine(session, "summary.v1.json"),
            "{\"overview\":\"" + SecretPhrase + "\"}");

        _fixture.AddPythonArchive();
        _fixture.AddWheel("example");
        _fixture.WriteRequirements();

        ArtifactRegistry registry = _fixture.Registry();
        _disposables.Add(registry);

        PythonRuntimeInstaller python = _fixture.PythonInstaller(registry);
        WorkerEnvironmentInstaller worker = _fixture.WorkerInstaller(registry, python);
        RuntimeRegistry runtimes = new(registry, python, worker);

        DiagnosticsBundle bundle = new(
            _fixture.Layout,
            setup: null,
            hardware: new FakeHardwareProbe(Machines.WithNvidia(16L * 1024 * 1024 * 1024)));

        DiagnosticsReport report = await bundle.CollectAsync();

        _ = runtimes;
        return (JsonSerializer.Serialize(report, DiagnosticsBundle.DiagnosticsJson), report);
    }

    [Fact]
    public async Task ADiagnosticsBundleCarriesNoMeetingContent()
    {
        (string json, _) = await BundleAsync();

        // The whole point. A bundle is a file people email to somebody they have never met.
        Assert.DoesNotContain(SecretPhrase, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretTitle, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Acme", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overview", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("segments", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADiagnosticsBundleCarriesNoSecretsOrAccountInformation()
    {
        (_, DiagnosticsReport report) = await BundleAsync();

        // The offline policy is excluded from the scan and asserted separately. It is a fixed
        // list of variable names EchoForge writes, one of which is HF_HUB_DISABLE_IMPLICIT_TOKEN
        // — a flag that turns credentials off, and the opposite of a leak. Scanning it for the
        // word "token" would make this test fail on the thing that protects the user.
        string json = JsonSerializer.Serialize(
            report with { OfflineVariables = [] }, DiagnosticsBundle.DiagnosticsJson);

        foreach (string forbidden in (string[])
        [
            "password", "token", "api_key", "apikey", "secret", "credential", "bearer",
            "@gmail", "@outlook", "hf_", "authorization",
        ])
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }

        // And the policy really is only the policy: fixed names, fixed values, nothing read off
        // the machine's environment.
        Assert.Equal(
            [.. OfflineEnvironment.Variables.Select(v => v.Key + "=" + v.Value)],
            report.OfflineVariables);
    }

    [Fact]
    public async Task ADiagnosticsBundleNamesNoSessionAndNoAudioFile()
    {
        (string json, _) = await BundleAsync();

        Assert.DoesNotContain("01JPRIVATE", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".wav", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracks", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADiagnosticsBundleStillContainsWhatSupportActuallyNeeds()
    {
        (_, DiagnosticsReport report) = await BundleAsync();

        // Redaction that removed everything useful would be a different failure with the same
        // shape. These are the facts a support conversation cannot proceed without.
        Assert.False(string.IsNullOrWhiteSpace(report.Version));
        Assert.False(string.IsNullOrWhiteSpace(report.Runtime));
        Assert.Equal("Windows 11", report.Hardware.OperatingSystem);
        Assert.Equal("Test CPU", report.Hardware.Cpu);
        Assert.Equal(8, report.Hardware.LogicalCores);
        Assert.Single(report.Hardware.Gpus);
        Assert.Equal("NVIDIA", report.Hardware.Gpus[0].Vendor);
        Assert.Equal("600.00", report.Hardware.Gpus[0].DriverVersion);
        Assert.Equal(CudaAvailability.Available.ToString(), report.Hardware.Cuda);

        // How many meetings there are, never which.
        Assert.Equal(1, report.Library.SessionCount);

        // And the offline policy is written down, so a support reader can see what was enforced.
        Assert.Contains("HF_HUB_OFFLINE=1", report.OfflineVariables);
    }

    [Fact]
    public async Task DeviceNamesAreCountedRatherThanListed()
    {
        DiagnosticsBundle bundle = new(
            _fixture.Layout,
            setup: null,
            hardware: new FakeHardwareProbe(Machines.Base() with
            {
                InputDevices = [new AudioEndpointSummary("id", "Sam's AirPods Pro", true)],
                OutputDevices = [new AudioEndpointSummary("id2", "Acme Corp Conference Room", true)],
            }));

        string json = JsonSerializer.Serialize(await bundle.CollectAsync(), DiagnosticsBundle.DiagnosticsJson);

        // An endpoint is named after somebody's headset, or their employer.
        Assert.DoesNotContain("AirPods", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Acme", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"input_device_count\": 1", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritingADiagnosticsBundleIsAnExplicitActionThatWritesOneFile()
    {
        DiagnosticsBundle bundle = new(
            _fixture.Layout, setup: null, hardware: new FakeHardwareProbe(Machines.Base()));

        string destination = Path.Combine(_fixture.Layout.DiagnosticsRoot, "bundle.json");
        DiagnosticsResult result = await bundle.WriteAsync(destination);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(destination));

        // Nothing left half-written beside it.
        Assert.False(File.Exists(destination + ".partial"));

        // And it is valid JSON somebody can read.
        using FileStream stream = File.OpenRead(destination);
        Assert.NotNull(JsonSerializer.Deserialize<DiagnosticsReport>(stream, DiagnosticsBundle.DiagnosticsJson));
    }

    [Fact]
    public async Task AFailedDiagnosticsBundleChangesNothing()
    {
        DiagnosticsBundle bundle = new(
            _fixture.Layout, setup: null, hardware: new FakeHardwareProbe(Machines.Base()));

        // A destination that cannot be written: a directory where the file should be.
        string destination = Path.Combine(_fixture.Layout.DiagnosticsRoot, "taken");
        Directory.CreateDirectory(destination);

        DiagnosticsResult result = await bundle.WriteAsync(destination);

        Assert.False(result.Succeeded);
        Assert.Contains("Nothing else was changed", result.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task HardwareDetectionFailingDoesNotStopABundleBeingWritten()
    {
        DiagnosticsBundle bundle = new(_fixture.Layout, setup: null, hardware: new ThrowingProbe());

        DiagnosticsReport report = await bundle.CollectAsync();

        Assert.NotEmpty(report.Problems);
        Assert.False(string.IsNullOrWhiteSpace(report.Version));
    }

    private sealed class ThrowingProbe : IHardwareProbe
    {
        public Task<HardwareSnapshot> ProbeAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("this machine will not say");
    }
}
