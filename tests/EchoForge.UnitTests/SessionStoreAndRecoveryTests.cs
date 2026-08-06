using System.Text;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

public sealed class FileSessionStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _store;

    public FileSessionStoreTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EventsRoundTripInOrder()
    {
        SessionPaths paths = _store.Create("session-a");
        _store.Append("session-a", JournalEvent.Create("first", DateTimeOffset.UnixEpoch, ("k", "1")));
        _store.Append("session-a", JournalEvent.Create("second", DateTimeOffset.UnixEpoch, ("k", "2")));

        JournalReadResult result = _store.ReadJournal("session-a");

        Assert.True(File.Exists(paths.JournalPath));
        Assert.Equal(["first", "second"], result.Events.Select(e => e.Type));
        Assert.Equal("2", result.Events[1].Field("k"));
        Assert.False(result.TruncatedFinalLine);
    }

    [Fact]
    public void ATruncatedFinalLineIsDiscardedAndEverythingBeforeItSurvives()
    {
        SessionPaths paths = _store.Create("session-b");
        _store.Append("session-b", JournalEvent.Create("first", DateTimeOffset.UnixEpoch));
        _store.Append("session-b", JournalEvent.Create("second", DateTimeOffset.UnixEpoch));

        // A process killed mid-append leaves a half-written final line.
        File.AppendAllText(paths.JournalPath, "{\"ts\":\"2026-08-06T00:00:00+00:00\",\"type\":\"thi", Encoding.UTF8);

        JournalReadResult result = _store.ReadJournal("session-b");

        Assert.True(result.TruncatedFinalLine);
        Assert.Equal(["first", "second"], result.Events.Select(e => e.Type));
    }

    [Fact]
    public void AMalformedLineInTheMiddleIsSkippedRatherThanLosingTheJournal()
    {
        SessionPaths paths = _store.Create("session-c");
        _store.Append("session-c", JournalEvent.Create("first", DateTimeOffset.UnixEpoch));
        File.AppendAllText(paths.JournalPath, "not json at all" + Environment.NewLine, Encoding.UTF8);
        _store.Append("session-c", JournalEvent.Create("third", DateTimeOffset.UnixEpoch));

        JournalReadResult result = _store.ReadJournal("session-c");

        Assert.Equal(1, result.SkippedLines);
        Assert.False(result.TruncatedFinalLine);
        Assert.Equal(["first", "third"], result.Events.Select(e => e.Type));
    }

    [Fact]
    public void SnapshotsRoundTripAndReplaceAtomically()
    {
        _store.Create("session-d");
        SessionSnapshot snapshot = new(
            "session-d", SessionState.Recorded, DateTimeOffset.UnixEpoch, null, null, [], []);

        _store.WriteSnapshot(snapshot);
        _store.WriteSnapshot(snapshot with { State = SessionState.NeedsAttention });

        SessionSnapshot? read = _store.ReadSnapshot("session-d");

        Assert.NotNull(read);
        Assert.Equal(SessionState.NeedsAttention, read.State);
        Assert.False(File.Exists(_store.Resolve("session-d").SnapshotPath + ".tmp"));
    }

    [Fact]
    public void AnUnreadableSnapshotReadsAsNullSoTheJournalCanRebuildIt()
    {
        SessionPaths paths = _store.Create("session-e");
        File.WriteAllText(paths.SnapshotPath, "{ this is not valid json", Encoding.UTF8);

        Assert.Null(_store.ReadSnapshot("session-e"));
    }

    [Fact]
    public void SessionsAreDiscoverableAfterRestart()
    {
        _store.Create("session-f");
        _store.Append("session-f", JournalEvent.Create("x", DateTimeOffset.UnixEpoch));

        FileSessionStore reopened = new(_temp.Path);

        Assert.Contains("session-f", reopened.EnumerateSessions());
    }
}

public sealed class SessionRecoveryTests : IDisposable
{
    private static readonly CaptureFormat Stereo48 = new(48_000, 2, 16);

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _store;
    private readonly FakeChunkRepairer _repairer = new();

    public SessionRecoveryTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private SessionRecoveryService NewService() => new(_store, _repairer);

