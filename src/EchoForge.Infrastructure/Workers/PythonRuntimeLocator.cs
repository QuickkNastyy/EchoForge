using System.Diagnostics;
using System.Globalization;

namespace EchoForge.Infrastructure.Workers;

/// <summary>Where a usable Python came from, and how it was found.</summary>
public sealed record PythonRuntime(string ExecutablePath, Version Version, string Source)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Python {Version} ({Source})");
}

/// <summary>
/// Finds the interpreter that runs the worker.
///
/// <para>
/// Phase 6 ships an app-local Python and this becomes a single directory lookup. Until
/// then the search is explicit and ordered, and it never guesses: a hard-coded developer
/// path would make the tests pass on one machine and fail everywhere else, and silently
/// accepting a 3.11 that happens to be first on PATH would produce failures far from their
/// cause.
/// </para>
/// </summary>
public static class PythonRuntimeLocator
{
    /// <summary>An explicit override, checked first. Points at a python executable.</summary>
    public const string OverrideVariable = "ECHOFORGE_PYTHON";

    /// <summary>The worker's floor. The plan pins Python 3.12 for the whole runtime stack.</summary>
    public static readonly Version MinimumVersion = new(3, 12);

    /// <summary>
    /// The first interpreter that meets the floor, or <c>null</c>.
    ///
    /// <para>
    /// Callers should say what is missing rather than failing vaguely — see
    /// <see cref="DescribeMissingRuntime"/>.
    /// </para>
    /// </summary>
    public static PythonRuntime? Locate()
    {
        string? overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            PythonRuntime? explicitRuntime = Probe(overridden, [], OverrideVariable);
            if (explicitRuntime is not null)
            {
                return explicitRuntime;
            }
        }

        // The Windows launcher resolves the pinned minor version without depending on
        // whatever happens to be first on PATH.
        PythonRuntime? launched = ProbeThroughLauncher();
        if (launched is not null)
        {
            return launched;
        }

        foreach (string candidate in (string[])["python3.12", "python3", "python"])
        {
            PythonRuntime? runtime = Probe(candidate, [], "PATH");
            if (runtime is not null)
            {
                return runtime;
            }
        }

        return null;
    }

    /// <summary>A message that names the missing component and the repair, as the plan requires.</summary>
    public static string DescribeMissingRuntime() => string.Create(
        CultureInfo.InvariantCulture,
        $"No CPython {MinimumVersion} or newer interpreter was found. Set {OverrideVariable} to a " +
        $"python.exe, or install Python {MinimumVersion} so that 'py -3.12' resolves.");

    private static PythonRuntime? ProbeThroughLauncher()
    {
        string minor = string.Create(CultureInfo.InvariantCulture, $"-{MinimumVersion.Major}.{MinimumVersion.Minor}");

        // py.exe is a launcher, not the interpreter. Resolve the real executable so the
        // supervisor starts python.exe directly and the process it waits on is the worker.
        string? resolved = Capture("py", [minor, "-c", "import sys; print(sys.executable)"]);
        return string.IsNullOrWhiteSpace(resolved) ? null : Probe(resolved.Trim(), [], "py launcher");
    }

    private static PythonRuntime? Probe(string executable, string[] prefixArguments, string source)
    {
        string[] arguments =
        [
            .. prefixArguments,
            "-c",
            "import sys; print('%d.%d.%d' % sys.version_info[:3]); print(sys.executable)",
        ];

        string? output = Capture(executable, arguments);
        if (output is null)
        {
            return null;
        }

        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !Version.TryParse(lines[0], out Version? version))
        {
            return null;
        }

        if (version < MinimumVersion)
        {
            return null;
        }

        string path = lines[1];
        return string.IsNullOrWhiteSpace(path) ? null : new PythonRuntime(path, version, source);
    }

    private static string? Capture(string fileName, string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            // Bounded: a probe that never returns must not hold up a recording.
            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // It exited between the check and the kill. Nothing to do.
                }

                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Not installed, not executable, or not on this platform. All the same answer.
            return null;
        }
    }
}
