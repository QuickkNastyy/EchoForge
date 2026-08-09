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
    private readonly OperationGate _gate = new();
    private readonly DispatcherTimer _timer;

    // A picture of the recording, never a record of it. Fed from the same refresh that reads the
    // meters, on the canonical active-time clock, and cleared when a fresh recording begins.
    private readonly Recording.SpeechActivityHistory _activity = new();

    private AudioEndpointInfo? _selectedRender;
    private AudioEndpointInfo? _selectedCapture;
    private string _statusHeadline = "Ready";
    private string? _notice;
    private bool _disposed;

    private readonly Func<Contracts.Audio.IDeviceLevelMonitor>? _levelMonitorFactory;
    private Contracts.Audio.IDeviceLevelMonitor? _monitor;

    public MainViewModel(
        RecordingController controller,
        IAudioDeviceCatalog catalog,
        ISettingsStore settings,
        string? recoverySummary = null,
        Func<Contracts.Audio.IDeviceLevelMonitor>? levelMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);

        _controller = controller;
        _catalog = catalog;
        _settings = settings;
        _levelMonitorFactory = levelMonitor;

        StartCommand = new AsyncRelayCommand(StartAsync, _gate, () => CanStart, m => Notice = m);
        PauseCommand = new AsyncRelayCommand(PauseAsync, _gate, () => IsRecording, m => Notice = m);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, _gate, () => IsPaused, m => Notice = m);
        StopCommand = new AsyncRelayCommand(StopAsync, _gate, () => IsRecording || IsPaused, m => Notice = m);
        RefreshDevicesCommand = new AsyncRelayCommand(
            () => Task.Run(RefreshDevices), _gate, () => DevicesEditable, m => Notice = m);
        TestDevicesCommand = new RelayCommand(ToggleDeviceTest, () => IsTesting || CanStart);
        ContinueRecoveredCommand = new AsyncRelayCommand(
            ContinueRecoveredAsync, _gate, () => IsReady && HasPendingContinuation && !IsRecording, m => Notice = m);
        FinishRecoveredCommand = new AsyncRelayCommand(
            FinishRecoveredAsync, _gate, () => IsReady && HasPendingContinuation && !IsRecording, m => Notice = m);

        _gate.Changed += (_, _) => Dispatch(RaiseCommands);

        LoadDevices();
        RestoreSelection(_settings.Load());

        // Added to, never assigned over. Restoring the selection is what discovers that a device
        // from last time has gone, and assigning the recovery summary here — usually null — threw
        // that away one line after computing it. The window then showed two empty pickers and a
        // dead Start button with nothing on screen saying why.
        Notice = Join(Notice, recoverySummary);

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

    /// <summary>
    /// The post-recording transcription surface, or null when the app was composed without one.
    ///
    /// <para>
    /// It is fed from this view model's refresh rather than observing the recorder itself, so
    /// there is exactly one place that reads capture state and the two panels cannot disagree
    /// about whether recording is live.
    /// </para>
    /// </summary>
    public TranscriptionViewModel? Transcription { get; private set; }

    public bool HasTranscription => Transcription is not null;

    /// <summary>The summary surface, or null when the app was composed without one.</summary>
    public SummaryViewModel? Summary { get; private set; }

    public bool HasSummary => Summary is not null;

    /// <summary>
    /// Makes setup reachable, whether or not anything is installed.
    ///
    /// <para>
    /// Attached even when the manifest could not be opened, because that is exactly the state a
    /// user most needs a screen for. Recording keeps working throughout: setup is where somebody
    /// goes to make transcription and summaries work, not a gate in front of the application.
    /// </para>
    /// </summary>
    public void AttachSetup(
        EchoForge.Infrastructure.Setup.SetupServices? services,
        EchoForge.Contracts.Audio.IAudioDeviceCatalog? audio) => Dispatch(() =>
    {
        SetupServices = services;
        SetupAudio = audio;

        OnChanged(nameof(HasSetup));
    });

    public EchoForge.Infrastructure.Setup.SetupServices? SetupServices { get; private set; }

    internal EchoForge.Contracts.Audio.IAudioDeviceCatalog? SetupAudio { get; private set; }

    public bool HasSetup => SetupServices is not null;

    /// <summary>
    /// The runtime and model surface, once it can be built.
    ///
    /// <para>
    /// It is a page rather than a window now. Setup used to be a second application with its own
    /// title bar, which is how model downloads, GPU facts and a machine report ended up feeling
    /// like a product of their own rather than a preference somebody adjusts twice a year.
    /// </para>
    /// </summary>
    public EchoForge.App.Setup.SetupViewModel? Setup { get; private set; }

    public void AttachSetupPage(EchoForge.App.Setup.SetupViewModel setup) => Dispatch(() =>
    {
        ArgumentNullException.ThrowIfNull(setup);

        Setup = setup;

        // Which row is highlighted decides what Use offers and whether it can run at all.
        setup.PropertyChanged += (_, e) =>
        {
            // Fully qualified: "Setup" alone binds to the property above, not the namespace.
            if (e.PropertyName is nameof(EchoForge.App.Setup.SetupViewModel.SelectedModel) or null)
            {
                RaiseModelChoice();
            }
        };

        OnChanged(nameof(Setup));
        OnChanged(nameof(HasSetup));
        RaiseModelChoice();
    });

    /// <summary>
    /// Makes the meeting library reachable.
    ///
    /// <para>
    /// Attached rather than constructed here for the same reason transcription is: the library
    /// needs an index, and building one reads every session on disk. The recorder must never wait
    /// on that, so it arrives when it is ready and the button appears then.
    /// </para>
    /// </summary>
    public void AttachLibrary(EchoForge.App.Library.LibraryViewModel library) => Dispatch(() =>
    {
        ArgumentNullException.ThrowIfNull(library);

        Library = library;

        // Both, and the first one matters most. The recordings page takes the library as its data
        // context, and the library arrives long after the window is built — so a window that is
        // never told the property changed binds to the null that was there at startup and stays
        // bound to it. Every symptom of that is downstream and confusing: an empty meeting list,
        // buttons whose labels are bindings and therefore blank, a menu that opens onto nothing.
        OnChanged(nameof(Library));
        OnChanged(nameof(HasLibrary));
        ShowRecordingsCommand.RaiseCanExecuteChanged();
    });

    public EchoForge.App.Library.LibraryViewModel? Library { get; private set; }

    public bool HasLibrary => Library is not null;

    // -- navigation ---------------------------------------------------------------------------

    private AppPage _page = AppPage.Record;

    /// <summary>
    /// Which page is showing.
    ///
    /// <para>
    /// EchoForge is one window. Recording, the meetings, and the settings that decide how meetings
    /// are processed used to be three separate top-level windows, which meant the same transcribe
    /// and summarise controls existed in two places and could disagree with each other. There is
    /// now one owner for each of those things, and moving between them is navigation rather than
    /// window management.
    /// </para>
    /// </summary>
    public AppPage Page
    {
        get => _page;
        private set
        {
            if (_page == value)
            {
                return;
            }

            _page = value;
            OnChanged(nameof(Page));
            OnChanged(nameof(IsRecordPage));
            OnChanged(nameof(IsRecordingsPage));
            OnChanged(nameof(IsSettingsPage));
        }
    }

    public bool IsRecordPage => Page == AppPage.Record;

    public bool IsRecordingsPage => Page == AppPage.Recordings;

    public bool IsSettingsPage => Page == AppPage.Settings;

    public RelayCommand ShowRecordCommand => _showRecord ??= new RelayCommand(() => Page = AppPage.Record);

    /// <summary>
    /// Opens the meetings page, and loads the index the first time.
    ///
    /// <para>
    /// Loading is deferred rather than done at startup because building the index reads every
    /// session on disk, and the recorder must never wait on that.
    /// </para>
    /// </summary>
    public RelayCommand ShowRecordingsCommand => _showRecordings ??= new RelayCommand(
        () =>
        {
            Page = AppPage.Recordings;
            _ = Library?.InitializeAsync();
        },
        () => HasLibrary);

    public RelayCommand ShowSettingsCommand => _showSettings ??= new RelayCommand(() => Page = AppPage.Settings);

    private RelayCommand? _showRecord;
    private RelayCommand? _showRecordings;
    private RelayCommand? _showSettings;

    // -- settings sections --------------------------------------------------------------------

    private SettingsSection _settingsSection = SettingsSection.Transcription;

    /// <summary>
    /// Which Settings section is showing. The rail's expanded Settings entry and the page read the
    /// same value, so the tick in the rail and the content on screen cannot disagree.
    /// </summary>
    public SettingsSection SettingsSection
    {
        get => _settingsSection;
        private set
        {
            if (_settingsSection == value)
            {
                return;
            }

            _settingsSection = value;
            OnChanged(nameof(SettingsSection));
            OnChanged(nameof(IsTranscriptionSection));
            OnChanged(nameof(IsBriefsSection));
            OnChanged(nameof(IsModelsSection));
            OnChanged(nameof(IsMachineSection));
            OnChanged(nameof(IsCompareSection));
        }
    }

    public bool IsTranscriptionSection => SettingsSection == SettingsSection.Transcription;

    public bool IsBriefsSection => SettingsSection == SettingsSection.Briefs;

    public bool IsModelsSection => SettingsSection == SettingsSection.Models;

    public bool IsMachineSection => SettingsSection == SettingsSection.Machine;

    public bool IsCompareSection => SettingsSection == SettingsSection.Compare;

    public RelayCommand ShowTranscriptionSettingsCommand =>
        _showTranscriptionSettings ??= new RelayCommand(() => ShowSection(SettingsSection.Transcription));

    public RelayCommand ShowBriefsSettingsCommand =>
        _showBriefsSettings ??= new RelayCommand(() => ShowSection(SettingsSection.Briefs));

    public RelayCommand ShowModelsSettingsCommand =>
        _showModelsSettings ??= new RelayCommand(() => ShowSection(SettingsSection.Models));

    public RelayCommand ShowMachineSettingsCommand =>
        _showMachineSettings ??= new RelayCommand(() => ShowSection(SettingsSection.Machine));

    public RelayCommand ShowCompareSettingsCommand =>
        _showCompareSettings ??= new RelayCommand(() => ShowSection(SettingsSection.Compare));

    private void ShowSection(SettingsSection section)
    {
        Page = AppPage.Settings;
        SettingsSection = section;
    }

    private RelayCommand? _showTranscriptionSettings;
    private RelayCommand? _showBriefsSettings;
    private RelayCommand? _showModelsSettings;
    private RelayCommand? _showMachineSettings;
    private RelayCommand? _showCompareSettings;

    // -- choosing a model from the Models list -------------------------------------------------

    /// <summary>
    /// Makes the highlighted row on the Models page the model EchoForge actually uses.
    ///
    /// <para>
    /// The Models list existed to install and repair, and selecting a row changed nothing else —
    /// while the page told the reader that selecting one would "change what EchoForge uses". The
    /// two pickers in Transcription and Meeting briefs were the only way to choose, which is not
    /// discoverable from a page that lists every speech and summary model together.
    /// </para>
    ///
    /// <para>
    /// This does not become a second source of truth. It sets the same property the picker sets,
    /// which persists the choice and refreshes exactly as it always did; the row is just another
    /// way to reach it. A speech row drives transcription, a summary or comparison row drives the
    /// meeting brief, because that is what each of those models is for.
    /// </para>
    /// </summary>
    public RelayCommand UseSelectedModelCommand => _useSelectedModel ??= new RelayCommand(
        UseSelectedModel,
        CanUseSelectedModel);

    private RelayCommand? _useSelectedModel;

    /// <summary>
    /// Enabled only when pressing it would actually change something.
    ///
    /// <para>
    /// The surface that owns the choice arrives late, and on an installation where processing
    /// never attached it does not arrive at all — so a button guarded only by "a row is selected"
    /// was offered on exactly the screen that says transcription is unavailable, and did nothing
    /// when pressed. A control that cannot act should not look like it can.
    /// </para>
    /// </summary>
    private bool CanUseSelectedModel()
    {
        if (Setup?.SelectedModel is not { } row)
        {
            return false;
        }

        return IsSpeechRow(row)
            ? Transcription?.AsrModels.Any(option => string.Equals(option.Id, row.Id, StringComparison.Ordinal)) is true
            : Summary?.SummaryModels.Any(option => string.Equals(option.ModelId, row.Id, StringComparison.Ordinal)) is true;
    }

    /// <summary>The model id transcription is set to, so the Models list can mark its own row.</summary>
    public string? ActiveAsrModelId => Transcription?.SelectedAsrModel.Id;

    /// <summary>The model id meeting briefs are set to.</summary>
    public string? ActiveSummaryModelId => Summary?.SelectedSummaryModel.ModelId;

    /// <summary>What the Use button offers, named after the thing it will change.</summary>
    public string UseSelectedModelLabel => Setup?.SelectedModel is { } row && IsSpeechRow(row)
        ? "Use for transcription"
        : "Use for meeting briefs";

    private static bool IsSpeechRow(Setup.ModelManagementRow row) =>
        string.Equals(row.Category, "SPEECH", StringComparison.OrdinalIgnoreCase);

    private void UseSelectedModel()
    {
        if (Setup is not { SelectedModel: { } row })
        {
            return;
        }

        if (IsSpeechRow(row))
        {
            AsrModelOption? speech = Transcription?.AsrModels
                .FirstOrDefault(option => string.Equals(option.Id, row.Id, StringComparison.Ordinal));
            if (speech is not null && Transcription is not null)
            {
                Transcription.SelectedAsrModel = speech;
            }
        }
        else
        {
            SummaryModelOption? brief = Summary?.SummaryModels
                .FirstOrDefault(option => string.Equals(option.ModelId, row.Id, StringComparison.Ordinal));
            if (brief is not null && Summary is not null)
            {
                Summary.SelectedSummaryModel = brief;
            }
        }

        RaiseModelChoice();
    }

    /// <summary>Re-reads what is chosen, so the list's own markers follow the change.</summary>
    private void RaiseModelChoice()
    {
        OnChanged(nameof(ActiveAsrModelId));
        OnChanged(nameof(ActiveSummaryModelId));
        OnChanged(nameof(UseSelectedModelLabel));
        UseSelectedModelCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// What happens to a recording once it stops, said on the page that produces one.
    ///
    /// <para>
    /// The Record page has no processing controls, so it owes the reader one sentence about where
    /// they went. It also has to be honest when nothing can process yet: a machine with no models
    /// installed should be told that here, not discover it after a meeting.
    /// </para>
    /// </summary>
    public string ProcessingHandoff => Transcription is null
        ? "Recording works with nothing installed. To turn a recording into a meeting brief, install a speech and a summary model in Settings."
        : "When you stop, the recording appears in Library. Process it there to get a transcript and a meeting brief, using the models chosen in Settings.";

    /// <summary>Opens a meeting on the recordings page, from anywhere.</summary>
    public void OpenMeeting(string sessionId)
    {
        ShowRecordingsCommand.Execute(null);
        _ = Library?.OpenAsync(sessionId);
    }

    public void AttachSummary(SummaryViewModel summary) => Dispatch(() =>
    {
        ArgumentNullException.ThrowIfNull(summary);

        Summary?.Dispose();
        Summary = summary;

        // The picker and the Models list show the same choice, so a change in either has to reach
        // the other. Both write the same property; this is only how the markers keep up.
        summary.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SummaryViewModel.SelectedSummaryModel) or null)
            {
                RaiseModelChoice();
            }
        };

        OnChanged(nameof(Summary));
        OnChanged(nameof(HasSummary));
        RaiseModelChoice();
        Refresh();
    });

    /// <summary>
    /// Attaches the transcription surface once it exists.
    ///
    /// <para>
    /// It arrives late because finding a Python runtime means starting processes, and doing that
    /// during startup would stall the window before it had painted. The panel appears when it is
    /// ready; the recorder never waits for it.
    /// </para>
    /// </summary>
    public void AttachTranscription(TranscriptionViewModel transcription) => Dispatch(() =>
    {
        ArgumentNullException.ThrowIfNull(transcription);

        Transcription?.Dispose();
        Transcription = transcription;

        transcription.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TranscriptionViewModel.SelectedAsrModel) or null)
            {
                RaiseModelChoice();
            }
        };

        OnChanged(nameof(Transcription));
        OnChanged(nameof(HasTranscription));
        OnChanged(nameof(ProcessingHandoff));
        RaiseModelChoice();
        Refresh();
    });

    public ObservableCollection<AudioEndpointInfo> RenderDevices { get; } = [];

    public ObservableCollection<AudioEndpointInfo> CaptureDevices { get; } = [];

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand PauseCommand { get; }

    public AsyncRelayCommand ResumeCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RefreshDevicesCommand { get; }

    public RelayCommand TestDevicesCommand { get; }

    /// <summary>True when this build can test devices (the monitor factory was supplied).</summary>
    public bool HasDeviceTest => _levelMonitorFactory is not null;

    /// <summary>True while the non-recording device test is open.</summary>
    public bool IsTesting { get; private set; }

    public string TestButtonLabel => IsTesting ? "Stop test" : "Test devices";

    public double TestYouLevel => IsTesting ? Meter(_monitor?.YouLevel ?? 0) : 0;

    public double TestRemoteLevel => IsTesting ? Meter(_monitor?.RemoteLevel ?? 0) : 0;

    /// <summary>A plain report of what the test found, per track.</summary>
    public string TestStatus
    {
        get
        {
            if (!IsTesting || _monitor is null)
            {
                return string.Empty;
            }

            string you = _monitor.YouWorking ? "microphone working"
                : _monitor.YouFault is not null ? "microphone could not open"
                : "waiting for your microphone…";
            string remote = _monitor.RemoteWorking ? "system audio working"
                : _monitor.RemoteFault is not null ? "system audio could not open"
                : "waiting for system audio… (play something to see it)";
            return you + " · " + remote;
        }
    }

    /// <summary>
    /// Starts or stops the device test. It opens the selected endpoints only to show their levels;
    /// it writes nothing, creates no session, and never becomes a recording.
    /// </summary>
    private void ToggleDeviceTest()
    {
        if (IsTesting)
        {
            StopDeviceTest();
            return;
        }

        if (_levelMonitorFactory is null || SelectedRender is null || SelectedCapture is null)
        {
            return;
        }

        try
        {
            _monitor = _levelMonitorFactory();
            _monitor.Start(SelectedCapture.Id, SelectedRender.Id);
            IsTesting = true;
            Notice = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            StopDeviceTest();
            Notice = "The device test could not open those devices.";
            return;
        }

        RaiseTestState();
        RaiseCommands();
    }

    private void StopDeviceTest()
    {
        _monitor?.Stop();
        _monitor?.Dispose();
        _monitor = null;
        IsTesting = false;
        RaiseTestState();
        RaiseCommands();
    }

    private void RaiseTestState()
    {
        foreach (string name in (string[])
            [nameof(IsTesting), nameof(TestButtonLabel), nameof(TestYouLevel), nameof(TestRemoteLevel), nameof(TestStatus)])
        {
            OnChanged(name);
        }
    }

    public AsyncRelayCommand ContinueRecoveredCommand { get; }

    public AsyncRelayCommand FinishRecoveredCommand { get; }

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
    /// The red indicator. Bound to whether a capture source may still be live, not to whether
    /// Stop has been requested — so it stays lit while the capture threads are winding down and
    /// only clears once they have genuinely stopped. It reports capture state and nothing else.
    /// </summary>
    public bool IndicatorVisible => _controller.CaptureMayBeLive;

    /// <summary>
    /// False until the startup recovery scan has finished.
    ///
    /// <para>
    /// Recording must not begin while recovery is still walking the session folders: the two
    /// could otherwise touch the same session, with recovery repairing chunks a live recorder is
    /// writing. Recovery failing still opens the gate — a recovery problem must not lock the user
    /// out of recording — but it says so.
    /// </para>
    /// </summary>
    public bool IsReady { get; private set; }

    public string ReadinessMessage { get; private set; } = "Checking interrupted recordings…";

    public bool CanStart =>
        IsReady && !IsRecording && !IsPaused && SelectedRender is not null && SelectedCapture is not null;

    /// <summary>
    /// An unfinished recording found at startup, waiting for the user to continue or finish it.
    /// EchoForge never resumes one on its own.
    /// </summary>
    public RecoveryCandidate? PendingContinuation { get; private set; }

    public bool HasPendingContinuation => PendingContinuation is not null;

    public string ContinuationMessage => PendingContinuation is null
        ? string.Empty
        : $"A recording from {PendingContinuation.CreatedUtc.ToLocalTime():MMM d, h:mm tt} was " +
          $"{PendingContinuation.Describe()}. It holds {PendingContinuation.ChunkCount} saved segments.";

    /// <summary>Offers a recovered recording. Shown, never acted on automatically.</summary>
    public void OfferContinuation(RecoveryCandidate? candidate) => Dispatch(() =>
    {
        PendingContinuation = candidate;
        OnChanged(nameof(PendingContinuation));
        OnChanged(nameof(HasPendingContinuation));
        OnChanged(nameof(ContinuationMessage));
        RaiseCommands();
    });

    /// <summary>
    /// Adopts the recovered session and opens a new epoch, after checking both pinned endpoints
    /// are actually present. A missing device leaves it paused and says which one.
    /// </summary>
    private async Task ContinueRecoveredAsync()
    {
        if (PendingContinuation is not { } candidate)
        {
            return;
        }

        List<string> missing = [];
        if (_catalog.FindById(candidate.RenderEndpointId) is null)
        {
            missing.Add($"the playback device ({candidate.RenderDeviceName})");
        }

        if (_catalog.FindById(candidate.CaptureEndpointId) is null)
        {
            missing.Add($"the microphone ({candidate.CaptureDeviceName})");
        }

        if (missing.Count > 0)
        {
            Notice = $"This recording cannot continue yet: {string.Join(" and ", missing)} " +
                     "is not available. Reconnect it and try again, or finish the recording instead.";
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                _controller.AdoptRecoveredSession(candidate);
                _controller.Resume();
            }).ConfigureAwait(true);

            OfferContinuation(null);
            Notice = null;
        }
        catch (InvalidOperationException ex)
        {
            Notice = ex.Message;
        }
    }

    /// <summary>Adopts the recovered session and immediately finishes it, keeping its audio.</summary>
    private async Task FinishRecoveredAsync()
    {
        if (PendingContinuation is not { } candidate)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                _controller.AdoptRecoveredSession(candidate);
                _controller.Stop();
            }).ConfigureAwait(true);

            OfferContinuation(null);
            Notice = "The earlier recording has been saved.";
        }
        catch (InvalidOperationException ex)
        {
            Notice = ex.Message;
        }
    }

    /// <summary>Opens the readiness gate once recovery has finished, successfully or not.</summary>
    public void MarkReady(string? summary = null, string? warning = null) => Dispatch(() =>
    {
        IsReady = true;
        ReadinessMessage = string.Empty;

        // Same rule as the constructor: a device that has gone missing still needs saying, and
        // recovery finishing is not a reason to stop saying it.
        Notice = Join(Notice, warning ?? summary);

        OnChanged(nameof(IsReady));
        OnChanged(nameof(ReadinessMessage));
        OnChanged(nameof(CanStart));
        OnChanged(nameof(StatusHeadline));
        RaiseCommands();
    });

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

    /// <summary>Keeps both halves of a notice when two things are worth saying at once.</summary>
    private static string? Join(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        return string.IsNullOrWhiteSpace(second) || first.Contains(second, StringComparison.Ordinal)
            ? first
            : first + " " + second;
    }

    /// <summary>The two-lane activity history the recording ribbon draws. Presentation only.</summary>
    public Recording.SpeechActivityHistory RibbonHistory => _activity;

    /// <summary>Bumped every refresh so the ribbon knows to redraw the history it was handed.</summary>
    public int RibbonRevision { get; private set; }

    public string Elapsed { get; private set; } = "00:00:00";

    public double YouLevel { get; private set; }

    public double RemoteLevel { get; private set; }

    /// <summary>The same reading as a number, in the units meters are actually read in.</summary>
    public string YouLevelText { get; private set; } = "—";

    public string RemoteLevelText { get; private set; } = "—";

    public string YouCaption { get; private set; } = "You";

    public string RemoteCaption { get; private set; } = "Remote";

    /// <summary>True when the microphone track has lost its device during a live recording.</summary>
    public bool YouLost { get; private set; }

    /// <summary>True when the system track has lost its device during a live recording.</summary>
    public bool RemoteLost { get; private set; }

    /// <summary>Which device dropped, for the degraded banner. Empty when nothing is lost.</summary>
    public string DegradedHeadline => YouLost && RemoteLost
        ? "Both devices disconnected"
        : YouLost ? "Your microphone disconnected"
        : RemoteLost ? "The system audio device disconnected"
        : string.Empty;

    public string DegradedDetail => YouLost && RemoteLost
        ? "Everything captured before the disconnection is saved and intact. Stop, reconnect a device, and start again."
        : YouLost ? "The system track is still recording, and everything captured before the disconnection is saved and intact."
        : RemoteLost ? "Your microphone is still recording, and everything captured before the disconnection is saved and intact."
        : string.Empty;

    public string ChunkSummary { get; private set; } = "0 + 0";

    public string FreeSpace { get; private set; } = "—";

    public string StorageRate { get; private set; } = "—";

    /// <summary>How much this session has put on disk so far.</summary>
    public string Written { get; private set; } = "0 MB";

    /// <summary>
    /// How long the free space would last at the rate the selected formats actually cost.
    ///
    /// <para>
    /// Hours remaining rather than bytes remaining, because hours is the question — nobody plans a
    /// meeting in gigabytes. Derived from the same per-second estimate the rate figure uses, so the
    /// two can never tell different stories.
    /// </para>
    /// </summary>
    public string Headroom { get; private set; } = "—";

    /// <summary>
    /// True once there is a session worth reporting bytes and chunks for.
    ///
    /// <para>
    /// At rest the useful facts are how much room there is and how long it lasts; during a
    /// recording they are what has been written, how fast, what is left, and how many chunks are
    /// down. Showing four figures that all read zero before anything starts is noise.
    /// </para>
    /// </summary>
    public bool ShowCaptureFacts => IsRecording || IsPaused;

    public string QueueSummary { get; private set; } = "—";

    /// <summary>
    /// What the window bar says. "EchoForge", and after an em dash what the window is doing, which
    /// the bar sets in a quieter tone. Same phase the indicator and the tray read, so the three
    /// cannot disagree about whether a recording is running.
    /// </summary>
    public string WindowTitle =>
        YouLost || RemoteLost ? "EchoForge — recording, " + (YouLost && RemoteLost
            ? "both devices lost"
            : YouLost ? "microphone lost" : "system audio lost")
        : _controller.Phase switch
        {
            CapturePhase.Capturing => "EchoForge — recording",
            CapturePhase.StoppingCapture => "EchoForge — stopping",
            CapturePhase.Saving => "EchoForge — saving",
            _ => IsPaused ? "EchoForge — paused" : "EchoForge",
        };

    /// <summary>The tray tooltip. Derived from the same phase the indicator uses, so they agree.</summary>
    public string TrayText => _controller.Phase switch
    {
        CapturePhase.Capturing => $"EchoForge — recording {Elapsed}",
        CapturePhase.StoppingCapture => "EchoForge — stopping",
        CapturePhase.Saving => "EchoForge — saving",
        _ => IsPaused ? "EchoForge — paused" : "EchoForge",
    };


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

    /// <summary>Starts on the selected pair of endpoints. Start means start.</summary>
    private async Task StartAsync()
    {
        if (SelectedRender is null || SelectedCapture is null)
        {
            Notice = "Choose a playback device and a microphone first.";
            return;
        }

        AudioEndpointInfo render = SelectedRender;
        AudioEndpointInfo capture = SelectedCapture;

        // Remember the pair, so the next session opens on the same two devices.
        _settings.Save(_settings.Load() with
        {
            RenderEndpointId = render.Id,
            CaptureEndpointId = capture.Id,
        });

        Notice = null;

        // A recording pins the devices, so the test — which was only borrowing them to show levels —
        // stops first. It never wrote anything, so there is nothing to finalize.
        StopDeviceTest();

        // A new recording starts a fresh ribbon; nothing from the last meeting carries over.
        _activity.Reset();

        await Task.Run(() => _controller.Start(new RecordingRequest(
            render.Id, render.FriendlyName, capture.Id, capture.FriendlyName))).ConfigureAwait(true);
    }

    /// <summary>
    /// Stops any open session and waits for it to be durable.
    ///
    /// <para>
    /// Everything slow — joining capture threads, finalizing and hashing chunks, fsyncing the
    /// journal, writing the snapshot — happens on a background thread, so the window keeps
    /// repainting and can show Stopping then Saving while it runs.
    /// </para>
    /// </summary>
    /// <summary>
    /// True once the app has begun closing. Processing actions disable from that moment: a job
    /// started while the window is going away could not report anything to anyone.
    /// </summary>
    public bool IsShuttingDown { get; private set; }

    /// <returns>False when the recording could not be made durable, in which case do not close.</returns>
    public async Task<bool> FinalizeForShutdownAsync()
    {
        IsShuttingDown = true;
        Transcription?.UpdateHost(
            _controller.SessionId,
            sessionSettled: false,
            recordingActive: _controller.CaptureMayBeLive,
            hostReady: IsReady,
            shuttingDown: true);

        try
        {
            return await Task.Run(() =>
            {
                _controller.Stop();

                // Signals and journal writes must both settle before the app may exit.
                bool signals = _controller.WaitForSignals(TimeSpan.FromSeconds(10));
                bool writes = _controller.FlushPendingWrites(TimeSpan.FromSeconds(10));
                return signals && writes;
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Notice = $"Saving failed: {ex.Message}";
            return false;
        }
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

        YouLevel = Meter(you?.PeakLevel ?? 0);
        RemoteLevel = Meter(remote?.PeakLevel ?? 0);
        YouLevelText = Decibels(you?.PeakLevel ?? 0);
        RemoteLevelText = Decibels(remote?.PeakLevel ?? 0);
        YouCaption = you is { IsHealthy: false } ? "You — not capturing" : "You";
        RemoteCaption = remote is { IsHealthy: false } ? "Remote — not capturing" : "Remote";

        // Which track, if any, has lost its device while the session is live. Drives the degraded
        // banner and is why the ribbon flatlines that lane in red.
        YouLost = IsRecording && you is { IsHealthy: false };
        RemoteLost = IsRecording && remote is { IsHealthy: false };

        // Feed the ribbon on the canonical active-time clock, only while capture is genuinely live.
        // A track with no live status, or an unhealthy one, is recorded as inactive so a lost device
        // flatlines truthfully rather than holding its last bar.
        if (_controller.CaptureMayBeLive)
        {
            // The same mapped reading the meters show, so the ribbon and the bars beside it agree
            // about how loud something was.
            _activity.Add(
                totals.ActiveDuration.TotalSeconds,
                YouLevel, you is { IsHealthy: true },
                RemoteLevel, remote is { IsHealthy: true });
            RibbonRevision++;
        }

        // While the device test is open, its meters come from the monitor, not the recorder.
        if (IsTesting)
        {
            RaiseTestState();
        }

        int youChunks = totals.ChunksPerTrack.GetValueOrDefault(SourceTrack.Microphone);
        int remoteChunks = totals.ChunksPerTrack.GetValueOrDefault(SourceTrack.System);
        ChunkSummary = $"{youChunks} + {remoteChunks}";

        QueueSummary = status.Tracks.Count == 0
            ? "—"
            : $"queue {status.Tracks.Max(t => t.QueuedFrames)} · dropped {status.Tracks.Sum(t => t.DroppedFrames)}";

        DiskStatus disk = _controller.Disk();
        FreeSpace = $"{disk.AvailableGigabytes:0.0} GB";

        // Rate from the formats actually being captured, not a constant.
        double bytesPerSecond = _controller.EstimatedBytesPerSecond();
        double gigabytesPerHour = bytesPerSecond * 3600.0 / 1_000_000_000.0;
        StorageRate = $"{gigabytesPerHour:0.00} GB/hr";

        Written = Bytes(totals.BytesWritten);
        Headroom = bytesPerSecond <= 0
            ? "—"
            : Hours(disk.AvailableBytes / bytesPerSecond / 3600.0);

        StatusHeadline = _controller.Phase switch
        {
            CapturePhase.StoppingCapture => "Stopping…",
            CapturePhase.Saving => "Saving…",
            _ => DescribeSessionState(),
        };

        // A session is offered for transcription only once its audio has genuinely settled.
        // CaptureMayBeLive rather than IsRecording, so the transcription actions stay disabled
        // while capture threads are still winding down.
        Transcription?.UpdateHost(
            _controller.SessionId,
            _controller.State is SessionState.Recorded or SessionState.NeedsAttention,
            _controller.CaptureMayBeLive,
            IsReady,
            IsShuttingDown);

        // The summary panel needs the selected transcript revision, not just the session: there
        // is nothing to summarise until one exists, and a summary written from an earlier one is
        // stale rather than wrong.
        Summary?.UpdateHost(
            _controller.SessionId,
            Transcription?.SelectedTranscriptRevision,
            _controller.CaptureMayBeLive,
            IsReady,
            IsShuttingDown);

        foreach (string name in RefreshedProperties)
        {
            OnChanged(name);
        }

        RaiseCommands();
    }

    /// <summary>
    /// A level meter's fill, from a linear amplitude.
    ///
    /// <para>
    /// Amplitude is the wrong scale to draw. Ordinary speech peaks around a tenth of full scale
    /// and sits far below that on average, so a bar drawn straight from the sample value barely
    /// leaves the left edge and only moves if somebody shouts — which is exactly how this read.
    /// Hearing is logarithmic and so is every meter people are used to, so this maps the bottom
    /// sixty decibels of full scale across the bar. Presentation only: the recorded audio is
    /// untouched, and <see cref="YouLevelText"/> reports the real figure.
    /// </para>
    /// </summary>
    private const double MeterFloorDb = -60.0;

    private static double Meter(double amplitude)
    {
        if (amplitude <= 0 || double.IsNaN(amplitude))
        {
            return 0;
        }

        double decibels = 20 * Math.Log10(Math.Min(amplitude, 1.0));
        return Math.Clamp((decibels - MeterFloorDb) / -MeterFloorDb, 0, 1);
    }

    /// <summary>The reading itself, in dBFS, or a dash when there is no signal at all.</summary>
    private static string Decibels(double amplitude)
    {
        if (amplitude <= 0 || double.IsNaN(amplitude))
        {
            return "—";
        }

        double decibels = 20 * Math.Log10(Math.Min(amplitude, 1.0));
        return decibels <= MeterFloorDb
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{decibels:0} dB");
    }

    /// <summary>Bytes as a person reads them, in the same units the disk figures use.</summary>
    private static string Bytes(long bytes) => bytes >= 1_000_000_000
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000.0:0.00} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000.0:0} MB");

    /// <summary>
    /// Headroom, rounded to something worth saying. Past a hundred hours the exact figure stops
    /// meaning anything, so it stops pretending to be exact.
    /// </summary>
    private static string Hours(double hours) => hours switch
    {
        >= 999 => "≈ 999+ hr",
        >= 100 => string.Create(CultureInfo.InvariantCulture, $"≈ {Math.Round(hours / 10) * 10:0} hr"),
        >= 1 => string.Create(CultureInfo.InvariantCulture, $"≈ {hours:0} hr"),
        _ => string.Create(CultureInfo.InvariantCulture, $"≈ {hours * 60:0} min"),
    };

    private string DescribeSessionState() =>
        _controller.State switch
        {
            SessionState.Recording => "Recording",
            SessionState.Degraded => "Recording · degraded",
            SessionState.Paused => _controller.AwaitingResumeAfterSuspend ? "Paused · computer slept" : "Paused",
            SessionState.Finalizing => "Saving",
            SessionState.Recorded => "Saved",
            SessionState.Failed => "Failed",
            SessionState.NeedsAttention => "Needs attention",
            _ => IsReady ? "Ready" : "Checking interrupted recordings…",
        };

    private static readonly string[] RefreshedProperties =
    [
        nameof(Elapsed), nameof(YouLevel), nameof(RemoteLevel), nameof(YouCaption), nameof(RemoteCaption),
        nameof(YouLevelText), nameof(RemoteLevelText),
        nameof(RibbonRevision),
        nameof(ChunkSummary), nameof(QueueSummary), nameof(FreeSpace), nameof(StorageRate),
        nameof(Written), nameof(Headroom), nameof(ShowCaptureFacts),
        nameof(IsRecording), nameof(IsPaused), nameof(IsDegraded), nameof(IndicatorVisible),
        nameof(YouLost), nameof(RemoteLost), nameof(DegradedHeadline), nameof(DegradedDetail),
        nameof(DevicesEditable), nameof(CanStart), nameof(IsBusy), nameof(TrayText), nameof(StatusHeadline),
        nameof(WindowTitle),
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
        TestDevicesCommand.RaiseCanExecuteChanged();
        ContinueRecoveredCommand.RaiseCanExecuteChanged();
        FinishRecoveredCommand.RaiseCanExecuteChanged();
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
        StopDeviceTest();
        _controller.StateChanged -= OnControllerStateChanged;
        _controller.Notice -= OnControllerNotice;
        Transcription?.Dispose();
        Summary?.Dispose();
    }
}