    /// <summary>Builds an interrupted session: journalled chunks, plus an abandoned active chunk.</summary>
    private SessionPaths BuildInterruptedSession(string id, bool withSidecar = true, bool stopped = false)
    {
        SessionPaths paths = _store.Create(id);
        DateTimeOffset t = DateTimeOffset.UnixEpoch;

        _store.Append(id, JournalEvent.Create(JournalEventTypes.SessionCreated, t, ("session_id", id)));
        _store.Append(id, JournalEvent.Create(JournalEventTypes.EpochStarted, t,
            ("epoch", "1"), ("start_qpc", "1000"), ("first_chunk_index", "1")));
        _store.Append(id, JournalEvent.Create(JournalEventTypes.TrackOpened, t,
            ("track", "Microphone"), ("device_id", "mic"), ("device_name", "Fake Mic"),
            ("sample_rate", "48000"), ("channels", "2")));
        _store.Append(id, JournalEvent.Create(JournalEventTypes.ChunkCompleted, t,
            ("track", "Microphone"), ("index", "1"), ("epoch", "1"),
            ("frames", "48000"), ("sha256", "abc"), ("sample_rate", "48000"), ("channels", "2")));

        if (stopped)
        {
            _store.Append(id, JournalEvent.Create(JournalEventTypes.SessionEnded, t,
                ("session_id", id), ("outcome", nameof(SessionState.Recorded))));
        }

        string active = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "active");
        Directory.CreateDirectory(active);
        Directory.CreateDirectory(Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks"));
        File.WriteAllBytes(Path.Combine(active, "000002.part.wav"), new byte[1024]);

        if (withSidecar)
        {
            WriteChunkRecord(
                Path.Combine(active, "000002.part.state.json"),
                track: "Microphone", index: 2, epoch: 1, frames: 1_000, startSeconds: 60.0);
        }

        return paths;
    }

    /// <summary>
    /// Writes the durable per-chunk record the writer emits. Recovery needs every field here to
    /// place a repaired chunk without inventing a start time or an epoch.
    /// </summary>
    private static void WriteChunkRecord(
        string path, string track, int index, int epoch, long frames, double startSeconds, bool finalized = false)
    {
        string relative = finalized
            ? $"tracks/{track.ToLowerInvariant()}/chunks/{index:D6}.wav"
            : $"tracks/{track.ToLowerInvariant()}/active/{index:D6}.part.wav";

        File.WriteAllText(path, $$"""
            {"schema_version":1,"track":"{{track}}","index":{{index}},"epoch":{{epoch}},
             "sample_rate":48000,"channels":2,"bits_per_sample":16,"frames":{{frames}},
             "start_seconds":{{startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
             "epoch_qpc":1000,"relative_path":"{{relative}}","finalized":{{(finalized ? "true" : "false")}},
             "discontinuities":[]}
            """.ReplaceLineEndings(string.Empty));
    }

