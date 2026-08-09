namespace EchoForge.Infrastructure.Setup;

/// <summary>
/// Every directory EchoForge uses, decided in one place.
///
/// <para>
/// <b>The application never looks for the repository.</b> Before this existed, the worker package
/// and the artifact manifest were found by walking upwards from the executable looking for
/// <c>EchoForge.slnx</c> — which works beautifully on the machine the code was written on and
/// nowhere else. Everything the application ships with is now resolved relative to the executable,
/// and everything the user accumulates is resolved relative to their profile. Neither one can
/// depend on a source tree existing.
/// </para>
///
/// <para>
/// The split matters beyond tidiness. The installation directory is written once by an installer
/// and is read-only to a standard user afterwards; the data directory is written constantly and
/// must survive uninstalling, repairing and upgrading the application. Putting a session anywhere
/// under Program Files would mean a repair could destroy a meeting.
/// </para>
/// </summary>
public sealed class AppLayout
{
    /// <summary>Points the data root somewhere else. For tests and for a portable layout.</summary>
    public const string DataRootVariable = "ECHOFORGE_DATA_ROOT";

    /// <summary>Points the installation root somewhere else. For tests of a published layout.</summary>
    public const string ApplicationRootVariable = "ECHOFORGE_APP_ROOT";

    private AppLayout(string applicationRoot, string dataRoot)
    {
        ApplicationRoot = applicationRoot;
        DataRoot = dataRoot;
    }

    /// <summary>The layout this process is actually running under.</summary>
    public static AppLayout Current { get; } = new(ResolveApplicationRoot(), ResolveDataRoot());

    /// <summary>An explicit layout, for tests and for the packaging scripts.</summary>
    public static AppLayout For(string applicationRoot, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        return new AppLayout(Path.GetFullPath(applicationRoot), Path.GetFullPath(dataRoot));
    }

    // -- what ships with the application ---------------------------------------------------------

    /// <summary>Where the executable and everything installed beside it live.</summary>
    public string ApplicationRoot { get; }

    /// <summary>
    /// The <c>echoforge_worker</c> package, copied into the publish output.
    ///
    /// <para>
    /// Shipped rather than referenced, so the running application never needs the repository. The
    /// directory becomes the worker's only <c>PYTHONPATH</c> entry, which is also what stops it
    /// importing a differently-versioned copy from somewhere else on the machine.
    /// </para>
    /// </summary>
    public string WorkerPackageRoot => Path.Combine(ApplicationRoot, "worker");

    /// <summary>
    /// The pinned artifact manifest, shipped with the application.
    ///
    /// <para>
    /// It has to travel with the binaries: it is the only list of things that may be downloaded,
    /// and an application that had to find it in a source tree could not enforce that after
    /// installation.
    /// </para>
    /// </summary>
    public string ManifestPath => Path.Combine(ApplicationRoot, "artifacts", "manifest.json");

    /// <summary>
    /// The provisioning and qualification scripts, shipped with the application.
    ///
    /// <para>
    /// They travel with the binaries for the same reason the manifest does: building an isolated
    /// Linux runtime and proving a model can run are things the installed application has to be
    /// able to do, not things a developer does from a source tree beforehand.
    /// </para>
    /// </summary>
    public string ScriptsRoot => Path.Combine(ApplicationRoot, "scripts");

    /// <summary>Licence texts for everything distributed, staged beside the application.</summary>
    public string LicensesRoot => Path.Combine(ApplicationRoot, "third_party", "licenses");

    public string NoticePath => Path.Combine(ApplicationRoot, "third_party", "NOTICE.md");

    // -- what the user accumulates ---------------------------------------------------------------

    /// <summary>
    /// Everything belonging to this user, under their profile.
    ///
    /// <para>
    /// Never inside the installation directory. Uninstalling must not be able to take a meeting
    /// with it, and a standard user must be able to record without write access to Program Files.
    /// </para>
    /// </summary>
    public string DataRoot { get; }

    /// <summary>Canonical recordings, transcripts and summaries. The only irreplaceable directory.</summary>
    public string SessionsRoot => Path.Combine(DataRoot, "sessions");

    /// <summary>Verified downloads, laid out by artifact and revision.</summary>
    public string ModelsRoot => Path.Combine(DataRoot, "models");

    /// <summary>Installed inference runtimes: the app-local Python, the worker environment, llama.cpp.</summary>
    public string RuntimeRoot => Path.Combine(DataRoot, "runtime");

    /// <summary>The unpacked app-local CPython, one directory per pinned revision.</summary>
    public string PythonRoot => Path.Combine(RuntimeRoot, "python");

    /// <summary>The worker's installed package environment.</summary>
    public string WorkerEnvironmentRoot => Path.Combine(RuntimeRoot, "worker-env");

    /// <summary>
    /// EchoForge's own interpreter inside that environment, or null when it is not installed.
    ///
    /// <para>
    /// There is deliberately no fall back to a Python on PATH. The pinned wheel closure is built
    /// for one CPython ABI, and borrowing whatever happens to be installed is how a machine that
    /// quietly moved to a newer Python starts failing at the first native import.
    /// </para>
    /// </summary>
    public string? WorkerPythonPath
    {
        get
        {
            string path = Path.Combine(WorkerEnvironmentRoot, "Scripts", "python.exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>A flat directory of verified wheels, because an offline install wants one place to look.</summary>
    public string WheelhouseRoot => Path.Combine(RuntimeRoot, "wheelhouse");

    public string ConfigRoot => Path.Combine(DataRoot, "config");

    public string LogsRoot => Path.Combine(DataRoot, "logs");

    /// <summary>Where a diagnostics bundle is written, only when a user asks for one.</summary>
    public string DiagnosticsRoot => Path.Combine(DataRoot, "diagnostics");

    /// <summary>
    /// Staging and scratch.
    ///
    /// <para>
    /// Under the data root rather than <c>%TEMP%</c> on purpose: a 7 GB model being unpacked wants
    /// to land on the same volume it will finally live on, so activation is a rename rather than a
    /// copy across drives.
    /// </para>
    /// </summary>
    public string TempRoot => Path.Combine(DataRoot, "temp");

    /// <summary>The library index. A cache beside the sessions, not inside any one of them.</summary>
    public string IndexPath => Path.Combine(SessionsRoot, "library.db");

    /// <summary>Creates the directories the application writes to. Read-only ones are not touched.</summary>
    public void EnsureDataDirectories()
    {
        foreach (string directory in (string[])[DataRoot, SessionsRoot, ModelsRoot, RuntimeRoot, ConfigRoot, LogsRoot])
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>True when this build was laid out by the packaging script rather than by a build.</summary>
    public bool LooksPublished =>
        File.Exists(Path.Combine(ApplicationRoot, "EchoForge.App.exe")) &&
        Directory.Exists(WorkerPackageRoot) &&
        File.Exists(ManifestPath);

    private static string ResolveApplicationRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable(ApplicationRootVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.GetFullPath(AppContext.BaseDirectory)
            : Path.GetFullPath(overridden);
    }

    private static string ResolveDataRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable(DataRootVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EchoForge");
    }
}
