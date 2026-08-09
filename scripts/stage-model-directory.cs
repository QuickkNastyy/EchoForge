#:project ../src/EchoForge.Infrastructure/EchoForge.Infrastructure.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// Assembles one profile's verified speech-model files into the single directory a recogniser can
// load, and prints its path.
//
// There is deliberately no second assembler here: this calls the same ArtifactRegistry the
// application uses, so a directory staged by this script and one staged by EchoForge are built by
// the same code and verified to the same standard. It exists so provisioning and qualification can
// be driven from a shell during development without launching the UI.
//
//   dotnet run scripts/stage-model-directory.cs -- asr-canary-qwen-2.5b

using EchoForge.Infrastructure.Artifacts;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: stage-model-directory.cs -- <profile-id>");
    return 2;
}

string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string manifestPath = Path.Combine(repoRoot, "artifacts", "manifest.json");

if (!File.Exists(manifestPath))
{
    manifestPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "manifest.json"));
}

using ArtifactRegistry? registry = ArtifactRegistry.TryOpen(manifestPath, out IReadOnlyList<string> problems);
if (registry is null)
{
    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"  manifest problem: {problem}");
    }

    return 2;
}

string? staged = registry.TryStageModelDirectory(args[0]);
if (staged is null)
{
    Console.Error.WriteLine($"{args[0]} could not be staged; some of its files are not installed and verified.");
    return 1;
}

Console.WriteLine(staged);
return 0;
