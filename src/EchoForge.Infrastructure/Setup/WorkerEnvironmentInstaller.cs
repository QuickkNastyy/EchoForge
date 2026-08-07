using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Infrastructure.Artifacts;

namespace EchoForge.Infrastructure.Setup;

/// <summary>The installed worker environment, and what it was built from.</summary>
public sealed record WorkerEnvironment(
    string PythonExecutable,
    string Root,
    string PythonVersion,
    string PackageSummary);

/// <summary>How building the worker environment ended.</summary>
public sealed record WorkerEnvironmentResult(WorkerEnvironment? Environment, string? Code, string Message)
{
    public bool Succeeded => Environment is not null;

    public static WorkerEnvironmentResult Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// Builds the Python environment the workers run in, offline, from verified bytes.
///
/// <para>
/// <b>The index is off, and that is the whole design.</b> Every wheel is a manifest entry whose
/// length and digest have already been checked, and they are copied into one flat directory so pip
/// has somewhere local to look. With <c>--no-index</c> there is nothing left that could reach a
/// package server, which means the installation is reproducible, works on a machine that has never
/// had a network connection, and cannot install something the manifest never vouched for. An
/// installer that could still fetch would make the pinning decorative.
/// </para>
///
/// <para>
/// <b>Activation is a rename.</b> The environment is built at <c>worker-env.building</c> and moved
/// into place only after every package installed and the imports were checked by actually running
/// them. A process killed halfway through leaves a <c>.building</c> directory the next attempt
/// discards; it never leaves a half-populated environment that looks complete because
/// <c>python.exe</c> is present.
/// </para>
///
/// <para>
/// The stamp records the interpreter revision and the digest of every wheel that went in, so a
/// re-pinned closure invalidates the environment rather than silently running against whatever was
/// installed last time. Repair rebuilds this component and touches nothing else — not the models,
/// and never anything under the sessions directory.
/// </para>
/// </summary>
public sealed class WorkerEnvironmentInstaller
{
    private const string StampFileName = "echoforge-environment.json";

    /// <summary>How long the whole installation may take before it is abandoned.</summary>
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);

    private readonly ArtifactRegistry _registry;
    private readonly PythonRuntimeInstaller _python;
    private readonly AppLayout _layout;
    private readonly Lock _sync = new();

    public WorkerEnvironmentInstaller(
        ArtifactRegistry registry,
        PythonRuntimeInstaller python,
        AppLayout? layout = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _python = python ?? throw new ArgumentNullException(nameof(python));
        _layout = layout ?? AppLayout.Current;
    }

    /// <summary>Every wheel the environment is built from: the manifest's runtime closure.</summary>
    public IReadOnlyList<ArtifactEntry> Wheels =>
    [
        .. _registry.Artifacts
            .Where(a => a.ArtifactId.StartsWith("runtime.", StringComparison.Ordinal))
            .Where(a => a.FileName.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.ArtifactId, StringComparer.Ordinal)
    ];

    public string Root => _layout.WorkerEnvironmentRoot;

    public static string ExecutableIn(string root) => Path.Combine(root, "Scripts", "python.exe");

    public long BytesRequired => Wheels.Sum(w => w.SizeBytes);

    /// <summary>
    /// The installed environment, or null.
    ///
    /// <para>
    /// Resolved from the stamp rather than from the presence of a <c>python.exe</c>: an
    /// environment whose packages were installed from a different closure is not this environment,
    /// however runnable it looks.
    /// </para>
    /// </summary>
    public WorkerEnvironment? TryResolve()
    {
        string executable = ExecutableIn(Root);
        if (!File.Exists(executable))
        {
            return null;
        }

        EnvironmentStamp? stamp = ReadStamp(Root);
        if (stamp is null || !stamp.Packages.SequenceEqual(WheelDigests(), StringComparer.Ordinal))
        {
            return null;
        }

        if (_python.TryResolve() is { } python &&
            !string.Equals(stamp.PythonRevision, python.Revision, StringComparison.Ordinal))
        {
            return null;
        }

        return new WorkerEnvironment(executable, Root, stamp.PythonVersion, stamp.PackageSummary);
    }

