using EchoForge.Contracts.Playback;

namespace EchoForge.Core.Playback;

/// <summary>What changed, so a surface can re-read the transport without polling for state.</summary>
public sealed class PlaybackStateChangedEventArgs(PlaybackState state, string? message) : EventArgs
{
    public PlaybackState State { get; } = state;

    public string? Message { get; } = message;
}

/// <summary>
/// The transport: play, pause, stop, seek, and the one number that says where in the meeting the
/// audio is.
///
/// <para>
/// <b>The canonical session timeline is the authority, and this class introduces no second one.</b>
/// The derivative is laid out so that frame <c>n</c> <i>is</i> session second <c>n / rate</c> —
/// gaps included as real silence, epochs placed at their real offsets — so seeking to a transcript
/// timestamp is one multiplication, not a search through chunks and an accumulated estimate. That
/// is why a jump lands within a sample of where it was asked to, at the start of a meeting and
/// three hours later alike.
/// </para>
///
/// <para>
/// <b>The device pulls; it never decides where it is.</b> A seek moves one number and asks the
/// device to drop what it has already queued. The logical position is correct the instant the seek
/// returns, whether or not any hardware exists — which is what makes the accuracy criterion
/// something a test can assert rather than something a listener has to judge.
/// </para>
/// </summary>
public sealed class PlaybackEngine : IPlaybackRenderer, IDisposable
{
    /// <summary>How many device channels the mix is rendered into. Stereo, both sides identical.</summary>
    public const int DeviceChannels = 2;

    private readonly IPlaybackAudioSource _source;
    private readonly IPlaybackDevice _device;
    private readonly bool _hasYou;
    private readonly bool _hasRemote;
    private readonly Lock _sync = new();

    private short[] _scratch = [];
    private long _cursor;
    private PlaybackState _state = PlaybackState.Ready;
    private string? _message;
    private PlaybackMix _mix = PlaybackMix.Default;
    private bool _disposed;

    /// <summary>
    /// Opens a transport over one meeting's aligned audio.
    /// </summary>
    /// <param name="hasYou">
    /// Whether the microphone channel carries anything. Taken from the derivative rather than
    /// guessed from the samples, so a quiet meeting is not mistaken for a missing track.
    /// </param>
    public PlaybackEngine(
        IPlaybackAudioSource source,
        IPlaybackDevice device,
        bool hasYou = true,
        bool hasRemote = true)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _hasYou = hasYou;
        _hasRemote = hasRemote;

        _device.Failed += OnDeviceFailed;

