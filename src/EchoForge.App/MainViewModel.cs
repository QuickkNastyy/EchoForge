using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Recording;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Settings;
using EchoForge.Core.Recording;
using EchoForge.Core.Storage;

namespace EchoForge.App;

/// <summary>
/// Observes the recorder and issues commands to it.
///
/// <para>
/// It holds no capture truth of its own. Every state it shows — recording, paused, degraded,
/// elapsed time, levels — is read from <see cref="RecordingController"/> on a timer, which is why
/// the window and the tray icon cannot disagree with what is actually being captured.
/// </para>
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RecordingController _controller;
    private readonly AudioDeviceCatalog _catalog;
    private readonly ISettingsStore _settings;
    private readonly DispatcherTimer _timer;

    private AudioEndpointInfo? _selectedRender;
    private AudioEndpointInfo? _selectedCapture;
    private string _statusHeadline = "Ready";
    private string? _notice;
    private bool _consentAcknowledged;
    private bool _disposed;

    public MainViewModel(
        RecordingController controller,
        AudioDeviceCatalog catalog,
        ISettingsStore settings,
        IReadOnlyList<string> recoveryNotices)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);

        _controller = controller;
        _catalog = catalog;
        _settings = settings;

        StartCommand = new RelayCommand(Start, () => CanStart);
        PauseCommand = new RelayCommand(Pause, () => IsRecording);
        ResumeCommand = new RelayCommand(Resume, () => IsPaused);
        StopCommand = new RelayCommand(Stop, () => IsRecording || IsPaused);
        RefreshDevicesCommand = new RelayCommand(LoadDevices, () => !IsRecording && !IsPaused);

        AppSettings loaded = settings.Load();
        _consentAcknowledged = loaded.ConsentAcknowledged;

        LoadDevices();
        RestoreSelection(loaded);

        if (recoveryNotices.Count > 0)
        {
            Notice = recoveryNotices.Count == 1
                ? recoveryNotices[0]
                : $"{recoveryNotices.Count} interrupted sessions were recovered on startup.";
        }

        _controller.StateChanged += OnControllerStateChanged;

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

    public RelayCommand StartCommand { get; }

    public RelayCommand PauseCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand RefreshDevicesCommand { get; }

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

    /// <summary>Devices cannot be changed while recording; the endpoints are pinned for the session.</summary>
    public bool DevicesEditable => !IsRecording && !IsPaused;

    public bool IsRecording => _controller.State is SessionState.Recording or SessionState.Degraded;

    public bool IsPaused => _controller.State is SessionState.Paused;

    public bool IsDegraded => _controller.State is SessionState.Degraded;

    /// <summary>True whenever capture is live. The red indicator is bound to this and nothing else.</summary>
    public bool IndicatorVisible => IsRecording;

    public bool CanStart =>
        !IsRecording && !IsPaused && SelectedRender is not null && SelectedCapture is not null;

    public bool ConsentAcknowledged
    {
        get => _consentAcknowledged;
        set
        {
            _consentAcknowledged = value;
            _settings.Save(_settings.Load() with { ConsentAcknowledged = value });
            OnChanged();
        }
    }

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

    public string StorageRate { get; private set; } = "1.04 GB/hr";

    public string QueueSummary { get; private set; } = "—";

    /// <summary>What the tray tooltip shows. Derived from the same state as the window.</summary>
    public string TrayText => IsRecording
        ? $"EchoForge — recording {Elapsed}"
        : IsPaused ? "EchoForge — paused" : "EchoForge";

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
    /// Restores the previously chosen endpoints by stable ID. A device that is gone is reported
    /// rather than quietly replaced with whatever Windows now considers default.
    /// </summary>
    private void RestoreSelection(AppSettings settings)
    {
        if (settings.RenderEndpointId is { } renderId)
        {
            AudioEndpointInfo? match = RenderDevices.FirstOrDefault(d => d.Id == renderId);
            if (match is not null)
            {
                SelectedRender = match;
            }
            else
            {
                Notice = "The playback device from last time is not available. Choose one before recording.";
            }
        }

        if (settings.CaptureEndpointId is { } captureId)
        {
            AudioEndpointInfo? match = CaptureDevices.FirstOrDefault(d => d.Id == captureId);
            if (match is not null)
            {
                SelectedCapture = match;
            }
            else
            {
                Notice = "The microphone from last time is not available. Choose one before recording.";
            }
        }
    }

    private void Start()
    {
        if (SelectedRender is null || SelectedCapture is null)
        {
            return;
        }

        (bool allowed, string? reason) = _controller.CanStart(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData));

        if (!allowed)
        {
            Notice = reason;
            return;
        }

        _settings.Save(_settings.Load() with
        {
            RenderEndpointId = SelectedRender.Id,
            CaptureEndpointId = SelectedCapture.Id,
            ConsentAcknowledged = true,
        });

        try
        {
            Notice = null;
            _controller.Start(new RecordingRequest(
                SelectedRender.Id, SelectedRender.FriendlyName,
                SelectedCapture.Id, SelectedCapture.FriendlyName));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Notice = $"Recording could not start: {ex.Message}";
        }
    }

    private void Pause() => _controller.Pause();

    private void Resume() => _controller.Resume();

    private void Stop() => _controller.Stop();

    private void OnControllerStateChanged(object? sender, RecordingStateChangedEventArgs e)
    {
        if (e.Reason is not null)
        {
            Notice = e.Reason;
        }

        Refresh();
    }

    /// <summary>Pulls the authoritative state. Runs on the UI cadence, never on a capture thread.</summary>
    private void Refresh()
    {
        RecorderStatus status = _controller.Poll();

        Elapsed = status.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        TrackLiveStatus? you = status.Tracks.FirstOrDefault(t => t.Track == SourceTrack.Microphone);
        TrackLiveStatus? remote = status.Tracks.FirstOrDefault(t => t.Track == SourceTrack.System);

        YouLevel = you?.PeakLevel ?? 0;
        RemoteLevel = remote?.PeakLevel ?? 0;
        YouCaption = you is { IsHealthy: false } ? "You — no signal" : "You";
        RemoteCaption = remote is { IsHealthy: false } ? "Remote — no signal" : "Remote";

        ChunkSummary = $"{you?.CompletedChunks ?? 0} + {remote?.CompletedChunks ?? 0}";
        QueueSummary = status.Tracks.Count == 0
            ? "—"
            : $"queue {status.Tracks.Max(t => t.QueuedFrames)} · dropped {status.Tracks.Sum(t => t.DroppedFrames)}";

        DiskStatus disk = _controller.Disk();
        FreeSpace = $"{disk.AvailableGigabytes:0.0} GB free";

        StatusHeadline = _controller.State switch
        {
            SessionState.Recording => "Recording",
            SessionState.Degraded => "Recording · degraded",
            SessionState.Paused => "Paused",
            SessionState.Finalizing => "Finalizing",
            SessionState.Recorded => "Saved",
            SessionState.Failed => "Failed",
            _ => "Ready",
        };

        OnChanged(nameof(Elapsed));
        OnChanged(nameof(YouLevel));
        OnChanged(nameof(RemoteLevel));
        OnChanged(nameof(YouCaption));
        OnChanged(nameof(RemoteCaption));
        OnChanged(nameof(ChunkSummary));
        OnChanged(nameof(QueueSummary));
        OnChanged(nameof(FreeSpace));
        OnChanged(nameof(IsRecording));
        OnChanged(nameof(IsPaused));
        OnChanged(nameof(IsDegraded));
        OnChanged(nameof(IndicatorVisible));
        OnChanged(nameof(DevicesEditable));
        OnChanged(nameof(CanStart));
        OnChanged(nameof(TrayText));
        RaiseCommands();
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
    }
}
