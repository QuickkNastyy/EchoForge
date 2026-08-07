using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.App;

/// <summary>One selectable summary revision.</summary>
public sealed record SummaryRevisionOption(
    int Revision,
    string Label,
    bool ProducesSummaries,
    bool IsStale,
    bool WasRepaired);

/// <summary>
/// The summary surface.
///
/// <para>
/// It holds no truth of its own: stage, revisions and progress all come from the coordinator,
/// which reads them from the journal. Everything slow — chunking a transcript, launching a
/// worker — runs off the UI thread, because this is the thread painting the recording indicator.
/// </para>
/// </summary>
public sealed class SummaryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SummaryCoordinator _coordinator;
    private readonly OperationGate _gate = new();

    private string? _sessionId;
    private bool _hasTranscript;
    private bool _hostReady;
    private bool _recordingActive;
    private bool _shuttingDown;
    private bool _disposed;

    private SummaryState _state = SummaryState.Empty;
    private string? _notice;
    private string? _error;
    private bool _useProductionModel;
    private double _installPercent;
    private bool _installing;

    public SummaryViewModel(SummaryCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        GenerateCommand = new AsyncRelayCommand(GenerateAsync, _gate, () => CanGenerate, m => Error = m);
        InstallModelCommand = new AsyncRelayCommand(InstallModelAsync, _gate, () => CanInstallModel, m => Error = m);

        // Preferred whenever it is actually here. Defaulting to it when it is not would make the
        // first click fail for a reason the user never chose.
        _useProductionModel = coordinator.ProductionAvailable;
        GenerateAgainCommand = new AsyncRelayCommand(GenerateAsync, _gate, () => CanGenerateAgain, m => Error = m);
        CancelCommand = new AsyncRelayCommand(CancelAsync, _gate, () => CanCancel, m => Error = m);

        _gate.Changed += (_, _) => Dispatch(RaiseCommands);

        _coordinator.StateChanged += OnStateChanged;
        _coordinator.ProgressChanged += OnProgress;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncRelayCommand GenerateCommand { get; }

    public AsyncRelayCommand GenerateAgainCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand InstallModelCommand { get; }

    // -- which summariser ---------------------------------------------------------------------

    /// <summary>
    /// True to run the local language model, false to run the deterministic placeholder.
    ///
    /// <para>
    /// Offered as a choice rather than decided silently, because the two produce genuinely
    /// different things and somebody who has just downloaded seven gigabytes is owed a clear
    /// answer about which one they are reading.
    /// </para>
    /// </summary>
    public bool UseProductionModel
    {
        get => _useProductionModel;
        set
        {
            if (_useProductionModel == value)
            {
                return;
            }

            _useProductionModel = value;
            Refresh();
        }
    }

    public bool ProductionAvailable => _coordinator.ProductionAvailable;

    /// <summary>Which summariser the next run would actually use. Never a guess.</summary>
    public string BackendText =>
        UseProductionModel && ProductionAvailable
            ? "Local model (Gemma 4 12B, on this machine)"
            : "Deterministic placeholder (quotes the transcript, understands nothing)";

    /// <summary>What the local model still needs, in one line the panel can show.</summary>
    public string ModelStatusText
    {
        get
        {
            if (_installing)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Downloading and verifying the local summary model - {_installPercent:F0}%");
            }

            if (_coordinator.RuntimeStatus() is not { } status)
            {
                return "No verified summary model is configured in this build.";
            }

            if (status.Ready)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Local summary model ready ({status.BytesRequired / 1_000_000_000.0:F1} GB, {status.ProfileId}).");
            }

            return status.BytesInstalled > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{status.Description} {status.BytesInstalled / 1_000_000_000.0:F1} of {status.BytesRequired / 1_000_000_000.0:F1} GB so far.")
                : string.Create(CultureInfo.InvariantCulture,
                    $"{status.Description} It is a {status.BytesRequired / 1_000_000_000.0:F1} GB download and then runs entirely on this machine.");
        }
    }

    public bool CanInstallModel =>
        !_installing && !ProductionAvailable && !_recordingActive && !IsWorking && !_shuttingDown &&
        _coordinator.RuntimeStatus() is { AnythingToDownload: true };

    public double InstallPercent => _installPercent;

    public bool IsInstallingModel => _installing;

    public ObservableCollection<SummaryRevisionOption> Revisions { get; } = [];

    public SummaryRevisionOption? SelectedRevision
    {
        get => Revisions.FirstOrDefault(r => r.Revision == _state.SelectedRevision);
        set
        {
            if (value is null || _sessionId is null || value.Revision == _state.SelectedRevision)
            {
                return;
            }

            if (!_coordinator.SelectRevision(_sessionId, value.Revision))
            {
                Error = "That summary version is no longer available.";
            }

            Refresh();
        }
    }

    // -- what the window shows -------------------------------------------------------------

    public string StageText => _state.Stage switch
    {
        ProcessingStageState.NotRequested => _hasTranscript ? "Not summarised" : "No transcript yet",
        ProcessingStageState.Queued => "Queued",
        ProcessingStageState.Running => "Summarising",
        ProcessingStageState.Succeeded => "Summarised",
        ProcessingStageState.Failed => "Summarising failed",
        ProcessingStageState.Cancelled => "Summarising cancelled",
        _ => "—",
    };

    public bool IsWorking => _state.Stage is ProcessingStageState.Queued or ProcessingStageState.Running;

    public double ProgressPercent { get; private set; }

    public string ProgressDescription { get; private set; } = string.Empty;

    public bool HasSummary => _state.Selected is not null;

    public string SummarySummary => _state.Selected is { } selected
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"Version {selected.Revision} · {selected.DecisionCount} decisions · {selected.ActionCount} actions · from transcript v{selected.TranscriptRevision}")
        : string.Empty;

    /// <summary>
    /// True whenever the summary on show was not written by a real summariser — and before
    /// anything has run, because the placeholder is what would run.
    /// </summary>
    public bool IsPlaceholderBackend => _state.Selected is null || !_state.Selected.ProducesSummaries;

    public string PlaceholderWarning => _state.Selected is { ProducesSummaries: false } selected
        ? $"Version {selected.Revision.ToString(CultureInfo.InvariantCulture)} was produced by a deterministic " +
          "placeholder. It read the real transcript and cites real segments, but it groups and quotes what " +
          "was said rather than understanding it — so it is not a summary and should not be read as one."
        : "This build summarises with a deterministic placeholder. It reads the real transcript and " +
          "cites real segments, but it groups and quotes what was said rather than understanding it — " +
          "so what it produces is not a summary and should not be read as one.";

    /// <summary>
    /// A summary is stale when the session's selected transcript is no longer the one it was
    /// written from. It stays fully readable, and its evidence still resolves, because it still
    /// points at its own revision.
    /// </summary>
    public bool IsStale => _state.Selected?.IsStaleAgainst(_transcriptRevision) ?? false;

    public string StaleNotice =>
        IsStale
            ? "This summary was written from an earlier transcript version. It is still accurate about that version; generate again to bring it up to date."
            : string.Empty;

    public string? Notice
    {
        get => _notice;
        private set { _notice = value; OnChanged(); OnChanged(nameof(HasNotice)); }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    public string? Error
    {
        get => _error;
        private set { _error = value; OnChanged(); OnChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    private int? _transcriptRevision;

    // -- availability -----------------------------------------------------------------------

    public bool CanGenerate =>
        _sessionId is not null && _hasTranscript && _hostReady && !_shuttingDown &&
        !_recordingActive && !IsWorking && !_coordinator.IsRunning && !HasSummary;

    public bool CanGenerateAgain =>
        _sessionId is not null && _hasTranscript && _hostReady && !_shuttingDown &&
        !_recordingActive && !IsWorking && !_coordinator.IsRunning && HasSummary;

    public bool CanCancel => IsWorking && !_shuttingDown;

    /// <summary>
    /// Pushed by the main view model, so there is one place that reads recorder state and the
    /// panels cannot disagree about whether capture is live.
    /// </summary>
    public void UpdateHost(
        string? sessionId,
        int? transcriptRevision,
        bool recordingActive,
        bool hostReady,
        bool shuttingDown)
    {
        bool sessionChanged = !string.Equals(_sessionId, sessionId, StringComparison.Ordinal);

        if (!sessionChanged &&
            _transcriptRevision == transcriptRevision &&
            _recordingActive == recordingActive &&
            _hostReady == hostReady &&
            _shuttingDown == shuttingDown)
        {
            return;
        }

        _sessionId = sessionId;
        _transcriptRevision = transcriptRevision;
        _hasTranscript = transcriptRevision is not null;
        _recordingActive = recordingActive;
        _hostReady = hostReady;
        _shuttingDown = shuttingDown;

        if (sessionChanged)
        {
            Error = null;
            Notice = null;
            ProgressPercent = 0;
        }

        Refresh();
    }

    // -- commands ---------------------------------------------------------------------------

    private async Task GenerateAsync()
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }

        Error = null;
        Notice = null;

        // Chunking walks the whole transcript and the worker launch blocks; neither belongs on
        // the thread that paints the window.
        SummaryOptions options = new()
        {
            Backend = UseProductionModel && ProductionAvailable
                ? SummaryOptions.ProductionBackend
                : SummaryOptions.MockBackend,
        };

        SummaryRunResult result = await Task
            .Run(() => _coordinator.SummarizeAsync(sessionId, options))
            .ConfigureAwait(true);

        if (result.Succeeded)
        {
            Notice = result.Message;
        }
        else if (result.State == ProcessingStageState.Cancelled)
        {
            Notice = result.Message;
        }
        else
        {
            Error = result.Message;
        }

        ProgressPercent = result.Succeeded ? 100 : 0;
        ProgressDescription = string.Empty;
        Refresh();
    }

    /// <summary>
    /// Downloads and unpacks the local model. Seven gigabytes, so it is never implicit.
    ///
    /// <para>
    /// Off the UI thread, like everything else here. This is the thread painting the recording
    /// indicator, and a download that blocked it would freeze a recording in progress.
    /// </para>
    /// </summary>
    private async Task InstallModelAsync()
    {
        Error = null;
        Notice = null;
        _installing = true;
        _installPercent = 0;
        Refresh();

        Progress<ArtifactProgressEventArgs> progress = new(update => Dispatch(() =>
        {
            _installPercent = Math.Round(update.Fraction * 100, 1);
            OnChanged(nameof(InstallPercent));
            OnChanged(nameof(ModelStatusText));
        }));

        try
        {
            bool installed = await Task
                .Run(() => _coordinator.InstallProductionAsync(progress: progress))
                .ConfigureAwait(true);

            Notice = installed
                ? "The local summary model is ready. Summaries are now generated on this machine by a real model."
                : "The summary model could not be installed. Nothing was changed, and the placeholder still works.";

            _useProductionModel = installed || _useProductionModel;
        }
        finally
        {
            _installing = false;
            Refresh();
        }
    }

    private Task CancelAsync()
    {
        Notice = null;
        return Task.Run(_coordinator.Cancel);
    }

    // -- coordinator plumbing ------------------------------------------------------------------

    private void OnStateChanged(object? sender, EventArgs e) => Dispatch(Refresh);

    private void OnProgress(object? sender, SummaryProgressEventArgs e) => Dispatch(() =>
    {
        if (!string.Equals(e.SessionId, _sessionId, StringComparison.Ordinal))
        {
            return;
        }

        ProgressPercent = Math.Round(e.Fraction * 100, 1);
        ProgressDescription = Describe(e);

        OnChanged(nameof(ProgressPercent));
        OnChanged(nameof(ProgressDescription));
    });

    /// <summary>
    /// What the progress bar says it is doing.
    ///
    /// <para>
    /// The repair is named rather than hidden behind a generic "working". A user who watches the
    /// same job appear to start over deserves to know it was refused and is being asked again,
    /// not to wonder whether something is stuck.
    /// </para>
    /// </summary>
    private static string Describe(SummaryProgressEventArgs e) => e.Stage switch
    {
        SummaryCoordinator.RepairingStage =>
            "The first summary was not supported by the transcript — generating it once more",
        "preparing" => "Loading the local model",
        "merging" => "Bringing what was found together",
        "validating" => "Checking every claim against the transcript",
        "writing_output" => "Saving",
        _ => e.TotalUnits <= 0
            ? "Working"
            : string.Create(CultureInfo.InvariantCulture, $"Reading the transcript — part {e.CompletedUnits} of {e.TotalUnits}"),
    };

    private void Refresh()
    {
        _state = _sessionId is null ? SummaryState.Empty : _coordinator.StateFor(_sessionId);

        if (_state.Stage == ProcessingStageState.Failed &&
            _state.CurrentJob?.FailureSummary is { } summary &&
            !IsWorking)
        {
            Error = summary;
        }

        RebuildRevisions();

        foreach (string name in RefreshedProperties)
        {
            OnChanged(name);
        }

        RaiseCommands();
    }

    private void RebuildRevisions()
    {
        List<SummaryRevisionOption> wanted =
        [
            .. _state.Revisions
                .Where(r => r.FileExists)
                .OrderByDescending(r => r.Revision)
                .Select(r => new SummaryRevisionOption(
                    r.Revision,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Version {r.Revision} · {r.CreatedUtc.ToLocalTime():d MMM HH:mm} · transcript v{r.TranscriptRevision}{(r.ProducesSummaries ? string.Empty : " · placeholder")}{(r.WasRepaired ? " · regenerated after a refusal" : string.Empty)}"),
                    r.ProducesSummaries,
                    r.IsStaleAgainst(_transcriptRevision),
                    r.WasRepaired))
        ];

        if (Revisions.SequenceEqual(wanted))
        {
            return;
        }

        Revisions.Clear();
        foreach (SummaryRevisionOption option in wanted)
        {
            Revisions.Add(option);
        }
    }

    private static readonly string[] RefreshedProperties =
    [
        nameof(StageText), nameof(IsWorking), nameof(HasSummary), nameof(SummarySummary),
        nameof(IsPlaceholderBackend), nameof(PlaceholderWarning), nameof(IsStale), nameof(StaleNotice),
        nameof(SelectedRevision), nameof(CanGenerate), nameof(CanGenerateAgain), nameof(CanCancel),
        nameof(ProgressPercent), nameof(ProgressDescription),
        nameof(UseProductionModel), nameof(ProductionAvailable), nameof(BackendText),
        nameof(ModelStatusText), nameof(CanInstallModel), nameof(IsInstallingModel),
        nameof(InstallPercent),
    ];

    private void RaiseCommands()
    {
        GenerateCommand.RaiseCanExecuteChanged();
        GenerateAgainCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        InstallModelCommand.RaiseCanExecuteChanged();
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

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.StateChanged -= OnStateChanged;
        _coordinator.ProgressChanged -= OnProgress;
    }
}
