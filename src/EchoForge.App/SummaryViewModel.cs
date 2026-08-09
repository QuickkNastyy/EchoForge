using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Inference;
using EchoForge.Contracts.Settings;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Inference;
using EchoForge.Core.Summaries;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.App;

/// <summary>One selectable summary revision.</summary>
public sealed record SummaryRevisionOption(
    int Revision,
    string Label,
    bool ProducesSummaries,
    bool IsStale,
    bool WasRepaired);

public sealed record SummaryModelOption(
    string ModelId,
    string Backend,
    string ProfileId,
    string DisplayName,
    bool Installed,
    bool ProducesSummaries,
    string Status)
{
    public string Label => $"{DisplayName} — {Status}";
}

public sealed class SummaryComparisonChoice(SummaryModelOption model) : INotifyPropertyChanged
{
    private bool _isSelected;
    public SummaryModelOption Model { get; } = model;
    public string Label => Model.DisplayName;
    public bool IsEnabled => Model.Installed && Model.ProducesSummaries;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            bool selected = value && IsEnabled;
            if (_isSelected == selected) return;
            _isSelected = selected;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

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
    private readonly ISettingsStore? _settings;
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
    private double _installPercent;
    private bool _installing;

    public SummaryViewModel(SummaryCoordinator coordinator, ISettingsStore? settings = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _settings = settings;

        GenerateCommand = new AsyncRelayCommand(GenerateAsync, _gate, () => CanGenerate, m => Error = m);
        InstallModelCommand = new AsyncRelayCommand(InstallModelAsync, _gate, () => CanInstallModel, m => Error = m);
        RunModelComparisonCommand = new AsyncRelayCommand(
            RunModelComparisonAsync, _gate, () => CanRunModelComparison, m => Error = m);
        CompareRevisionsCommand = new AsyncRelayCommand(
            CompareRevisionsAsync, _gate, () => CanCompareRevisions, m => Error = m);

        // Preferred whenever it is actually here. Defaulting to it when it is not would make the
        // first click fail for a reason the user never chose.
        RebuildModels();
        SelectInitialModel();
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

    public AsyncRelayCommand RunModelComparisonCommand { get; }

    public AsyncRelayCommand CompareRevisionsCommand { get; }

    public Action<SummaryComparisonResult>? ShowComparison { get; set; }

    // -- which summariser ---------------------------------------------------------------------

    public ObservableCollection<SummaryModelOption> SummaryModels { get; } = [];

    public ObservableCollection<SummaryComparisonChoice> ComparisonModels { get; } = [];

    private SummaryModelOption? _selectedSummaryModel;

    public SummaryModelOption SelectedSummaryModel
    {
        get => _selectedSummaryModel ?? SummaryModels[0];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_selectedSummaryModel, value)) return;
            _selectedSummaryModel = value;
            if (_settings is not null)
            {
                AppSettings current = _settings.Load();
                _settings.Save(current with { SummaryModelId = value.ModelId });
            }
            Refresh();
        }
    }

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
        get => SelectedSummaryModel.ProducesSummaries;
        set
        {
            SummaryModelOption? desired = value
                ? SummaryModels.FirstOrDefault(model => model.Backend == SummaryOptions.ProductionBackend && model.Installed)
                : SummaryModels.FirstOrDefault(model => model.Backend == SummaryOptions.MockBackend);
            if (desired is null || ReferenceEquals(desired, _selectedSummaryModel))
            {
                return;
            }
            SelectedSummaryModel = desired;
        }
    }

    public bool ProductionAvailable => _coordinator.ProductionAvailable;

    /// <summary>Which summariser the next run would actually use. Never a guess.</summary>
    public string BackendText => SelectedSummaryModel.ProducesSummaries
        ? $"{SelectedSummaryModel.DisplayName} · local llama.cpp · exact transcript revision {_transcriptRevision?.ToString(CultureInfo.InvariantCulture) ?? "—"}"
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

            SummaryModelOption target = InstallTarget;
            if (_coordinator.RuntimeStatus(target.ProfileId) is not { } status)
            {
                return "No verified summary model is installed yet. Install it below to summarise with the local model.";
            }

            if (status.Ready)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"{target.DisplayName} ready ({status.BytesRequired / 1_000_000_000.0:F1} GB, {status.ProfileId}).");
            }

            return status.BytesInstalled > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{status.Description} {status.BytesInstalled / 1_000_000_000.0:F1} of {status.BytesRequired / 1_000_000_000.0:F1} GB so far.")
                : string.Create(CultureInfo.InvariantCulture,
                    $"{status.Description} It is a {status.BytesRequired / 1_000_000_000.0:F1} GB download and then runs entirely on this machine.");
        }
    }

    public bool CanInstallModel =>
        !_installing && !InstallTarget.Installed
        && !_recordingActive && !IsWorking && !_shuttingDown &&
        _coordinator.RuntimeStatus(InstallTarget.ProfileId) is { AnythingToDownload: true };

    private SummaryModelOption InstallTarget => SelectedSummaryModel.ProducesSummaries
        ? SelectedSummaryModel
        : SummaryModels.First(model => model.Backend == SummaryOptions.ProductionBackend);

    public double InstallPercent => _installPercent;

    public bool IsInstallingModel => _installing;

    public ObservableCollection<SummaryRevisionOption> Revisions { get; } = [];

    private SummaryRevisionOption? _comparisonLeftRevision;
    private SummaryRevisionOption? _comparisonRightRevision;

    public SummaryRevisionOption? ComparisonLeftRevision
    {
        get => _comparisonLeftRevision;
        set { _comparisonLeftRevision = value; OnChanged(); RaiseCommands(); }
    }

    public SummaryRevisionOption? ComparisonRightRevision
    {
        get => _comparisonRightRevision;
        set { _comparisonRightRevision = value; OnChanged(); RaiseCommands(); }
    }

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

    public string SummarySummary
    {
        get
        {
            if (_state.Selected is not { } selected)
            {
                return string.Empty;
            }

            string timing = selected.ProcessingSeconds is > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $" · {TimeSpan.FromSeconds(selected.ProcessingSeconds.Value):hh\\:mm\\:ss} processing")
                : string.Empty;
            string fallback = selected.FellBack ? " · fallback used" : string.Empty;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Version {selected.Revision} · {selected.ModelId} · {selected.DecisionCount} decisions · " +
                $"{selected.ActionCount} actions · from transcript v{selected.TranscriptRevision}{timing}{fallback}");
        }
    }

    /// <summary>
    /// True whenever the summary on show was not written by a real summariser — and before
    /// anything has run, because the placeholder is what would run.
    /// </summary>
    /// <summary>
    /// True when the summary on show, or the one that would be written next, is the placeholder.
    ///
    /// <para>
    /// Not true simply because nothing has been summarised yet: the local model is preferred
    /// wherever it is installed, and announcing a placeholder that is not going to run would be
    /// wrong. Same rule as the transcription surface, for the same reason.
    /// </para>
    /// </summary>
    public bool IsPlaceholderBackend => _state.Selected is { } shown
        ? !shown.ProducesSummaries
        : !SelectedSummaryModel.ProducesSummaries;

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
        (SelectedSummaryModel.Installed || !SelectedSummaryModel.ProducesSummaries) &&
        !_recordingActive && !IsWorking && !_coordinator.IsRunning && !HasSummary;

    public bool CanGenerateAgain =>
        _sessionId is not null && _hasTranscript && _hostReady && !_shuttingDown &&
        (SelectedSummaryModel.Installed || !SelectedSummaryModel.ProducesSummaries) &&
        !_recordingActive && !IsWorking && !_coordinator.IsRunning && HasSummary;

    public bool CanCancel => IsWorking && !_shuttingDown;

    public bool CanRunModelComparison =>
        _sessionId is not null && _transcriptRevision is not null && _hostReady && !_shuttingDown
        && !_recordingActive && !IsWorking && !_coordinator.IsRunning
        && ComparisonModels.Count(choice => choice.IsSelected && choice.IsEnabled) >= 2;

    public bool CanCompareRevisions =>
        ComparisonLeftRevision is { } left && ComparisonRightRevision is { } right
        && left.Revision != right.Revision && !_shuttingDown && !IsWorking;

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

    /// <summary>
    /// The backend currently chosen, shared with "generate again" in the library so both mean the
    /// same thing rather than each carrying its own default.
    /// </summary>
    public SummaryOptions CurrentOptions() => new()
    {
        Backend = SelectedSummaryModel.Backend,
        SummaryProfile = SelectedSummaryModel.ProfileId,
        TranscriptRevision = _transcriptRevision,
    };

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
        SummaryOptions options = CurrentOptions();

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

    private async Task RunModelComparisonAsync()
    {
        if (_sessionId is not { } sessionId || _transcriptRevision is not { } transcriptRevision)
        {
            return;
        }

        List<SummaryModelOption> models =
        [
            .. ComparisonModels.Where(choice => choice.IsSelected && choice.IsEnabled)
                .Select(choice => choice.Model)
        ];
        if (models.Count < 2)
        {
            Error = "Select at least two installed summary models.";
            return;
        }

        Error = null;
        List<int> revisions = [];
        for (int index = 0; index < models.Count; index++)
        {
            SummaryModelOption model = models[index];
            Notice = $"Running {index + 1} of {models.Count}: {model.DisplayName}. Models are loaded sequentially.";
            SummaryRunResult result = await Task.Run(() => _coordinator.SummarizeAsync(
                sessionId,
                new SummaryOptions
                {
                    Backend = model.Backend,
                    SummaryProfile = model.ProfileId,
                    TranscriptRevision = transcriptRevision,
                })).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                if (result.State == ProcessingStageState.Cancelled)
                {
                    Notice = $"Summary comparison cancelled after {revisions.Count} completed runs.";
                }
                else
                {
                    Error = $"{model.DisplayName}: {result.Message}";
                }
                break;
            }
            if (result.Revision is { } revision) revisions.Add(revision);
            Refresh();
        }

        if (revisions.Count == models.Count)
        {
            Notice = $"Comparison complete. {revisions.Count} immutable summaries were saved from transcript v{transcriptRevision}.";
            RebuildRevisions();
            ComparisonLeftRevision = Revisions.FirstOrDefault(option => option.Revision == revisions[^2]);
            ComparisonRightRevision = Revisions.FirstOrDefault(option => option.Revision == revisions[^1]);
        }
        Refresh();
    }

    private async Task CompareRevisionsAsync()
    {
        if (_sessionId is not { } sessionId
            || ComparisonLeftRevision is not { } left
            || ComparisonRightRevision is not { } right)
        {
            return;
        }

        SummaryComparisonResult? comparison = await Task.Run(() =>
        {
            SummaryDocument? a = _coordinator.ReadSummary(sessionId, left.Revision);
            SummaryDocument? b = _coordinator.ReadSummary(sessionId, right.Revision);
            return a is null || b is null ? null : SummaryComparer.Compare(a, b);
        }).ConfigureAwait(true);

        if (comparison is null)
        {
            Error = "One of those summary revisions could not be read.";
            return;
        }
        ShowComparison?.Invoke(comparison);
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
        SummaryModelOption target = InstallTarget;
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
                .Run(() => _coordinator.InstallProductionAsync(
                    profileId: target.ProfileId, progress: progress))
                .ConfigureAwait(true);

            Notice = installed
                ? "The local summary model is ready. Summaries are now generated on this machine by a real model."
                : "The summary model could not be installed. Nothing was changed, and the placeholder still works.";

            string selectedId = installed ? target.ModelId : SelectedSummaryModel.ModelId;
            RebuildModels();
            _selectedSummaryModel = SummaryModels.FirstOrDefault(model => model.ModelId == selectedId)
                ?? SummaryModels[0];
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
        int? leftRevision = _comparisonLeftRevision?.Revision;
        int? rightRevision = _comparisonRightRevision?.Revision;
        List<SummaryRevisionOption> wanted =
        [
            .. _state.Revisions
                .Where(r => r.FileExists)
                .OrderByDescending(r => r.Revision)
                .Select(r => new SummaryRevisionOption(
                    r.Revision,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Version {r.Revision} · {r.ModelId} · {r.CreatedUtc.ToLocalTime():MMM d, h:mm tt} · transcript v{r.TranscriptRevision}{(r.ProducesSummaries ? string.Empty : " · placeholder")}{(r.WasRepaired ? " · regenerated after a refusal" : string.Empty)}{(r.FellBack ? " · fallback used" : string.Empty)}"),
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

        _comparisonLeftRevision = leftRevision is { } left
            ? Revisions.FirstOrDefault(option => option.Revision == left)
            : Revisions.Skip(1).FirstOrDefault();
        _comparisonRightRevision = rightRevision is { } right
            ? Revisions.FirstOrDefault(option => option.Revision == right)
            : Revisions.FirstOrDefault();
        OnChanged(nameof(ComparisonLeftRevision));
        OnChanged(nameof(ComparisonRightRevision));
    }

    private void RebuildModels()
    {
        string? selectedId = _selectedSummaryModel?.ModelId;
        HashSet<string> compared =
        [
            .. ComparisonModels.Where(choice => choice.IsSelected).Select(choice => choice.Model.ModelId)
        ];

        SummaryModels.Clear();
        SummaryModels.Add(new SummaryModelOption(
            SummaryModelIds.Mock,
            SummaryOptions.MockBackend,
            string.Empty,
            "Deterministic placeholder",
            Installed: true,
            ProducesSummaries: false,
            "Available"));
        foreach (SummaryModelDefinition definition in InferenceModelRegistry.SummaryModels)
        {
            bool installed = _coordinator.RuntimeStatus(definition.ArtifactProfileId)?.Ready == true;
            string status = installed
                ? "Installed"
                : definition.Maturity == ModelMaturity.Experimental ? "Optional / install" : "Install";
            SummaryModels.Add(new SummaryModelOption(
                definition.Id,
                definition.BackendId,
                definition.ArtifactProfileId,
                definition.DisplayName,
                installed,
                ProducesSummaries: true,
                status));
        }

        if (selectedId is not null)
        {
            _selectedSummaryModel = SummaryModels.FirstOrDefault(model => model.ModelId == selectedId)
                ?? _selectedSummaryModel;
        }

        foreach (SummaryComparisonChoice choice in ComparisonModels)
        {
            choice.PropertyChanged -= OnComparisonChoiceChanged;
        }
        ComparisonModels.Clear();
        foreach (SummaryModelOption model in SummaryModels.Where(model => model.ProducesSummaries))
        {
            SummaryComparisonChoice choice = new(model) { IsSelected = compared.Contains(model.ModelId) };
            choice.PropertyChanged += OnComparisonChoiceChanged;
            ComparisonModels.Add(choice);
        }
        OnChanged(nameof(SummaryModels));
        OnChanged(nameof(ComparisonModels));
    }

    private void SelectInitialModel()
    {
        string? remembered = _settings?.Load().SummaryModelId;
        _selectedSummaryModel = SummaryModels.FirstOrDefault(model => model.ModelId == remembered)
            ?? SummaryModels.FirstOrDefault(model =>
                model.Backend == SummaryOptions.ProductionBackend && model.Installed)
            ?? SummaryModels[0];
    }

    private void OnComparisonChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SummaryComparisonChoice.IsSelected))
        {
            OnChanged(nameof(CanRunModelComparison));
            RaiseCommands();
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
        nameof(InstallPercent), nameof(SelectedSummaryModel), nameof(SummaryModels),
        nameof(ComparisonLeftRevision), nameof(ComparisonRightRevision),
        nameof(CanRunModelComparison), nameof(CanCompareRevisions),
    ];

    private void RaiseCommands()
    {
        GenerateCommand.RaiseCanExecuteChanged();
        GenerateAgainCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        InstallModelCommand.RaiseCanExecuteChanged();
        RunModelComparisonCommand.RaiseCanExecuteChanged();
        CompareRevisionsCommand.RaiseCanExecuteChanged();
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
        foreach (SummaryComparisonChoice choice in ComparisonModels)
        {
            choice.PropertyChanged -= OnComparisonChoiceChanged;
        }
    }
}
