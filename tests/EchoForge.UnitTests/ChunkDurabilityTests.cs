using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// The crash window between "a WAV is complete on disk" and "the journal knows about it".
///
/// <para>
/// Chunks finalize every 60 seconds while recording, but the journal used to learn about them
/// only when the epoch closed. A crash in between left valid audio that recovery ignored, and a
/// rebuilt snapshot could report a long meeting as empty.
/// </para>
/// </summary>
public sealed class ChunkDurabilityTests : IDisposable
{
    private static readonly CaptureFormat Mono48 = new(48_000, 1, 16);

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _store;
    private readonly FakeChunkRepairer _repairer = new();

    public ChunkDurabilityTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private SessionRecoveryService NewService() => new(_store, _repairer);

    private static PacketHeader Header(long frame, int frames) =>
        new(frame, frame * 10_000_000 / 48_000, frames, AudioPacketConditions.None);

    private static byte[] Frames(int count) => new byte[count * 2];

    /// <summary>Records two 1-second chunks with the real writer, returning what it finalized.</summary>
    private (SessionPaths Paths, List<AudioChunkMetadata> Chunks) RecordTwoChunks(string sessionId)
    {
        SessionPaths paths = _store.Create(sessionId);
        List<AudioChunkMetadata> finalized = [];

        using (PcmChunkWriter writer = new(
            paths.TrackRoot(SourceTrack.Microphone), SourceTrack.Microphone, Mono48, 0,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200), 1, 1, paths.Root, finalized.Add))
        {
            for (int i = 0; i < 200; i++)
            {
                writer.Write(Header(i * 480, 480), Frames(480));
            }
        }

