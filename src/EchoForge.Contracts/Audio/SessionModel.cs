namespace EchoForge.Contracts.Audio;

/// <summary>A selectable audio endpoint, identified by its stable Windows endpoint ID.</summary>
/// <param name="Id">Stable endpoint ID. Pinned at Start; EchoForge never follows the default device silently.</param>
/// <param name="FriendlyName">Display name. Useful session metadata, kept out of general logs.</param>
/// <param name="IsDefault">Whether Windows currently treats this as the default endpoint.</param>
/// <param name="MixFormat">The shared-mode mix format the endpoint reports.</param>
public sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    bool IsDefault,
    CaptureFormat MixFormat);

/// <summary>Why a run of frames is missing or suspect. Preserved in the record; never hidden.</summary>
public enum DiscontinuityKind
{
    /// <summary>No packets arrived because the endpoint produced no audio.</summary>
    Silence,

    /// <summary>The engine reported dropped data.</summary>
    EngineDiscontinuity,

    /// <summary>The bounded queue overflowed and the writer could not keep up.</summary>
    QueueOverflow,

    /// <summary>The packet timestamp was flagged unreliable.</summary>
    TimestampError,
}

/// <summary>A recorded gap or fault on one track, expressed in device frames.</summary>
public sealed record CaptureDiscontinuity(
    DiscontinuityKind Kind,
    long AtDevicePosition,
    long FrameCount,
    string Detail)
{
    public static CaptureDiscontinuity Silence(long at, long frames) =>
        new(DiscontinuityKind.Silence, at, frames, "no packets; silence inserted from device position");
}

/// <summary>
/// A finalized, immutable source chunk. Source audio is never rewritten after finalization.
/// </summary>
public sealed record AudioChunkMetadata(
    int Index,
    string RelativePath,
    SourceTrack Track,
    double StartSeconds,
    double EndSeconds,
    int SampleRate,
    int Channels,
    long SampleFrames,
    string Sha256,
    IReadOnlyList<CaptureDiscontinuity> Discontinuities);

/// <summary>
/// A continuous stretch of capture on one shared clock. Pause, device loss, and
/// sleep all close an epoch; resuming opens a new one with an explicit gap.
/// </summary>
public sealed record CaptureEpoch(
    int Index,
    long StartQpc,
    long? EndQpc,
    long QpcFrequency)
{
    public TimeSpan? Duration => EndQpc is null
        ? null
        : TimeSpan.FromSeconds((double)(EndQpc.Value - StartQpc) / QpcFrequency);
}
