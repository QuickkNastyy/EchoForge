#:project ../src/EchoForge.Infrastructure/EchoForge.Infrastructure.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// The summary evaluation harness.
//
//   dotnet run scripts/evaluate-summaries.cs -- validate
//   dotnet run scripts/evaluate-summaries.cs -- run --corpus synthetic --model gemma-4-12b
//   dotnet run scripts/evaluate-summaries.cs -- run --corpus development --compare
//   dotnet run scripts/evaluate-summaries.cs -- run --corpus release --compare --acceptance-run
//
// Two rules are enforced here rather than left to whoever is running it:
//
//   The release corpus is refused without --acceptance-run. Reading the held-out set by accident
//   is not recoverable - once numbers from it have been seen, the set has informed a decision and
//   is no longer held out.
//
//   Every report says what kind of data produced it, in the JSON and in the first line of the
//   Markdown. A development number and an acceptance number are the same shape on a page, and the
//   difference between them is the entire Phase 3 gate.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Evaluation;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Evaluation;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Summaries;
using EchoForge.Infrastructure.Workers;

string repoRoot = Directory.GetCurrentDirectory();
string benchmarkRoot = Path.Combine(repoRoot, "tests", "fixtures", "summary-benchmark");

string command = args.Length > 0 ? args[0] : "help";
string corpusName = Option("--corpus") ?? "synthetic";
bool compare = Flag("--compare");
bool acceptanceRun = Flag("--acceptance-run");
bool resume = !Flag("--fresh");
string outputRoot = Option("--out") ?? Path.Combine(repoRoot, "artifacts", "evaluation");

