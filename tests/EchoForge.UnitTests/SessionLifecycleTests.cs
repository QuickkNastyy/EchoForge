using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// Cumulative totals, an immutable end timestamp, and clean failed starts.
/// </summary>
public sealed class SessionLifecycleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FileSessionStore _store;

    public SessionLifecycleTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private RecordingController NewController() => new(_store, _engines, _clock, _disk);

    private static RecordingRequest Request => new("render-id", "Headphones", "capture-id", "Microphone");

    [Fact]
    public void ElapsedAndChunkTotalsDoNotResetAcrossPauseAndResume()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        _clock.Advance(TimeSpan.FromMinutes(3));
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        _engines.Latest.EmitChunk(SourceTrack.System);

        SessionTotals whileRecording = controller.Totals();
        Assert.Equal(TimeSpan.FromMinutes(3), whileRecording.ActiveDuration);
        Assert.Equal(2, whileRecording.ChunkCount);

        controller.Pause();

        // Paused totals stay put: they neither reset nor keep climbing.
        SessionTotals atPause = controller.Totals();
        Assert.Equal(TimeSpan.FromMinutes(3), atPause.ActiveDuration);
        Assert.Equal(2, atPause.ChunkCount);

        _clock.Advance(TimeSpan.FromMinutes(10));
        SessionTotals stillPaused = controller.Totals();
        Assert.Equal(TimeSpan.FromMinutes(3), stillPaused.ActiveDuration);
        Assert.Equal(2, stillPaused.ChunkCount);

        controller.Resume();
        _clock.Advance(TimeSpan.FromMinutes(2));
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        SessionTotals afterResume = controller.Totals();
        Assert.Equal(TimeSpan.FromMinutes(5), afterResume.ActiveDuration);
        Assert.Equal(3, afterResume.ChunkCount);

        controller.Stop();

        SessionTotals afterStop = controller.Totals();
        Assert.Equal(TimeSpan.FromMinutes(5), afterStop.ActiveDuration);
        Assert.Equal(3, afterStop.ChunkCount);
    }

    [Fact]
    public void PauseGapsAreReportedSeparatelyFromRecordedTime()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _clock.Advance(TimeSpan.FromMinutes(1));
        controller.Pause();
        _clock.Advance(TimeSpan.FromMinutes(7));
        controller.Resume();
        _clock.Advance(TimeSpan.FromMinutes(1));
        controller.Stop();

        SessionTotals totals = controller.Totals();

        Assert.Equal(TimeSpan.FromMinutes(2), totals.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(7), totals.PausedDuration);
    }

    [Fact]
    public void CumulativeChunksAreReportedPerTrack()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        _engines.Latest.EmitChunk(SourceTrack.System);
        controller.Pause();
        controller.Resume();
        _engines.Latest.EmitChunk(SourceTrack.System);
        controller.Stop();

        SessionTotals totals = controller.Totals();

        Assert.Equal(2, totals.ChunksPerTrack[SourceTrack.Microphone]);
        Assert.Equal(2, totals.ChunksPerTrack[SourceTrack.System]);
    }

    [Fact]
    public void EndedUtcIsNullBeforeStopAndFrozenAfterwards()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        Assert.Null(controller.EndedUtc);
        Assert.Null(controller.Snapshot().EndedUtc);

        _clock.Advance(TimeSpan.FromMinutes(1));
        controller.Stop();

        DateTimeOffset? first = controller.Snapshot().EndedUtc;
        Assert.NotNull(first);

        // Time moves on; the recorded end time must not.
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(first, controller.Snapshot().EndedUtc);
        Assert.Equal(first, controller.Snapshot().EndedUtc);
        Assert.Equal(first, controller.EndedUtc);
    }

    [Fact]
    public void StartingANewSessionResetsTheEndTimestamp()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        controller.Stop();
        DateTimeOffset? firstEnd = controller.EndedUtc;

        _clock.Advance(TimeSpan.FromMinutes(5));
        controller.Start(Request);

        Assert.Null(controller.EndedUtc);
        Assert.NotEqual(firstEnd, controller.EndedUtc);
        Assert.Equal(0, controller.Totals().ChunkCount);
        Assert.Single(controller.Epochs);
    }

    [Fact]
    public void AControlledDiskStopAlsoFreezesTheEndTimestamp()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _disk.Available = 1_000_000_000;
        controller.Poll();

        DateTimeOffset? end = controller.EndedUtc;
        Assert.NotNull(end);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(end, controller.Snapshot().EndedUtc);
    }

    [Fact]
    public void AFailedStartIsRecordedAsFailedNotAsRecoverableAudio()
    {
        using RecordingController controller = NewController();
        _engines.FailNextStart = true;

        Assert.Throws<InvalidOperationException>(() => controller.Start(Request));

        Assert.Equal(SessionState.Failed, controller.State);
        Assert.True(_engines.Latest.Disposed);

        string sessionId = controller.SessionId!;
        JournalReadResult journal = _store.ReadJournal(sessionId);
        Assert.Contains(journal.Events, e => e.Type == JournalEventTypes.SessionStartFailed);

        SessionSnapshot? snapshot = _store.ReadSnapshot(sessionId);
        Assert.NotNull(snapshot);
        Assert.Equal(SessionState.Failed, snapshot.State);
        Assert.False(snapshot.HasAudio);
    }

    [Fact]
    public void RecoveryDoesNotOfferAFailedStartAsARecoveredSession()
    {
        using RecordingController controller = NewController();
        _engines.FailNextStart = true;
        Assert.Throws<InvalidOperationException>(() => controller.Start(Request));
        string sessionId = controller.SessionId!;

        SessionRecoveryService recovery = new(_store, new FakeChunkRepairer());
        RecoveryOutcome outcome = recovery.Recover(sessionId);

        Assert.Equal(SessionState.Failed, outcome.State);
        Assert.Equal(0, outcome.ChunksRecovered);
        Assert.Equal(0, outcome.ChunksReconciled);
        Assert.Contains(outcome.Notes, n => n.Contains("failed to start", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewSessionCanStartAfterAFailedAttempt()
    {
        using RecordingController controller = NewController();
        _engines.FailNextStart = true;
        Assert.Throws<InvalidOperationException>(() => controller.Start(Request));
        string failedId = controller.SessionId!;

        controller.Start(Request);

        Assert.Equal(SessionState.Recording, controller.State);
        Assert.NotEqual(failedId, controller.SessionId);
        Assert.Single(controller.Epochs);
        Assert.Null(controller.EndedUtc);
    }

    [Fact]
    public void StorageRateIsDerivedFromTheCapturedFormats()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        // Two 48 kHz stereo PCM16 tracks: 2 x 192,000 bytes per second.
        Assert.Equal(384_000, controller.EstimatedBytesPerSecond(), 0);
    }

    [Fact]
    public void ATrackFaultRaisedByTheEngineIsJournalledAndDegradesTheSession()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        _engines.Latest.FailTrack(SourceTrack.Microphone, "COMException (HRESULT 0x88890004)");
        controller.Poll();

        Assert.Equal(SessionState.Degraded, controller.State);
        Assert.True(controller.IsCapturing);

        controller.FlushPendingWrites(TimeSpan.FromSeconds(5));

        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        Assert.Contains(journal.Events, e =>
            e.Type == JournalEventTypes.TrackFailed &&
            (e.Field("fault") ?? string.Empty).Contains("HRESULT", StringComparison.Ordinal));
    }
}

