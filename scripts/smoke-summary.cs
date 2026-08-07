#:project ../src/EchoForge.Infrastructure/EchoForge.Infrastructure.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// The production local summariser, end to end, on real hardware.
//
// This runs the actual path: the pinned llama.cpp launches the pinned GGUF, the model reads a
// real transcript revision, and the host's own validator decides whether what came back may be
// activated. Nothing is stubbed. If this passes, local summarisation works on this machine.
//
// It is a machinery test, not a quality benchmark. One fixture proves the pipeline runs and that
// the guardrails hold; it proves nothing about summary quality on real meetings, which is what
// the annotated corpora in the next pass are for.
//
//   dotnet run scripts/smoke-summary.cs

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Summaries;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Summaries;
using EchoForge.Infrastructure.Workers;

string repoRoot = Directory.GetCurrentDirectory();
string manifestPath = Path.Combine(repoRoot, "artifacts", "manifest.json");

List<string> failures = [];
void Check(bool condition, string what)
{
    Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {what}");
    if (!condition)
    {
        failures.Add(what);
    }
}

Console.WriteLine("EchoForge production summary smoke test");
Console.WriteLine($"manifest: {manifestPath}\n");

using ArtifactRegistry? registry = ArtifactRegistry.TryOpen(manifestPath, out IReadOnlyList<string> problems);
if (registry is null)
{
    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"manifest problem: {problem}");
    }

    return 2;
}

LlamaRuntimeStager stager = new(registry);

Console.WriteLine("== runtime ==");
LlamaRuntimePaths? runtime = await stager.EnsureAsync(ProcessingProfile.SummaryCudaQ4);
if (runtime is null)
{
    Console.Error.WriteLine("the CUDA summary profile is not installed. Run:");
    Console.Error.WriteLine("  dotnet run scripts/fetch-artifacts.cs -- --profile summary-cuda-q4");
    return 3;
}

Check(File.Exists(runtime.ServerBinary), $"llama.cpp server staged from a verified archive ({Path.GetFileName(runtime.ServerBinary)})");
Check(File.Exists(runtime.ModelPath), $"pinned GGUF present ({Path.GetFileName(runtime.ModelPath)})");
Console.WriteLine($"  model revision: {runtime.ModelRevision}");

// The digest is re-established from the bytes on disk, not taken from the marker: this is the
// one place where "the model EchoForge is about to run" and "the model the manifest pinned" are
// checked against each other rather than assumed to have stayed equal.
ArtifactState verified = await registry.VerifyInstalledAsync(stager.ModelEntry!.ArtifactId);
Check(verified.Status == ArtifactStatus.Installed, "the GGUF still hashes to the pinned SHA-256");

// -- a session with a transcript worth summarising -------------------------------------------

string root = Path.Combine(Path.GetTempPath(), "echoforge-smoke-summary", Guid.NewGuid().ToString("n"));
Directory.CreateDirectory(root);

const string SessionId = "01JSMOKESUMMARY";
FileSessionStore sessions = new(root);
FileTranscriptionStore transcripts = new(sessions);
FileSummaryStore summaries = new(sessions);
sessions.Create(SessionId);

// Every line here is deliberate. There is a decision, a decision that reverses it, an action with
// a named owner and an explicit date, an open question, an action nobody was assigned, and a
// tempting detail that is discussed without being agreed - which must not come out as a decision.
(string speaker, string text)[] lines =
[
    ("You", "Right, let's start. We need to settle the release date today."),
    ("Remote", "We will ship the beta on Friday."),
    ("You", "Agreed, Friday works for the beta."),
    ("Remote", "Alex will prepare the release notes by 2026-08-14."),
    ("You", "Who is going to handle the migration guide?"),
    ("Remote", "Someone will have to write it up, we can decide later."),
    ("You", "Actually, hold on. Support says Friday is bad for them."),
    ("Remote", "Fine, we will ship the beta on Monday instead."),
    ("You", "Should we also raise the price to ninety-nine dollars? Just thinking aloud."),
    ("Remote", "Let's not decide that now, it needs finance."),
];

