using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.UnitTests;

/// <summary>
/// The coordinator against the real worker.
///
/// <para>
/// Every case here runs a Python child. The whole point of the coordinator is what it does when
/// that child misbehaves, and a fake supervisor would only demonstrate that the fake behaves.
/// </para>
/// </summary>
public sealed class TranscriptionCoordinatorTests : IDisposable
{
    private const string SessionId = "01JCOORD";

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _sessions;
    private readonly FileTranscriptionStore _transcripts;
    private readonly SwitchableGate _gate = new();

    public TranscriptionCoordinatorTests()
    {
        _sessions = new FileSessionStore(_temp.Path);
        _transcripts = new FileTranscriptionStore(_sessions);
    }

    public void Dispose() => _temp.Dispose();

    private SessionSnapshot GivenARecordedSession(bool silent = false, SessionState state = SessionState.Recorded) =>
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId, silent: silent, state: state);

    private TranscriptionCoordinator NewCoordinator(
        TimeSpan? timeout = null,
        string? moduleName = null,
        TimeSpan? cancelGrace = null) =>
        new(
            _sessions,
            _transcripts,
            new WorkerSupervisor(WorkerTestEnvironment.Options(
                timeout: timeout,
                workerRoot: moduleName is null ? null : WorkerTestEnvironment.StubRoot,
                moduleName: moduleName,
                cancelGrace: cancelGrace)),
            _gate);

    private static TranscriptionOptions Mock(string? testMode = null, double? delay = null) => new()
    {
        Backend = WorkerProtocol.MockBackend,
        TestMode = testMode,
        TestDelaySeconds = delay,
    };

    // -- the round trip ---------------------------------------------------------------------

    [WorkerFact]
    public async Task ARecordedSessionBecomesAnActivatedTranscriptRevision()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        TranscriptionTicket ticket = coordinator.Request(SessionId, Mock());
        Assert.Equal(TranscriptionAcceptance.Started, ticket.Acceptance);

        TranscriptionRunResult result = await ticket.Completion;

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Revision);

        TranscriptionState state = coordinator.StateFor(SessionId);
        Assert.Equal(ProcessingStageState.Succeeded, state.Stage);
        Assert.Equal(1, state.SelectedRevision);

        TranscriptDocument? transcript = _transcripts.ReadTranscript(SessionId, 1);
        Assert.NotNull(transcript);
        Assert.True(TranscriptValidator.Validate(transcript!).IsValid);
        Assert.NotEmpty(transcript!.Segments);
    }

    [WorkerFact]
    public async Task TheActivatedRevisionRecordsWhatProducedItIncludingThatItRecognisesNoSpeech()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        await coordinator.Request(SessionId, Mock()).Completion;

        TranscriptRevisionRecord record = coordinator.StateFor(SessionId).Selected!;

        Assert.Equal("mock", record.Backend);
        Assert.False(record.RecognizesSpeech);
        Assert.Equal(WorkerProtocol.Version, record.ProtocolVersion);
        Assert.NotEmpty(record.WorkerVersion);
        Assert.Equal(64, record.SourceManifestSha256.Length);
        Assert.Equal(64, record.TranscriptSha256.Length);
    }

    [WorkerFact]
    public async Task TheResultSaysPlainlyThatThePlaceholderBackendRecognisesNoSpeech()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        TranscriptionRunResult result = await coordinator.Request(SessionId, Mock()).Completion;

        Assert.Contains("does not recognise speech", result.Message, StringComparison.Ordinal);
    }

    [WorkerFact]
    public async Task ProgressIsReportedAndPersisted()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        List<TranscriptionProgressEventArgs> updates = [];
        coordinator.ProgressChanged += (_, e) => { lock (updates) { updates.Add(e); } };

        await coordinator.Request(SessionId, Mock()).Completion;

        lock (updates)
        {
            Assert.NotEmpty(updates);

            int completed = 0;
            foreach (TranscriptionProgressEventArgs update in updates)
            {
                Assert.True(update.CompletedUnits >= completed);
                Assert.True(update.CompletedUnits <= update.TotalUnits);
                Assert.Equal(SessionId, update.SessionId);
                completed = update.CompletedUnits;
            }

            // Two chunks, one per track.
            Assert.Equal(2, updates[^1].TotalUnits);
        }
    }

    [WorkerFact]
    public async Task ARunningJobIsVisibleAsRunningWhileItRuns()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        TranscriptionTicket ticket = coordinator.Request(SessionId, Mock(WorkerTestModes.Delay, delay: 6));

        ProcessingStageState observed = ProcessingStageState.NotRequested;
        for (int attempt = 0; attempt < 60 && observed != ProcessingStageState.Running; attempt++)
        {
            await Task.Delay(100);
            observed = coordinator.StateFor(SessionId).CurrentJob?.State ?? ProcessingStageState.NotRequested;
        }

        Assert.Equal(ProcessingStageState.Running, observed);

        coordinator.Cancel();
        await ticket.Completion;
    }

    [WorkerFact]
    public async Task ReprocessingProducesASecondRevisionAndLeavesTheFirstAlone()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        await coordinator.Request(SessionId, Mock()).Completion;
        byte[] first = await File.ReadAllBytesAsync(_transcripts.RevisionPath(SessionId, 1));

        TranscriptionRunResult second = await coordinator.Request(SessionId, Mock()).Completion;

        Assert.True(second.Succeeded);
        Assert.Equal(2, second.Revision);
        Assert.Equal(2, coordinator.StateFor(SessionId).SelectedRevision);
        Assert.Equal(first, await File.ReadAllBytesAsync(_transcripts.RevisionPath(SessionId, 1)));
    }

    [WorkerFact]
    public async Task TranscribingLeavesTheSourceAudioExactlyAsItWas()
    {
        GivenARecordedSession();
        string root = _sessions.Resolve(SessionId).TracksRoot;
        DirectorySnapshot before = DirectorySnapshot.Capture(root);

        using TranscriptionCoordinator coordinator = NewCoordinator();
        await coordinator.Request(SessionId, Mock()).Completion;

        DirectorySnapshot after = DirectorySnapshot.Capture(root);
        Assert.True(after.Matches(before), after.Describe());
    }

    // -- failure modes -------------------------------------------------------------------------

    [WorkerFact]
    public async Task AWorkerCrashLeavesThePreviousRevisionSelected()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        await coordinator.Request(SessionId, Mock()).Completion;

        TranscriptionRunResult result = await coordinator.Request(SessionId, Mock(WorkerTestModes.Crash)).Completion;

        Assert.Equal(ProcessingStageState.Failed, result.State);
        Assert.Equal("worker_crashed", result.FailureCode);
        Assert.Equal(1, coordinator.StateFor(SessionId).SelectedRevision);
        Assert.False(File.Exists(_transcripts.RevisionPath(SessionId, 2)));
    }

    [WorkerFact]
    public async Task ATimeoutIsRecordedAsAFailureWithoutTouchingAnything()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator(timeout: TimeSpan.FromSeconds(4));

        TranscriptionRunResult result = await coordinator.Request(SessionId, Mock(WorkerTestModes.Hang)).Completion;

        Assert.Equal(ProcessingStageState.Failed, result.State);
        Assert.Equal("timeout", result.FailureCode);
        Assert.Null(coordinator.StateFor(SessionId).SelectedRevision);
    }

    [WorkerFact]
    public async Task AProtocolErrorIsDistinguishedFromAWorkerFailure()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator(moduleName: "stub_lying_result");

        TranscriptionRunResult result = await coordinator.Request(SessionId, Mock()).Completion;

        Assert.Equal(ProcessingStageState.Failed, result.State);
        Assert.Equal("protocol_error", result.FailureCode);
        Assert.Empty(coordinator.StateFor(SessionId).Revisions);
    }

    [WorkerFact]
    public async Task ATranscriptThatFailsValidationIsNeverActivated()
    {
        GivenARecordedSession();

        // A first, good revision, so the refusal has something to fail to replace.
        using (TranscriptionCoordinator good = NewCoordinator())
        {
            await good.Request(SessionId, Mock()).Completion;
        }

        using TranscriptionCoordinator coordinator = NewCoordinator(moduleName: "stub_invalid_transcript");
        TranscriptionRunResult result = await coordinator.Request(SessionId, Mock()).Completion;

        Assert.Equal(ProcessingStageState.Failed, result.State);
        Assert.Equal("transcript_invalid", result.FailureCode);

        TranscriptionState state = coordinator.StateFor(SessionId);
        Assert.Equal(1, state.SelectedRevision);
        Assert.Single(state.Revisions);
        Assert.False(File.Exists(_transcripts.RevisionPath(SessionId, 2)));
    }

    [WorkerFact]
    public async Task AFailureMessageNamesNoPathAndQuotesNoWorkerText()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        TranscriptionRunResult result = await coordinator.Request(SessionId, Mock(WorkerTestModes.Crash)).Completion;

        Assert.DoesNotContain(_temp.Path, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Traceback", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SessionId, result.Message, StringComparison.Ordinal);
    }

    // -- source verification ----------------------------------------------------------------------

    [Fact]
    public void AnUnknownSessionIsRefusedWithoutStartingAnything()
    {
        using TranscriptionCoordinator coordinator = NewCoordinator();

        TranscriptionTicket ticket = coordinator.Request("01JNOSUCHSESSION", Mock());

        Assert.Equal(TranscriptionAcceptance.Rejected, ticket.Acceptance);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public void AudioThatNoLongerMatchesItsRecordedDigestIsRefused()
    {
        GivenARecordedSession();

        // Something changed the file after it was finalized.
        string chunk = Path.Combine(_sessions.Resolve(SessionId).Root, "tracks", "microphone", "chunks", "000001.wav");
        byte[] bytes = File.ReadAllBytes(chunk);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(chunk, bytes);

        using TranscriptionCoordinator coordinator = NewCoordinator();
        TranscriptionTicket ticket = coordinator.Request(SessionId, Mock());

        Assert.Equal(TranscriptionAcceptance.Rejected, ticket.Acceptance);
        Assert.Contains("no longer matches", ticket.Message, StringComparison.Ordinal);

        // Nothing was journalled, because no attempt ever existed.
        Assert.Equal(ProcessingStageState.NotRequested, coordinator.StateFor(SessionId).Stage);
    }

    [Fact]
    public void MissingAudioIsRefused()
    {
        GivenARecordedSession();
        File.Delete(Path.Combine(_sessions.Resolve(SessionId).Root, "tracks", "system", "chunks", "000001.wav"));

        using TranscriptionCoordinator coordinator = NewCoordinator();
        TranscriptionTicket ticket = coordinator.Request(SessionId, Mock());

        Assert.Equal(TranscriptionAcceptance.Rejected, ticket.Acceptance);
        Assert.Contains("Nothing has been changed or deleted", ticket.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionThatHasNotFinishedSavingIsRefused()
    {
        GivenARecordedSession(state: SessionState.Finalizing);

        using TranscriptionCoordinator coordinator = NewCoordinator();
        TranscriptionTicket ticket = coordinator.Request(SessionId, Mock());

        Assert.Equal(TranscriptionAcceptance.Rejected, ticket.Acceptance);
        Assert.Contains("has not finished saving", ticket.Message, StringComparison.Ordinal);
    }

    // -- recording has priority -----------------------------------------------------------------

    [Fact]
    public void ARequestMadeWhileRecordingIsQueuedRatherThanRun()
    {
        GivenARecordedSession();
        _gate.IsCaptureActive = true;

        using TranscriptionCoordinator coordinator = NewCoordinator();
        TranscriptionTicket ticket = coordinator.Request(SessionId, Mock());

        Assert.Equal(TranscriptionAcceptance.Deferred, ticket.Acceptance);
        Assert.True(coordinator.IsQueued);
        Assert.False(coordinator.IsRunning);
        Assert.False(ticket.Completion.IsCompleted);

        // Durably queued, not merely remembered in memory.
        Assert.Equal(ProcessingStageState.Queued, coordinator.StateFor(SessionId).CurrentJob!.State);

        coordinator.Cancel();
    }

    [WorkerFact]
    public async Task AQueuedRequestRunsByItselfOnceRecordingStops()
    {
        GivenARecordedSession();
        _gate.IsCaptureActive = true;

        using TranscriptionCoordinator coordinator = NewCoordinator();
        TranscriptionTicket ticket = coordinator.Request(SessionId, Mock());
        Assert.Equal(TranscriptionAcceptance.Deferred, ticket.Acceptance);

        _gate.IsCaptureActive = false;
        coordinator.CaptureStateChanged();

        TranscriptionRunResult result = await ticket.Completion.WaitAsync(TimeSpan.FromMinutes(2));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, coordinator.StateFor(SessionId).SelectedRevision);
    }

    [WorkerFact]
    public async Task RecordingStartingMidJobStopsTheWorkerAndKeepsTheRequest()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        TranscriptionTicket ticket = coordinator.Request(SessionId, Mock(WorkerTestModes.Delay, delay: 5));
        Assert.Equal(TranscriptionAcceptance.Started, ticket.Acceptance);

        await Task.Delay(800);

        // The user starts recording. Recording wins.
        _gate.IsCaptureActive = true;
        coordinator.CaptureStateChanged();

        // The job is not lost: it goes back to waiting for the machine.
        for (int attempt = 0; attempt < 100 && coordinator.IsRunning; attempt++)
        {
            await Task.Delay(100);
        }

        Assert.False(coordinator.IsRunning);
        Assert.True(coordinator.IsQueued);
        Assert.False(ticket.Completion.IsCompleted);

        _gate.IsCaptureActive = false;
        coordinator.CaptureStateChanged();

        TranscriptionRunResult result = await ticket.Completion.WaitAsync(TimeSpan.FromMinutes(2));

        Assert.True(result.Succeeded, result.Message);

        // The abandoned attempt kept its own revision number rather than being reused, so the
        // journal can tell the two runs apart.
        Assert.Equal(2, result.Revision);
        Assert.False(File.Exists(_transcripts.RevisionPath(SessionId, 1)));
    }

    // -- cancellation and single-job enforcement ---------------------------------------------------

    [WorkerFact]
    public async Task CancellingLeavesTheRecordingAndAnyEarlierTranscriptUntouched()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        await coordinator.Request(SessionId, Mock()).Completion;

        TranscriptionTicket ticket = coordinator.Request(SessionId, Mock(WorkerTestModes.Delay, delay: 60));
        await Task.Delay(1000);
        coordinator.Cancel();

        TranscriptionRunResult result = await ticket.Completion.WaitAsync(TimeSpan.FromMinutes(2));

        Assert.Equal(ProcessingStageState.Cancelled, result.State);
        Assert.Equal(1, coordinator.StateFor(SessionId).SelectedRevision);
        Assert.False(File.Exists(_transcripts.RevisionPath(SessionId, 2)));
    }

    [Fact]
    public async Task CancellingAQueuedJobSettlesItWithoutEverStartingAWorker()
    {
        GivenARecordedSession();
        _gate.IsCaptureActive = true;

        using TranscriptionCoordinator coordinator = NewCoordinator();
        TranscriptionTicket ticket = coordinator.Request(SessionId, Mock());

        coordinator.Cancel();

        TranscriptionRunResult result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ProcessingStageState.Cancelled, result.State);
        Assert.False(coordinator.IsQueued);
    }

    [Fact]
    public void OnlyOneJobIsAcceptedAtATime()
    {
        GivenARecordedSession();
        _gate.IsCaptureActive = true;

        using TranscriptionCoordinator coordinator = NewCoordinator();
        Assert.Equal(TranscriptionAcceptance.Deferred, coordinator.Request(SessionId, Mock()).Acceptance);

        TranscriptionTicket second = coordinator.Request(SessionId, Mock());

        Assert.Equal(TranscriptionAcceptance.Busy, second.Acceptance);
        Assert.Contains("Only one runs at a time", second.Message, StringComparison.Ordinal);

        coordinator.Cancel();
    }

    [WorkerFact]
    public async Task AFailedJobCanBeRetriedImmediately()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        TranscriptionRunResult failed = await coordinator.Request(SessionId, Mock(WorkerTestModes.Crash)).Completion;
        Assert.Equal(ProcessingStageState.Failed, failed.State);

        // The coordinator is free again the moment the job settles.
        Assert.False(coordinator.IsRunning);
        Assert.False(coordinator.IsQueued);

        TranscriptionRunResult retried = await coordinator.Request(SessionId, Mock()).Completion;

        Assert.True(retried.Succeeded, retried.Message);
        Assert.Equal(2, retried.Revision);
    }

    [WorkerFact]
    public async Task NoWorkerSurvivesACompletedJob()
    {
        GivenARecordedSession();
        using TranscriptionCoordinator coordinator = NewCoordinator();

        await coordinator.Request(SessionId, Mock()).Completion;

        Assert.True(await coordinator.DrainAsync(TimeSpan.FromSeconds(5)));
        Assert.False(coordinator.IsRunning);
    }

    private sealed class SwitchableGate : ICaptureActivityGate
    {
        public bool IsCaptureActive { get; set; }
    }
}