/// <summary>Recovery must compare the whole canonical state, not just how many chunks there are.</summary>
public sealed class SnapshotComparisonTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _store;

    public SnapshotComparisonTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ASnapshotWithTheRightCountButWrongMetadataIsReplaced()
    {
        const string Id = "cmp-1";
        _store.Create(Id);
        DateTimeOffset t = DateTimeOffset.UnixEpoch;

        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionCreated, t, ("session_id", Id)));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.EpochStarted, t, ("epoch", "1"), ("start_qpc", "10")));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.TrackOpened, t,
            ("track", "Microphone"), ("device_id", "mic"), ("device_name", "Real Mic"),
            ("sample_rate", "48000"), ("channels", "2")));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.ChunkCompleted, t,
            ("track", "Microphone"), ("index", "1"), ("epoch", "1"), ("frames", "48000"),
            ("start_seconds", "0"), ("sample_rate", "48000"), ("channels", "2"), ("sha256", "correcthash")));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionStopped, t, ("session_id", Id)));

        // Same chunk count, wrong index, hash, epoch, start time, and device name.
        _store.WriteSnapshot(new SessionSnapshot(
            Id, SessionState.Recorded, t, t, t,
            [],
            [new SessionTrack(SourceTrack.Microphone, "mic", "Wrong Device", new CaptureFormat(48_000, 2, 16),
                [new AudioChunkMetadata(99, "x", SourceTrack.Microphone, 42, 43, 48_000, 2, 48_000, "wronghash", [], 7)])]));

        RecoveryOutcome outcome = new SessionRecoveryService(_store, new FakeChunkRepairer()).Recover(Id);

        Assert.True(outcome.SnapshotRebuilt);

        SessionSnapshot fixedUp = _store.ReadSnapshot(Id)!;
        AudioChunkMetadata chunk = Assert.Single(fixedUp.Tracks.SelectMany(t2 => t2.Chunks));
        Assert.Equal(1, chunk.Index);
        Assert.Equal("correcthash", chunk.Sha256);
        Assert.Equal(1, chunk.EpochIndex);
        Assert.Equal(0, chunk.StartSeconds, 3);
        Assert.Equal("Real Mic", fixedUp.Tracks[0].DeviceName);
        Assert.Single(fixedUp.Epochs);
    }

    [Fact]
    public void AMatchingSnapshotIsLeftAlone()
    {
        const string Id = "cmp-2";
        _store.Create(Id);
        DateTimeOffset t = DateTimeOffset.UnixEpoch;

        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionCreated, t, ("session_id", Id)));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionStopped, t, ("session_id", Id)));

        SessionRecoveryService recovery = new(_store, new FakeChunkRepairer());
        recovery.Recover(Id);

        DirectorySnapshot before = DirectorySnapshot.Capture(_temp.Path);
        RecoveryOutcome second = recovery.Recover(Id);

        Assert.False(second.SnapshotRebuilt);
        Assert.True(DirectorySnapshot.Capture(_temp.Path).Matches(before));
    }
}
