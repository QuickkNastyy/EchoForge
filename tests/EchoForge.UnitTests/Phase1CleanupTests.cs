using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// The four bounded defects found in the Phase 1 review: the lease being dropped mid-save, a
/// retry contradicting a durable outcome, an interrupted epoch left open across continuation, and
/// adoption mutating state before it could fail.
/// </summary>
public sealed class Phase1CleanupTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FileSessionStore _store;
    private readonly FileSessionLeaseProvider _leases;

    public Phase1CleanupTests()
    {
        _store = new FileSessionStore(_temp.Path);
        _leases = new FileSessionLeaseProvider(_store);
    }

    public void Dispose() => _temp.Dispose();

    private RecordingController NewController(ISessionStore? store = null) =>
        new(store ?? _store, _engines, _clock, _disk, null, null, null, _leases);

    private static RecordingRequest Request => new("render-id", "Headphones", "capture-id", "Microphone");

    private SessionRecoveryService Recovery() => new(_store, new FakeChunkRepairer(), null, _leases);

    // ---- 1: the lease covers the whole finalization attempt ----

    [Fact]
    public void TheLeaseIsStillHeldWhileTheTerminalWritesHappen()
    {
        LeaseObservingStore store = new(_store, _leases);
        using RecordingController controller = NewController(store);
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        store.WatchSessionId = controller.SessionId;
        controller.Stop();

        // Recovery must not be able to slip in between the terminal event and the snapshot.
        Assert.True(store.LeaseHeldDuringTerminalAppend);
        Assert.True(store.LeaseHeldDuringSnapshotWrite);

        // And it is released once the attempt is over.
        Assert.False(_leases.IsLeased(controller.SessionId!));
    }

    [Fact]
    public void ARetryReacquiresTheLeaseBeforeWriting()
    {
        SwitchableStore store = new(_store);
        using RecordingController controller = NewController(store);
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        string sessionId = controller.SessionId!;
        store.FailTerminalWrites = true;
        controller.Stop();

        // The failed attempt released the lease, so nothing is stranded.
        Assert.False(_leases.IsLeased(sessionId));

        // Someone else holds it: the retry must refuse to write rather than race them.
        ISessionLease? intruder = _leases.TryAcquire(sessionId);
        Assert.NotNull(intruder);

        store.FailTerminalWrites = false;
        controller.Stop();

        Assert.Null(controller.EndedUtc);
        Assert.DoesNotContain(
            _store.ReadJournal(sessionId).Events, e => e.Type == JournalEventTypes.SessionEnded);

        // Once they release it, the retry succeeds.
        intruder.Dispose();
        controller.Stop();

        Assert.NotNull(controller.EndedUtc);
        Assert.Single(_store.ReadJournal(sessionId).Events, e => e.Type == JournalEventTypes.SessionEnded);
    }

    // ---- 2: a durable terminal outcome is immutable ----

    [Fact]
    public void ASnapshotRetryUsesTheOutcomeAlreadyRecordedInTheJournal()
    {
        SwitchableStore store = new(_store);
        using RecordingController controller = NewController(store);
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        string sessionId = controller.SessionId!;
        store.FailSnapshotWrites = true;
        controller.Stop();

        store.FailSnapshotWrites = false;
        controller.Stop();

        JournalEvent terminal = Assert.Single(
            _store.ReadJournal(sessionId).Events, e => e.Type == JournalEventTypes.SessionEnded);

        // Controller, journal, snapshot, and recovery all say the same thing.
        Assert.Equal(nameof(SessionState.Recorded), terminal.Field("outcome"));
        Assert.Equal(SessionState.Recorded, controller.State);
        Assert.Equal(SessionState.Recorded, _store.ReadSnapshot(sessionId)!.State);
        Assert.Equal(SessionState.Recorded, Recovery().Recover(sessionId).State);

        // The projection failure is still reported, just not as a different verdict.
        Assert.True(controller.NeedsReconciliation);
    }

    [Fact]
    public void WhenTheTerminalAppendItselfFailedTheEventualOutcomeMayBeNeedsAttention()
    {
        SwitchableStore store = new(_store);
        using RecordingController controller = NewController(store);
        controller.Start(Request);

        string sessionId = controller.SessionId!;
        store.FailTerminalWrites = true;
        controller.Stop();

        // Nothing durable was written, so the outcome is still open to being downgraded.
        Assert.DoesNotContain(
            _store.ReadJournal(sessionId).Events, e => e.Type == JournalEventTypes.SessionEnded);

        store.FailTerminalWrites = false;
        controller.Stop();

        JournalEvent terminal = Assert.Single(
            _store.ReadJournal(sessionId).Events, e => e.Type == JournalEventTypes.SessionEnded);

        Assert.Equal(nameof(SessionState.NeedsAttention), terminal.Field("outcome"));
        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.Equal(SessionState.NeedsAttention, Recovery().Recover(sessionId).State);
    }

    // ---- 3: interrupted epochs are closed before continuation ----

    [Fact]
    public void AnEpochLeftOpenByACrashIsClosedBeforeTheContinuationEpochOpens()
    {
        string sessionId = BuildCrashedSession();
        Recovery().Recover(sessionId);
        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        Assert.Equal(SessionContinuationReason.InterruptedWhileRecording, candidate.Reason);
        Assert.Null(candidate.Epochs[^1].EndedUtc);

        // The app restarts some time after the crash.
        _clock.Advance(TimeSpan.FromMinutes(10));

        using RecordingController continued = NewController();
        continued.AdoptRecoveredSession(candidate);

        // The open epoch is closed at the last durable audio boundary, not left hanging.
        SessionEpoch closed = Assert.Single(continued.Epochs);
        Assert.NotNull(closed.EndedUtc);
        Assert.Equal(EpochEndReason.Interrupted, closed.EndReason);
        Assert.Equal(TimeSpan.FromSeconds(60), closed.EndedUtc!.Value - closed.StartedUtc);

        continued.Resume();

        // Two epochs, and only the new one is open.
        Assert.Equal(2, continued.Epochs.Count);
        Assert.Single(continued.Epochs, e => e.EndedUtc is null);
        Assert.Equal(2, continued.Epochs[1].Index);

        // The gap between them survives.
        Assert.True(continued.Epochs[1].StartedUtc >= continued.Epochs[0].EndedUtc!.Value);

        JournalReadResult journal = _store.ReadJournal(sessionId);
        Assert.Contains(journal.Events, e =>
            e.Type == JournalEventTypes.EpochEnded && e.Field("reason") == nameof(EpochEndReason.Interrupted));
    }

    // ---- 4: adoption is transactional and restores totals ----

    [Fact]
    public void ARefusedLeaseLeavesTheControllerCompletelyUnchanged()
    {
        string sessionId = BuildCrashedSession();
        Recovery().Recover(sessionId);
        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        using RecordingController first = NewController();
        first.AdoptRecoveredSession(candidate);

        using RecordingController second = NewController();
        Assert.Throws<InvalidOperationException>(() => second.AdoptRecoveredSession(candidate));

        // Untouched: no session, no epochs, no state change.
        Assert.Equal(SessionState.New, second.State);
        Assert.Null(second.SessionId);
        Assert.Empty(second.Epochs);

        // And still usable for an ordinary recording.
        second.Start(Request);
        Assert.Equal(SessionState.Recording, second.State);
        second.Stop();
    }

    [Fact]
    public void AFailedAdoptionWriteReleasesTheLeaseAndLeavesTheControllerUsable()
    {
        string sessionId = BuildCrashedSession();
        Recovery().Recover(sessionId);
        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        SwitchableStore store = new(_store) { FailAdoptionWrites = true };
        using RecordingController controller = NewController(store);

        Assert.Throws<InvalidOperationException>(() => controller.AdoptRecoveredSession(candidate));

        Assert.Equal(SessionState.New, controller.State);
        Assert.Null(controller.SessionId);
        Assert.Empty(controller.Epochs);

        // The lease was handed back, so a later attempt can take it.
        Assert.False(_leases.IsLeased(sessionId));

        store.FailAdoptionWrites = false;
        controller.AdoptRecoveredSession(candidate);
        Assert.Equal(SessionState.Paused, controller.State);
    }

    [Fact]
    public void ContinuedTotalsEqualTheHistoryPlusTheNewCapture()
    {
        string sessionId = BuildCrashedSession();
        Recovery().Recover(sessionId);
        RecoveryCandidate candidate = Assert.Single(Recovery().FindContinuationCandidates());

        using RecordingController continued = NewController();
        continued.AdoptRecoveredSession(candidate);

        // Immediately after adoption the totals describe what was already recorded.
        SessionTotals adopted = continued.Totals();
        Assert.Equal(2, adopted.ChunkCount);
        Assert.Equal(TimeSpan.FromSeconds(60), adopted.ActiveDuration);
        Assert.True(adopted.BytesWritten > 0);

        long historicalBytes = adopted.BytesWritten;

        continued.Resume();
        _clock.Advance(TimeSpan.FromSeconds(30));
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        SessionTotals after = continued.Totals();

        // History plus new capture, never a restart from zero.
        Assert.Equal(3, after.ChunkCount);
        Assert.Equal(TimeSpan.FromSeconds(90), after.ActiveDuration);
        Assert.True(after.BytesWritten > historicalBytes);
        Assert.Equal(2, after.ChunksPerTrack[SourceTrack.Microphone]);
        Assert.Equal(1, after.ChunksPerTrack[SourceTrack.System]);

        continued.Stop();
        Assert.Equal(3, _store.ReadSnapshot(sessionId)!.Tracks.Sum(t => t.Chunks.Count));
    }

    /// <summary>A session whose process died with its first epoch still open.</summary>
    private string BuildCrashedSession()
    {
        const string Id = "crashed-open";
        _store.Create(Id);
        DateTimeOffset t = new(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);

        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionCreated, t, ("session_id", Id)));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.EpochStarted, t,
            ("epoch", "1"), ("start_qpc", "1"), ("first_chunk_index", "1")));

        foreach ((string track, string device) in new[] { ("Microphone", "capture-id"), ("System", "render-id") })
        {
            _store.Append(Id, JournalEvent.Create(JournalEventTypes.TrackOpened, t,
                ("track", track), ("device_id", device), ("device_name", track),
                ("sample_rate", "48000"), ("channels", "2"), ("epoch", "1")));

            _store.Append(Id, JournalEvent.Create(JournalEventTypes.ChunkCompleted, t.AddSeconds(60),
                ("track", track), ("index", track == "Microphone" ? "1" : "2"), ("epoch", "1"),
                ("frames", (48_000 * 60).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("start_seconds", "0"), ("sample_rate", "48000"), ("channels", "2"),
                ("sha256", $"hash-{track}")));
        }

        return Id;
    }

    /// <summary>Records whether the session lease was held while each terminal write happened.</summary>
    private sealed class LeaseObservingStore(ISessionStore inner, ISessionLeaseProvider leases) : ISessionStore
    {
        private readonly ISessionStore _inner = inner;
        private readonly ISessionLeaseProvider _leases = leases;

        public string? WatchSessionId { get; set; }

        public bool LeaseHeldDuringTerminalAppend { get; private set; }

        public bool LeaseHeldDuringSnapshotWrite { get; private set; }

        public SessionPaths Create(string sessionId) => _inner.Create(sessionId);

        public SessionPaths Resolve(string sessionId) => _inner.Resolve(sessionId);

        public void Append(string sessionId, JournalEvent journalEvent)
        {
            if (journalEvent.Type == JournalEventTypes.SessionEnded && sessionId == WatchSessionId)
            {
                LeaseHeldDuringTerminalAppend = _leases.IsLeased(sessionId);
            }

            _inner.Append(sessionId, journalEvent);
        }

        public JournalReadResult ReadJournal(string sessionId) => _inner.ReadJournal(sessionId);

        public void WriteSnapshot(SessionSnapshot snapshot)
        {
            if (snapshot.SessionId == WatchSessionId && snapshot.EndedUtc is not null)
            {
                LeaseHeldDuringSnapshotWrite = _leases.IsLeased(snapshot.SessionId);
            }

            _inner.WriteSnapshot(snapshot);
        }

        public SessionSnapshot? ReadSnapshot(string sessionId) => _inner.ReadSnapshot(sessionId);

        public IReadOnlyList<string> EnumerateSessions() => _inner.EnumerateSessions();
    }

    /// <summary>A store whose terminal, snapshot, and adoption writes can each be failed.</summary>
    private sealed class SwitchableStore(ISessionStore inner) : ISessionStore
    {
        private readonly ISessionStore _inner = inner;

        public bool FailTerminalWrites { get; set; }

        public bool FailSnapshotWrites { get; set; }

        public bool FailAdoptionWrites { get; set; }

        public SessionPaths Create(string sessionId) => _inner.Create(sessionId);

        public SessionPaths Resolve(string sessionId) => _inner.Resolve(sessionId);

        public void Append(string sessionId, JournalEvent journalEvent)
        {
            if (FailTerminalWrites && journalEvent.Type == JournalEventTypes.SessionEnded)
            {
                throw new IOException("simulated terminal write failure");
            }

            if (FailAdoptionWrites && journalEvent.Type == JournalEventTypes.SessionAdopted)
            {
                throw new IOException("simulated adoption write failure");
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
