#:project ../src/EchoForge.Infrastructure/EchoForge.Infrastructure.csproj
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false

// Runs one real transcription against a real session and prints what the worker actually said.
//
// The supervisor captures the child's stderr into WorkerRunResult.StandardErrorTail and the user
// only ever sees a safe sentence, which is right — stderr can carry meeting content. This is the
// diagnostic path that sentence is supposed to have: run it here, on purpose, and look.
//
//   dotnet run scripts/probe-worker.cs -- <sessionId> [profile]

using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Setup;
using EchoForge.Infrastructure.Workers;

string sessionId = args.Length > 0 ? args[0] : throw new ArgumentException("pass a session id");
string profile = args.Length > 1 ? args[1] : "cuda-fp16";

AppLayout layout = AppLayout.Current;
FileSessionStore sessions = new(layout.SessionsRoot);

SessionSnapshot? snapshot = sessions.ReadSnapshot(sessionId);
if (snapshot is null)
{
    Console.WriteLine("no snapshot for " + sessionId);
    return 1;
}

SessionPaths paths = sessions.Resolve(sessionId);
string output = Path.Combine(Path.GetTempPath(), $"echoforge-probe-{Guid.NewGuid():n}.json");

RequestBuildResult built = TranscriptionRequestBuilder.Build(
    snapshot,
    paths.Root,
    output,
    transcriptRevision: 99,
    DateTimeOffset.UtcNow,
    new RequestOptions
    {
        Backend = "faster-whisper",
        Profile = profile,
        Language = "en",
    });

if (built.Request is not { } request)
{
    Console.WriteLine("request could not be built: " + built.Failure?.Code + " " + built.Failure?.Detail);
    return 1;
}

Console.WriteLine($"session   {sessionId}");
Console.WriteLine($"profile   {profile}");
Console.WriteLine($"tracks    {string.Join(", ", request.Tracks.Select(t => $"{t.SourceTrack}:{t.Chunks.Count}"))}");
Console.WriteLine();

// Exactly what the installed application resolves: its own interpreter, and the worker package it
// shipped with. Discover() searches PATH and is explicitly not the application's path.
// AppLayout resolves relative to this script's own bin directory, so point setup at the installed
// application instead: its manifest, its runtime, its worker package.
string installed = args.Length > 2
    ? args[2]
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "EchoForge");

using SetupServices services = SetupServices.TryOpen(out IReadOnlyList<string> problems, AppLayout.For(installed, layout.DataRoot))
    ?? throw new InvalidOperationException("setup could not open: " + string.Join("; ", problems));

WorkerLaunchOptions options = services.TryResolveWorkerLaunch()
    ?? throw new InvalidOperationException("the worker environment could not be resolved");



// The coordinator enriches a production request with prepared audio, a window plan and a staged
// model directory before the worker ever sees it. Reproduced here, or the probe would be testing a
// request the application never sends.
if (!string.Equals(profile, "mock", StringComparison.Ordinal))
{
    ProcessingPreparation preparation = new(sessions, services.Artifacts);

    PreparationResult prepared = await preparation.PrepareAsync(
        request, profile, installMissing: false, cancellationToken: CancellationToken.None);

    Console.WriteLine($"prepare   {prepared.Stage} ready={prepared.IsReady} {prepared.FailureCode} {prepared.Message}");

    if (!prepared.IsReady || prepared.Plan is null || prepared.Derivatives is null)
    {
        return 1;
    }

    string? modelDirectory = services.Artifacts.TryStageModelDirectory(profile);
    Console.WriteLine($"model     {modelDirectory ?? "<not staged>"}");
    Console.WriteLine($"windows   {prepared.Plan.Windows.Count}");

    request = request with
    {
        Derivatives = [.. prepared.Derivatives.Derivatives.Select(d => new RequestDerivative
        {
            SourceTrack = d.SourceTrack,
            RelativePath = d.RelativePath,
            TimingMapRelativePath = d.TimingMapRelativePath,
            SampleRate = d.SampleRate,
            Channels = d.Channels,
            TotalFrames = d.TotalFrames,
            Sha256 = d.Sha256,
        })],
        Windows = [.. prepared.Plan.Windows.Select(w => new RequestWindow
        {
            Id = w.Id,
            SourceTrack = w.SourceTrack,
            Epoch = w.Epoch,
            StartFrame = w.StartFrame,
            EndFrame = w.EndFrame,
            SessionStartSeconds = w.SessionStartSeconds,
            SessionEndSeconds = w.SessionEndSeconds,
            OverlapBeforeSeconds = w.OverlapBeforeSeconds,
            OverlapAfterSeconds = w.OverlapAfterSeconds,
            InputFingerprint = w.InputFingerprint,
        })],
        Options = request.Options with
        {
            ModelPath = modelDirectory,
            ComputeProfile = profile,
            Profile = prepared.Plan.PlanningVersion,
        },
    };
}

Console.WriteLine();
Console.WriteLine($"python    {options.PythonExecutable}");
Console.WriteLine($"worker    {options.WorkerRoot}");
Console.WriteLine();

WorkerSupervisor supervisor = new(options);
WorkerRunResult result = await supervisor.TranscribeAsync(
    "probe-" + Guid.NewGuid().ToString("n"), request, progress: null, CancellationToken.None);

Console.WriteLine($"outcome   {result.Outcome}");
Console.WriteLine($"exit code {result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<none>"}");
Console.WriteLine($"error     {result.Error?.Code} {result.Error?.Detail}");
Console.WriteLine($"message   {result.UserMessage}");
Console.WriteLine();
Console.WriteLine("---- worker stderr ----");
Console.WriteLine(string.IsNullOrWhiteSpace(result.StandardErrorTail) ? "<empty>" : result.StandardErrorTail);

return result.Succeeded ? 0 : 1;