    [Fact]
    public void AnAbandonedActiveChunkIsRepairedAndPromoted()
    {
        SessionPaths paths = BuildInterruptedSession("rec-1");

        RecoveryOutcome outcome = NewService().Recover("rec-1");

        Assert.Equal(1, outcome.ChunksRecovered);
        Assert.Equal(0, outcome.ChunksQuarantined);
        Assert.Single(_repairer.Repaired);

        string promoted = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks", "000002.wav");
        Assert.True(File.Exists(promoted));
        Assert.Empty(Directory.GetFiles(Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "active")));
    }

    [Fact]
    public void AnUnrepairableChunkIsQuarantinedAndTheSessionNeedsAttention()
    {
        _repairer.RepairSucceeds = false;
        SessionPaths paths = BuildInterruptedSession("rec-2");

        RecoveryOutcome outcome = NewService().Recover("rec-2");

        Assert.Equal(0, outcome.ChunksRecovered);
        Assert.Equal(1, outcome.ChunksQuarantined);
        Assert.Equal(SessionState.NeedsAttention, outcome.State);
        Assert.Contains(outcome.Notes, n => n.Contains("quarantined", StringComparison.Ordinal));

        // Audio, its metadata record, and the reason are all preserved under their own names.
        string[] quarantined = Directory.GetFiles(paths.QuarantineRoot);
        Assert.Contains(quarantined, f => f.EndsWith("000002.part.wav", StringComparison.Ordinal));
        Assert.Contains(quarantined, f => f.EndsWith("000002.part.state.json", StringComparison.Ordinal));
        Assert.Contains(quarantined, f => f.EndsWith(".reason.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void AnActiveChunkWithNoSidecarIsQuarantinedRatherThanGuessedAt()
    {
        BuildInterruptedSession("rec-3", withSidecar: false);

        RecoveryOutcome outcome = NewService().Recover("rec-3");

        Assert.Equal(1, outcome.ChunksQuarantined);
        Assert.Empty(_repairer.Repaired);
        Assert.Contains(outcome.Notes, n => n.Contains("metadata record", StringComparison.Ordinal));

        // The sidecar is diagnostic evidence and the reason is preserved beside the audio.
        SessionPaths paths = _store.Resolve("rec-3");
        Assert.Contains(Directory.GetFiles(paths.QuarantineRoot), f => f.EndsWith(".reason.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingSnapshotIsRebuiltFromTheJournal()
    {
        BuildInterruptedSession("rec-4", stopped: true);
        Assert.Null(_store.ReadSnapshot("rec-4"));

        RecoveryOutcome outcome = NewService().Recover("rec-4");

        Assert.True(outcome.SnapshotRebuilt);
        SessionSnapshot? snapshot = _store.ReadSnapshot("rec-4");
        Assert.NotNull(snapshot);
        Assert.Equal(SessionState.Recorded, snapshot.State);

        SessionTrack track = Assert.Single(snapshot.Tracks);
        Assert.Equal(SourceTrack.Microphone, track.Track);
        Assert.Equal("Fake Mic", track.DeviceName);
        Assert.Contains(track.Chunks, c => c.Index == 1);
    }

    [Fact]
    public void ATruncatedJournalTailIsReportedAndDoesNotLoseEarlierChunks()
    {
        SessionPaths paths = BuildInterruptedSession("rec-5");
        File.AppendAllText(paths.JournalPath, "{\"ts\":\"2026-08-06T00:00:00+00:00\",\"ty");

        RecoveryOutcome outcome = NewService().Recover("rec-5");

        Assert.True(outcome.JournalTruncated);
        SessionSnapshot? snapshot = _store.ReadSnapshot("rec-5");
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot.Tracks.SelectMany(t => t.Chunks), c => c.Index == 1);
    }

    [Fact]
    public void OneHealthyTrackAndOneDamagedTrackKeepsTheHealthyAudio()
    {
        SessionPaths paths = BuildInterruptedSession("rec-6");

        // A second track whose active chunk has no metadata record.
        string systemActive = Path.Combine(paths.TrackRoot(SourceTrack.System), "active");
        Directory.CreateDirectory(systemActive);
        Directory.CreateDirectory(Path.Combine(paths.TrackRoot(SourceTrack.System), "chunks"));
        File.WriteAllBytes(Path.Combine(systemActive, "000003.part.wav"), new byte[512]);

        RecoveryOutcome outcome = NewService().Recover("rec-6");

        Assert.Equal(1, outcome.ChunksRecovered);
        Assert.Equal(1, outcome.ChunksQuarantined);
        Assert.Equal(SessionState.NeedsAttention, outcome.State);

        // The microphone's repaired chunk survived even though the system track was damaged.
        Assert.True(File.Exists(Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks", "000002.wav")));
    }

    [Fact]
    public void RunningRecoveryTwiceChangesNothingTheSecondTime()
    {
        BuildInterruptedSession("rec-7");
        SessionRecoveryService service = NewService();

        RecoveryOutcome first = service.Recover("rec-7");
        DirectorySnapshot afterFirst = DirectorySnapshot.Capture(_temp.Path);

        RecoveryOutcome second = service.Recover("rec-7");

        Assert.True(first.ChangedAnything);
        Assert.Equal(0, second.ChunksRecovered);
        Assert.Equal(0, second.ChunksQuarantined);

        // The journal may not grow and no audio may move on a second pass.
        Assert.True(DirectorySnapshot.Capture(_temp.Path).Matches(afterFirst));
    }

    [Fact]
    public void RecoveryNeverOverwritesAnAlreadyFinalizedChunk()
    {
        SessionPaths paths = BuildInterruptedSession("rec-8");
        string chunks = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks");
        File.WriteAllBytes(Path.Combine(chunks, "000002.wav"), [1, 2, 3, 4]);

        RecoveryOutcome outcome = NewService().Recover("rec-8");

        Assert.Equal(0, outcome.ChunksRecovered);
        Assert.Equal(1, outcome.ChunksQuarantined);
        Assert.Equal(4, new FileInfo(Path.Combine(chunks, "000002.wav")).Length);
    }

    [Fact]
    public void ScanAllReportsOnlySessionsThatNeededWork()
    {
        BuildInterruptedSession("rec-9");
        _store.Create("rec-10");
        _store.Append("rec-10", JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));
        _store.WriteSnapshot(new SessionSnapshot(
            "rec-10", SessionState.NeedsAttention, DateTimeOffset.UnixEpoch, null, null, [], []));

        IReadOnlyList<RecoveryOutcome> outcomes = NewService().ScanAll();

        Assert.Contains(outcomes, o => o.SessionId == "rec-9");
    }
}