List<TranscriptSegment> segments = [];
for (int i = 0; i < lines.Length; i++)
{
    (string speakerId, string speakerName) = TranscriptSpeakers.For(
        lines[i].speaker == "You" ? TranscriptSpeakers.MicrophoneTrack : TranscriptSpeakers.SystemTrack);

    segments.Add(new TranscriptSegment
    {
        Id = $"segment-{i + 1:D6}",
        Epoch = 1,
        StartSeconds = i * 12.0,
        EndSeconds = (i * 12.0) + 10.0,
        SpeakerId = speakerId,
        SpeakerName = speakerName,
        SourceTrack = lines[i].speaker == "You" ? TranscriptSpeakers.MicrophoneTrack : TranscriptSpeakers.SystemTrack,
        Text = lines[i].text,
        Confidence = null,
        Language = "en",
        Words = [],
    });
}

TranscriptDocument transcript = new()
{
    SessionId = SessionId,
    TranscriptRevision = 1,
    CreatedAtUtc = DateTimeOffset.UtcNow,
    SourceManifestSha256 = new string('a', 64),
    DurationSeconds = 600,
    Model = new TranscriptModel("echoforge-mock", "mock", "mock-v1", "mock-v1", "none", false, "0.1.0"),
    Epochs = [new TranscriptEpoch(1, 0, 600)],
    Speakers =
    [
        new TranscriptSpeaker(TranscriptSpeakers.YouId, TranscriptSpeakers.YouName, TranscriptSpeakers.MicrophoneTrack),
        new TranscriptSpeaker(TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName, TranscriptSpeakers.SystemTrack),
    ],
    Languages = [new TranscriptLanguage(TranscriptSpeakers.MicrophoneTrack, "en", null)],
    Segments = segments,
};

TranscriptionAttempt attempt = transcripts.BeginAttempt(
    SessionId, "job-smoke-transcript", new string('a', 64), new TranscriptionOptions(), DateTimeOffset.UtcNow);

byte[] payload = JsonSerializer.SerializeToUtf8Bytes(transcript, TranscriptDocument.Json);
Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
File.WriteAllBytes(attempt.StagingPath, payload);

ActivationOutcome activated = transcripts.Activate(
    new ActivationRequest(attempt, Convert.ToHexStringLower(SHA256.HashData(payload)), transcript, 1, null),
    DateTimeOffset.UtcNow);

if (!activated.Activated)
{
    Console.Error.WriteLine($"could not stage the transcript fixture: {activated.Refusal}");
    return 4;
}

// -- summarise, through the real coordinator --------------------------------------------------

Console.WriteLine("\n== generating (this loads a 6.98 GB model; expect a minute or two) ==");

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
    Backend = SummaryOptions.ProductionBackend,
    MeetingDate = new DateOnly(2026, 8, 7),
});
clock.Stop();

Console.WriteLine($"\n== result after {clock.Elapsed.TotalSeconds:F1}s ==");
Console.WriteLine($"  {result.State}  {result.FailureCode}  {result.Message}");

Check(result.Succeeded, "the production run activated a summary revision");

if (!result.Succeeded)
{
    Console.Error.WriteLine("\nsmoke test failed before it could inspect a summary.");
    return 1;
}

SummaryDocument summary = summaries.ReadSummary(SessionId, result.Revision!.Value)!;
SummaryRevisionRecord record = summaries.Read(SessionId).Selected!;

Console.WriteLine($"\n  backend: {summary.Model.Backend}  model: {summary.Model.ModelId}");
Console.WriteLine($"  runtime: {summary.Model.Runtime}  context: {summary.Model.ContextTokens}");
Console.WriteLine($"  thinking: {summary.Model.Thinking}   produces_summaries: {summary.Model.ProducesSummaries}");

Console.WriteLine($"\n  overview: {Trim(summary.Overview)}");
foreach (SummaryItem decision in summary.Decisions)
{
    Console.WriteLine($"  decision: {Trim(decision.Text)}  [{decision.Certainty}] {Cites(decision.Evidence)}");
}

