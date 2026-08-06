using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Recording;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// A session ends exactly one way, and everyone must agree about which.
///
/// <para>
/// The failure paths used to call the stop path — which persisted <c>Recorded</c> — and then
/// change only in-memory state to <c>Failed</c>. The snapshot on disk, the journal, and what the
/// user had been told all disagreed, and recovery later reconstructed the wrong answer.
/// </para>
/// </summary>
public sealed class TerminalOutcomeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FakeEndpointMonitor _devices = new();
    private readonly FileSessionStore _store;

    public TerminalOutcomeTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private RecordingController NewController() =>
        new(_store, _engines, _clock, _disk, null, _devices, null);

    private static RecordingRequest Request => new("render-id", "Headphones", "capture-id", "Microphone");

    /// <summary>
    /// Asserts the controller, the journal, the snapshot, and recovery all report one outcome.
    /// </summary>
    private void AssertEveryoneAgrees(RecordingController controller, SessionState expected)
    {
        string sessionId = controller.SessionId!;
        Assert.True(controller.FlushPendingWrites(TimeSpan.FromSeconds(5)));

        // 1. In-memory state.
        Assert.Equal(expected, controller.State);

        // 2. Immutable end time, assigned exactly once.
        DateTimeOffset? ended = controller.EndedUtc;
        Assert.NotNull(ended);
        _clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(ended, controller.EndedUtc);
        Assert.Equal(ended, controller.Snapshot().EndedUtc);

        // 3. Exactly one terminal journal event, carrying the outcome.
        JournalReadResult journal = _store.ReadJournal(sessionId);
        JournalEvent terminal = Assert.Single(journal.Events, e => e.Type == JournalEventTypes.SessionEnded);
        Assert.Equal(expected.ToString(), terminal.Field("outcome"));

        // 4. Persisted snapshot.
        SessionSnapshot? persisted = _store.ReadSnapshot(sessionId);
        Assert.NotNull(persisted);
        Assert.Equal(expected, persisted.State);
        Assert.Equal(ended, persisted.EndedUtc);

        // 5. Recovery reconstructs the same answer.
        RecoveryOutcome recovered = new SessionRecoveryService(_store, new FakeChunkRepairer()).Recover(sessionId);
        Assert.Equal(expected, recovered.State);
        Assert.Equal(expected, _store.ReadSnapshot(sessionId)!.State);
    }

    [Fact]
    public void ManualStopIsRecorded()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        controller.Stop();

        AssertEveryoneAgrees(controller, SessionState.Recorded);
    }

    [Fact]
    public void DiskProtectionStopIsRecorded()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        _disk.Available = 1_000_000_000;
        controller.Poll();

        AssertEveryoneAgrees(controller, SessionState.Recorded);
    }

    [Fact]
    public void ApplicationShutdownIsRecorded()
    {
        RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        string sessionId = controller.SessionId!;

        controller.Dispose();

        JournalReadResult journal = _store.ReadJournal(sessionId);
        JournalEvent terminal = Assert.Single(journal.Events, e => e.Type == JournalEventTypes.SessionEnded);
        Assert.Equal(nameof(SessionState.Recorded), terminal.Field("outcome"));

        SessionSnapshot persisted = _store.ReadSnapshot(sessionId)!;
        Assert.Equal(SessionState.Recorded, persisted.State);
        Assert.NotNull(persisted.EndedUtc);

        RecoveryOutcome recovered = new SessionRecoveryService(_store, new FakeChunkRepairer()).Recover(sessionId);
        Assert.Equal(SessionState.Recorded, recovered.State);
    }

    [Fact]
    public void LosingBothEndpointsIsFailedEverywhere()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        _devices.Lose("capture-id");
        _devices.Lose("render-id");

        // This is the case that used to persist Recorded while reporting Failed.
        AssertEveryoneAgrees(controller, SessionState.Failed);
    }

    [Fact]
    public void LosingBothCaptureTracksIsFailedEverywhere()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        _engines.Latest.FailTrack(SourceTrack.Microphone, "gone");
        _engines.Latest.FailTrack(SourceTrack.System, "gone");
        controller.Poll();

        AssertEveryoneAgrees(controller, SessionState.Failed);
    }

    [Fact]
    public void AFailedStartIsFailedEverywhere()
    {
        using RecordingController controller = NewController();
        _engines.FailNextStart = true;

        Assert.Throws<InvalidOperationException>(() => controller.Start(Request));

        AssertEveryoneAgrees(controller, SessionState.Failed);
    }

    [Fact]
    public void AJournalPersistenceFailureDowngradesTheOutcomeToNeedsAttention()
    {
        FailingSessionStore failing = new(_store);
        using RecordingController controller = new(failing, _engines, _clock, _disk);

        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        // Break the ledger while the audio keeps being written.
        failing.FailQueuedWrites = true;
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        controller.FlushPendingWrites(TimeSpan.FromSeconds(5));

        controller.Stop();

        Assert.True(controller.NeedsReconciliation);
        Assert.Equal(SessionState.NeedsAttention, controller.State);

        string sessionId = controller.SessionId!;
        JournalReadResult journal = _store.ReadJournal(sessionId);
        JournalEvent terminal = Assert.Single(journal.Events, e => e.Type == JournalEventTypes.SessionEnded);
        Assert.Equal(nameof(SessionState.NeedsAttention), terminal.Field("outcome"));

        Assert.Equal(SessionState.NeedsAttention, _store.ReadSnapshot(sessionId)!.State);
    }

    [Fact]
    public void TheTerminalOutcomeIsWrittenOnlyOnceHoweverManyTimesStopIsCalled()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        controller.Stop();
        DateTimeOffset? first = controller.EndedUtc;

        _clock.Advance(TimeSpan.FromMinutes(30));
        controller.Stop();
        controller.Stop();
        controller.Dispose();

        Assert.Equal(first, controller.EndedUtc);

        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        Assert.Single(journal.Events, e => e.Type == JournalEventTypes.SessionEnded);
    }

    [Fact]
    public void AnInterruptedSessionWithNoTerminalEventStaysDistinctFromAFinishedOne()
    {
        // No terminal event at all: the process died. Audio that survived is still usable.
        const string Id = "interrupted";
        _store.Create(Id);
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.TrackOpened, DateTimeOffset.UnixEpoch,
            ("track", "Microphone"), ("sample_rate", "48000"), ("channels", "2")));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.ChunkCompleted, DateTimeOffset.UnixEpoch,
            ("track", "Microphone"), ("index", "1"), ("epoch", "1"), ("frames", "48000"),
            ("start_seconds", "0"), ("sample_rate", "48000"), ("channels", "2"), ("sha256", "h")));

        RecoveryOutcome outcome = new SessionRecoveryService(_store, new FakeChunkRepairer()).Recover(Id);

        Assert.Equal(SessionState.Recorded, outcome.State);
        Assert.Null(_store.ReadSnapshot(Id)!.EndedUtc);
    }
}

/// <summary>A store whose queued writes can be made to fail on demand.</summary>
public sealed class FailingSessionStore(ISessionStore inner) : ISessionStore
{
    private readonly ISessionStore _inner = inner;

    public bool FailQueuedWrites { get; set; }

    public SessionPaths Create(string sessionId) => _inner.Create(sessionId);

    public SessionPaths Resolve(string sessionId) => _inner.Resolve(sessionId);

    public void Append(string sessionId, JournalEvent journalEvent)
    {
        // Chunk events travel through the persistence queue; terminal events are written
        // synchronously, so only the former are broken here.
        if (FailQueuedWrites && journalEvent.Type == JournalEventTypes.ChunkCompleted)
        {
            throw new IOException("simulated journal failure");
        }

        _inner.Append(sessionId, journalEvent);
    }

    public JournalReadResult ReadJournal(string sessionId) => _inner.ReadJournal(sessionId);

    public void WriteSnapshot(SessionSnapshot snapshot) => _inner.WriteSnapshot(snapshot);

    public SessionSnapshot? ReadSnapshot(string sessionId) => _inner.ReadSnapshot(sessionId);

    public IReadOnlyList<string> EnumerateSessions() => _inner.EnumerateSessions();
}