string? Option(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

bool Flag(string name) => Array.IndexOf(args, name) >= 0;

switch (command)
{
    case "validate":
        return Validate();
    case "run":
        return await RunAsync();
    default:
        Console.WriteLine("commands: validate | run");
        Console.WriteLine("  --corpus <synthetic|development|release>   which corpus (default synthetic)");
        Console.WriteLine("  --model <backend>                          one model to measure");
        Console.WriteLine("  --compare                                  measure both bake-off candidates");
        Console.WriteLine("  --acceptance-run                           required to read the held-out release corpus");
        Console.WriteLine("  --fresh                                    ignore checkpoints and re-run everything");
        Console.WriteLine("  --out <directory>                          where reports are written");
        return 0;
}

// ---------------------------------------------------------------------------------------------

int Validate()
{
    int problems = 0;
    Dictionary<string, SummaryCorpus> loaded = [];

    foreach (string kind in (string[])["synthetic", "development", "release"])
    {
        string path = Path.Combine(benchmarkRoot, kind, "corpus.json");

        if (!File.Exists(path))
        {
            Console.WriteLine($"{kind,-12} no corpus file — the annotated data is supplied separately");
            continue;
        }

        SummaryCorpus? corpus = CorpusValidator.TryLoad(path, out IReadOnlyList<string> issues);

        if (corpus is null)
        {
            Console.WriteLine($"{kind,-12} INVALID");
            foreach (string issue in issues)
            {
                Console.WriteLine($"             - {issue}");
            }

            problems += issues.Count;
            continue;
        }

        loaded[kind] = corpus;

        // Gold evidence is checked against the transcript it will actually be scored on. A gold
        // fact citing a segment that does not exist can never be matched, and would show up as a
        // model failing to find something no model could have found.
        int transcriptProblems = 0;
        foreach (CorpusMeeting meeting in corpus.Meetings)
        {
            TranscriptDocument? transcript = LoadTranscript(Path.Combine(benchmarkRoot, kind), meeting);
            if (transcript is null)
            {
                Console.WriteLine($"             - {meeting.MeetingId}: transcript could not be read");
                transcriptProblems++;
                continue;
            }

            CorpusVerdict verdict = CorpusValidator.ValidateAgainstTranscript(meeting, transcript);
            foreach (string issue in verdict.Problems)
            {
                Console.WriteLine($"             - {issue}");
                transcriptProblems++;
            }
        }

        problems += transcriptProblems;

        Console.WriteLine(
            $"{kind,-12} {(transcriptProblems == 0 ? "OK     " : "PROBLEM")} {corpus.Meetings.Count} meeting(s), fingerprint {CorpusValidator.Fingerprint(corpus)[..12]}");
    }

    if (loaded.TryGetValue("development", out SummaryCorpus? development) &&
        loaded.TryGetValue("release", out SummaryCorpus? release))
    {
        CorpusVerdict separation = CorpusValidator.ValidateSeparation(development, release);
        Console.WriteLine(separation.IsValid
            ? "separation   OK      development and release do not overlap"
            : "separation   FAILED");

        foreach (string issue in separation.Problems)
        {
            Console.WriteLine($"             - {issue}");
            problems++;
        }
    }
    else
    {
        Console.WriteLine("separation   n/a     both corpora would have to exist to check overlap");
    }

    Console.WriteLine();
    Console.WriteLine(problems == 0 ? "corpora valid" : $"{problems} problem(s)");
    return problems == 0 ? 0 : 1;
}

async Task<int> RunAsync()
{
    string corpusDirectory = Path.Combine(benchmarkRoot, corpusName);
    string corpusPath = Path.Combine(corpusDirectory, "corpus.json");

    if (!File.Exists(corpusPath))
    {
        Console.Error.WriteLine($"there is no corpus at {corpusPath}.");
        Console.Error.WriteLine("The human-annotated development and release corpora are supplied separately.");
        return 2;
    }

    SummaryCorpus? corpus = CorpusValidator.TryLoad(corpusPath, out IReadOnlyList<string> issues);
    if (corpus is null)
    {
        foreach (string issue in issues)
        {
            Console.Error.WriteLine($"corpus problem: {issue}");
        }

        return 2;
    }

    // Refused rather than warned about. Once release numbers have been seen they have informed a
    // decision, and no flag added afterwards makes the set held out again.
    if (corpus.IsAcceptanceData && !acceptanceRun)
    {
        Console.Error.WriteLine("This is the held-out release corpus. Reading it is the Phase 3 acceptance gate.");
        Console.Error.WriteLine("Re-run with --acceptance-run if that is genuinely what you intend.");
        return 3;
    }

    if (!corpus.IsAcceptanceData && acceptanceRun)
    {
        Console.Error.WriteLine($"--acceptance-run was passed but '{corpusName}' is not the release corpus.");
        Console.Error.WriteLine("Acceptance is decided by the held-out set and nothing else.");
        return 3;
    }

    using ArtifactRegistry? registry = ArtifactRegistry.TryOpen(
        Path.Combine(repoRoot, "artifacts", "manifest.json"), out IReadOnlyList<string> manifestProblems);

    if (registry is null)
    {
        foreach (string problem in manifestProblems)
        {
            Console.Error.WriteLine($"manifest problem: {problem}");
        }

        return 2;
    }

    LlamaRuntimeStager stager = new(registry);

    List<string> backends = compare
        ? [SummaryOptions.ProductionBackend, SummaryOptions.ComparisonBackend]
        : [Option("--model") ?? SummaryOptions.ProductionBackend];

    string corpusFingerprint = CorpusValidator.Fingerprint(corpus);
    Directory.CreateDirectory(outputRoot);
    string journalPath = Path.Combine(outputRoot, $"{corpus.CorpusId}.journal.json");
    EvaluationJournal journal = resume ? EvaluationCheckpoints.Load(journalPath) : new EvaluationJournal();

    Console.WriteLine($"corpus     {corpus.CorpusId} ({corpus.Kind.ToString().ToLowerInvariant()}), {corpus.Meetings.Count} meeting(s)");
    Console.WriteLine($"models     {string.Join(", ", backends)}");
    Console.WriteLine($"resume     {(resume ? "on" : "off")}  journal {journalPath}");

    if (corpus.IsSynthetic)
    {
        Console.WriteLine();
        Console.WriteLine("SYNTHETIC DATA. What follows tests the harness. It is not evidence about any model.");
    }

    Console.WriteLine();

    List<ModelEvaluation> evaluations = [];

    foreach (string backend in backends)
    {
        string profileId = backend == SummaryOptions.ComparisonBackend
            ? ProcessingProfile.SummaryBakeoff
            : ProcessingProfile.SummaryCudaQ4;

        LlamaRuntimePaths? runtime = await stager.EnsureAsync(profileId);
        if (runtime is null)
        {
            Console.Error.WriteLine($"{backend}: the '{profileId}' profile is not installed. Run:");
            Console.Error.WriteLine($"  dotnet run scripts/fetch-artifacts.cs -- --profile {profileId}");
            return 4;
        }

        List<MeetingScore> scores = [];

        foreach (CorpusMeeting meeting in corpus.Meetings)
        {
            string settings = $"seed=7;temp=0;profile={profileId}";
            string fingerprint = EvaluationCheckpoints.Fingerprint(
                corpusFingerprint, meeting.MeetingId, backend, runtime.ModelRevision, ["meeting-summary-v1"], settings);

            if (EvaluationCheckpoints.Reusable(journal, meeting.MeetingId, backend, fingerprint) is { } cached)
            {
                Console.WriteLine($"  {backend,-16} {meeting.MeetingId,-16} reused from checkpoint");
                scores.Add(cached);
                continue;
            }

            Console.Write($"  {backend,-16} {meeting.MeetingId,-16} running... ");

            MeetingScore score = await ScoreMeetingAsync(corpusDirectory, corpus, meeting, backend, profileId, stager, runtime);
            scores.Add(score);

            Console.WriteLine(score.ProducedSummary
                ? $"precision {score.CombinedPrecision}, recall {score.CombinedRecall}"
                : $"FAILED: {score.FailureReason}");

            journal = EvaluationCheckpoints.Append(journalPath, journal with { CorpusId = corpus.CorpusId },
                new EvaluationCheckpoint
                {
                    MeetingId = meeting.MeetingId,
                    Backend = backend,
                    InputFingerprint = fingerprint,
                    CompletedUtc = DateTimeOffset.UtcNow,
                    Score = score,
                });
        }

        evaluations.Add(SummaryScorer.Aggregate(backend, scores, scores.FirstOrDefault(s => s.Run is not null)?.Run));
    }

    EvaluationReport report = new()
    {
        CorpusId = corpus.CorpusId,
        CorpusKind = corpus.Kind,
        CorpusSha256 = corpusFingerprint,
        GeneratedUtc = DateTimeOffset.UtcNow,
        PromptVersions = ["extract-v1", "synthesize-v1", "repair-v1"],
        Models = evaluations,
        Acceptance = evaluations.Count > 0 ? SummaryScorer.Judge(evaluations[0], corpus.Kind) : null,
        Bakeoff = evaluations.Count == 2 ? BakeoffDecision.Decide(evaluations[0], evaluations[1]) : null,
    };

    string jsonPath = Path.Combine(outputRoot, $"{corpus.CorpusId}.report.json");
    string markdownPath = Path.Combine(outputRoot, $"{corpus.CorpusId}.report.md");

    File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, EvaluationReport.Json));
    File.WriteAllText(markdownPath, EvaluationMarkdown.Render(report));

    Console.WriteLine();
    Console.WriteLine(report.Acceptance?.Statement);
    Console.WriteLine();
    Console.WriteLine($"json      {jsonPath}");
    Console.WriteLine($"markdown  {markdownPath}");

    return 0;
}

