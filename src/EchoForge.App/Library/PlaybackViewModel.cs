using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Playback;
using EchoForge.Core.Playback;
using EchoForge.Infrastructure.Playback;

namespace EchoForge.App.Library;

/// <summary>
/// The transport a reader sees: a play button, a line to drag, and a clock.
///
/// <para>
/// <b>Preparing happens off the UI thread and only once.</b> The first time a meeting is played its
/// aligned audio has to be built, which for a long meeting means reading every chunk; afterwards
/// the derivative is reused and opening is instant. Neither case may block the thread that paints
/// the recording indicator.
/// </para>
///
/// <para>
/// <b>Jumping to a moment cues it; it does not start playing.</b> One rule, used by the timeline,
/// by a transcript timestamp and by a citation alike: seeking moves the position and leaves the
/// transport as it found it. Clicking a citation while reading in silence should not suddenly make
/// noise, and clicking one while listening should carry straight on from the new place.
/// </para>
/// </summary>
public sealed class PlaybackViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>How often the clock and the timeline catch up while playing.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    private readonly string _sessionId;
    private readonly PlaybackPreparer _preparer;
    private readonly Func<IPlaybackDevice> _devices;
    private readonly DispatcherTimer? _timer;
    private readonly CancellationTokenSource _cancellation = new();

    private PlaybackEngine? _engine;
    private PlaybackState _state = PlaybackState.Idle;
    private string? _message;
    private string? _seekNotice;
    private double _preparation;
    private double _position;
    private double _duration;
    private PlaybackMix _mix = PlaybackMix.Default;
    private bool _hasYou = true;
    private bool _hasRemote = true;
    private bool _disposed;

    public PlaybackViewModel(
        string sessionId,
        PlaybackPreparer preparer,
        Func<IPlaybackDevice> devices,
        bool startTimer = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        _sessionId = sessionId;
        _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));

        PlayPauseCommand = new RelayCommand(TogglePlay, () => _engine is not null && _state != PlaybackState.Failed);
        StopCommand = new RelayCommand(() => _engine?.Stop(), () => _engine is not null && _state != PlaybackState.Failed);

        if (startTimer)
        {
            // A timer rather than an event per frame: the transport is asked where it is a few
            // times a second, which is all a clock and a slider can show, and costs nothing.
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Tick };
            _timer.Tick += (_, _) => Sample();
            _timer.Start();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand PlayPauseCommand { get; }

    public RelayCommand StopCommand { get; }

    public string SessionId => _sessionId;

    public PlaybackState State
    {
        get => _state;
        private set
        {
            _state = value;
            Changed();
            Changed(nameof(IsPlaying));
            Changed(nameof(IsPreparing));
            Changed(nameof(HasFailed));
            Changed(nameof(PlayPauseLabel));
            Changed(nameof(StatusText));
            PlayPauseCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsPlaying => _state == PlaybackState.Playing;

    public bool IsPreparing => _state == PlaybackState.Preparing;

    public bool HasFailed => _state == PlaybackState.Failed;

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>0 to 1 while the aligned audio is being built.</summary>
    public double PreparationFraction
    {
        get => _preparation;
        private set { _preparation = value; Changed(); }
    }

    public string? Message
    {
        get => _message;
        private set { _message = value; Changed(); Changed(nameof(HasMessage)); Changed(nameof(StatusText)); }
    }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    /// <summary>
    /// Says when a jump was approximate.
    ///
    /// <para>
    /// A citation whose transcript revision is gone can only be followed to the time stored with
    /// it. That is genuinely useful and it is not the same thing as landing on the sentence, so it
    /// says so rather than looking identical to an exact seek.
    /// </para>
    /// </summary>
    public string? SeekNotice
    {
        get => _seekNotice;
        private set { _seekNotice = value; Changed(); Changed(nameof(HasSeekNotice)); }
    }

    public bool HasSeekNotice => !string.IsNullOrWhiteSpace(SeekNotice);

    public string StatusText => _state switch
    {
        PlaybackState.Preparing => "Preparing the audio…",
        PlaybackState.Failed => Message ?? "This meeting cannot be played.",
        PlaybackState.Ended => "End of the meeting.",
        _ => Message ?? string.Empty,
    };

    /// <summary>Session-relative seconds. Read by the slider; never written by it.</summary>
    public double PositionSeconds
    {
        get => _position;
        private set { _position = value; Changed(); Changed(nameof(PositionText)); }
    }

    public double DurationSeconds
    {
        get => _duration;
        private set { _duration = value; Changed(); Changed(nameof(DurationText)); }
    }

    public string PositionText => Format(PositionSeconds);

    public string DurationText => Format(DurationSeconds);

    /// <summary>True while the user is dragging, so the clock does not fight their thumb.</summary>
    public bool IsScrubbing { get; set; }

    public bool HasYouTrack
    {
        get => _hasYou;
        private set { _hasYou = value; Changed(); }
    }

    public bool HasRemoteTrack
    {
        get => _hasRemote;
        private set { _hasRemote = value; Changed(); }
    }

    public bool MuteYou
    {
        get => _mix.MuteYou;
        set => ApplyMix(_mix with { MuteYou = value });
    }

    public bool MuteRemote
    {
        get => _mix.MuteRemote;
        set => ApplyMix(_mix with { MuteRemote = value });
    }

    public double YouLevel
    {
        get => _mix.YouLevel;
        set => ApplyMix(_mix with { YouLevel = value });
    }

    public double RemoteLevel
    {
        get => _mix.RemoteLevel;
        set => ApplyMix(_mix with { RemoteLevel = value });
    }

    // -- lifecycle ---------------------------------------------------------------------------------

    /// <summary>
    /// Builds the aligned audio if it is not already there, then opens the device.
    ///
    /// <para>
    /// Safe to call more than once: a meeting opened, closed and opened again reuses the derivative
    /// and does not rebuild it.
    /// </para>
    /// </summary>
    public async Task PrepareAsync()
    {
        if (_disposed || _engine is not null || _state == PlaybackState.Preparing)
        {
            return;
        }

        State = PlaybackState.Preparing;
        Message = null;

        void OnProgress(object? sender, PlaybackBuildProgressEventArgs e) => Dispatch(() => PreparationFraction = e.Fraction);
        _preparer.Progress += OnProgress;

        PlaybackPreparation prepared;
        try
        {
            prepared = await Task
                .Run(() => _preparer.PrepareAsync(_sessionId, cancellationToken: _cancellation.Token))
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _preparer.Progress -= OnProgress;
        }

        if (_disposed)
        {
            return;
        }

        if (!prepared.Succeeded)
        {
            Message = prepared.Message;
            State = PlaybackState.Failed;
            return;
        }

        try
        {
            PlaybackDerivativeRecord record = prepared.Record!;
            HasYouTrack = record.For("microphone")?.HasAudio ?? false;
            HasRemoteTrack = record.For("system")?.HasAudio ?? false;

            _engine = new PlaybackEngine(
                WavPlaybackAudioSource.Open(prepared.AudioPath!),
                _devices(),
                HasYouTrack,
                HasRemoteTrack)
            {
                Mix = _mix,
            };
        }
        catch (PlaybackDeviceException ex)
        {
            Message = ex.Message;
            State = PlaybackState.Failed;
            return;
        }

        _engine.StateChanged += OnEngineStateChanged;

        DurationSeconds = _engine.DurationSeconds;
        Message = _engine.Message;
        State = _engine.State;

        Sample();
    }

    private void OnEngineStateChanged(object? sender, PlaybackStateChangedEventArgs e) => Dispatch(() =>
    {
        Message = e.Message;
        State = e.State;
        Sample();
    });

    // -- transport ---------------------------------------------------------------------------------

    public void TogglePlay()
    {
        if (_engine is null)
        {
            return;
        }

        if (_engine.State == PlaybackState.Playing)
        {
            _engine.Pause();
        }
        else
        {
            _engine.Play();
        }
    }

    public void Stop() => _engine?.Stop();

    /// <summary>Moves to a moment. The transport keeps whatever state it was in.</summary>
    public void SeekTo(double sessionSeconds)
    {
        SeekNotice = null;
        _engine?.Seek(sessionSeconds);
        Sample();
    }

    /// <summary>
    /// Follows a playback request produced by the evidence layer or by a transcript timestamp.
    ///
    /// <para>
    /// The request's time is taken as given. An approximate one is <b>not</b> improved by looking
    /// its segment up in whichever transcript happens to be selected — that would silently
    /// re-point a citation at a different piece of speech and present the result as exact.
    /// </para>
    /// </summary>
    public void Cue(PlaybackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _engine?.Seek(request.StartSeconds);

        SeekNotice = request.IsApproximate
            ? "This is the time stored with the citation. The transcript version it named is not available, so the position is approximate."
            : null;

        Sample();
    }

    private void ApplyMix(PlaybackMix mix)
    {
        _mix = mix;

        if (_engine is not null)
        {
            _engine.Mix = mix;
        }

        foreach (string name in (string[])[nameof(MuteYou), nameof(MuteRemote), nameof(YouLevel), nameof(RemoteLevel)])
        {
            Changed(name);
        }
    }

    private void Sample()
    {
        if (_engine is null || IsScrubbing)
        {
            return;
        }

        PositionSeconds = _engine.PositionSeconds;
        DurationSeconds = _engine.DurationSeconds;
    }

    private static string Format(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span >= TimeSpan.FromHours(1)
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes:00}:{span.Seconds:00}");
    }

    private static void Dispatch(Action action)
    {
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Stops the clock, cancels any preparation, and releases the device and the file.
    ///
    /// <para>
    /// Called when the meeting is closed or another one is opened. A transport left alive would
    /// keep an audio endpoint claimed for a window nobody can see.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _timer?.Stop();
        _cancellation.Cancel();

        if (_engine is not null)
        {
            _engine.StateChanged -= OnEngineStateChanged;
            _engine.Dispose();
            _engine = null;
        }

        _cancellation.Dispose();
    }
}
