using EchoForge.Contracts.Audio;

namespace EchoForge.Contracts.Recording;

/// <summary>What a track is doing right now.</summary>
public sealed record TrackLiveStatus(
    SourceTrack Track,
    string DeviceId,
    string DeviceName,
    CaptureFormat Format,
    bool IsCapturing,
    string? Fault,
    double PeakLevel,
    long SessionFrames,
    int CompletedChunks,
    long QueuedFrames,
    long DroppedFrames)
{
    public bool IsHealthy => IsCapturing && Fault is null;
}

/// <summary>Everything the UI needs for one refresh, taken from the recorder, not inferred.</summary>
public sealed record RecorderStatus(
    bool IsCapturing,
    TimeSpan Elapsed,
    long BytesWritten,
    IReadOnlyList<TrackLiveStatus> Tracks)
{
    public bool IsDegraded => Tracks.Count > 0 && Tracks.Any(t => !t.IsHealthy) && Tracks.Any(t => t.IsHealthy);

    public bool AllTracksFailed => Tracks.Count > 0 && Tracks.All(t => !t.IsHealthy);
}

/// <summary>What to capture, with the endpoints pinned by stable ID for the whole epoch.</summary>
public sealed record CaptureRequest(
    string RenderEndpointId,
    string CaptureEndpointId,
    string TracksRoot,
    long EpochQpc,
    int FirstChunkIndex);

/// <summary>
/// The capture engine, abstracted so the recording state machine can be driven by a fake.
///
/// <para>
/// One instance captures exactly one epoch. Pause and resume are the state machine's concern:
/// it disposes an engine to close an epoch and constructs a new one to open the next, which is
/// what guarantees a finalized chunk is never appended to.
/// </para>
/// </summary>
public interface ICaptureEngine : IDisposable
{
    /// <summary>Opens both endpoints and begins capturing.</summary>
    void Start();

    /// <summary>Stops capture and finalizes every chunk. Idempotent.</summary>
    void Stop(long stopQpc);

    /// <summary>Current counters. Safe to call at any time, including before start.</summary>
    RecorderStatus Status();

    /// <summary>Chunks finalized by this epoch, in order.</summary>
    IReadOnlyList<AudioChunkMetadata> CompletedChunks { get; }
}

/// <summary>Creates capture engines. Injected so the state machine can be tested without hardware.</summary>
public interface ICaptureEngineFactory
{
    ICaptureEngine Create(CaptureRequest request);
}

/// <summary>Reads the performance counter. Injected so tests can drive time deterministically.</summary>
public interface ICaptureClock
{
    /// <summary>Current performance-counter value in 100-nanosecond units.</summary>
    long NowQpc();

    /// <summary>Current wall-clock time, for journal timestamps only.</summary>
    DateTimeOffset UtcNow();
}