async Task<MeetingScore> ScoreMeetingAsync(
    string corpusDirectory,
    SummaryCorpus corpus,
    CorpusMeeting meeting,
    string backend,
    string profileId,
    LlamaRuntimeStager stager,
    LlamaRuntimePaths runtime)
{
    TranscriptDocument? transcript = LoadTranscript(corpusDirectory, meeting);
    if (transcript is null)
    {
        return SummaryScorer.Score(meeting, null, EmptyTranscript(), corpus.MatchThreshold,
            failureReason: "the meeting's transcript could not be read");
    }

    string root = Path.Combine(Path.GetTempPath(), "echoforge-eval", Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(root);

    try
    {
        string sessionId = "01JEVAL" + Math.Abs(meeting.MeetingId.GetHashCode(StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture);
        FileSessionStore sessions = new(root);
        FileTranscriptionStore transcripts = new(sessions);
        FileSummaryStore summaries = new(sessions);
        sessions.Create(sessionId);

        TranscriptDocument planted = transcript with { SessionId = sessionId, TranscriptRevision = 1 };
        TranscriptionAttempt attempt = transcripts.BeginAttempt(
            sessionId, "job-eval", new string('a', 64), new TranscriptionOptions(), DateTimeOffset.UtcNow);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(planted, TranscriptDocument.Json);
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
        File.WriteAllBytes(attempt.StagingPath, payload);

        ActivationOutcome activated = transcripts.Activate(
            new ActivationRequest(attempt, Convert.ToHexStringLower(SHA256.HashData(payload)), planted, 1, null),
            DateTimeOffset.UtcNow);

        if (!activated.Activated)
        {
            return SummaryScorer.Score(meeting, null, planted, corpus.MatchThreshold,
                failureReason: $"the transcript could not be staged: {activated.Refusal}");
        }

        WorkerLaunchOptions? launch = WorkerLaunchOptions.Discover(Path.Combine(repoRoot, "worker"));
        if (launch is null)
        {
            return SummaryScorer.Score(meeting, null, planted, corpus.MatchThreshold,
                failureReason: "no Python interpreter for the worker was found");
        }

        WorkerSupervisor supervisor = new(launch with { Timeout = TimeSpan.FromMinutes(60) });
        using SummaryCoordinator coordinator = new(sessions, summaries, transcripts, supervisor, runtime: stager);

        Stopwatch clock = Stopwatch.StartNew();
        SummaryRunResult result = await coordinator.SummarizeAsync(sessionId, new SummaryOptions
        {
            Backend = backend,
            SummaryProfile = profileId,
            MeetingDate = meeting.MeetingDate is { } date ? DateOnly.Parse(date, CultureInfo.InvariantCulture) : null,
        });
        clock.Stop();

        if (!result.Succeeded)
        {
            return SummaryScorer.Score(meeting, null, planted, corpus.MatchThreshold,
                failureReason: $"{result.FailureCode}: {result.Message}");
        }

        SummaryDocument summary = summaries.ReadSummary(sessionId, result.Revision!.Value)!;
        RunMeasurements measurements = ReadTelemetry(summaries.PathFor(sessionId, result.Revision.Value), clock.Elapsed.TotalSeconds, backend);

        return SummaryScorer.Score(meeting, summary, planted, corpus.MatchThreshold, measurements);
    }
    finally
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not an evaluation failure.
        }
    }
}

