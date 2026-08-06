namespace EchoForge.Contracts.Audio;

/// <summary>Which physical source a track was captured from. Never merged.</summary>
public enum SourceTrack
{
    /// <summary>Loopback of the selected render endpoint. Labelled "Remote".</summary>
    System,

    /// <summary>The selected capture endpoint. Always labelled "You".</summary>
    Microphone,
}

/// <summary>
/// Per-packet condition flags reported by the audio engine. Mirrors the WASAPI buffer
/// flags without taking a dependency on any audio library.
/// </summary>
[Flags]
public enum AudioPacketConditions
{
    None = 0,

    /// <summary>The engine dropped data before this packet. Recorded, never smoothed over.</summary>
    DataDiscontinuity = 1,

    /// <summary>The packet is digital silence and its payload may be ignored.</summary>
    Silent = 2,

    /// <summary>The device position or QPC position for this packet is unreliable.</summary>
    TimestampError = 4,
}

/// <summary>
/// The PCM format a track is actually recorded in. This is the negotiated format, not
/// the requested one; the plan requires storing what the endpoint really gave us.
/// </summary>
public sealed record CaptureFormat(int SampleRate, int Channels, int BitsPerSample)
{
    public int BytesPerFrame => Channels * (BitsPerSample / 8);

    public long FramesForDuration(TimeSpan duration) =>
        (long)Math.Round(duration.TotalSeconds * SampleRate, MidpointRounding.AwayFromZero);

    public TimeSpan DurationForFrames(long frames) =>
        TimeSpan.FromSeconds((double)frames / SampleRate);

    public override string ToString() =>
        $"{SampleRate} Hz · {(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : $"{Channels} ch")} · {BitsPerSample}-bit";
}

/// <summary>
/// The clock anchors for one captured packet, taken straight from the audio engine.
///
/// <para>
/// <see cref="DevicePosition"/> and <see cref="QpcPosition"/> are the authority for the
/// session timeline. The moment managed code observed the packet is deliberately absent
/// from this type: arrival time is not a clock, and the plan names treating it as one as
/// the most likely architectural failure.
/// </para>
/// </summary>
/// <param name="DevicePosition">Device stream position of the first frame, in frames.</param>
/// <param name="QpcPosition">Performance-counter value for the packet, in 100-nanosecond units.</param>
/// <param name="FrameCount">Number of frames in the packet.</param>
/// <param name="Flags">Condition flags reported with the packet.</param>
public readonly record struct PacketHeader(
    long DevicePosition,
    long QpcPosition,
    int FrameCount,
    AudioPacketConditions Flags)
{
    /// <summary>Device position immediately after this packet.</summary>
    public long EndDevicePosition => DevicePosition + FrameCount;
}