        return (paths, finalized);
    }

    [Fact]
    public void AChunkIsNotifiedAndDurableTheMomentItFinalizes()
    {
        (SessionPaths paths, List<AudioChunkMetadata> finalized) = RecordTwoChunks("dur-1");

        // Two whole chunks were notified while recording, not at epoch close.
        Assert.Equal(2, finalized.Count(c => c.SampleFrames == 48_000));

        foreach (AudioChunkMetadata chunk in finalized)
        {
            string meta = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks", $"{chunk.Index:D6}.meta.json");
            Assert.True(File.Exists(meta), $"no durable record for chunk {chunk.Index}");
        }
    }

    [Fact]
    public void FinalizedWavsWithNoJournalEntriesAreReconciled()
    {
        // Scenario: the process died after the WAVs were complete but before any journal append.
        (SessionPaths paths, List<AudioChunkMetadata> finalized) = RecordTwoChunks("dur-2");
        _store.Append("dur-2", JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));

        RecoveryOutcome outcome = NewService().Recover("dur-2");

        Assert.Equal(finalized.Count, outcome.ChunksReconciled);

        SessionSnapshot? snapshot = _store.ReadSnapshot("dur-2");
        Assert.NotNull(snapshot);
        Assert.Equal(finalized.Count, snapshot.Tracks.Sum(t => t.Chunks.Count));
        Assert.True(snapshot.HasAudio);
        Assert.Equal(_ = paths.SessionId, snapshot.SessionId);
    }

    [Fact]
    public void ReconciledChunksCarryTheirRealEpochAndStartTime()
    {
        (_, List<AudioChunkMetadata> finalized) = RecordTwoChunks("dur-3");
        _store.Append("dur-3", JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));

        NewService().Recover("dur-3");
        SessionSnapshot snapshot = _store.ReadSnapshot("dur-3")!;
        List<AudioChunkMetadata> chunks = [.. snapshot.Tracks.SelectMany(t => t.Chunks).OrderBy(c => c.Index)];

        // The second chunk starts one second in. A default of zero would be wrong and silent.
        Assert.Equal(0.0, chunks[0].StartSeconds, 3);
        Assert.Equal(1.0, chunks[1].StartSeconds, 3);
        Assert.All(chunks, c => Assert.Equal(1, c.EpochIndex));
        Assert.All(chunks, c => Assert.Equal(64, c.Sha256.Length));
        Assert.Equal(finalized.Count(c => c.SampleFrames == 48_000), chunks.Count(c => c.SampleFrames == 48_000));
    }

    [Fact]
    public void AJournalEntryWithNoSnapshotStillRebuildsTheSession()
    {
        RecordTwoChunks("dur-4");
        _store.Append("dur-4", JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));

        Assert.Null(_store.ReadSnapshot("dur-4"));

        RecoveryOutcome outcome = NewService().Recover("dur-4");

        Assert.True(outcome.SnapshotRebuilt);
        Assert.True(_store.ReadSnapshot("dur-4")!.HasAudio);
    }

    [Fact]
    public void ReconciliationIsIdempotent()
    {
        RecordTwoChunks("dur-5");
        _store.Append("dur-5", JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));

        SessionRecoveryService service = NewService();
        RecoveryOutcome first = service.Recover("dur-5");
        DirectorySnapshot afterFirst = DirectorySnapshot.Capture(_temp.Path);

        RecoveryOutcome second = service.Recover("dur-5");

        Assert.True(first.ChunksReconciled > 0);
        Assert.Equal(0, second.ChunksReconciled);
        Assert.False(second.SnapshotRebuilt);
        Assert.True(DirectorySnapshot.Capture(_temp.Path).Matches(afterFirst));
    }

    [Fact]
    public void ReconciliationNeverDuplicatesAChunkThatIsAlreadyJournalled()
    {
        (_, List<AudioChunkMetadata> finalized) = RecordTwoChunks("dur-6");

        _store.Append("dur-6", JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));
        AudioChunkMetadata first = finalized[0];
        _store.Append("dur-6", JournalEvent.Create(
            JournalEventTypes.ChunkCompleted, DateTimeOffset.UnixEpoch,
            ("track", "Microphone"),
            ("index", first.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("epoch", "1"),
            ("frames", first.SampleFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("start_seconds", "0"),
            ("sample_rate", "48000"),
            ("channels", "1"),
            ("sha256", first.Sha256)));

        RecoveryOutcome outcome = NewService().Recover("dur-6");

        // Only the chunks the journal was missing get reconciled.
        Assert.Equal(finalized.Count - 1, outcome.ChunksReconciled);

        SessionSnapshot snapshot = _store.ReadSnapshot("dur-6")!;
        List<int> indices = [.. snapshot.Tracks.SelectMany(t => t.Chunks).Select(c => c.Index)];
        Assert.Equal(indices.Count, indices.Distinct().Count());
    }

    [Fact]
    public void AWavWithNoMetadataRecordIsKeptButFlaggedRatherThanPlacedAtZero()
    {
        SessionPaths paths = _store.Create("dur-7");
        string chunks = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks");
        Directory.CreateDirectory(chunks);
        File.WriteAllBytes(Path.Combine(chunks, "000004.wav"), new byte[2048]);
        _store.Append("dur-7", JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));

        RecoveryOutcome outcome = NewService().Recover("dur-7");

        Assert.Equal(0, outcome.ChunksReconciled);
        Assert.Equal(SessionState.NeedsAttention, outcome.State);
        Assert.Contains(outcome.Notes, n => n.Contains("cannot be placed", StringComparison.Ordinal));

        // The audio is retained, never deleted or renumbered.
        Assert.True(File.Exists(Path.Combine(chunks, "000004.wav")));
    }

    [Fact]
    public void AnUnrecognisedChunkFilenameNeverBecomesChunkZero()
    {
        SessionPaths paths = _store.Create("dur-8");
        string chunks = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks");
        Directory.CreateDirectory(chunks);
        File.WriteAllBytes(Path.Combine(chunks, "scratch.wav"), new byte[512]);
        _store.Append("dur-8", JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));

        RecoveryOutcome outcome = NewService().Recover("dur-8");

        Assert.Equal(0, outcome.ChunksReconciled);
        Assert.Contains(outcome.Notes, n => n.Contains("unrecognised name", StringComparison.Ordinal));

        SessionSnapshot? snapshot = _store.ReadSnapshot("dur-8");
        Assert.DoesNotContain(snapshot!.Tracks.SelectMany(t => t.Chunks), c => c.Index == 0);
    }
}
