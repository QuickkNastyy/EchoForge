using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Recording;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Settings;
using EchoForge.Core.Recording;
using EchoForge.Core.Storage;

namespace EchoForge.App;

/// <summary>
/// Asks the user to confirm before each recording starts. Abstracted so the view model's consent
/// behaviour can be tested without showing a dialog.
/// </summary>
public interface IConsentPrompt
{
    /// <summary>Returns true only on an affirmative action. Cancelling must return false.</summary>
    Task<bool> ConfirmAsync();
}

/// <summary>
/// Observes the recorder and issues commands to it.
///
/// <para>
/// It holds no capture truth of its own. Every state it shows — recording, paused, degraded,
/// elapsed time, levels — is read from <see cref="RecordingController"/> on a timer, which is why
/// the window and the tray icon cannot disagree with what is actually being captured. Totals come
/// from the controller's cumulative session figures, so they do not reset on Pause and Resume.
/// </para>
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RecordingController _controller;
    private readonly IAudioDeviceCatalog _catalog;
    private readonly ISettingsStore _settings;
    private readonly IConsentPrompt _consent;
    private readonly OperationGate _gate = new();
    private readonly DispatcherTimer _timer;

    private AudioEndpointInfo? _selectedRender;
    private AudioEndpointInfo? _selectedCapture;
    private string _statusHeadline = "Ready";
    private string? _notice;
    private bool _disposed;

    public MainViewModel(
        RecordingController controller,
        IAudioDeviceCatalog catalog,
        ISettingsStore settings,
        IConsentPrompt consent,
        string? recoverySummary = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(consent);

        _controller = controller;
        _catalog = catalog;
        _settings = settings;
        _consent = consent;

        StartCommand = new AsyncRelayCommand(StartAsync, _gate, () => CanStart, m => Notice = m);
        PauseCommand = new AsyncRelayCommand(PauseAsync, _gate, () => IsRecording, m => Notice = m);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, _gate, () => IsPaused, m => Notice = m);
        StopCommand = new AsyncRelayCommand(StopAsync, _gate, () => IsRecording || IsPaused, m => Notice = m);
        RefreshDevicesCommand = new AsyncRelayCommand(
            () => Task.Run(RefreshDevices), _gate, () => DevicesEditable, m => Notice = m);

        _gate.Changed += (_, _) => Dispatch(RaiseCommands);

        LoadDevices();
        RestoreSelection(_settings.Load());

        Notice = recoverySummary;

        _controller.StateChanged += OnControllerStateChanged;
        _controller.Notice += OnControllerNotice;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AudioEndpointInfo> RenderDevices { get; } = [];

    public ObservableCollection<AudioEndpointInfo> CaptureDevices { get; } = [];

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand PauseCommand { get; }

    public AsyncRelayCommand ResumeCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RefreshDevicesCommand { get; }

    public AudioEndpointInfo? SelectedRender
    {
        get => _selectedRender;
        set { _selectedRender = value; OnChanged(); OnChanged(nameof(CanStart)); RaiseCommands(); }
    }

    public AudioEndpointInfo? SelectedCapture
    {
        get => _selectedCapture;
        set { _selectedCapture = value; OnChanged(); OnChanged(nameof(CanStart)); RaiseCommands(); }
    }

    /// <summary>True while a lifecycle operation is running. Conflicting commands disable.</summary>
    public bool IsBusy => _gate.IsBusy;

    public bool DevicesEditable => !IsRecording && !IsPaused && !IsBusy;

    public bool IsRecording => _controller.State is SessionState.Recording or SessionState.Degraded;

    public bool IsPaused => _controller.State is SessionState.Paused;

    public bool IsDegraded => _controller.State is SessionState.Degraded;

    /// <summary>
    /// The red indicator. Bound to authoritative capture state, so it keeps showing the truth
    /// while a stop is finalizing rather than blanking the moment the button is pressed.
    /// </summary>
    public bool IndicatorVisible => IsRecording;

    public bool CanStart =>
        !IsRecording && !IsPaused && SelectedRender is not null && SelectedCapture is not null;

    public string StatusHeadline
    {
        get => _statusHeadline;
        private set { _statusHeadline = value; OnChanged(); }
    }

    public string? Notice
    {
        get => _notice;
        private set { _notice = value; OnChanged(); OnChanged(nameof(HasNotice)); }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    public string Elapsed { get; private set; } = "00:00:00";

    public double YouLevel { get; private set; }

    public double RemoteLevel { get; private set; }

    public string YouCaption { get; private set; } = "You";

    public string RemoteCaption { get; private set; } = "Remote";

    public string ChunkSummary { get; private set; } = "0 + 0";

    public string FreeSpace { get; private set; } = "—";

    public string StorageRate { get; private set; } = "—";

    public string QueueSummary { get; private set; } = "—";

    public string TrayText => IsRecording
        ? $"EchoForge — recording {Elapsed}"
        : IsPaused ? "EchoForge — paused" : "EchoForge";

    /// <summary>Shows the startup recovery result, once the background scan has finished.</summary>
    public void ShowRecoverySummary(string summary) => Dispatch(() => Notice = summary);

    private void LoadDevices()
    {
        RenderDevices.Clear();
        CaptureDevices.Clear();

        foreach (AudioEndpointInfo endpoint in _catalog.GetRenderEndpoints())
        {
            RenderDevices.Add(endpoint);
        }

        foreach (AudioEndpointInfo endpoint in _catalog.GetCaptureEndpoints())
        {
            CaptureDevices.Add(endpoint);
        }

        SelectedRender ??= RenderDevices.FirstOrDefault(d => d.IsDefault) ?? RenderDevices.FirstOrDefault();
        SelectedCapture ??= CaptureDevices.FirstOrDefault(d => d.IsDefault) ?? CaptureDevices.FirstOrDefault();
    }

    /// <summary>
    /// Re-enumerates while preserving the selection by stable endpoint ID.
    ///
    /// <para>
    /// The selected object must be replaced with the equivalent object from the new collection,
    /// or the ComboBox holds an instance that is no longer in its item source. If the endpoint is
    /// gone the selection is cleared and the user is told, never quietly moved to another device.
    /// </para>
    /// </summary>
    public void RefreshDevices()
    {
        if (IsRecording || IsPaused)
        {
            Dispatch(() => Notice = "Devices cannot be changed while a recording is in progress.");
            return;
        }

        string? renderId = SelectedRender?.Id;
        string? captureId = SelectedCapture?.Id;

        IReadOnlyList<AudioEndpointInfo> render = _catalog.GetRenderEndpoints();
        IReadOnlyList<AudioEndpointInfo> capture = _catalog.GetCaptureEndpoints();

        Dispatch(() =>
        {
            RenderDevices.Clear();
            foreach (AudioEndpointInfo endpoint in render)
            {
                RenderDevices.Add(endpoint);
            }

            CaptureDevices.Clear();
            foreach (AudioEndpointInfo endpoint in capture)
            {
                CaptureDevices.Add(endpoint);
            }

            List<string> missing = [];

            SelectedRender = Reselect(RenderDevices, renderId, "playback device", missing);
            SelectedCapture = Reselect(CaptureDevices, captureId, "microphone", missing);

            Notice = missing.Count == 0
                ? null
                : $"The {string.Join(" and ", missing)} you had selected is no longer available. Choose another before recording.";
        });
    }

    private static AudioEndpointInfo? Reselect(
        ObservableCollection<AudioEndpointInfo> devices, string? previousId, string label, List<string> missing)
    {
        if (previousId is null)
        {
            return devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
        }

        AudioEndpointInfo? match = devices.FirstOrDefault(d => d.Id == previousId);
        if (match is not null)
        {
            return match;
        }

        missing.Add(label);
        return null;
    }

    private void RestoreSelection(AppSettings settings)
    {
        List<string> missing = [];

        if (settings.RenderEndpointId is { } renderId)
        {
            SelectedRender = Reselect(RenderDevices, renderId, "playback device", missing);
        }

        if (settings.CaptureEndpointId is { } captureId)
        {
            SelectedCapture = Reselect(CaptureDevices, captureId, "microphone", missing);
        }

        if (missing.Count > 0)
        {
            Notice = $"The {string.Join(" and ", missing)} from last time is not available. Choose another before recording.";
        }
    }

    /// <summary>
    /// Confirms consent, then starts. Clicking Start is not itself consent: the reminder is shown
    /// before every recording and requires an affirmative answer.
    /// </summary>
    private async Task StartAsync()
    {
        if (SelectedRender is null || SelectedCapture is null)
        {
            Notice = "Choose a playback device and a microphone first.";
            return;
        }

        if (!await _consent.ConfirmAsync().ConfigureAwait(true))
        {
            Notice = "Recording cancelled.";
            return;
        }

        AudioEndpointInfo render = SelectedRender;
        AudioEndpointInfo capture = SelectedCapture;

        // Remember the devices, and that the user has seen the responsibility notice. This is
        // never a stored claim that meeting participants consented.
        _settings.Save(_settings.Load() with
        {
            RenderEndpointId = render.Id,
            CaptureEndpointId = capture.Id,
            ConsentAcknowledged = true,
        });

        Notice = null;

        await Task.Run(() => _controller.Start(new RecordingRequest(
            render.Id, render.FriendlyName, capture.Id, capture.FriendlyName))).ConfigureAwait(true);
    }

    private Task PauseAsync() => Task.Run(_controller.Pause);

    private Task ResumeAsync() => Task.Run(_controller.Resume);

    private Task StopAsync() => Task.Run(_controller.Stop);

    private void OnControllerStateChanged(object? sender, RecordingStateChangedEventArgs e) =>
        Dispatch(() =>
        {
            if (e.Reason is not null)
            {
                Notice = e.Reason;
            }

            Refresh();
        });

    private void OnControllerNotice(object? sender, string message) => Dispatch(() => Notice = message);

    /// <summary>Pulls the authoritative state. Runs on the UI cadence, never on a capture thread.</summary>
    private void Refresh()
    {
        RecorderStatus status = _controller.Poll();
        SessionTotals totals = _controller.Totals();

        // Cumulative across epochs: pausing and resuming does not restart the clock.
        Elapsed = totals.ActiveDuration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        TrackLiveStatus? you = status.Tracks.FirstOrDefault(t => t.Track == SourceTrack.Microphone);
        TrackLiveStatus? remote = status.Tracks.FirstOrDefault(t => t.Track == SourceTrack.System);

        YouLevel = you?.PeakLevel ?? 0;
        RemoteLevel = remote?.PeakLevel ?? 0;
        YouCaption = you is { IsHealthy: false } ? "You — not capturing" : "You";
        RemoteCaption = remote is { IsHealthy: false } ? "Remote — not capturing" : "Remote";

        int youChunks = totals.ChunksPerTrack.GetValueOrDefault(SourceTrack.Microphone);
        int remoteChunks = totals.ChunksPerTrack.GetValueOrDefault(SourceTrack.System);
        ChunkSummary = $"{youChunks} + {remoteChunks}";

        QueueSummary = status.Tracks.Count == 0
            ? "—"
            : $"queue {status.Tracks.Max(t => t.QueuedFrames)} · dropped {status.Tracks.Sum(t => t.DroppedFrames)}";

        DiskStatus disk = _controller.Disk();
        FreeSpace = $"{disk.AvailableGigabytes:0.0} GB free";

        // Rate from the formats actually being captured, not a constant.
        double gigabytesPerHour = _controller.EstimatedBytesPerSecond() * 3600.0 / 1_000_000_000.0;
        StorageRate = $"{gigabytesPerHour:0.00} GB/hr";

        StatusHeadline = _controller.State switch
        {
            SessionState.Recording => "Recording",
            SessionState.Degraded => "Recording · degraded",
            SessionState.Paused => _controller.AwaitingResumeAfterSuspend ? "Paused · computer slept" : "Paused",
            SessionState.Finalizing => "Saving",
            SessionState.Recorded => "Saved",
            SessionState.Failed => "Failed",
            _ => "Ready",
        };

        foreach (string name in RefreshedProperties)
        {
            OnChanged(name);
        }

        RaiseCommands();
    }

    private static readonly string[] RefreshedProperties =
    [
        nameof(Elapsed), nameof(YouLevel), nameof(RemoteLevel), nameof(YouCaption), nameof(RemoteCaption),
        nameof(ChunkSummary), nameof(QueueSummary), nameof(FreeSpace), nameof(StorageRate),
        nameof(IsRecording), nameof(IsPaused), nameof(IsDegraded), nameof(IndicatorVisible),
        nameof(DevicesEditable), nameof(CanStart), nameof(IsBusy), nameof(TrayText), nameof(StatusHeadline),
    ];

    /// <summary>Marshals UI state changes onto the dispatcher when raised from a worker thread.</summary>
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

    private void RaiseCommands()
    {
        StartCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RefreshDevicesCommand.RaiseCanExecuteChanged();
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _controller.StateChanged -= OnControllerStateChanged;
        _controller.Notice -= OnControllerNotice;
    }
}
