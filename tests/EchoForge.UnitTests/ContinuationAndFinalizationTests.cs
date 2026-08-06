using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// Recovery owns the lease, finalization survives a failed write, and an unfinished recording can
/// be continued rather than being declared finished on the user's behalf.
/// </summary>
public sealed class ContinuationAndFinalizationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FakePowerMonitor _power = new();
    private readonly FileSessionStore _store;
    private readonly FileSessionLeaseProvider _leases;

    public ContinuationAndFinalizationTests()
    {
        _store = new FileSessionStore(_temp.Path);
        _leases = new FileSessionLeaseProvider(_store);
    }

    public void Dispose() => _temp.Dispose();

    private RecordingController NewController(ISessionStore? store = null) =>
        new(store ?? _store, _engines, _clock, _disk, null, null, _power, _leases);

    private static RecordingRequest Request => new("render-id", "Headphones", "capture-id", "Microphone");

    private SessionRecoveryService Recovery(ISessionStore? store = null) =>
        new(store ?? _store, new FakeChunkRepairer(), null, _leases);

    // ---- 1: recovery owns the lease ----

    [Fact]
    public void RecoveryHoldsTheLeaseWhileItWorks()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        string sessionId = controller.SessionId!;
        controller.Stop();

        // Nobody holds it once the session has ended, and recovery takes it for its own run.
        Assert.False(_leases.IsLeased(sessionId));
        RecoveryOutcome outcome = Recovery().Recover(sessionId);

        Assert.False(outcome.Skipped);

        // It is released again afterwards, so a later run or a new recording can claim it.
        Assert.False(_leases.IsLeased(sessionId));
        using ISessionLease? after = _leases.TryAcquire(sessionId);
        Assert.NotNull(after);
    }

    [Fact]
    public void RecoveryAcquiresRatherThanMerelyCheckingSoAClaimCannotSlipIn()
    {
        const string Id = "contested";
        _store.Create(Id);
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));

        // Something else holds the lease. Acquisition — not a check — is what refuses recovery.
        using ISessionLease? held = _leases.TryAcquire(Id);
        Assert.NotNull(held);

        DirectorySnapshot before = DirectorySnapshot.Capture(_temp.Path);
        RecoveryOutcome outcome = Recovery().Recover(Id);

        Assert.True(outcome.Skipped);
        Assert.True(DirectorySnapshot.Capture(_temp.Path).Matches(before));
    }

    // ---- 2: finalization is failure-safe ----

    [Fact]
    public void AFailedTerminalJournalWriteBecomesNeedsAttentionAndStaysRetryable()
    {
        SwitchableStore store = new(_store);
        using RecordingController controller = NewController(store);
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        string sessionId = controller.SessionId!;
        store.FailTerminalWrites = true;
        controller.Stop();

        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.True(controller.NeedsReconciliation);
        Assert.Null(controller.EndedUtc);

        // Capture has stopped, so the lease must not be stranded even though saving failed.
        Assert.False(_leases.IsLeased(sessionId));

        // The retry succeeds and produces exactly one terminal event.
        store.FailTerminalWrites = false;
        controller.Stop();

        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.NotNull(controller.EndedUtc);

        JournalReadResult journal = _store.ReadJournal(sessionId);
        Assert.Single(journal.Events, e => e.Type == JournalEventTypes.SessionEnded);
    }

    [Fact]
    public void AFailedSnapshotWriteDoesNotRewriteADurableTerminalOutcome()
    {
        SwitchableStore store = new(_store);
        using RecordingController controller = NewController(store);
        controller.Start(Request);

        string sessionId = controller.SessionId!;
        store.FailSnapshotWrites = true;
        controller.Stop();

        // The terminal event landed as Recorded before the projection failed. That outcome is
        // now canonical, so the failure is a reconciliation warning rather than a new verdict.
        Assert.Equal(SessionState.Recorded, controller.State);
        Assert.True(controller.NeedsReconciliation);
        Assert.Null(controller.EndedUtc);

        JournalEvent afterFailure = Assert.Single(
            _store.ReadJournal(sessionId).Events, e => e.Type == JournalEventTypes.SessionEnded);
        Assert.Equal(nameof(SessionState.Recorded), afterFailure.Field("outcome"));

        // The retry writes the snapshot with that same outcome, never a contradictory one.
        store.FailSnapshotWrites = false;
        controller.Stop();

        Assert.Equal(SessionState.Recorded, controller.State);
        Assert.NotNull(controller.EndedUtc);

        JournalReadResult journal = _store.ReadJournal(sessionId);
        Assert.Single(journal.Events, e => e.Type == JournalEventTypes.SessionEnded);
        Assert.Equal(SessionState.Recorded, _store.ReadSnapshot(sessionId)!.State);
    }

    [Fact]
    public void TheIntendedEndTimeDoesNotDriftAcrossRetries()
    {
        SwitchableStore store = new(_store);
        using RecordingController controller = NewController(store);
        controller.Start(Request);

        store.FailTerminalWrites = true;
        controller.Stop();

        _clock.Advance(TimeSpan.FromHours(4));
        store.FailTerminalWrites = false;
        controller.Stop();

        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        JournalEvent terminal = Assert.Single(journal.Events, e => e.Type == JournalEventTypes.SessionEnded);

        // The recorded end time is when the session actually ended, not when the retry happened.
        Assert.Equal(terminal.TimestampUtc, controller.EndedUtc);
        Assert.True(controller.EndedUtc < _clock.UtcNow() - TimeSpan.FromHours(3));
    }

    [Fact]
    public void RecoverySettlesASessionWhoseFinalizationNeverCompleted()
    {
        SwitchableStore store = new(_store);
        RecordingController controller = NewController(store);
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        string sessionId = controller.SessionId!;
        store.FailTerminalWrites = true;
        controller.Stop();
        controller.Dispose();

        // No terminal event was ever written, so recovery treats it as unfinished rather than
        // inventing an outcome.
        RecoveryOutcome outcome = Recovery().Recover(sessionId);
        Assert.Equal(SessionState.Paused, outcome.State);
        Assert.NotNull(RecoveryCandidate.From(_store.ReadSnapshot(sessionId)!));
    }

    // ---- 3: continuation ----

    /// <summary>Records, pauses or suspends, then abandons the controller without stopping.</summary>
    private string BuildUnfinishedSession(bool suspend)
    {
        RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        _engines.Latest.EmitChunk(SourceTrack.System);
        _clock.Advance(TimeSpan.FromMinutes(2));

        if (suspend)
        {
            _power.Suspend();
            controller.WaitForSignals(TimeSpan.FromSeconds(5));
        }
        else
        {
            controller.Pause();
        }

        string sessionId = controller.SessionId!;
        controller.FlushPendingWrites(TimeSpan.FromSeconds(5));

        // Release the lease without finalizing, as a killed process would.
        typeof(RecordingController)
            .GetMethod("ReleaseLease", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(controller, null);

        return sessionId;
    }

    [Fact]
    public void AManuallyPausedSessionIsOfferedForContinuationNotDeclaredFinished()
    {
        string sessionId = BuildUnfinishedSession(suspend: false);

        RecoveryOutcome outcome = Recovery().Recover(sessionId);
        Assert.Equal(SessionState.Paused, outcome.State);

        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());
        Assert.Equal(sessionId, candidate.SessionId);
        Assert.Equal(SessionContinuationReason.ManuallyPaused, candidate.Reason);
        Assert.Equal(2, candidate.ChunkCount);
        Assert.Equal("render-id", candidate.RenderEndpointId);
        Assert.Equal("capture-id", candidate.CaptureEndpointId);
        Assert.Equal(2, candidate.NextEpochIndex);
        Assert.Equal(3, candidate.NextChunkIndex);
    }

    [Fact]
    public void ASuspendedSessionIsRecognisedAsSuchAndDescribedProperly()
    {
        string sessionId = BuildUnfinishedSession(suspend: true);
        Recovery().Recover(sessionId);

        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        Assert.Equal(SessionContinuationReason.Suspended, candidate.Reason);
        Assert.Contains("sleep", candidate.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASessionInterruptedWhileRecordingIsAlsoResumable()
    {
        const string Id = "crashed";
        _store.Create(Id);
        DateTimeOffset t = DateTimeOffset.UnixEpoch;

        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionCreated, t, ("session_id", Id)));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.EpochStarted, t,
            ("epoch", "1"), ("start_qpc", "1"), ("first_chunk_index", "1")));
        foreach (string track in new[] { "Microphone", "System" })
        {
            _store.Append(Id, JournalEvent.Create(JournalEventTypes.TrackOpened, t,
                ("track", track), ("device_id", track == "Microphone" ? "capture-id" : "render-id"),
                ("device_name", track), ("sample_rate", "48000"), ("channels", "2"), ("epoch", "1")));
        }

        _store.Append(Id, JournalEvent.Create(JournalEventTypes.ChunkCompleted, t,
            ("track", "Microphone"), ("index", "1"), ("epoch", "1"), ("frames", "48000"),
            ("start_seconds", "0"), ("sample_rate", "48000"), ("channels", "2"), ("sha256", "h")));

        Recovery().Recover(Id);
        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        Assert.Equal(SessionContinuationReason.InterruptedWhileRecording, candidate.Reason);
        Assert.Equal(2, candidate.NextEpochIndex);
        Assert.Equal(2, candidate.NextChunkIndex);
    }

    [Fact]
    public void AdoptingThenResumingContinuesTheSessionWithNewChunkNumbers()
    {
        string sessionId = BuildUnfinishedSession(suspend: false);
        Recovery().Recover(sessionId);
        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        using RecordingController continued = NewController();
        continued.AdoptRecoveredSession(candidate);

        // Adopted, not started: nothing is capturing until the user says so.
        Assert.Equal(SessionState.Paused, continued.State);
        Assert.Equal(sessionId, continued.SessionId);
        Assert.False(continued.CaptureMayBeLive);
        Assert.Equal(2, continued.Snapshot().Tracks.Sum(t => t.Chunks.Count));

        continued.Resume();

        Assert.Equal(SessionState.Recording, continued.State);
        Assert.Equal(2, continued.Epochs.Count);

        // The new epoch writes new files; it never reuses an index a finalized chunk holds.
        Assert.Equal(3, _engines.Latest.Request.FirstChunkIndex);
        Assert.Equal(2, _engines.Latest.Request.EpochIndex);

        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        continued.Stop();

        // Old chunks survive alongside the new one, under one session.
        SessionSnapshot final = _store.ReadSnapshot(sessionId)!;
        Assert.Equal(3, final.Tracks.Sum(t => t.Chunks.Count));
        Assert.Equal([1, 2, 3], final.Tracks.SelectMany(t => t.Chunks).Select(c => c.Index).Order());
        Assert.Equal(SessionState.Recorded, final.State);
    }

    [Fact]
    public void FinalizingInsteadOfResumingEndsTheSessionNormally()
    {
        string sessionId = BuildUnfinishedSession(suspend: false);
        Recovery().Recover(sessionId);
        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        using RecordingController continued = NewController();
        continued.AdoptRecoveredSession(candidate);
        continued.Stop();

        Assert.Equal(SessionState.Recorded, continued.State);
        Assert.NotNull(continued.EndedUtc);

        JournalReadResult journal = _store.ReadJournal(sessionId);
        JournalEvent terminal = Assert.Single(journal.Events, e => e.Type == JournalEventTypes.SessionEnded);
        Assert.Equal(nameof(SessionState.Recorded), terminal.Field("outcome"));

        // The audio it already had is still there, and it is no longer offered for continuation.
        Assert.Equal(2, _store.ReadSnapshot(sessionId)!.Tracks.Sum(t => t.Chunks.Count));
        Assert.Empty(Recovery().FindContinuationCandidates());
    }

    [Fact]
    public void OnlyOneOwnerMayAdoptASession()
    {
        string sessionId = BuildUnfinishedSession(suspend: false);
        Recovery().Recover(sessionId);
        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        using RecordingController first = NewController();
        first.AdoptRecoveredSession(candidate);

        using RecordingController second = NewController();
        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => second.AdoptRecoveredSession(candidate));

        Assert.Contains("already open", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SessionState.New, second.State);
    }

    [Fact]
    public void AMissingEndpointLeavesTheAdoptedSessionPausedWithItsReason()
    {
        string sessionId = BuildUnfinishedSession(suspend: false);
        Recovery().Recover(sessionId);
        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        using RecordingController continued = NewController();
        continued.AdoptRecoveredSession(candidate);

        // The endpoint is gone, so opening the new epoch fails.
        _engines.FailNextStart = true;
        Assert.Throws<InvalidOperationException>(continued.Resume);

        Assert.Equal(SessionState.Paused, continued.State);
        Assert.Single(continued.Epochs);
        Assert.Null(continued.EndedUtc);

        // Still continuable once the device comes back.
        continued.Resume();
        Assert.Equal(SessionState.Recording, continued.State);
    }

    [Fact]
    public void AFinishedSessionIsNeverOfferedForContinuation()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        controller.Stop();

        Assert.Empty(Recovery().FindContinuationCandidates());
        Assert.Null(RecoveryCandidate.From(_store.ReadSnapshot(controller.SessionId!)!));
    }

    [Fact]
    public void RecoveryAfterAContinuedSessionEndsFindsNothingLeftToDo()
    {
        string sessionId = BuildUnfinishedSession(suspend: false);
        Recovery().Recover(sessionId);
        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        using (RecordingController continued = NewController())
        {
            continued.AdoptRecoveredSession(candidate);
            continued.Resume();
            _engines.Latest.EmitChunk(SourceTrack.System);
            continued.Stop();
        }

        RecoveryOutcome outcome = Recovery().Recover(sessionId);

        Assert.Equal(SessionState.Recorded, outcome.State);
        Assert.False(outcome.ChangedAnything);
        Assert.Empty(Recovery().FindContinuationCandidates());
    }

    /// <summary>A store whose terminal and snapshot writes can be failed independently.</summary>
    private sealed class SwitchableStore(ISessionStore inner) : ISessionStore
    {
        private readonly ISessionStore _inner = inner;

        public bool FailTerminalWrites { get; set; }

        public bool FailSnapshotWrites { get; set; }

        public SessionPaths Create(string sessionId) => _inner.Create(sessionId);

        public SessionPaths Resolve(string sessionId) => _inner.Resolve(sessionId);

        public void Append(string sessionId, JournalEvent journalEvent)
        {
            if (FailTerminalWrites && journalEvent.Type == JournalEventTypes.SessionEnded)
            {
                throw new IOException("simulated terminal write failure");
            }

            _inner.Append(sessionId, journalEvent);
        }

        public JournalReadResult ReadJournal(string sessionId) => _inner.ReadJournal(sessionId);

        public void WriteSnapshot(SessionSnapshot snapshot)
        {
            if (FailSnapshotWrites)
            {
                throw new IOException("simulated snapshot write failure");
            }

            _inner.WriteSnapshot(snapshot);
        }

        public SessionSnapshot? ReadSnapshot(string sessionId) => _inner.ReadSnapshot(sessionId);

        public IReadOnlyList<string> EnumerateSessions() => _inner.EnumerateSessions();
    }
}
