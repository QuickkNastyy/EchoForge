#:project ../src/EchoForge.Infrastructure/EchoForge.Infrastructure.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// Runs one real transcript through one summary backend, and says what came back.
//
// The smoke test proves the pipeline works on a fixture built to be easy: ten clean lines, one
// speaker turn each, every claim citable. That is the right shape for "does the machinery run",
// and the wrong shape for "why does this model fail on a real meeting" — which is a question about
// messy input, not about plumbing.
//
// So this takes a session that already has a transcript, copies it into a scratch store, and asks
// a named backend to summarise it. Nothing is written back to the session it read.
//
//   dotnet run scripts/diagnose-summary.cs -- <session-folder> [backend]

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Summaries;
using EchoForge.Infrastructure.Workers;

string repoRoot = Directory.GetCurrentDirectory();

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run scripts/diagnose-summary.cs -- <session-folder> [backend]");
    return 2;
}

string sessionFolder = args[0];
string backend = args.Length > 1 ? args[1] : SummaryOptions.GptOssBackend;

string sourceTranscript = Directory
    .EnumerateFiles(Path.Combine(sessionFolder, "transcript"), "transcript.v*.json")
    .OrderBy(path => path)
    .LastOrDefault()
    ?? throw new FileNotFoundException($"no transcript under {sessionFolder}");

Console.WriteLine("EchoForge summary diagnosis");
Console.WriteLine($"  transcript: {sourceTranscript}");
Console.WriteLine($"  backend   : {backend}\n");

TranscriptDocument source = JsonSerializer.Deserialize<TranscriptDocument>(
    File.ReadAllBytes(sourceTranscript), TranscriptDocument.Json)!;

Console.WriteLine($"  segments  : {source.Segments.Count}");
Console.WriteLine($"  duration  : {source.DurationSeconds:F0}s");
Console.WriteLine($"  first id  : {(source.Segments.Count > 0 ? source.Segments[0].Id : "none")}");
Console.WriteLine($"  last id   : {(source.Segments.Count > 0 ? source.Segments[^1].Id : "none")}\n");

using ArtifactRegistry? registry = ArtifactRegistry.TryOpen(
    Path.Combine(repoRoot, "artifacts", "manifest.json"), out IReadOnlyList<string> problems);
if (registry is null)
{
    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"manifest problem: {problem}");
    }

    return 3;
}

LlamaRuntimeStager stager = new(registry);

string root = Path.Combine(Path.GetTempPath(), "echoforge-diagnose", Guid.NewGuid().ToString("n"));
Directory.CreateDirectory(root);

const string SessionId = "01JDIAGNOSESUMMARY";
FileSessionStore sessions = new(root);
FileTranscriptionStore transcripts = new(sessions);
FileSummaryStore summaries = new(sessions);
sessions.Create(SessionId);

// The same transcript, under a session this run owns, so the original is never written to.
TranscriptDocument transcript = source with { SessionId = SessionId, TranscriptRevision = 1 };

TranscriptionAttempt attempt = transcripts.BeginAttempt(
    SessionId, "job-diagnose", new string('a', 64), new TranscriptionOptions(), DateTimeOffset.UtcNow);

byte[] payload = JsonSerializer.SerializeToUtf8Bytes(transcript, TranscriptDocument.Json);
Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
File.WriteAllBytes(attempt.StagingPath, payload);

ActivationOutcome activated = transcripts.Activate(
    new ActivationRequest(attempt, Convert.ToHexStringLower(SHA256.HashData(payload)), transcript, 1, null),
    DateTimeOffset.UtcNow);

if (!activated.Activated)
{
    Console.Error.WriteLine($"could not stage the transcript: {activated.Refusal}");
    return 4;
}

WorkerLaunchOptions? launch = WorkerLaunchOptions.Discover(Path.Combine(repoRoot, "worker"));
if (launch is null)
{
    Console.Error.WriteLine("no suitable Python interpreter for the worker was found.");
    return 5;
}

WorkerSupervisor supervisor = new(launch with { Timeout = TimeSpan.FromMinutes(45) });
using SummaryCoordinator coordinator = new(sessions, summaries, transcripts, supervisor, runtime: stager);
coordinator.ProgressChanged += (_, e) => Console.WriteLine($"  .. {e.Stage} {e.CompletedUnits}/{e.TotalUnits}");

Stopwatch clock = Stopwatch.StartNew();
SummaryRunResult result = await coordinator.SummarizeAsync(SessionId, new SummaryOptions
{
    Backend = backend,
    MeetingDate = DateOnly.FromDateTime(DateTime.Now),
});
clock.Stop();

Console.WriteLine($"\n== result after {clock.Elapsed.TotalSeconds:F1}s ==");
Console.WriteLine($"  {result.State}  {result.FailureCode}");
Console.WriteLine($"  {result.Message}");

// Telemetry is written whether or not the revision activated, and it is the only record of what
// the model actually produced when the answer was thrown away for being unsupported.
foreach (string file in Directory.EnumerateFiles(
    Path.Combine(root), "*.telemetry.json", SearchOption.AllDirectories))
{
    Console.WriteLine($"\n== {Path.GetFileName(file)} ==");
    Console.WriteLine(File.ReadAllText(file));
}

return result.Succeeded ? 0 : 1;