        try
        {
            _device.Open(new PlaybackFormat(_source.SampleRate, DeviceChannels), this);
        }
        catch (PlaybackDeviceException ex)
        {
            // A machine with no usable output is a normal state, not a crash. The meeting stays
            // open and readable; only the transport says it cannot play.
            _state = PlaybackState.Failed;
            _message = ex.Message;
        }
    }

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    public PlaybackState State
    {
        get { lock (_sync) { return _state; } }
    }

    public string? Message
    {
        get { lock (_sync) { return _message; } }
    }

    public double DurationSeconds => _source.DurationSeconds;

    public bool HasYouTrack => _hasYou;

    public bool HasRemoteTrack => _hasRemote;

    /// <summary>
    /// Where the audio is, in session-relative seconds.
    ///
    /// <para>
    /// The read cursor minus whatever the device is still holding, so this is what a listener is
    /// hearing rather than what has been handed over. A device that cannot report its backlog
    /// reports zero, which makes this the read cursor — the same number the seek criterion is
    /// measured against.
    /// </para>
    /// </summary>
    public double PositionSeconds
    {
        get
        {
            long cursor;
            lock (_sync)
            {
                cursor = _cursor;
            }

            long audible = cursor - Math.Max(0, _device.PendingFrames);
            return _source.SampleRate <= 0
                ? 0
                : Math.Clamp((double)Math.Max(0, audible) / _source.SampleRate, 0, DurationSeconds);
        }
    }

    /// <summary>The exact frame the next sample will come from. The seek criterion measures this.</summary>
    public long PositionFrame
    {
        get { lock (_sync) { return _cursor; } }
    }

    public PlaybackMix Mix
    {
        get { lock (_sync) { return _mix; } }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_sync)
            {
                _mix = value;
            }
        }
    }

    public bool CanPlay => State is not (PlaybackState.Failed or PlaybackState.Playing) && DurationSeconds > 0;

    // -- transport -------------------------------------------------------------------------------

    public void Play()
    {
        lock (_sync)
        {
            if (_disposed || _state == PlaybackState.Failed || _source.TotalFrames <= 0)
            {
                return;
            }

            // Reaching the end and pressing play again starts the meeting over, which is what
            // every other player does and what nobody has to be told.
            if (_cursor >= _source.TotalFrames)
            {
                _cursor = 0;
            }

            _state = PlaybackState.Playing;
            _message = null;
        }

        if (!TryDevice(_device.Play, "playback_start_failed"))
        {
            return;
        }

        Raise();
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed || _state != PlaybackState.Playing)
            {
                return;
            }

            _state = PlaybackState.Paused;
        }

        TryDevice(_device.Pause, "playback_pause_failed");
        Raise();
    }

    /// <summary>Stops and returns to the start, which is what stop means everywhere else.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed || _state == PlaybackState.Failed)
            {
                return;
            }

            _cursor = 0;
            _state = PlaybackState.Ready;
        }

        TryDevice(_device.Stop, "playback_stop_failed");
        Raise();
    }

    /// <summary>
    /// Moves to a session-relative moment.
    ///
    /// <para>
    /// Clamped into the meeting rather than refused: a citation a fraction of a second past the
    /// last frame should land at the end, not report an error at somebody who clicked a timestamp.
    /// </para>
    /// </summary>
    public void Seek(double sessionSeconds)
    {
        bool wasPlaying;

        lock (_sync)
        {
            if (_disposed || _state == PlaybackState.Failed)
            {
                return;
            }

            _cursor = FrameAt(sessionSeconds, _source.SampleRate, _source.TotalFrames);
            wasPlaying = _state == PlaybackState.Playing;

            // Seeking out of a finished meeting puts the transport back in a state that can play.
            if (_state == PlaybackState.Ended)
            {
                _state = PlaybackState.Ready;
            }
        }

        // Drop whatever was queued, so what comes out next is where the user asked to be rather
        // than the tail of where they were.
        TryDevice(_device.Flush, "playback_seek_failed");

        if (wasPlaying)
        {
            TryDevice(_device.Play, "playback_seek_failed");
        }

        Raise();
    }

    /// <summary>
    /// The frame a session time lands on: one rounding, from the time itself.
    ///
    /// <para>
    /// The same definition the derivative was written with, which is what makes the two agree
    /// exactly rather than approximately.
    /// </para>
    /// </summary>
    public static long FrameAt(double sessionSeconds, int sampleRate, long totalFrames)
    {
        if (sampleRate <= 0)
        {
            return 0;
        }

        long frame = (long)Math.Round(Math.Max(0, sessionSeconds) * sampleRate, MidpointRounding.AwayFromZero);
        return Math.Clamp(frame, 0, Math.Max(0, totalFrames));
    }

    // -- rendering -------------------------------------------------------------------------------

    /// <summary>
    /// Fills a device buffer from the current position. Runs on the device's thread.
    /// </summary>
    public int Render(Span<short> destination, int frames)
    {
        if (frames <= 0)
        {
            return 0;
        }

        long cursor;
        PlaybackMix mix;

        lock (_sync)
        {
            if (_disposed || _state != PlaybackState.Playing)
            {
                return 0;
            }

            cursor = _cursor;
            mix = _mix;

            int needed = frames * _source.Channels;
            if (_scratch.Length < needed)
            {
                _scratch = new short[needed];
            }
        }

        int read = _source.Read(cursor, _scratch, frames);

        if (read <= 0)
        {
            Finish();
            return 0;
        }

        (double you, double remote) = PlaybackMixer.EffectiveGains(mix, _hasYou, _hasRemote);
        PlaybackMixer.Mix(_scratch, _source.Channels, destination, DeviceChannels, read, you, remote);

        bool ended;
        lock (_sync)
        {
            _cursor = cursor + read;
            ended = _cursor >= _source.TotalFrames;
        }

        if (ended)
        {
            Finish();
        }

        return read;
    }

    private void Finish()
    {
        lock (_sync)
        {
            if (_state != PlaybackState.Playing)
            {
                return;
            }

            _state = PlaybackState.Ended;
        }

        Raise();
    }

    // -- failure ---------------------------------------------------------------------------------

    private void OnDeviceFailed(object? sender, PlaybackFailedEventArgs e) =>
        Fail(e.Failure.Message);

    private bool TryDevice(Action action, string code)
    {
        try
        {
            action();
            return true;
        }
        catch (PlaybackDeviceException ex)
        {
            Fail(ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _ = code;
            Fail("The audio device stopped responding, so playback stopped. Your recording is unaffected.");
            return false;
        }
    }

    private void Fail(string message)
    {
        lock (_sync)
        {
            if (_state == PlaybackState.Failed)
            {
                return;
            }

            _state = PlaybackState.Failed;
            _message = message;
        }

        Raise();
    }

    private void Raise()
    {
        PlaybackState state;
        string? message;

        lock (_sync)
        {
            state = _state;
            message = _message;
        }

        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(state, message));
    }

    /// <summary>
    /// Releases the device and the file handle.
    ///
    /// <para>
    /// Both, and in that order. A closed meeting that left a device open would keep an audio
    /// endpoint claimed for a window nobody can see, and a source left open would keep the
    /// derivative locked against the rebuild that a reprocess needs.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _state = PlaybackState.Idle;
        }

        _device.Failed -= OnDeviceFailed;

        try
        {
            _device.Stop();
        }
        catch (Exception ex) when (ex is PlaybackDeviceException or InvalidOperationException or ObjectDisposedException)
        {
            // Already gone. Disposal must not throw on the way out of a window closing.
        }

        _device.Dispose();
        _source.Dispose();
    }
}
