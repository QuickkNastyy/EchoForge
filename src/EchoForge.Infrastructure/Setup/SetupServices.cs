using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Setup;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Summaries;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.Infrastructure.Setup;

/// <summary>
/// Everything the setup surface needs, composed once.
///
/// <para>
/// The application, the setup smoke script and the published-layout smoke all build the same
/// object graph, and building it in one place is what stops them drifting into testing three
/// slightly different applications. It is also the only place that knows the manifest may be
/// unopenable: when it is, nothing that could download is composed at all, which is a stronger
/// guarantee than every caller remembering to check.
/// </para>
/// </summary>
public sealed class SetupServices : IDisposable
{
    private bool _disposed;

    private SetupServices(
        AppLayout layout,
        ArtifactRegistry registry,
        PythonRuntimeInstaller python,
        WorkerEnvironmentInstaller worker,
        LlamaRuntimeStager llama,
        RuntimeRegistry runtimes,
        IReadOnlyList<string> manifestProblems)
    {
        Layout = layout;
        Artifacts = registry;
        Python = python;
        WorkerEnvironment = worker;
        Llama = llama;
        Runtimes = runtimes;
        ManifestProblems = manifestProblems;
    }

    /// <summary>
    /// Opens everything, or explains why the manifest cannot be trusted.
    ///
    /// <para>
    /// Returns null when the manifest fails validation. That is deliberately all-or-nothing: a
    /// manifest EchoForge cannot verify permits nothing rather than permitting less, so the
    /// application keeps recording and no download can happen.
    /// </para>
    /// </summary>
    public static SetupServices? TryOpen(out IReadOnlyList<string> problems, AppLayout? layout = null)
    {
        AppLayout resolved = layout ?? AppLayout.Current;

        ArtifactRegistry? registry = ArtifactRegistry.TryOpen(
            resolved.ManifestPath, out problems, resolved.ModelsRoot);

        if (registry is null)
        {
            return null;
        }

        PythonRuntimeInstaller python = new(registry, resolved);
        WorkerEnvironmentInstaller worker = new(registry, python, resolved);
        LlamaRuntimeStager llama = new(registry);

        return new SetupServices(
            resolved, registry, python, worker, llama, new RuntimeRegistry(registry, python, worker, llama), problems);
    }

    public AppLayout Layout { get; }

    public ArtifactRegistry Artifacts { get; }

    public PythonRuntimeInstaller Python { get; }

    public WorkerEnvironmentInstaller WorkerEnvironment { get; }

    public LlamaRuntimeStager Llama { get; }

    public RuntimeRegistry Runtimes { get; }

    /// <summary>Non-fatal complaints about the manifest, if any. An empty list is the usual case.</summary>
    public IReadOnlyList<string> ManifestProblems { get; }

    /// <summary>A hardware probe wired to ask the installed stack about CUDA rather than guess.</summary>
    public IHardwareProbe HardwareProbe(IAudioDeviceCatalog? audio = null) =>
        new WindowsHardwareProbe(Layout, audio, new CudaProbe(WorkerEnvironment).ProbeAsync);

    /// <summary>
    /// How to launch a worker, or null when the app-local runtime is not installed.
    ///
    /// <para>
    /// <b>There is no fall back to a Python on the machine.</b> The pinned wheel closure is built
    /// for one CPython ABI; borrowing whatever happens to be on PATH is how a machine that quietly
    /// moved to a newer Python starts failing at the first native import. A missing runtime is a
    /// thing setup can install, which is a much better answer than a confusing crash.
    /// </para>
    /// </summary>
    public WorkerLaunchOptions? TryResolveWorkerLaunch()
    {
        WorkerEnvironment? environment = WorkerEnvironment.TryResolve();

        return environment is null
            ? null
            : WorkerLaunchOptions.ForAppLocalPython(environment.PythonExecutable, Layout.WorkerPackageRoot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Artifacts.Dispose();
    }
}
