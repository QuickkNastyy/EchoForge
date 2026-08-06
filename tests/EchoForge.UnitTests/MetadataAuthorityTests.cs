using System.Text.Json;
using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// Metadata is evidence, not testimony. A finalized WAV plus a <em>validated</em> record is the
/// canonical source chunk; where a record contradicts the audio, the filename, the path, or the
/// journal, nothing is changed and the session is flagged instead.
/// </summary>
public sealed class MetadataAuthorityTests : IDisposable
{
    private static readonly CaptureFormat Mono48 = new(48_000, 1, 16);

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _store;
    private readonly WavChunkRepairer _repairer = new();

    public MetadataAuthorityTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private const string Id = "auth";

    /// <summary>Writes one valid finalized chunk plus a correct record, then returns both paths.</summary>
    private (SessionPaths Paths, string Wav, string Meta) BuildChunk(long frames = 4_800)
    {
        SessionPaths paths = _store.Create(Id);
        string chunks = Path.Combine(paths.TrackRoot(SourceTrack.Microphone), "chunks");
        Directory.CreateDirectory(chunks);

        string wav = Path.Combine(chunks, "000001.wav");
        using (WavPcm16Writer writer = new(wav, Mono48))
        {
            writer.WriteSilence(frames);
            writer.Close();
        }

        string meta = Path.Combine(chunks, "000001.meta.json");
        WriteRecord(meta, CorrectRecord(wav, frames));

        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));
        return (paths, wav, meta);
    }

    private static ChunkRecord CorrectRecord(string wav, long frames) => new()
    {
        Track = "Microphone",
        Index = 1,
        Epoch = 2,
        SampleRate = 48_000,
        Channels = 1,
        BitsPerSample = 16,
        Frames = frames,
        StartSeconds = 120.0,
        EpochQpc = 5_000,
        RelativePath = "tracks/microphone/chunks/000001.wav",
        Sha256 = Sha256(wav),
        Finalized = true,
        Discontinuities = [],
    };

    private static void WriteRecord(string path, ChunkRecord record) =>
        File.WriteAllText(path, JsonSerializer.Serialize(record, ChunkRecord.Json));

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private RecoveryOutcome Recover() => new SessionRecoveryService(_store, _repairer).Recover(Id);

    private static void Corrupt(string meta, string wav, long frames, Func<ChunkRecord, ChunkRecord> mutate) =>
        WriteRecord(meta, mutate(CorrectRecord(wav, frames)));

    [Fact]
    public void ACorrectRecordIsAcceptedAndBecomesCanonical()
    {
        (_, _, _) = BuildChunk();

        RecoveryOutcome outcome = Recover();

        Assert.Equal(1, outcome.ChunksReconciled);
        Assert.Equal(SessionState.Recorded, outcome.State);

        AudioChunkMetadata chunk = Assert.Single(_store.ReadSnapshot(Id)!.Tracks.SelectMany(t => t.Chunks));
        Assert.Equal(2, chunk.EpochIndex);
        Assert.Equal(120.0, chunk.StartSeconds, 3);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("not-finalized")]
    [InlineData("wrong-track")]
    [InlineData("wrong-index")]
    [InlineData("escaping-path")]
    [InlineData("mismatched-path")]
    [InlineData("bad-format")]
    [InlineData("bad-epoch")]
    [InlineData("negative-start")]
    [InlineData("wrong-frames")]
    [InlineData("wrong-rate")]
    [InlineData("wrong-hash")]
    public void ACorruptFieldIsRefusedAndNothingIsChanged(string corruption)
    {
        (_, string wav, string meta) = BuildChunk();

        Corrupt(meta, wav, 4_800, record => corruption switch
        {
            "schema" => record with { SchemaVersion = 99 },
            "not-finalized" => record with { Finalized = false },
            "wrong-track" => record with { Track = "System" },
            "wrong-index" => record with { Index = 7 },
            "escaping-path" => record with { RelativePath = "../../elsewhere/000001.wav" },
            "mismatched-path" => record with { RelativePath = "tracks/microphone/chunks/000009.wav" },
            "bad-format" => record with { Channels = 0 },
            "bad-epoch" => record with { Epoch = 0 },
            "negative-start" => record with { StartSeconds = -5 },
            "wrong-frames" => record with { Frames = 999_999 },
            "wrong-rate" => record with { SampleRate = 16_000 },
            _ => record with { Sha256 = new string('a', 64) },
        });

        string wavHashBefore = Sha256(wav);
        string metaBefore = File.ReadAllText(meta);

        RecoveryOutcome outcome = Recover();

        Assert.Equal(0, outcome.ChunksReconciled);
        Assert.Equal(SessionState.NeedsAttention, outcome.State);
        Assert.Contains(outcome.Notes, n => n.Contains("could not be verified", StringComparison.Ordinal));

        // The audio and its record are preserved byte for byte, exactly as found.
        Assert.True(File.Exists(wav));
        Assert.True(File.Exists(meta));
        Assert.Equal(wavHashBefore, Sha256(wav));
        Assert.Equal(metaBefore, File.ReadAllText(meta));

        // Nothing was journalled as canonical.
        JournalReadResult journal = _store.ReadJournal(Id);
        Assert.DoesNotContain(journal.Events, e => e.Type == JournalEventTypes.ChunkCompleted);
    }

    [Fact]
    public void AJournalEntryThatContradictsTheRecordWins()
    {
        (_, _, _) = BuildChunk();

        // The ledger already describes this chunk with a different hash.
        _store.Append(Id, JournalEvent.Create(
            JournalEventTypes.ChunkCompleted, DateTimeOffset.UnixEpoch,
            ("track", "Microphone"), ("index", "1"), ("epoch", "2"),
            ("frames", "4800"), ("start_seconds", "120"),
            ("sample_rate", "48000"), ("channels", "1"), ("sha256", "adifferenthash")));

        RecoveryOutcome outcome = Recover();

        // The chunk is already known, so reconciliation leaves it alone rather than re-journalling.
        Assert.Equal(0, outcome.ChunksReconciled);
    }

    [Fact]
    public void AFinalizedWavWithNoRecordIsReconstructedFromItsActiveSidecar()
    {
        SessionPaths paths = _store.Create(Id);
        string track = paths.TrackRoot(SourceTrack.Microphone);
        string chunks = Path.Combine(track, "chunks");
        string active = Path.Combine(track, "active");
        Directory.CreateDirectory(chunks);
        Directory.CreateDirectory(active);

        // Crash window: renamed and hashed, but the finalized record was never written.
        string wav = Path.Combine(chunks, "000001.wav");
        using (WavPcm16Writer writer = new(wav, Mono48))
        {
            writer.WriteSilence(4_800);
            writer.Close();
        }

        WriteRecord(Path.Combine(active, "000001.part.state.json"), CorrectRecord(wav, 0) with
        {
            Finalized = false,
            Sha256 = null,
            RelativePath = "tracks/microphone/active/000001.part.wav",
        });

        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));

        RecoveryOutcome outcome = Recover();

        Assert.Equal(1, outcome.ChunksReconciled);
        Assert.Contains(outcome.Notes, n => n.Contains("reconstructed", StringComparison.Ordinal));

        // The epoch and start time came from the sidecar, not from a zero default.
        AudioChunkMetadata chunk = Assert.Single(_store.ReadSnapshot(Id)!.Tracks.SelectMany(t => t.Chunks));
        Assert.Equal(2, chunk.EpochIndex);
        Assert.Equal(120.0, chunk.StartSeconds, 3);
        Assert.Equal(4_800, chunk.SampleFrames);

        // A finalized record now exists, and the sidecar was archived rather than deleted.
        Assert.True(File.Exists(Path.Combine(chunks, "000001.meta.json")));
        Assert.False(File.Exists(Path.Combine(active, "000001.part.state.json")));
        Assert.True(Directory.Exists(Path.Combine(paths.DiagnosticsRoot, "recovered-sidecars")));
    }

    [Fact]
    public void ARepairedChunkIsJournalledExactlyOnce()
    {
        SessionPaths paths = _store.Create(Id);
        string track = paths.TrackRoot(SourceTrack.Microphone);
        string active = Path.Combine(track, "active");
        Directory.CreateDirectory(active);
        Directory.CreateDirectory(Path.Combine(track, "chunks"));

        // An abandoned active chunk with a valid record: repair promotes it, and reconciliation
        // must then recognise it as already known rather than adding a second event.
        string part = Path.Combine(active, "000001.part.wav");
        using (FileStream stream = new(part, FileMode.Create))
        {
            WavPcm16Writer.WriteHeader(stream, Mono48, dataBytes: 0);
            stream.Write(new byte[2 * 4_800]);
        }

        WriteRecord(Path.Combine(active, "000001.part.state.json"), new ChunkRecord
        {
            Track = "Microphone",
            Index = 1,
            Epoch = 3,
            SampleRate = 48_000,
            Channels = 1,
            Frames = 0,
            StartSeconds = 60.0,
            EpochQpc = 1,
            RelativePath = "tracks/microphone/active/000001.part.wav",
            Finalized = false,
            Discontinuities = [],
        });

        _store.Append(Id, JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));

        RecoveryOutcome outcome = Recover();

        Assert.Equal(1, outcome.ChunksRecovered);
        Assert.Equal(0, outcome.ChunksReconciled);

        JournalReadResult journal = _store.ReadJournal(Id);
        Assert.Single(journal.Events, e =>
            e.Type == JournalEventTypes.ChunkCompleted && e.IntField("index") == 1);
    }

    [Fact]
    public void TrackDurationSumsEachChunkAtItsOwnRate()
    {
        SessionTrack track = new(
            SourceTrack.Microphone, "id", "Mic", new CaptureFormat(16_000, 1, 16),
            [
                new AudioChunkMetadata(1, "a", SourceTrack.Microphone, 0, 60, 48_000, 2, 48_000 * 60, "x", [], 1),
                new AudioChunkMetadata(2, "b", SourceTrack.Microphone, 0, 60, 16_000, 1, 16_000 * 60, "y", [], 2),
            ]);

        // Sixty seconds at 48 kHz plus sixty at 16 kHz. Measuring both at the track's latest
        // 16 kHz format would report the first chunk as three minutes.
        Assert.Equal(TimeSpan.FromMinutes(2), track.Duration);
    }
}
