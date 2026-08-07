using EchoForge.Contracts.Playback;

namespace EchoForge.UnitTests;

/// <summary>
/// An output device with no hardware behind it.
///
/// <para>
/// Every routine playback test uses this. A test that waited for a speaker would be measuring the
/// machine it happened to run on, and would fail on a build agent with no sound card — which is
/// precisely why the transport owns the logical position and the device owns nothing but frames.
/// </para>
/// </summary>
public sealed class FakePlaybackDevice : IPlaybackDevice
{
    private IPlaybackRenderer? _renderer;

    public PlaybackFormat Format { get; private set; }

    public bool IsOpen { get; private set; }

    public bool IsPlaying { get; private set; }

    public bool IsDisposed { get; private set; }

    public int Opens { get; private set; }

    public int Flushes { get; private set; }

    public int Stops { get; private set; }

    /// <summary>Set to simulate a device that has queued frames it has not yet played.</summary>
    public long PendingFrames { get; set; }

    /// <summary>Set to make <see cref="Open"/> refuse, as a machine with no output would.</summary>
    public PlaybackDeviceException? OpenFailure { get; set; }

    /// <summary>Everything the transport has rendered, so a test can check what would be heard.</summary>
    public List<short> Rendered { get; } = [];

    public event EventHandler<PlaybackFailedEventArgs>? Failed;

    public void Open(PlaybackFormat format, IPlaybackRenderer renderer)
    {
        if (OpenFailure is not null)
        {
            throw OpenFailure;
        }

        Format = format;
        _renderer = renderer;
        IsOpen = true;
        Opens++;
    }

    public void Play() => IsPlaying = true;

    public void Pause() => IsPlaying = false;

    public void Stop()
    {
        IsPlaying = false;
        PendingFrames = 0;
        Stops++;
    }

    public void Flush()
    {
        Flushes++;
        PendingFrames = 0;
    }

    /// <summary>Pulls frames the way a driver would, and returns how many it got.</summary>
    public int Pump(int frames)
    {
        short[] buffer = new short[frames * Math.Max(1, Format.Channels)];
        int produced = _renderer?.Render(buffer, frames) ?? 0;

        Rendered.AddRange(buffer.AsSpan(0, produced * Math.Max(1, Format.Channels)).ToArray());
        return produced;
    }

    /// <summary>Reports a device that went away mid-playback.</summary>
    public void FailNow(string message = "The audio device stopped responding.") =>
        Failed?.Invoke(this, new PlaybackFailedEventArgs(new PlaybackFailure("playback_device_lost", message)));

    public void Dispose()
    {
        IsDisposed = true;
        IsPlaying = false;
        IsOpen = false;
    }
}

/// <summary>
/// A source whose sample at frame <c>n</c> encodes <c>n</c>, so a test can read the position back
/// out of what was rendered rather than trusting the transport to report it honestly.
/// </summary>
public sealed class CountingPlaybackSource(int sampleRate, long totalFrames, int channels = 2) : IPlaybackAudioSource
{
    public int SampleRate { get; } = sampleRate;

    public int Channels { get; } = channels;

    public long TotalFrames { get; } = totalFrames;

    public double DurationSeconds => (double)TotalFrames / SampleRate;

    public bool IsDisposed { get; private set; }

    public int Read(long startFrame, Span<short> destination, int frames)
    {
        if (startFrame < 0 || startFrame >= TotalFrames)
        {
            return 0;
        }

        int want = (int)Math.Min(frames, TotalFrames - startFrame);
        want = Math.Min(want, destination.Length / Channels);

        for (int i = 0; i < want; i++)
        {
            for (int channel = 0; channel < Channels; channel++)
            {
                destination[(i * Channels) + channel] = (short)((startFrame + i) % 1000);
            }
        }

        return want;
    }

    public void Dispose() => IsDisposed = true;
}
