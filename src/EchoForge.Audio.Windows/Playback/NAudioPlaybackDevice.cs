using System.Runtime.InteropServices;
using EchoForge.Contracts.Playback;
using NAudio;
using NAudio.Wave;

namespace EchoForge.Audio.Windows.Playback;

/// <summary>
/// The real output device, behind the transport's abstraction.
///
/// <para>
/// Everything that decides <i>what</i> to play lives in the transport; this only carries frames to
/// a sound card. That split is what lets the seek criterion be measured deterministically — the
/// tests drive the same transport with a device that has no hardware behind it, and nothing about
/// which frame is logically playing depends on a driver having been reached.
/// </para>
///
/// <para>
/// The latency is set well below the seek criterion on purpose. NAudio's default would leave up to
/// a third of a second of already-queued audio to drain, so a jump would be logically exact and
/// audibly late; four fifty-millisecond buffers keep a flush cheap and the gap imperceptible.
/// </para>
/// </summary>
public sealed class NAudioPlaybackDevice : IPlaybackDevice
{
    private const int LatencyMilliseconds = 200;

    private const int Buffers = 4;

    private readonly Lock _sync = new();

    private WaveOutEvent? _output;
    private RendererWaveProvider? _provider;
    private PlaybackFormat _format;
    private bool _flushing;
    private bool _disposed;

    public event EventHandler<PlaybackFailedEventArgs>? Failed;

    public bool IsPlaying
    {
        get { lock (_sync) { return _output?.PlaybackState == NAudio.Wave.PlaybackState.Playing; } }
    }

    /// <summary>
    /// Frames handed over that have not been heard yet, from the driver's own position.
    ///
    /// <para>
    /// Reported so the transport can subtract it and show what is audible rather than what has
    /// been queued. Zero when the device cannot say, which is honest: an estimate would be a
    /// second opinion about time, and the whole design has exactly one.
    /// </para>
    /// </summary>
    public long PendingFrames
    {
        get
        {
            lock (_sync)
            {
                if (_output is null || _provider is null || _format.BytesPerFrame <= 0)
                {
                    return 0;
                }

                try
                {
                    long played = _output.GetPosition() / _format.BytesPerFrame;
                    return Math.Max(0, _provider.FramesHandedOver - played);
                }
                catch (Exception ex) when (ex is MmException or InvalidOperationException or ObjectDisposedException)
                {
                    return 0;
                }
            }
        }
    }

    public void Open(PlaybackFormat format, IPlaybackRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            Close();

            _format = format;
            _provider = new RendererWaveProvider(renderer, format);

            try
            {
                _output = new WaveOutEvent
                {
                    DesiredLatency = LatencyMilliseconds,
                    NumberOfBuffers = Buffers,
                };

                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(_provider);
            }
            catch (Exception ex) when (ex is MmException or InvalidOperationException or COMException or ArgumentException)
            {
                Close();

                throw new PlaybackDeviceException(
                    "playback_device_unavailable",
                    "No audio output device is available, so this meeting cannot be played here. " +
                    "Your recording is unaffected.");
            }
        }
    }

    public void Play()
    {
        lock (_sync)
        {
            if (_disposed || _output is null)
            {
                return;
            }

            try
            {
                _output.Play();
            }
            catch (Exception ex) when (ex is MmException or InvalidOperationException or ObjectDisposedException)
            {
                throw new PlaybackDeviceException(
                    "playback_device_lost",
                    "The audio device stopped responding, so playback stopped. Your recording is unaffected.");
            }
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed || _output is null)
            {
                return;
            }

            try
            {
                _output.Pause();
            }
            catch (Exception ex) when (ex is MmException or InvalidOperationException or ObjectDisposedException)
            {
                // Pausing something that is already not playing is not a failure worth surfacing.
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed || _output is null)
            {
                return;
            }

            _flushing = true;

            try
            {
                _output.Stop();
            }
            catch (Exception ex) when (ex is MmException or InvalidOperationException or ObjectDisposedException)
            {
                // Nothing to stop.
            }
            finally
            {
                _flushing = false;
                _provider?.ResetHandover();
            }
        }
    }

    /// <summary>
    /// Drops everything already queued.
    ///
    /// <para>
    /// Stopping and restarting is what discards the driver's buffers; the flag is what stops that
    /// being mistaken for the meeting having ended.
    /// </para>
    /// </summary>
    public void Flush()
    {
        lock (_sync)
        {
            if (_disposed || _output is null)
            {
                return;
            }

            bool wasPlaying = _output.PlaybackState == NAudio.Wave.PlaybackState.Playing;
            _flushing = true;

            try
            {
                _output.Stop();
                _provider?.ResetHandover();

                if (wasPlaying)
                {
                    _output.Play();
                }
            }
            catch (Exception ex) when (ex is MmException or InvalidOperationException or ObjectDisposedException)
            {
                throw new PlaybackDeviceException(
                    "playback_device_lost",
                    "The audio device stopped responding, so playback stopped. Your recording is unaffected.");
            }
            finally
            {
                _flushing = false;
            }
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        bool flushing;
        lock (_sync)
        {
            flushing = _flushing;
        }

        // A stop we asked for is not a failure, and neither is running off the end of the meeting:
        // the transport already knows, because it is the thing that stopped producing frames.
        if (flushing || e.Exception is null)
        {
            return;
        }

        Failed?.Invoke(this, new PlaybackFailedEventArgs(new PlaybackFailure(
            "playback_device_lost",
            "The audio device stopped responding, so playback stopped. Your recording is unaffected.")));
    }

    private void Close()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;

            try
            {
                _output.Stop();
            }
            catch (Exception ex) when (ex is MmException or InvalidOperationException or ObjectDisposedException)
            {
                // Already gone.
            }

            _output.Dispose();
            _output = null;
        }

        _provider = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _flushing = true;
            Close();
        }
    }

    /// <summary>
    /// Adapts the transport to NAudio's pull interface, and counts what it has handed over.
    ///
    /// <para>
    /// Returning fewer bytes than asked for is how NAudio is told a stream ended, which is exactly
    /// what the transport means when it renders a short block at the end of a meeting.
    /// </para>
    /// </summary>
    private sealed class RendererWaveProvider(IPlaybackRenderer renderer, PlaybackFormat format) : IWaveProvider
    {
        private readonly IPlaybackRenderer _renderer = renderer;
        private long _handedOver;

        public WaveFormat WaveFormat { get; } = new(format.SampleRate, 16, format.Channels);

        /// <summary>Frames given to the driver since the last reset.</summary>
        public long FramesHandedOver => Interlocked.Read(ref _handedOver);

        public void ResetHandover() => Interlocked.Exchange(ref _handedOver, 0);

        public int Read(byte[] buffer, int offset, int count)
        {
            int bytesPerFrame = WaveFormat.Channels * 2;
            int frames = count / bytesPerFrame;

            if (frames <= 0)
            {
                return 0;
            }

            Span<short> samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, frames * bytesPerFrame));
            int produced = _renderer.Render(samples, frames);

            if (produced <= 0)
            {
                return 0;
            }

            Interlocked.Add(ref _handedOver, produced);
            return produced * bytesPerFrame;
        }
    }
}