// The worker writes telemetry beside the summary it activated. Read from there rather than
// re-derived, so what is reported is what the run actually measured about itself.
RunMeasurements ReadTelemetry(string summaryPath, double wallClockSeconds, string backend)
{
    foreach (string candidate in (string[])[summaryPath + ".telemetry.json", summaryPath + ".staging.telemetry.json"])
    {
        try
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(candidate);
            RunMeasurements? measured = JsonSerializer.Deserialize<RunMeasurements>(stream, EvaluationReport.Json);
            if (measured is not null)
            {
                return measured with { TotalSeconds = measured.TotalSeconds > 0 ? measured.TotalSeconds : wallClockSeconds };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            break;
        }
    }

    return new RunMeasurements { Backend = backend, TotalSeconds = wallClockSeconds };
}

TranscriptDocument? LoadTranscript(string corpusDirectory, CorpusMeeting meeting)
{
    try
    {
        string path = Path.Combine(corpusDirectory, meeting.TranscriptPath.Replace('/', Path.DirectorySeparatorChar));
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<TranscriptDocument>(stream, TranscriptDocument.Json);
    }
    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
    {
        return null;
    }
}

static TranscriptDocument EmptyTranscript() => new()
{
    SessionId = "unreadable",
    TranscriptRevision = 1,
    CreatedAtUtc = DateTimeOffset.UtcNow,
    SourceManifestSha256 = new string('0', 64),
    DurationSeconds = 0,
    Model = new TranscriptModel("none", "none", "none", "none", "none", false, "0"),
    Epochs = [],
    Speakers = [],
    Languages = [],
    Segments = [],
};