foreach (SummaryAction action in summary.ActionItems)
{
    Console.WriteLine($"  action:   {Trim(action.Task)}  owner={action.Owner ?? "null"}({action.OwnerStatus}) due={action.DueDate ?? "null"}({action.DueDateStatus}) {Cites(action.Evidence)}");
}

foreach (SummaryItem question in summary.OpenQuestions)
{
    Console.WriteLine($"  question: {Trim(question.Text)} {Cites(question.Evidence)}");
}

Console.WriteLine();

// -- what must be true ------------------------------------------------------------------------

Check(summary.Model.Backend == "gemma-4-12b", "the revision records the production backend");
Check(summary.Model.ProducesSummaries, "the revision is not labelled a placeholder");
Check(!summary.Model.Thinking, "reasoning mode is off");
Check(summary.Model.ContextTokens > 0, $"the context it actually ran at is recorded ({summary.Model.ContextTokens})");
Check(!string.IsNullOrWhiteSpace(record.Runtime), $"the runtime profile is recorded ({record.Runtime})");

Check(
    SummaryValidator.Validate(summary, transcript).IsValid,
    "the activated summary passes the host validator against its own transcript revision");

HashSet<string> real = [.. segments.Select(s => s.Id)];
List<SummaryEvidence> citations = [.. summary.AllItems.SelectMany(i => i.Evidence), .. summary.ActionItems.SelectMany(a => a.Evidence)];

Check(citations.Count > 0, "the summary cites the transcript at all");
Check(citations.All(c => real.Contains(c.SegmentId)), "every citation resolves to a real segment");
Check(citations.All(c => c.TranscriptRevision == 1), "every citation names the transcript revision it was written from");

Check(
    summary.Decisions.All(d => d.Evidence.Count > 0) && summary.ActionItems.All(a => a.Evidence.Count > 0),
    "every decision and action cites at least one segment");

Check(
    summary.ActionItems.All(a => a.OwnerStatus != "unknown" || a.Owner is null),
    "no action claims an unknown owner while naming one");

Check(
    summary.ActionItems.All(a => a.DueDateStatus != "unknown" || a.DueDate is null),
    "no action claims an unknown due date while giving one");

// The transcript deliberately contains "Someone will have to write it up". An owner called
// "Someone" is the unsupported-owner failure the whole certainty model exists to prevent.
string[] notNames = ["someone", "somebody", "anyone", "we", "they", "nobody"];
Check(
    summary.ActionItems.All(a => a.Owner is null || !notNames.Contains(a.Owner.Trim().ToLowerInvariant())),
    "an indefinite pronoun was not recorded as an owner");

// The price was discussed and explicitly deferred. If it comes out as a decision, the model
// turned "let's not decide that now" into a decision.
Check(
    !summary.Decisions.Any(d => d.Text.Contains("99", StringComparison.Ordinal) || d.Text.Contains("ninety-nine", StringComparison.OrdinalIgnoreCase)),
    "the deferred price change was not emitted as a decision");

Console.WriteLine("\n== process hygiene ==");
Process[] survivors = Process.GetProcessesByName("llama-server");
Check(survivors.Length == 0, $"no llama-server process survived the job (found {survivors.Length})");
foreach (Process survivor in survivors)
{
    survivor.Dispose();
}

try
{
    Directory.Delete(root, recursive: true);
}
catch (IOException)
{
    // A leftover temp directory is not a test failure.
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("SMOKE TEST PASSED");
    Console.WriteLine("This proves the machinery runs locally. It says nothing about summary quality.");
    return 0;
}

Console.WriteLine($"SMOKE TEST FAILED ({failures.Count})");
foreach (string failure in failures)
{
    Console.WriteLine($"  - {failure}");
}

return 1;

static string Trim(string text) => text.Length <= 110 ? text : text[..107] + "...";

static string Cites(IReadOnlyList<SummaryEvidence> evidence) =>
    "<" + string.Join(",", evidence.Select(e => e.SegmentId.Replace("segment-", "s", StringComparison.Ordinal))) + ">";
