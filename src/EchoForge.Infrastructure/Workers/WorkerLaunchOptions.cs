using EchoForge.Contracts.Workers;
using EchoForge.Infrastructure.Setup;

namespace EchoForge.Infrastructure.Workers;

/// <summary>How to start a worker and how long to put up with it.</summary>
public sealed record WorkerLaunchOptions
{
    /// <summary>Absolute path to the interpreter. Resolved, never a launcher shim.</summary>
    public required string PythonExecutable { get; init; }

    /// <summary>
    /// The directory containing the <c>echoforge_worker</c> package. It becomes both the
    /// working directory and the only entry on the child's <c>PYTHONPATH</c>, so the worker
    /// cannot accidentally import a differently-versioned copy from somewhere else.
    /// </summary>
    public required string WorkerRoot { get; init; }

    /// <summary>
    /// The module the interpreter runs. Only the supervisor's own tests change it, to point
    /// at a stub that misbehaves on purpose — a supervisor judged solely against a
    /// well-behaved worker has never been tested at all.
    /// </summary>
    public string ModuleName { get; init; } = "echoforge_worker";

    /// <summary>
    /// How long the whole run may take before the tree is killed. Generous by default: a
    /// long meeting on a CPU fallback is slow, not stuck.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a cancelled worker gets to stop at a safe boundary before it is killed.
    /// Cancellation is a request first and a termination second.
    /// </summary>
    public TimeSpan CancelGracePeriod { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a finished worker gets to exit by itself. It should exit immediately after
    /// its terminal message; this only bounds the case where it does not.
    /// </summary>
    public TimeSpan ExitGracePeriod { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How much of the child's stderr to keep. Bounded because a worker in a loop can
    /// produce diagnostics faster than anything wants to store them.
    /// </summary>
    public int StandardErrorCharacterLimit { get; init; } = 16 * 1024;

    public string HostVersion { get; init; } = "EchoForge/0.2";

    /// <summary>
    /// Unlocks the worker's fault-injection modes. The application never sets this; only
    /// the supervisor's own tests do, and the worker ignores every test mode without it.
    /// </summary>
    public bool AllowTestModes { get; init; }

    /// <summary>
    /// The conventional developer layout, where Python comes from
    /// <see cref="PythonRuntimeLocator"/>. Returns <c>null</c> when no suitable interpreter
    /// exists.
    ///
    /// <para>
    /// The application does not use this: an installed EchoForge runs
    /// <see cref="ForAppLocalPython"/> against the interpreter it shipped with. This is what the
    /// repository's own tests and scripts use, and what a developer gets when they set
    /// <see cref="PythonRuntimeLocator.OverrideVariable"/>.
    /// </para>
    /// </summary>
    public static WorkerLaunchOptions? Discover(string workerRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerRoot);

        PythonRuntime? runtime = PythonRuntimeLocator.Locate();
        return runtime is null
            ? null
            : new WorkerLaunchOptions
            {
                PythonExecutable = runtime.ExecutablePath,
                WorkerRoot = Path.GetFullPath(workerRoot),
            };
    }

    /// <summary>
    /// The installed layout: EchoForge's own interpreter, and the worker package it shipped with.
    ///
    /// <para>
    /// No search, no PATH, no launcher. The pinned wheel closure is built for exactly one CPython
    /// ABI, and a machine that had quietly moved to a newer Python would fail at the first native
    /// import with a message about tags that means nothing to the person holding the meeting.
    /// </para>
    /// </summary>
    public static WorkerLaunchOptions ForAppLocalPython(string pythonExecutable, string workerRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerRoot);

        return new WorkerLaunchOptions
        {
            PythonExecutable = Path.GetFullPath(pythonExecutable),
            WorkerRoot = Path.GetFullPath(workerRoot),
        };
    }

    /// <summary>The environment the child runs with, built rather than inherited wholesale.</summary>
    internal void ApplyEnvironment(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // Offline first: no worker may reach a model host, and the libraries in the stack do not
        // agree with that by default. See OfflineEnvironment for what each flag stops.
        OfflineEnvironment.Apply(environment);

        // The only place the worker package may be imported from, so it cannot pick up a
        // differently-versioned copy from somewhere else on the machine.
        environment["PYTHONPATH"] = WorkerRoot;

        if (AllowTestModes)
        {
            environment[WorkerProtocol.AllowTestModesVariable] = "1";
        }
        else
        {
            environment.Remove(WorkerProtocol.AllowTestModesVariable);
        }
    }
}