    /// <summary>What state the environment is in, without running anything.</summary>
    public RuntimeComponentState Status()
    {
        long required = BytesRequired;

        if (TryResolve() is { } environment)
        {
            return RuntimeComponentState.Ready(
                RuntimeComponentId.WorkerEnvironment, environment.PackageSummary, required);
        }

        if (_python.TryResolve() is null)
        {
            return new RuntimeComponentState(
                RuntimeComponentId.WorkerEnvironment,
                RuntimeComponentStatus.NotInstalled,
                "EchoForge's own copy of Python has to be installed first.",
                0,
                required);
        }

        IReadOnlyList<ArtifactState> states = [.. Wheels.Select(_registry.Status)];
        long installed = states.Sum(s => s.IsUsable ? s.Entry.SizeBytes : s.BytesOnDisk);

        if (states.Any(s => s.Status == ArtifactStatus.Invalid))
        {
            return new RuntimeComponentState(
                RuntimeComponentId.WorkerEnvironment,
                RuntimeComponentStatus.Corrupt,
                "One of the downloaded packages does not match what was pinned.",
                installed,
                required);
        }

        if (!states.All(s => s.IsUsable))
        {
            return new RuntimeComponentState(
                RuntimeComponentId.WorkerEnvironment,
                installed > 0 ? RuntimeComponentStatus.Downloading : RuntimeComponentStatus.NotInstalled,
                installed > 0
                    ? "The speech-recognition packages are still downloading."
                    : "The speech-recognition packages have not been downloaded yet.",
                installed,
                required);
        }

        // Everything downloaded, nothing installed: either it was never built, or the build was
        // interrupted, or the closure moved underneath it. All of them are repaired the same way.
        return new RuntimeComponentState(
            RuntimeComponentId.WorkerEnvironment,
            File.Exists(ExecutableIn(Root)) ? RuntimeComponentStatus.Corrupt : RuntimeComponentStatus.Installing,
            File.Exists(ExecutableIn(Root))
                ? "The worker environment does not match the packages EchoForge expects."
                : "The packages are downloaded and the worker environment still has to be built.",
            installed,
            required);
    }

