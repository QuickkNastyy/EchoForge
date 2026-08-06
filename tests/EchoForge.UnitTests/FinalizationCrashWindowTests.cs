using System.Text.Json;
using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// Finalizing a chunk is a sequence of steps, and a crash can land between any two of them.
///
/// <para>
/// The order is: write the active WAV, rename it into <c>chunks</c>, hash it, write the finalized
/// metadata record, enqueue the journal event, append it durably, then replace the snapshot. Each
/// test below kills the process at one of those seams and asserts recovery preserves the audio,
/// adds no duplicate event, and produces either a valid canonical chunk or <c>NeedsAttention</c>
/// with a reason.
/// </para>
/// </summary>
public sealed class FinalizationCrashWindowTests : IDisposable
{
    private static readonly CaptureFormat Mono48 = new(48_000, 1, 16);
    private const string Id = "crash";
    private const long Frames = 4_800;

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _store;
    private readonly WavChunkRepairer _repairer = new();

    public FinalizationCrashWindowTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private RecoveryOutcome Recover() => new SessionRecoveryService(_store, _repairer).Recover(Id);

    private SessionPaths Prepare()
    {
        SessionPaths paths = _store.Create(Id);
        Directory.CreateDirectory(Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks"));
        Directory.CreateDirectory(Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "active"));

        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.EpochStarted, DateTimeOffset.UnixEpoch,
            ("epoch", "2"), ("start_qpc", "500"), ("first_chunk_index", "1")));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.TrackOpened, DateTimeOffset.UnixEpoch,
            ("track", "Microphone"), ("device_id", "mic"), ("device_name", "Mic"),
            ("sample_rate", "48000"), ("channels", "1"), ("epoch", "2")));

        return paths;
    }

    private static void WriteWav(string path, bool patchHeader)
    {
        if (patchHeader)
        {
            using WavPcm16Writer writer = new(path, Mono48);
            writer.WriteSilence(Frames);
            writer.Close();
            return;
        }

        // Unpatched header, exactly as a killed process leaves an active chunk.
        using FileStream stream = new(path, FileMode.Create);
        WavPcm16Writer.WriteHeader(stream, Mono48, dataBytes: 0);
        stream.Write(new byte[Frames * 2]);
    }

    private static ChunkRecord Record(bool finalized, string? sha) => new()
    {
        Track = "Microphone",
        Index = 1,
        Epoch = 2,
        SampleRate = 48_000,
        Channels = 1,
        Frames = finalized ? Frames : 0,
        StartSeconds = 90.0,
        EpochQpc = 500,
        RelativePath = finalized
            ? "tracks/microphone/chunks/000001.wav"
            : "tracks/microphone/active/000001.part.wav",
        Sha256 = sha,
        Finalized = finalized,
        Discontinuities = [],
    };

    private static void Write(string path, ChunkRecord record) =>
        File.WriteAllText(path, JsonSerializer.Serialize(record, ChunkRecord.Json));

    private static string Sha(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>The chunk landed on the timeline exactly once, at the right place.</summary>
    private void AssertCanonicalChunk()
    {
        JournalReadResult journal = _store.ReadJournal(Id);
        JournalEvent completed = Assert.Single(
            journal.Events, e => e.Type == JournalEventTypes.ChunkCompleted && e.IntField("index") == 1);

        Assert.Equal(2, completed.IntField("epoch"));
        Assert.Equal(Frames, completed.LongField("frames"));

        SessionSnapshot snapshot = _store.ReadSnapshot(Id)!;
        AudioChunkMetadata chunk = Assert.Single(snapshot.Tracks.SelectMany(t => t.Chunks));
        Assert.Equal(1, chunk.Index);
        Assert.Equal(2, chunk.EpochIndex);
        Assert.Equal(90.0, chunk.StartSeconds, 3);
        Assert.Equal(Frames, chunk.SampleFrames);
    }

    [Fact]
    public void WindowOneCrashBeforeTheActiveWavWasRenamed()
    {
        SessionPaths paths = Prepare();
        string active = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "active");
        WriteWav(Path.Combine(active, "000001.part.wav"), patchHeader: false);
        Write(Path.Combine(active, "000001.part.state.json"), Record(finalized: false, sha: null));

        RecoveryOutcome outcome = Recover();

        Assert.Equal(1, outcome.ChunksRecovered);

        // The epoch never closed, so this is an unfinished recording the user can continue or
        // finalize — not something recovery may quietly declare finished on their behalf.
        Assert.Equal(SessionState.Paused, outcome.State);
        AssertCanonicalChunk();

        // The audio moved into chunks and nothing was left behind in active.
        Assert.True(File.Exists(Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks", "000001.wav")));
        Assert.Empty(Directory.GetFiles(active));
    }

    [Fact]
    public void WindowTwoCrashAfterRenameButBeforeHashingAndMetadata()
    {
        SessionPaths paths = Prepare();
        string track = paths.TrackRoot(SourceTrack.Microphone);

        // Renamed and complete, but no finalized record yet. The active sidecar still describes it.
        WriteWav(Path.Combine(track, "chunks", "000001.wav"), patchHeader: true);
        Write(Path.Combine(track, "active", "000001.part.state.json"), Record(finalized: false, sha: null));

        RecoveryOutcome outcome = Recover();

        Assert.Equal(1, outcome.ChunksReconciled);
        Assert.Contains(outcome.Notes, n => n.Contains("reconstructed", StringComparison.Ordinal));
        AssertCanonicalChunk();

        // A finalized record now exists and the sidecar was archived, not deleted.
        Assert.True(File.Exists(Path.Combine(track, "chunks", "000001.meta.json")));
        Assert.True(Directory.Exists(Path.Combine(paths.DiagnosticsRoot, "recovered-sidecars")));
    }

    [Fact]
    public void WindowThreeCrashAfterMetadataButBeforeTheJournalWasEnqueued()
    {
        SessionPaths paths = Prepare();
        string chunks = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks");
        string wav = Path.Combine(chunks, "000001.wav");

        WriteWav(wav, patchHeader: true);
        Write(Path.Combine(chunks, "000001.meta.json"), Record(finalized: true, sha: Sha(wav)));

        RecoveryOutcome outcome = Recover();

        Assert.Equal(1, outcome.ChunksReconciled);
        Assert.Equal(SessionState.Paused, outcome.State);
        AssertCanonicalChunk();
    }

    [Fact]
    public void WindowFourCrashAfterTheJournalWasQueuedButBeforeItWasDurable()
    {
        // Indistinguishable on disk from window 3: the queued event never reached the file.
        // What matters is that reconciliation adds it exactly once.
        SessionPaths paths = Prepare();
        string chunks = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks");
        string wav = Path.Combine(chunks, "000001.wav");

        WriteWav(wav, patchHeader: true);
        Write(Path.Combine(chunks, "000001.meta.json"), Record(finalized: true, sha: Sha(wav)));

        SessionRecoveryService recovery = new(_store, _repairer);
        recovery.Recover(Id);

        // Running again must not add a second event for the same chunk.
        RecoveryOutcome second = recovery.Recover(Id);

        Assert.Equal(0, second.ChunksReconciled);
        AssertCanonicalChunk();
    }

    [Fact]
    public void WindowFiveCrashAfterTheJournalWasAppendedButBeforeTheSnapshotWasReplaced()
    {
        SessionPaths paths = Prepare();
        string chunks = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks");
        string wav = Path.Combine(chunks, "000001.wav");

        WriteWav(wav, patchHeader: true);
        Write(Path.Combine(chunks, "000001.meta.json"), Record(finalized: true, sha: Sha(wav)));
        _store.Append(Id, JournalEvent.Create(JournalEventTypes.ChunkCompleted, DateTimeOffset.UnixEpoch,
            ("track", "Microphone"), ("index", "1"), ("epoch", "2"),
            ("frames", Frames.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("start_seconds", "90"), ("sample_rate", "48000"), ("channels", "1"), ("sha256", Sha(wav))));

        Assert.Null(_store.ReadSnapshot(Id));

        RecoveryOutcome outcome = Recover();

        // The journal already knew, so nothing is reconciled; only the projection is rebuilt.
        Assert.Equal(0, outcome.ChunksReconciled);
        Assert.True(outcome.SnapshotRebuilt);
        AssertCanonicalChunk();
    }

    [Fact]
    public void AChunkWhoseAudioContradictsItsRecordIsPreservedAndFlagged()
    {
        SessionPaths paths = Prepare();
        string chunks = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks");
        string wav = Path.Combine(chunks, "000001.wav");

        WriteWav(wav, patchHeader: true);

        // The record claims a hash the audio does not have — a torn or truncated write.
        Write(Path.Combine(chunks, "000001.meta.json"), Record(finalized: true, sha: new string('b', 64)));

        string before = Sha(wav);
        RecoveryOutcome outcome = Recover();

        Assert.Equal(0, outcome.ChunksReconciled);
        Assert.Equal(SessionState.NeedsAttention, outcome.State);
        Assert.Contains(outcome.Notes, n => n.Contains("could not be verified", StringComparison.Ordinal));

        // The audio is untouched and was never journalled as canonical.
        Assert.Equal(before, Sha(wav));
        JournalReadResult journal = _store.ReadJournal(Id);
        Assert.DoesNotContain(journal.Events, e => e.Type == JournalEventTypes.ChunkCompleted);
    }

    [Fact]
    public void RecoveringTwiceAcrossEveryWindowIsStable()
    {
        SessionPaths paths = Prepare();
        string track = paths.TrackRoot(SourceTrack.Microphone);

        // One chunk in each of the first three states at once.
        WriteWav(Path.Combine(track, "active", "000003.part.wav"), patchHeader: false);
        Write(Path.Combine(track, "active", "000003.part.state.json"),
            Record(finalized: false, sha: null) with { Index = 3, RelativePath = "tracks/microphone/active/000003.part.wav" });

        WriteWav(Path.Combine(track, "chunks", "000002.wav"), patchHeader: true);
        Write(Path.Combine(track, "active", "000002.part.state.json"),
            Record(finalized: false, sha: null) with { Index = 2, RelativePath = "tracks/microphone/active/000002.part.wav" });

        string wav1 = Path.Combine(track, "chunks", "000001.wav");
        WriteWav(wav1, patchHeader: true);
        Write(Path.Combine(track, "chunks", "000001.meta.json"), Record(finalized: true, sha: Sha(wav1)));

        SessionRecoveryService recovery = new(_store, _repairer);
        RecoveryOutcome first = recovery.Recover(Id);
        DirectorySnapshot afterFirst = DirectorySnapshot.Capture(_temp.Path);

        RecoveryOutcome second = recovery.Recover(Id);

        Assert.True(first.ChangedAnything);
        Assert.Equal(0, second.ChunksRecovered);
        Assert.Equal(0, second.ChunksReconciled);
        Assert.False(second.SnapshotRebuilt);
        Assert.True(DirectorySnapshot.Capture(_temp.Path).Matches(afterFirst));

        // Three distinct chunks, each journalled exactly once.
        JournalReadResult journal = _store.ReadJournal(Id);
        List<int> indices = [.. journal.Events
            .Where(e => e.Type == JournalEventTypes.ChunkCompleted)
            .Select(e => e.IntField("index") ?? 0)];

        Assert.Equal([1, 2, 3], indices.Order());
    }
}
