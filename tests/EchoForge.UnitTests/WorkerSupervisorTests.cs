using System.Text.Json;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.UnitTests;

/// <summary>
/// The supervisor against a real child process.
///
/// <para>
/// Every case here launches Python. Mocking the process layer would only prove that the mock
/// behaves; what has to be true is that a real pipe, a real exit code, and a real killed
/// process tree are handled, and none of those exist in a fake.
/// </para>
/// </summary>
public sealed class WorkerSupervisorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static WorkerSupervisor Supervisor(
        WorkerLaunchOptions? options = null,
        ICaptureActivityGate? gate = null) =>
        new(options ?? WorkerTestEnvironment.Options(), gate);

    private static async Task<WorkerRunResult> RunAsync(
        TranscriptionRequest request,
        WorkerLaunchOptions? options = null,
        IProgress<ProgressMessage>? progress = null,
        CancellationToken cancellationToken = default) =>
        await Supervisor(options).TranscribeAsync("job-1", request, progress, cancellationToken);

    private static TranscriptDocument ReadTranscript(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<TranscriptDocument>(stream, TranscriptDocument.Json)!;
    }

    // -- the round trip -----------------------------------------------------------------

    [Fact]
    public async Task NeMoRequestWithoutTheIsolatedRuntimeFailsBeforeAnyWorkerCanLaunch()
    {
        WorkerLaunchOptions impossible = new()
        {
            PythonExecutable = @"C:\this\worker\does-not-exist.exe",
            WorkerRoot = _temp.Path,
        };
        TranscriptionRequest request = WorkerProtocolTests.SmallRequest() with
        {
            Options = new RequestOptions
            {
                Backend = "nemo",
                ModelId = "parakeet-unified-en-0.6b",
                ComputeProfile = "cuda-fp16",
                VadMode = "accuracy",
            },
        };

        WorkerRunResult result = await new WorkerSupervisor(impossible).TranscribeAsync("job-nemo", request);

        Assert.Equal(WorkerOutcome.Failed, result.Outcome);
        Assert.Equal(WorkerErrorCodes.BackendUnavailable, result.Error!.Code);
        Assert.Null(result.ExitCode);
        Assert.Contains("complete setup", result.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [WorkerFact]
    public async Task ASessionGoesToPythonAndComesBackAsAValidTranscript()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path);

        WorkerRunResult result = await RunAsync(request);

        Assert.Equal(WorkerOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.Output);
        Assert.True(File.Exists(result.Output!.OutputPath));
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.ProtocolViolations);

        TranscriptDocument transcript = ReadTranscript(result.Output.OutputPath);
        TranscriptVerdict verdict = TranscriptValidator.Validate(transcript);

        Assert.True(verdict.IsValid, string.Join("; ", verdict.Problems));
        Assert.NotEmpty(transcript.Segments);
        Assert.Equal(result.Output.SegmentCount, transcript.Segments.Count);
    }

    [WorkerFact]
    public async Task MicrophoneSegmentsAreYouAndSystemSegmentsAreRemote()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path);

        WorkerRunResult result = await RunAsync(request);
        TranscriptDocument transcript = ReadTranscript(result.Output!.OutputPath);

        Assert.Contains(transcript.Segments, s => s.SourceTrack == TranscriptSpeakers.MicrophoneTrack);
        Assert.Contains(transcript.Segments, s => s.SourceTrack == TranscriptSpeakers.SystemTrack);

        foreach (TranscriptSegment segment in transcript.Segments)
        {
            (string id, string name) = TranscriptSpeakers.For(segment.SourceTrack);
            Assert.Equal(id, segment.SpeakerId);
            Assert.Equal(name, segment.SpeakerName);
        }
    }

    [WorkerFact]
    public async Task TheHostAndTheWorkerAgreeOnWhichAudioTheTranscriptCameFrom()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path);

        WorkerRunResult result = await RunAsync(request);
        TranscriptDocument transcript = ReadTranscript(result.Output!.OutputPath);

        // Two independent implementations of the same canonical form. If they ever disagree,
        // the host could not recognise a transcript as belonging to its own audio.
        Assert.Equal(
            TranscriptionRequestBuilder.SourceManifestSha256(request),
            transcript.SourceManifestSha256);
    }

    [WorkerFact]
    public async Task ProgressIsReportedAndNeverGoesBackwards()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path);
        List<ProgressMessage> updates = [];

        await RunAsync(request, progress: new Progress<ProgressMessage>(updates.Add));

        // Progress<T> posts asynchronously; give the last few a moment to arrive.
        await Task.Delay(200);

        Assert.NotEmpty(updates);
        int completed = 0;
        foreach (ProgressMessage update in updates)
        {
            Assert.True(update.CompletedUnits >= completed);
            Assert.True(update.CompletedUnits <= update.TotalUnits);
            completed = update.CompletedUnits;
        }
    }

    [WorkerFact]
    public async Task SilentAudioProducesAnEmptyButValidTranscript()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path, silent: true);

        WorkerRunResult result = await RunAsync(request);

        Assert.Equal(WorkerOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.Output!.SegmentCount);
        Assert.True(TranscriptValidator.Validate(ReadTranscript(result.Output.OutputPath)).IsValid);
    }

    [WorkerFact]
    public async Task RunningTwiceOverTheSameAudioProducesTheSameTranscript()
    {
        string first = Path.Combine(_temp.Path, "one");
        string second = Path.Combine(_temp.Path, "two");

        WorkerRunResult a = await RunAsync(WorkerTestEnvironment.BuildSession(first));
        WorkerRunResult b = await RunAsync(WorkerTestEnvironment.BuildSession(second));

        Assert.Equal(a.Output!.Sha256, b.Output!.Sha256);
        Assert.Equal(
            await File.ReadAllBytesAsync(a.Output.OutputPath),
            await File.ReadAllBytesAsync(b.Output.OutputPath));
    }

    [WorkerFact]
    public async Task TheSourceAudioIsNotTouched()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path);
        DirectorySnapshot before = DirectorySnapshot.Capture(request.SessionRoot);

        Assert.Equal(WorkerOutcome.Succeeded, (await RunAsync(request)).Outcome);

        DirectorySnapshot after = DirectorySnapshot.Capture(request.SessionRoot);
        Assert.True(after.Matches(before), after.Describe());
    }

    // -- awkward paths ------------------------------------------------------------------

    [WorkerTheory]
    [InlineData("a folder with spaces")]
    [InlineData("sesión-日本語-Ω")]
    [InlineData("деловая встреча (2026)")]
    public async Task PathsWithSpacesAndNonAsciiCharactersWork(string folder)
    {
        string root = Path.Combine(_temp.Path, folder);
        Directory.CreateDirectory(root);

        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(root, sessionId: "sesión-01");
        WorkerRunResult result = await RunAsync(request);

        Assert.Equal(WorkerOutcome.Succeeded, result.Outcome);
        TranscriptDocument transcript = ReadTranscript(result.Output!.OutputPath);
        Assert.Equal("sesión-01", transcript.SessionId);
    }

    // -- a worker that dies --------------------------------------------------------------

    [WorkerFact]
    public async Task AWorkerThatCrashesIsReportedAsCrashedWithItsDiagnosticsKept()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path, testMode: WorkerTestModes.Crash);

        WorkerRunResult result = await RunAsync(request);

        Assert.Equal(WorkerOutcome.Crashed, result.Outcome);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("DeliberateCrash", result.StandardErrorTail, StringComparison.Ordinal);
        Assert.False(File.Exists(request.OutputPath));
    }

    [WorkerFact]
    public async Task AWorkerThatExitsWithoutSayingAnythingIsCrashedNotFailed()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path, testMode: WorkerTestModes.NonzeroExit);

        WorkerRunResult result = await RunAsync(request);

        Assert.Equal(WorkerOutcome.Crashed, result.Outcome);
        Assert.Equal(7, result.ExitCode);
    }

    [WorkerFact]
    public async Task AWorkerThatNeverAnswersTheHandshakeIsCrashed()
    {
        WorkerLaunchOptions options = WorkerTestEnvironment.Options(
            workerRoot: WorkerTestEnvironment.StubRoot,
            moduleName: "stub_silent_exit");

        WorkerRunResult result = await RunAsync(WorkerTestEnvironment.BuildSession(_temp.Path), options);

        Assert.Equal(WorkerOutcome.Crashed, result.Outcome);
        Assert.Contains("handshake", result.Error!.Detail!, StringComparison.Ordinal);
    }

    [WorkerFact]
    public async Task StandardErrorIsCapturedWithoutDisturbingASuccessfulJob()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path, testMode: WorkerTestModes.Stderr);

        WorkerRunResult result = await RunAsync(request);

        Assert.Equal(WorkerOutcome.Succeeded, result.Outcome);
        Assert.Contains("diagnostic line 0", result.StandardErrorTail, StringComparison.Ordinal);
    }

    // -- a worker that talks nonsense ------------------------------------------------------

    [WorkerTheory]
    [InlineData(WorkerTestModes.InvalidJson)]
    [InlineData(WorkerTestModes.UnknownMessage)]
    [InlineData(WorkerTestModes.MalformedProgress)]
    [InlineData(WorkerTestModes.MalformedResult)]
    public async Task AWorkerThatBreaksTheProtocolIsReportedAsAProtocolError(string mode)
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path, testMode: mode);

        WorkerRunResult result = await RunAsync(request);

        Assert.Equal(WorkerOutcome.ProtocolError, result.Outcome);
        Assert.Equal(WorkerErrorCodes.ProtocolError, result.Error!.Code);
    }

    [WorkerFact]
    public async Task AWorkerSpeakingAnUnsupportedProtocolVersionIsRefused()
    {
        WorkerLaunchOptions options = WorkerTestEnvironment.Options(
            workerRoot: WorkerTestEnvironment.StubRoot,
            moduleName: "stub_wrong_version");

        WorkerRunResult result = await RunAsync(WorkerTestEnvironment.BuildSession(_temp.Path), options);

        Assert.Equal(WorkerOutcome.ProtocolError, result.Outcome);
        Assert.Contains("UnsupportedVersion", result.Error!.Detail!, StringComparison.Ordinal);
    }

    [WorkerFact]
    public async Task AWorkerWithNoVersionInCommonIsRefusedEvenThoughTheLineParsed()
    {
        WorkerLaunchOptions options = WorkerTestEnvironment.Options(
            workerRoot: WorkerTestEnvironment.StubRoot,
            moduleName: "stub_no_common_version");

        WorkerRunResult result = await RunAsync(WorkerTestEnvironment.BuildSession(_temp.Path), options);

        Assert.Equal(WorkerOutcome.ProtocolError, result.Outcome);
        Assert.Contains("this host speaks", result.Error!.Detail!, StringComparison.Ordinal);
    }

    [WorkerFact]
    public async Task AWorkerAcknowledgingSomeoneElsesJobIsRefused()
    {
        WorkerLaunchOptions options = WorkerTestEnvironment.Options(
            workerRoot: WorkerTestEnvironment.StubRoot,
            moduleName: "stub_wrong_job");

        WorkerRunResult result = await RunAsync(WorkerTestEnvironment.BuildSession(_temp.Path), options);

        Assert.Equal(WorkerOutcome.ProtocolError, result.Outcome);
        Assert.Contains("different job", result.Error!.Detail!, StringComparison.Ordinal);
    }

    [WorkerTheory]
    [InlineData(WorkerTestModes.DuplicateResult)]
    [InlineData(WorkerTestModes.OutputAfterCompletion)]
    public async Task TalkingAfterTheJobIsOverIsRecordedButDoesNotRetractVerifiedWork(string mode)
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path, testMode: mode);

        WorkerRunResult result = await RunAsync(request);

        // The transcript was written and its digest verified before the extra chatter
        // arrived. Throwing that away would be the worse failure; recording it is enough.
        Assert.Equal(WorkerOutcome.Succeeded, result.Outcome);
        Assert.NotEmpty(result.ProtocolViolations);
        Assert.True(File.Exists(result.Output!.OutputPath));
    }

    [WorkerFact]
    public async Task ATranscriptThatDoesNotMatchTheReportedDigestIsNotAccepted()
    {
        WorkerLaunchOptions options = WorkerTestEnvironment.Options(
            workerRoot: WorkerTestEnvironment.StubRoot,
            moduleName: "stub_lying_result");

        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path);
        WorkerRunResult result = await RunAsync(request, options);

        // The message was well formed, so only reading the bytes could catch this.
        Assert.Equal(WorkerOutcome.ProtocolError, result.Outcome);
        Assert.Contains("does not match the digest", result.Error!.Detail!, StringComparison.Ordinal);
        Assert.Null(result.Output);
    }

    [WorkerFact]
    public async Task AWorkerThatReportsAnOutputItNeverWroteIsRefused()
    {
        WorkerLaunchOptions options = WorkerTestEnvironment.Options(
            workerRoot: WorkerTestEnvironment.StubRoot,
            moduleName: "stub_missing_output");

        WorkerRunResult result = await RunAsync(WorkerTestEnvironment.BuildSession(_temp.Path), options);

        Assert.Equal(WorkerOutcome.ProtocolError, result.Outcome);
        Assert.Contains("does not exist", result.Error!.Detail!, StringComparison.Ordinal);
    }

    // -- timeout, cancellation, and cleanup --------------------------------------------------

    [WorkerFact]
    public async Task AHangingWorkerIsStoppedWhenItsTimeExpires()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path, testMode: WorkerTestModes.Hang);
        WorkerLaunchOptions options = WorkerTestEnvironment.Options(timeout: TimeSpan.FromSeconds(4));

        WorkerRunResult result = await RunAsync(request, options);

        Assert.Equal(WorkerOutcome.TimedOut, result.Outcome);
        Assert.False(File.Exists(request.OutputPath));
        Assert.Contains("time limit", result.UserMessage, StringComparison.Ordinal);
    }

    [WorkerFact]
    public async Task CancellationStopsTheWorkerAndLeavesNoTranscriptBehind()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(
            _temp.Path,
            testMode: WorkerTestModes.Delay,
            testDelaySeconds: 60);

        using CancellationTokenSource cancellation = new();
        Task<WorkerRunResult> run = RunAsync(request, cancellationToken: cancellation.Token);

        await Task.Delay(1500);
        await cancellation.CancelAsync();

        WorkerRunResult result = await run;

        Assert.Equal(WorkerOutcome.Cancelled, result.Outcome);
        Assert.False(File.Exists(request.OutputPath));
        // It stopped at a boundary and left, rather than being killed.
        Assert.Equal(0, result.ExitCode);
    }

    [WorkerFact]
    public async Task AWorkerThatIgnoresCancellationIsKilledAfterTheGracePeriod()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path, testMode: WorkerTestModes.Hang);
        WorkerLaunchOptions options = WorkerTestEnvironment.Options(
            timeout: TimeSpan.FromMinutes(2),
            cancelGrace: TimeSpan.FromSeconds(2));

        using CancellationTokenSource cancellation = new();
        Task<WorkerRunResult> run = RunAsync(request, options, cancellationToken: cancellation.Token);

        await Task.Delay(1000);
        await cancellation.CancelAsync();

        WorkerRunResult result = await run;

        Assert.Equal(WorkerOutcome.Cancelled, result.Outcome);
    }

    [WorkerFact]
    public async Task TerminatingAWorkerTakesEverythingItStartedWithIt()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path);
        WorkerLaunchOptions options = WorkerTestEnvironment.Options(
            timeout: TimeSpan.FromSeconds(4),
            workerRoot: WorkerTestEnvironment.StubRoot,
            moduleName: "stub_spawns_child");

        WorkerRunResult result = await RunAsync(request, options);

        Assert.Equal(WorkerOutcome.TimedOut, result.Outcome);

        string grandchild = request.OutputPath + ".grandchild";
        Assert.True(File.Exists(grandchild), "the helper process never ran, so this proves nothing");

        // If the tree had survived, the helper would keep rewriting this file.
        string first = await ReadWhenReadableAsync(grandchild);
        await Task.Delay(1500);
        string second = await ReadWhenReadableAsync(grandchild);

        Assert.Equal(first, second);
    }

    private static async Task<string> ReadWhenReadableAsync(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(path);
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
        }

        throw new IOException($"could not read {path}");
    }

    // -- recording has priority -----------------------------------------------------------

    [Fact]
    public async Task NoWorkerIsLaunchedAtAllWhileCaptureIsLive()
    {
        // Deliberately not a WorkerFact: the point is that nothing is launched, so this must
        // hold on a machine with no Python at all.
        WorkerLaunchOptions options = new()
        {
            PythonExecutable = @"C:\this\python\does\not\exist.exe",
            WorkerRoot = _temp.Path,
        };

        WorkerRunResult result = await new WorkerSupervisor(options, new BusyGate())
            .TranscribeAsync("job-1", WorkerProtocolTests.SmallRequest());

        Assert.Equal(WorkerOutcome.Busy, result.Outcome);
        Assert.Null(result.Error);
        Assert.Contains("recording", result.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMissingInterpreterIsAClearLaunchFailureRatherThanACrash()
    {
        WorkerLaunchOptions options = new()
        {
            PythonExecutable = Path.Combine(_temp.Path, "no-such-python.exe"),
            WorkerRoot = _temp.Path,
        };

        WorkerRunResult result = await new WorkerSupervisor(options)
            .TranscribeAsync("job-1", WorkerProtocolTests.SmallRequest());

        Assert.Equal(WorkerOutcome.LaunchFailed, result.Outcome);
        Assert.Contains("Python", result.UserMessage, StringComparison.Ordinal);
    }

    // -- what the user is allowed to see ----------------------------------------------------

    [WorkerFact]
    public async Task AFailureMessageShownToTheUserQuotesNoPathAndNoWorkerText()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path, backend: "faster-whisper");

        WorkerRunResult result = await RunAsync(request);

        Assert.Equal(WorkerOutcome.Failed, result.Outcome);
        Assert.Equal(WorkerErrorCodes.BackendUnavailable, result.Error!.Code);

        // The diagnostic may say anything; the sentence the user reads may not.
        string message = result.UserMessage;
        Assert.DoesNotContain(request.SessionRoot, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(request.OutputPath, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_temp.Path, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("faster-whisper", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Error.Detail ?? "<none>", message, StringComparison.Ordinal);
    }

    [WorkerFact]
    public async Task AMissingChunkIsReportedWithoutNamingTheFile()
    {
        TranscriptionRequest request = WorkerTestEnvironment.BuildSession(_temp.Path);
        File.Delete(Path.Combine(request.SessionRoot, "tracks", "microphone", "chunks", "000001.wav"));

        WorkerRunResult result = await RunAsync(request);

        Assert.Equal(WorkerOutcome.Failed, result.Outcome);
        Assert.Equal(WorkerErrorCodes.InputMissing, result.Error!.Code);
        Assert.Contains("Nothing has been changed or deleted", result.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("000001.wav", result.UserMessage, StringComparison.Ordinal);
    }

    private sealed class BusyGate : ICaptureActivityGate
    {
        public bool IsCaptureActive => true;
    }
}