    /// <summary>Downloads whatever is missing, then builds the environment. Safe when already built.</summary>
    public async Task<WorkerEnvironmentResult> EnsureAsync(
        IProgress<ArtifactProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (TryResolve() is { } existing)
        {
            return new WorkerEnvironmentResult(existing, null, "The worker environment is ready.");
        }

        AppLocalPython? python = await _python.EnsureAsync(progress, cancellationToken).ConfigureAwait(false);
        if (python is null)
        {
            return WorkerEnvironmentResult.Fail(
                "python_missing", "EchoForge's own copy of Python could not be installed.");
        }

        foreach (ArtifactEntry wheel in Wheels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ArtifactState state = await _registry
                .EnsureAsync(wheel.ArtifactId, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!state.IsUsable)
            {
                return WorkerEnvironmentResult.Fail(
                    "packages_missing",
                    "A speech-recognition package could not be installed: " + (state.Detail ?? state.Status.ToString()));
            }
        }

        return await BuildAsync(python, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds this component from scratch.
    ///
    /// <para>
    /// Every wheel is re-hashed first, because "the environment is broken" and "one of the wheels
    /// it was built from is corrupt" look identical from the outside and have different repairs.
    /// </para>
    /// </summary>
    public async Task<WorkerEnvironmentResult> RepairAsync(
        IProgress<ArtifactProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        foreach (ArtifactEntry wheel in Wheels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _registry.VerifyInstalledAsync(wheel.ArtifactId, cancellationToken).ConfigureAwait(false);
        }

        TryDeleteDirectory(Root);
        TryDeleteDirectory(_layout.WheelhouseRoot);

        return await EnsureAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    // -- building ----------------------------------------------------------------------------------

    private async Task<WorkerEnvironmentResult> BuildAsync(AppLocalPython python, CancellationToken cancellationToken)
    {
        string building = Root + ".building";

        using CancellationTokenSource timeout = new(InstallTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            lock (_sync)
            {
                TryDeleteDirectory(building);
            }

            string wheelhouse = StageWheelhouse();
            string requirements = RequirementsPath();

            if (!File.Exists(requirements))
            {
                return WorkerEnvironmentResult.Fail(
                    "requirements_missing", "The package list EchoForge ships with is missing from this installation.");
            }

            // A virtual environment rather than installing into the shipped interpreter, so the
            // pinned runtime stays exactly as it was published and can be reused after a repair.
            ProcessResult venv = await RunAsync(
                python.ExecutablePath, ["-m", "venv", building], linked.Token).ConfigureAwait(false);

            if (venv.ExitCode != 0)
            {
                TryDeleteDirectory(building);
                return WorkerEnvironmentResult.Fail("venv_failed", "The worker environment could not be created.");
            }

            string environmentPython = ExecutableIn(building);

            ProcessResult install = await RunAsync(
                environmentPython,
                [
                    "-m", "pip", "install",
                    "--disable-pip-version-check",
                    "--no-input",
                    // Nothing may be fetched. Everything needed is verified and local.
                    "--no-index",
                    "--find-links", wheelhouse,
                    "--requirement", requirements,
                ],
                linked.Token).ConfigureAwait(false);

            if (install.ExitCode != 0)
            {
                TryDeleteDirectory(building);
                return WorkerEnvironmentResult.Fail(
                    "install_failed", "The speech-recognition packages could not be installed.");
            }

            // Imported rather than assumed. A wheel set that installs and does not import is a
            // broken environment that would fail on the first meeting instead of here.
            ProcessResult probe = await RunAsync(
                environmentPython, ["-c", ProbeScript], linked.Token).ConfigureAwait(false);

            if (probe.ExitCode != 0)
            {
                TryDeleteDirectory(building);
                return WorkerEnvironmentResult.Fail(
                    "imports_failed", "The installed packages did not load on this machine.");
            }

            string summary = probe.StandardOutput.Trim().Replace("\r\n", " · ", StringComparison.Ordinal)
                .Replace("\n", " · ", StringComparison.Ordinal);

            WriteStamp(building, python, summary);

            lock (_sync)
            {
                TryDeleteDirectory(Root);
                Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
                Directory.Move(building, Root);
            }

            WorkerEnvironment? resolved = TryResolve();
            return resolved is null
                ? WorkerEnvironmentResult.Fail("activate_failed", "The worker environment could not be activated.")
                : new WorkerEnvironmentResult(resolved, null, "The worker environment is ready.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryDeleteDirectory(building);
            return WorkerEnvironmentResult.Fail("timeout", "Building the worker environment took too long.");
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(building);
            return WorkerEnvironmentResult.Fail("cancelled", "Building the worker environment was cancelled.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The interpreter would not start. An incomplete unpack, a file the antivirus took,
            // or an architecture mismatch all land here, and all of them are repaired the same
            // way. It must be a result rather than an exception: this runs from a setup screen.
            TryDeleteDirectory(building);
            return WorkerEnvironmentResult.Fail(
                "interpreter_unusable",
                "EchoForge's own copy of Python would not start. Repairing it will install it again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(building);
            return WorkerEnvironmentResult.Fail(
                "write_failed", $"The worker environment could not be written ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Copies the verified wheels into one flat directory.
    ///
    /// <para>
    /// <c>--find-links</c> wants one place to look, and the artifact store is laid out by artifact
    /// and revision so each file can be verified independently. This is the bridge, and it copies
    /// only bytes the registry has already proven.
    /// </para>
    /// </summary>
    private string StageWheelhouse()
    {
        string wheelhouse = _layout.WheelhouseRoot;
        Directory.CreateDirectory(wheelhouse);

        foreach (ArtifactEntry wheel in Wheels)
        {
            string source = _registry.InstallPath(wheel);
            string destination = Path.Combine(wheelhouse, wheel.FileName);

            if (File.Exists(destination) && new FileInfo(destination).Length == wheel.SizeBytes)
            {
                continue;
            }

            File.Copy(source, destination, overwrite: true);
        }

        return wheelhouse;
    }

    /// <summary>The package list, shipped beside the worker it describes.</summary>
    private string RequirementsPath() =>
        Path.Combine(_layout.WorkerPackageRoot, "requirements-production.txt");

    private const string ProbeScript =
        "import sys, faster_whisper, ctranslate2, av, onnxruntime; " +
        "print('python ' + '.'.join(str(p) for p in sys.version_info[:3])); " +
        "print('faster-whisper ' + faster_whisper.__version__); " +
        "print('ctranslate2 ' + ctranslate2.__version__); " +
        "print('onnxruntime ' + onnxruntime.__version__); " +
        "print('cuda-devices ' + str(ctranslate2.get_cuda_device_count()))";

    private IReadOnlyList<string> WheelDigests() =>
    [
        .. Wheels.Select(w => string.Create(CultureInfo.InvariantCulture, $"{w.ArtifactId}:{w.Sha256}"))
    ];

    private static string StampPath(string root) => Path.Combine(root, StampFileName);

    private static EnvironmentStamp? ReadStamp(string root)
    {
        try
        {
            if (!File.Exists(StampPath(root)))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(StampPath(root));
            return JsonSerializer.Deserialize(stream, EnvironmentStampContext.Default.EnvironmentStamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteStamp(string root, AppLocalPython python, string summary)
    {
        EnvironmentStamp stamp = new()
        {
            PythonRevision = python.Revision,
            PythonVersion = python.Version,
            PackageSummary = summary,
            Packages = [.. WheelDigests()],
            InstalledUtc = DateTimeOffset.UtcNow,
        };

        using FileStream stream = File.Create(StampPath(root));
        JsonSerializer.Serialize(stream, stamp, EnvironmentStampContext.Default.EnvironmentStamp);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An environment with no stamp reads as not installed, which is the truth.
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>
    /// Runs one of the installation steps and collects what it said.
    ///
    /// <para>
    /// The environment is built rather than inherited, and it carries the offline flags: a pip
    /// that inherited a proxy or an index URL from the machine could still reach a package server
    /// despite <c>--no-index</c> being passed.
    /// </para>
    /// </summary>
    private static async Task<ProcessResult> RunAsync(
        string executable, string[] arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        OfflineEnvironment.Apply(startInfo.Environment);
        startInfo.Environment["PIP_NO_INPUT"] = "1";
        startInfo.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        startInfo.Environment["PIP_NO_INDEX"] = "1";
        startInfo.Environment.Remove("PIP_INDEX_URL");
        startInfo.Environment.Remove("PIP_EXTRA_INDEX_URL");

        using Process process = new() { StartInfo = startInfo };

        StringBuilder output = new();
        StringBuilder error = new();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { error.AppendLine(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }
}

/// <summary>What an installed environment was built from, so a re-pin invalidates it.</summary>
internal sealed record EnvironmentStamp
{
    [JsonPropertyName("python_revision")]
    public string PythonRevision { get; init; } = string.Empty;

    [JsonPropertyName("python_version")]
    public string PythonVersion { get; init; } = string.Empty;

    [JsonPropertyName("package_summary")]
    public string PackageSummary { get; init; } = string.Empty;

    [JsonPropertyName("packages")]
    public IReadOnlyList<string> Packages { get; init; } = [];

    [JsonPropertyName("installed_utc")]
    public DateTimeOffset InstalledUtc { get; init; }
}

[JsonSerializable(typeof(EnvironmentStamp))]
internal sealed partial class EnvironmentStampContext : JsonSerializerContext;
