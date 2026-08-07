#:project ../src/EchoForge.Infrastructure/EchoForge.Infrastructure.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// Installs EchoForge's own Python and its worker environment, offline, from verified artifacts.
//
// This runs the *production* code — PythonRuntimeInstaller and WorkerEnvironmentInstaller, the
// same classes the application's setup screen calls. That is the point of it existing: the earlier
// PowerShell installer resolved packages its own way, which meant a developer machine and an
// installed machine were built by two different implementations and only one of them was tested.
//
// Nothing here can reach a package index. Every wheel is a manifest entry whose length and digest
// were checked before it was copied into the wheelhouse, and pip runs with --no-index.
//
//   dotnet run scripts/install-runtime.cs
//   dotnet run scripts/install-runtime.cs -- --status
//   dotnet run scripts/install-runtime.cs -- --repair

using System.IO;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Infrastructure.Setup;

bool statusOnly = args.Contains("--status", StringComparer.Ordinal);
bool repair = args.Contains("--repair", StringComparer.Ordinal);

// The repository copy of the manifest and the worker, so this works before anything is published.
string repoRoot = Directory.GetCurrentDirectory();
AppLayout layout = AppLayout.For(
    Path.Combine(repoRoot),
    Environment.GetEnvironmentVariable(AppLayout.DataRootVariable)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoForge"));

using SetupServices? setup = SetupServices.TryOpen(out IReadOnlyList<string> problems, layout);

if (setup is null)
{
    Console.WriteLine("The pinned manifest could not be trusted, so nothing was installed:");
    foreach (string problem in problems)
    {
        Console.WriteLine("  - " + problem);
    }

    return 2;
}

Console.WriteLine("EchoForge runtime installation");
Console.WriteLine("  manifest   " + layout.ManifestPath);
Console.WriteLine("  worker     " + layout.WorkerPackageRoot);
Console.WriteLine("  models     " + layout.ModelsRoot);
Console.WriteLine("  runtime    " + layout.RuntimeRoot);
Console.WriteLine();

void Report()
{
    RuntimeComponentState python = setup.Python.Status();
    RuntimeComponentState worker = setup.WorkerEnvironment.Status();

    Console.WriteLine("  python     " + python.Status + "  " + python.Detail);
    Console.WriteLine("  worker     " + worker.Status + "  " + worker.Detail);

    if (setup.Python.TryResolve() is { } resolved)
    {
        Console.WriteLine("  interpreter " + resolved.ExecutablePath);
    }

    if (setup.WorkerEnvironment.TryResolve() is { } environment)
    {
        Console.WriteLine("  packages    " + environment.PackageSummary);
        Console.WriteLine("  worker python " + environment.PythonExecutable);
    }
}

if (statusOnly)
{
    Report();
    return 0;
}

string last = string.Empty;

Progress<ArtifactProgressEventArgs> progress = new(p =>
{
    string line = $"  {p.ArtifactId}  {p.Status}  {p.Fraction:P0}";
    if (!string.Equals(line, last, StringComparison.Ordinal))
    {
        last = line;
        Console.WriteLine(line);
    }
});

if (repair)
{
    // Verify first, download second. A model that is present and simply has no proof recorded
    // against it — because something other than this application downloaded it — is repaired by
    // hashing it, not by fetching 1.6 GB again.
    Console.WriteLine("Verifying everything already on disk...");

    foreach (ArtifactEntry entry in setup.Artifacts.Artifacts)
    {
        ArtifactState before = setup.Artifacts.Status(entry);
        if (before.Status is ArtifactStatus.NotInstalled)
        {
            continue;
        }

        ArtifactState after = await setup.Artifacts.VerifyInstalledAsync(entry.ArtifactId);
        Console.WriteLine("  " + entry.ArtifactId.PadRight(46) + after.Status
            + (after.Detail is null ? string.Empty : "  " + after.Detail));
    }

    Console.WriteLine();
    Console.WriteLine("Rebuilding the worker environment...");
    WorkerEnvironmentResult repaired = await setup.WorkerEnvironment.RepairAsync(progress);

    Console.WriteLine();
    Console.WriteLine("  " + repaired.Message);
    Report();
    return repaired.Succeeded ? 0 : 1;
}

Console.WriteLine("Installing...");
WorkerEnvironmentResult result = await setup.WorkerEnvironment.EnsureAsync(progress);

Console.WriteLine();
Console.WriteLine("  " + result.Message);
Console.WriteLine();
Report();

return result.Succeeded ? 0 : 1;
