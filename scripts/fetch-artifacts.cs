#:project ../src/EchoForge.Infrastructure/EchoForge.Infrastructure.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// Installs pinned artifacts through the real ArtifactRegistry.
//
// There is deliberately no second downloader here. Everything about resuming, size checking,
// hashing, quarantine and atomic activation belongs to the registry the application itself
// uses, so a file fetched by this script and a file fetched by the app are installed by the
// same code and verified to the same standard. This is a thin front door, nothing more.
//
//   dotnet run scripts/fetch-artifacts.cs -- summary.gemma-4-12b-it-qat-q4-0 ...
//   dotnet run scripts/fetch-artifacts.cs -- --profile summary-cuda-q4

using EchoForge.Contracts.Artifacts;
using EchoForge.Infrastructure.Artifacts;

string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string manifestPath = Path.Combine(repoRoot, "artifacts", "manifest.json");

if (!File.Exists(manifestPath))
{
    manifestPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "manifest.json"));
}

Console.WriteLine($"manifest: {manifestPath}");

using ArtifactRegistry? registry = ArtifactRegistry.TryOpen(manifestPath, out IReadOnlyList<string> problems);
if (registry is null)
{
    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"  manifest problem: {problem}");
    }

    return 2;
}

Console.WriteLine($"model root: {registry.ModelRoot}");

List<string> wanted = [];
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--profile" && i + 1 < args.Length)
    {
        ProcessingProfile? profile = registry.Profile(args[++i]);
        if (profile is null)
        {
            Console.Error.WriteLine($"unknown profile: {args[i]}");
            return 2;
        }

        wanted.AddRange(profile.Artifacts.Select(a => a.ArtifactId));
        continue;
    }

    wanted.Add(args[i]);
}

if (wanted.Count == 0)
{
    Console.Error.WriteLine("nothing asked for. Pass artifact ids, or --profile <id>.");
    return 2;
}

int failures = 0;

foreach (string artifactId in wanted.Distinct(StringComparer.Ordinal))
{
    ArtifactEntry? entry = registry.Find(artifactId);
    if (entry is null)
    {
        Console.Error.WriteLine($"not in the manifest: {artifactId}");
        failures++;
        continue;
    }

    Console.WriteLine($"\n{artifactId}  ({entry.SizeBytes:N0} bytes)");
    Console.WriteLine($"  {entry.Url}");

    long lastReported = -1;
    Progress<ArtifactProgressEventArgs> progress = new(update =>
    {
        if (update.TotalBytes <= 0)
        {
            return;
        }

        long percent = update.BytesCompleted * 100 / update.TotalBytes;
        if (percent == lastReported)
        {
            return;
        }

        lastReported = percent;
        Console.WriteLine($"  {update.Status,-12} {percent,3}%  {update.BytesCompleted:N0} / {update.TotalBytes:N0}");
    });

    ArtifactState state = await registry.EnsureAsync(artifactId, progress);

    Console.WriteLine($"  -> {state.Status}{(state.Detail is null ? string.Empty : $": {state.Detail}")}");
    if (state.Status != ArtifactStatus.Installed)
    {
        failures++;
    }
}

Console.WriteLine(failures == 0 ? "\nall requested artifacts installed" : $"\n{failures} artifact(s) not installed");
return failures == 0 ? 0 : 1;
