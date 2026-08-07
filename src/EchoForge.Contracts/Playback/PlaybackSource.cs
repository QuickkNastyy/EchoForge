namespace EchoForge.Contracts.Playback;

/// <summary>
/// Random-access interleaved PCM16 over one meeting's aligned playback derivative.
///
/// <para>
/// Addressed by absolute frame rather than by a stream position, because that is what makes a seek
/// exact: frame <c>n</c> is session second <c>n / rate</c> by construction of the derivative, so
/// there is no cursor to keep in step and nothing that can drift away from the transcript.
/// </para>
/// </summary>
public interface IPlaybackAudioSource : IDisposable
{
    int SampleRate { get; }

    /// <summary>Two: microphone in channel 0, system in channel 1.</summary>
    int Channels { get; }

    long TotalFrames { get; }

    double DurationSeconds => SampleRate <= 0 ? 0 : (double)TotalFrames / SampleRate;

    /// <summary>
    /// Reads interleaved frames starting at an absolute frame. Returns how many frames were read,
    /// which is 0 at or past the end.
    /// </summary>
    int Read(long startFrame, Span<short> destination, int frames);
}

/// <summary>
/// Per-track listening levels.
///
/// <para>
/// Applied on the way to the device, never written into the derivative. Muting a track is
/// therefore free and reversible, and it cannot change what a citation points at.
/// </para>
/// </summary>
public sealed record PlaybackMix
{
    public static readonly PlaybackMix Default = new();

    public bool MuteYou { get; init; }

    public bool MuteRemote { get; init; }

    /// <summary>0 to 1. Clamped rather than trusted.</summary>
    public double YouLevel { get; init; } = 1.0;

    public double RemoteLevel { get; init; } = 1.0;
}
