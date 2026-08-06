using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Recording;
using EchoForge.Core.Storage;

namespace EchoForge.UnitTests;

/// <summary>A capture engine that records what it was asked to do and emits chunks on command.</summary>
public sealed class FakeCaptureEngine : ICaptureEngine
{
    private readonly List<AudioChunkMetadata> _chunks = [];
    private readonly Dictionary<SourceTrack, TrackLiveStatus> _tracks;

    public FakeCaptureEngine(CaptureRequest request)
    {
        Request = request;
        _tracks = new Dictionary<SourceTrack, TrackLiveStatus>
        {
            [SourceTrack.System] = Track(SourceTrack.System, "render-id", "Fake Headphones"),
            [SourceTrack.Microphone] = Track(SourceTrack.Microphone, "capture-id", "Fake Microphone"),
        };
    }

    public CaptureRequest Request { get; }

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public int StopCount { get; private set; }

    public long StopQpc { get; private set; }

    public bool ThrowOnStart { get; set; }

    public IReadOnlyList<AudioChunkMetadata> CompletedChunks => _chunks;

    public event EventHandler<ChunkFinalizedEventArgs>? ChunkFinalized;

    public event EventHandler<TrackFaultedEventArgs>? TrackFaulted;

    public void Start()
    {
        if (ThrowOnStart)
        {
            throw new InvalidOperationException("endpoint refused to open");
        }

        Started = true;
    }

    public void Stop(long stopQpc)
    {
        if (Stopped)
        {
            return;
        }

        Stopped = true;
        StopCount++;
        StopQpc = stopQpc;
    }

    public RecorderStatus Status() => new(
        Started && !Stopped,
        TimeSpan.FromSeconds(_chunks.Count),
        _chunks.Sum(c => c.SampleFrames * 4),
        [.. _tracks.Values]);

    /// <summary>Finalizes one chunk on the given track, continuing this epoch's numbering.</summary>
    public AudioChunkMetadata EmitChunk(SourceTrack track, long frames = 48_000)
    {
        int index = _chunks.Count == 0 ? Request.FirstChunkIndex : _chunks.Max(c => c.Index) + 1;
        AudioChunkMetadata chunk = new(
            index,
            $"tracks/{track.ToString().ToLowerInvariant()}/chunks/{index:D6}.wav",
            track,
            0,
            frames / 48_000.0,
            48_000,
            2,
            frames,
            $"hash-{index:D6}",
            [],
            Request.EpochIndex);

        _chunks.Add(chunk);
        ChunkFinalized?.Invoke(this, new ChunkFinalizedEventArgs(chunk));
        return chunk;
    }

    /// <summary>Marks a track as no longer capturing, as a device loss or thread fault would.</summary>
    public void FailTrack(SourceTrack track, string fault)
    {
        _tracks[track] = _tracks[track] with { IsCapturing = false, Fault = fault };
        TrackFaulted?.Invoke(this, new TrackFaultedEventArgs(track, fault));
    }

    public void Dispose() => Disposed = true;

    private static TrackLiveStatus Track(SourceTrack track, string id, string name) =>
        new(track, id, name, new CaptureFormat(48_000, 2, 16), true, null, 0, 0, 0, 0, 0);
}

/// <summary>Hands out fake engines and keeps every one it created.</summary>
public sealed class FakeCaptureEngineFactory : ICaptureEngineFactory
{
    public List<FakeCaptureEngine> Created { get; } = [];

    public bool FailNextStart { get; set; }

    public FakeCaptureEngine Latest => Created[^1];

    public ICaptureEngine Create(CaptureRequest request)
    {
        FakeCaptureEngine engine = new(request) { ThrowOnStart = FailNextStart };
        FailNextStart = false;
        Created.Add(engine);
        return engine;
    }
}

/// <summary>A clock the test drives by hand, so no test depends on real elapsed time.</summary>
public sealed class FakeCaptureClock : ICaptureClock
{
    private long _qpc;
    private DateTimeOffset _utc = new(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);

    public long NowQpc() => _qpc;

    public DateTimeOffset UtcNow() => _utc;

    public void Advance(TimeSpan amount)
    {
        _qpc += (long)(amount.TotalSeconds * 10_000_000);
        _utc = _utc.Add(amount);
    }
}

/// <summary>Free space the test controls.</summary>
public sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
{
    public long Available { get; set; } = 500_000_000_000;

    public long AvailableBytes(string path) => Available;
}

/// <summary>A repairer whose verdict the test chooses, so recovery is tested without real WAVs.</summary>
public sealed class FakeChunkRepairer : IActiveChunkRepairer
{
    public bool RepairSucceeds { get; set; } = true;

    public long FrameCount { get; set; } = 48_000;

    public int TrimmedBytes { get; set; }

    public List<string> Repaired { get; } = [];

    public ChunkRepairOutcome Repair(string partPath, CaptureFormat format)
    {
        Repaired.Add(partPath);
        return RepairSucceeds
            ? new ChunkRepairOutcome(true, FrameCount, TrimmedBytes, null)
            : new ChunkRepairOutcome(false, 0, 0, "header could not be reconstructed");
    }

    public ChunkValidation Validate(string chunkPath) => new(true, FrameCount, null);
}
