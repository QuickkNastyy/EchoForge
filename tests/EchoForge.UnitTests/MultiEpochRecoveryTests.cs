using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// A paused-and-resumed session opens each track once per epoch, so the journal carries several
/// <c>track_opened</c> events per track. Reconstruction used to replace the track builder on each
/// one, discarding every chunk from earlier epochs.
/// </summary>
public sealed class MultiEpochRecoveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _store;

    public MultiEpochRecoveryTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private void Append(string id, string type, DateTimeOffset at, params (string, string)[] fields) =>
        _store.Append(id, JournalEvent.Create(type, at, fields));

    /// <summary>
    /// Two tracks, two epochs, chunks in both, a pause and a resume, and no session.json.
    /// </summary>
    private string BuildTwoEpochSession()
    {
        const string Id = "epoch-session";
        _store.Create(Id);
        DateTimeOffset t0 = new(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);

        Append(Id, JournalEventTypes.SessionCreated, t0, ("session_id", Id));

        // Epoch 1 — both tracks at 48 kHz stereo, two chunks each.
        Append(Id, JournalEventTypes.EpochStarted, t0, ("epoch", "1"), ("start_qpc", "1000"), ("first_chunk_index", "1"));
        OpenTrack(Id, t0, "Microphone", "mic-id", "Yeti Nano", 48_000, 2, 1);
        OpenTrack(Id, t0, "System", "sys-id", "Headphones", 48_000, 2, 1);
        Chunk(Id, t0.AddSeconds(60), "Microphone", 1, 1, 48_000 * 60, 0, 48_000, 2, "mic-1");
        Chunk(Id, t0.AddSeconds(120), "System", 2, 1, 48_000 * 60, 0, 48_000, 2, "sys-2");
        Append(Id, JournalEventTypes.EpochEnded, t0.AddMinutes(2), ("epoch", "1"), ("end_qpc", "2000"), ("reason", "Paused"));

        // Epoch 2 — resumed five minutes later; the microphone came back at a different rate.
        DateTimeOffset t1 = t0.AddMinutes(7);
        Append(Id, JournalEventTypes.EpochStarted, t1, ("epoch", "2"), ("start_qpc", "3000"), ("first_chunk_index", "3"));
        OpenTrack(Id, t1, "Microphone", "mic-id", "Yeti Nano", 16_000, 1, 2);
        OpenTrack(Id, t1, "System", "sys-id", "Headphones", 48_000, 2, 2);
        Chunk(Id, t1.AddSeconds(60), "Microphone", 3, 2, 16_000 * 60, 0, 16_000, 1, "mic-3");
        Chunk(Id, t1.AddSeconds(120), "System", 4, 2, 48_000 * 60, 0, 48_000, 2, "sys-4");
        Append(Id, JournalEventTypes.EpochEnded, t1.AddMinutes(2), ("epoch", "2"), ("end_qpc", "4000"), ("reason", "Stopped"));

        Append(Id, JournalEventTypes.SessionStopped, t1.AddMinutes(2), ("session_id", Id));
        return Id;
    }

    private void OpenTrack(string id, DateTimeOffset at, string track, string deviceId, string name, int rate, int channels, int epoch) =>
        Append(id, JournalEventTypes.TrackOpened, at,
            ("track", track), ("device_id", deviceId), ("device_name", name),
            ("sample_rate", rate.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("channels", channels.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("epoch", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private void Chunk(string id, DateTimeOffset at, string track, int index, int epoch, long frames, double start, int rate, int channels, string sha) =>
        Append(id, JournalEventTypes.ChunkCompleted, at,
            ("track", track),
            ("index", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("epoch", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("frames", frames.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("start_seconds", start.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("sample_rate", rate.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("channels", channels.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("sha256", sha));

    [Fact]
    public void EveryChunkFromEveryEpochSurvivesReconstruction()
    {
        string id = BuildTwoEpochSession();
        Assert.Null(_store.ReadSnapshot(id));

        RecoveryOutcome outcome = new SessionRecoveryService(_store, new FakeChunkRepairer()).Recover(id);

        Assert.True(outcome.SnapshotRebuilt);
        SessionSnapshot snapshot = _store.ReadSnapshot(id)!;

        List<AudioChunkMetadata> chunks = [.. snapshot.Tracks.SelectMany(t => t.Chunks).OrderBy(c => c.Index)];

        // All four chunks, not just the last epoch's two.
        Assert.Equal(4, chunks.Count);
        Assert.Equal([1, 2, 3, 4], chunks.Select(c => c.Index));
        Assert.Equal([1, 1, 2, 2], chunks.Select(c => c.EpochIndex));
        Assert.Equal(["mic-1", "sys-2", "mic-3", "sys-4"], chunks.Select(c => c.Sha256));
    }

    [Fact]
    public void BothEpochsAreReconstructedWithTheirBoundaries()
    {
        string id = BuildTwoEpochSession();
        new SessionRecoveryService(_store, new FakeChunkRepairer()).Recover(id);

        SessionSnapshot snapshot = _store.ReadSnapshot(id)!;

        Assert.Equal(2, snapshot.Epochs.Count);
        Assert.Equal(EpochEndReason.Paused, snapshot.Epochs[0].EndReason);
        Assert.Equal(EpochEndReason.Stopped, snapshot.Epochs[1].EndReason);

        // The gap between epochs is preserved, not collapsed.
        TimeSpan gap = snapshot.Epochs[1].StartedUtc - snapshot.Epochs[0].EndedUtc!.Value;
        Assert.Equal(TimeSpan.FromMinutes(5), gap);
    }

    [Fact]
    public void AFormatChangeBetweenEpochsIsRepresentedPerChunk()
    {
        string id = BuildTwoEpochSession();
        new SessionRecoveryService(_store, new FakeChunkRepairer()).Recover(id);

        SessionSnapshot snapshot = _store.ReadSnapshot(id)!;
        SessionTrack microphone = snapshot.Tracks.First(t => t.Track == SourceTrack.Microphone);

        AudioChunkMetadata first = microphone.Chunks.First(c => c.EpochIndex == 1);
        AudioChunkMetadata second = microphone.Chunks.First(c => c.EpochIndex == 2);

        // The second epoch negotiated 16 kHz mono. Applying it retroactively to epoch 1 would
        // misreport that chunk's duration by a factor of three.
        Assert.Equal(48_000, first.SampleRate);
        Assert.Equal(2, first.Channels);
        Assert.Equal(16_000, second.SampleRate);
        Assert.Equal(1, second.Channels);

        Assert.Equal(60, first.EndSecondsWithin(), 1);
        Assert.Equal(60, second.EndSecondsWithin(), 1);
    }

    [Fact]
    public void DeviceMetadataIsPreservedAcrossRepeatedTrackOpens()
    {
        string id = BuildTwoEpochSession();
        new SessionRecoveryService(_store, new FakeChunkRepairer()).Recover(id);

        SessionSnapshot snapshot = _store.ReadSnapshot(id)!;

        Assert.Equal(2, snapshot.Tracks.Count);
        Assert.Equal("Yeti Nano", snapshot.Tracks.First(t => t.Track == SourceTrack.Microphone).DeviceName);
        Assert.Equal("Headphones", snapshot.Tracks.First(t => t.Track == SourceTrack.System).DeviceName);
    }

    [Fact]
    public void ReconstructionIsStableWhenRunTwice()
    {
        string id = BuildTwoEpochSession();
        SessionRecoveryService recovery = new(_store, new FakeChunkRepairer());

        recovery.Recover(id);
        DirectorySnapshot afterFirst = DirectorySnapshot.Capture(_temp.Path);

        RecoveryOutcome second = recovery.Recover(id);

        Assert.False(second.SnapshotRebuilt);
        Assert.True(DirectorySnapshot.Capture(_temp.Path).Matches(afterFirst));
    }
}

internal static class ChunkTestExtensions
{
    /// <summary>Duration of a chunk in seconds, from its own recorded format.</summary>
    public static double EndSecondsWithin(this AudioChunkMetadata chunk) =>
        chunk.SampleRate == 0 ? 0 : (double)chunk.SampleFrames / chunk.SampleRate;
}
