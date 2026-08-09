using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Inference;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Settings;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Exports;
using EchoForge.Core.Inference;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Processing;

namespace EchoForge.App;

/// <summary>Where an export should go. Null from the prompt means the user cancelled.</summary>
public sealed record ExportDestination(string Path, bool OverwriteConfirmed);

/// <summary>
/// Asks the user where to save an export. Abstracted so export behaviour can be tested without
/// showing a dialog.
/// </summary>
public interface IExportDestinationPrompt
{
    ExportDestination? Ask(string suggestedFileName, TranscriptExportFormat format);
}

/// <summary>One selectable revision, described for a list.</summary>
public sealed record TranscriptRevisionOption(int Revision, string Label, bool RecognizesSpeech);

/// <summary>One export format, with the name a person would recognise.</summary>
public sealed record ExportFormatOption(TranscriptExportFormat Format, string Label);

/// <summary>A selectable backend. The label always says whether it recognises speech.</summary>
public sealed record BackendOption(string Id, string Label, bool RecognizesSpeech);

/// <summary>A selectable compute profile. The worker may climb down from it, and says so.</summary>
public sealed record ComputeProfileOption(string Id, string Label);

/// <summary>A model and its current verified installation/usability state.</summary>
public sealed record AsrModelOption(
    AsrModelDefinition Definition,
    bool Installed,
    bool Usable,
    string Status)
{
    public string Id => Definition.Id;
    public string Label => $"{Definition.DisplayName} — {Status}";
}

public sealed record VadModeOption(VadMode Mode, string Label, string Description);

/// <summary>One model in a sequential, same-recording comparison run.</summary>
public sealed class AsrComparisonChoice(AsrModelOption model) : INotifyPropertyChanged
{
    private bool _isSelected;

    public AsrModelOption Model { get; } = model;

    public string Label => Model.Definition.DisplayName;

    public bool IsEnabled => Model.Usable;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            bool selected = value && IsEnabled;
            if (_isSelected == selected)
            {
                return;
            }

            _isSelected = selected;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A language, or automatic detection when the code is null.</summary>
public sealed record LanguageOption(string? Code, string Label);

/// <summary>
/// The transcription surface: ask for a transcript, watch it happen, choose which revision is
/// current, and export it.
///
/// <para>
/// It holds no processing truth of its own. Stage, progress, revisions, and the selected revision
/// are all read from the coordinator, which reads them from the journal — so the window cannot
/// disagree with what was actually activated. Everything slow (hashing source chunks, launching a
/// worker, writing an export) happens off the UI thread.
/// </para>
/// </summary>
public sealed class TranscriptionViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly TranscriptionCoordinator _coordinator;
    private readonly IExportDestinationPrompt _exportPrompt;
    private readonly ISettingsStore? _settings;
    private readonly string? _recommendedModelId;
    private readonly string? _recommendedComputeProfile;
    private readonly OperationGate _gate = new();

    private string? _sessionId;
    private bool _sessionSettled;
    private bool _recordingActive;
    private bool _hostReady;
    private bool _shuttingDown;
    private bool _disposed;

    private TranscriptionState _state = TranscriptionState.Empty;
    private string? _notice;
    private string? _error;
    private string _progressDescription = string.Empty;
    private double _progressPercent;

    public TranscriptionViewModel(
        TranscriptionCoordinator coordinator,
        IExportDestinationPrompt exportPrompt,
        ISettingsStore? settings = null,
        string? recommendedModelId = null,
        string? recommendedComputeProfile = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(exportPrompt);

        _coordinator = coordinator;
        _exportPrompt = exportPrompt;
        _settings = settings;
        _recommendedModelId = recommendedModelId;
        _recommendedComputeProfile = recommendedComputeProfile;

        TranscribeCommand = new AsyncRelayCommand(TranscribeAsync, _gate, () => CanTranscribe, m => Error = m);
        TranscribeAgainCommand = new AsyncRelayCommand(TranscribeAsync, _gate, () => CanTranscribeAgain, m => Error = m);
        CancelCommand = new AsyncRelayCommand(CancelAsync, _gate, () => CanCancel, m => Error = m);
        ExportCommand = new AsyncRelayCommand(ExportAsync, _gate, () => CanExport, m => Error = m);
        RunModelComparisonCommand = new AsyncRelayCommand(
            RunModelComparisonAsync, _gate, () => CanRunModelComparison, m => Error = m);
        CompareRevisionsCommand = new AsyncRelayCommand(
            CompareRevisionsAsync, _gate, () => CanCompareRevisions, m => Error = m);
        PrepareProductionCommand = new AsyncRelayCommand(
            () => PrepareAsync(installMissing: false), _gate, () => CanPrepare, m => Error = m);
        InstallModelsCommand = new AsyncRelayCommand(
            () => PrepareAsync(installMissing: true), _gate, () => CanPrepare, m => Error = m);

        _gate.Changed += (_, _) => Dispatch(RaiseCommands);

        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.ProgressChanged += OnCoordinatorProgress;
        _coordinator.PreparationProgress += OnPreparationProgress;

        RebuildModels();
        RestoreInferenceSelections();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncRelayCommand TranscribeCommand { get; }

    public AsyncRelayCommand TranscribeAgainCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public AsyncRelayCommand RunModelComparisonCommand { get; }

    public AsyncRelayCommand CompareRevisionsCommand { get; }

    /// <summary>Provided by the composition root so comparison remains testable without a Window.</summary>
    public Action<TranscriptComparisonResult>? ShowComparison { get; set; }

    /// <summary>Checks artifacts and prepares audio, without downloading anything.</summary>
    public AsyncRelayCommand PrepareProductionCommand { get; }

    /// <summary>The same, but permitted to download the pinned models first.</summary>
    public AsyncRelayCommand InstallModelsCommand { get; }

    public ObservableCollection<TranscriptRevisionOption> Revisions { get; } = [];

    public ObservableCollection<AsrComparisonChoice> ComparisonModels { get; } = [];

    private TranscriptRevisionOption? _comparisonLeftRevision;
    private TranscriptRevisionOption? _comparisonRightRevision;

    public TranscriptRevisionOption? ComparisonLeftRevision
    {
        get => _comparisonLeftRevision;
        set { _comparisonLeftRevision = value; OnChanged(); RaiseCommands(); }
    }

    public TranscriptRevisionOption? ComparisonRightRevision
    {
        get => _comparisonRightRevision;
        set { _comparisonRightRevision = value; OnChanged(); RaiseCommands(); }
    }

    public ObservableCollection<ExportFormatOption> ExportFormats { get; } =
    [
        new(TranscriptExportFormat.Text, TranscriptExporter.Describe(TranscriptExportFormat.Text)),
        new(TranscriptExportFormat.Json, TranscriptExporter.Describe(TranscriptExportFormat.Json)),
        new(TranscriptExportFormat.Srt, TranscriptExporter.Describe(TranscriptExportFormat.Srt)),
        new(TranscriptExportFormat.Vtt, TranscriptExporter.Describe(TranscriptExportFormat.Vtt)),
    ];

    private ExportFormatOption? _selectedExportFormat;

    public ExportFormatOption SelectedExportFormat
    {
        get => _selectedExportFormat ??= ExportFormats[0];
        set { _selectedExportFormat = value; OnChanged(); }
    }

    /// <summary>
    /// Which revision the app treats as current. Setting it records the choice durably; a
    /// selection the store refuses is reverted rather than shown as if it had taken.
    /// </summary>
    public TranscriptRevisionOption? SelectedRevision
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
                Error = "That transcript version is no longer available.";
            }

            RefreshFromCoordinator();
        }
    }

    // -- what the window shows ---------------------------------------------------------------

    public ProcessingStageState Stage => _state.Stage;

    public string StageText => _state.Stage switch
    {
        ProcessingStageState.NotRequested => HasSession ? "Not transcribed" : "No recording selected",
        ProcessingStageState.Queued => "Queued — waiting for recording to finish",
        ProcessingStageState.Running => "Transcribing",
        ProcessingStageState.Succeeded => "Transcribed",
        ProcessingStageState.Failed => "Transcription failed",
        ProcessingStageState.Cancelled => "Transcription cancelled",
        _ => "—",
    };

    public bool IsWorking => _state.Stage is ProcessingStageState.Queued or ProcessingStageState.Running;

    public double ProgressPercent
    {
        get => _progressPercent;
        private set { _progressPercent = value; OnChanged(); }
    }

    /// <summary>Which track and how far through it, in words rather than internal stage names.</summary>
    public string ProgressDescription
    {
        get => _progressDescription;
        private set { _progressDescription = value; OnChanged(); }
    }

    public bool HasTranscript => _state.Selected is not null;

    /// <summary>Which transcript revision is current, for anything downstream of it.</summary>
    public int? SelectedTranscriptRevision => _state.SelectedRevision;

    public string TranscriptSummary
    {
        get
        {
            TranscriptRevisionRecord? selected = _state.Selected;
            if (selected is null)
            {
                return string.Empty;
            }

            double? elapsed = selected.TotalProcessingSeconds ?? selected.ProcessingSeconds;
            string timing = elapsed is > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $" · {TimeSpan.FromSeconds(elapsed.Value):hh\\:mm\\:ss} processing")
                : string.Empty;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Version {selected.Revision} · {selected.ModelId} · " +
                $"requested {selected.RequestedComputeProfile} / actual {selected.ActualComputeProfile} · " +
                $"{selected.VadMode} VAD · {selected.SegmentCount} segments · " +
                $"{TimeSpan.FromSeconds(selected.DurationSeconds):hh\\:mm\\:ss} source{timing}");
        }
    }

    /// <summary>
    /// True when what is on show, or what is about to run, is not real speech recognition.
    ///
    /// <para>
    /// Deliberately not true merely because nothing has run yet. This build has real recognition
    /// and selects it by default wherever its models are installed, so leading an idle screen with
    /// "this build understands nothing" would be false as well as loud. The warning appears when a
    /// placeholder transcript is being shown, or when the placeholder is genuinely what the next
    /// run would use.
    /// </para>
    /// </summary>
    public bool IsPlaceholderBackend => _state.Selected is { } shown
        ? !shown.RecognizesSpeech
        : !SelectedBackend.RecognizesSpeech;

    public string PlaceholderWarning => _state.Selected is { RecognizesSpeech: false } selected
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"Version {selected.Revision} was produced by a deterministic placeholder. It read the real " +
            $"audio but performed no speech recognition, so the words are not a record of what was said.")
        : "This build transcribes with a deterministic placeholder. It reads the real audio but " +
          "performs no speech recognition, so the words are not a record of what was said.";

    /// <summary>What actually ran, as reported by the worker rather than inferred here.</summary>
    public string BackendSummary
    {
        get
        {
            string worker = _coordinator.LastWorkerEnvironment?.Summary ?? "worker not started yet";
            TranscriptRevisionRecord? selected = _state.Selected;

            return selected is null
                ? worker
                : $"{selected.Backend} · {selected.ModelId}@{selected.ModelRevision} · " +
                  $"requested {selected.RequestedComputeProfile} / actual {selected.ActualComputeProfile} · {worker}";
        }
    }

    public string? Notice
    {
        get => _notice;
        private set { _notice = value; OnChanged(); OnChanged(nameof(HasNotice)); }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    /// <summary>An actionable, safe message. Never worker text and never a path.</summary>
    public string? Error
    {
        get => _error;
        private set { _error = value; OnChanged(); OnChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool HasSession => _sessionId is not null;

    // -- availability -------------------------------------------------------------------------

    /// <summary>
    /// True only when there is a finished recording, startup has settled, nothing is shutting
    /// down, and no other processing job holds the coordinator.
    /// </summary>
    public bool CanTranscribe =>
        HasSession && _sessionSettled && _hostReady && !_shuttingDown &&
        SelectedAsrModel.Usable && !IsWorking && !_coordinator.IsRunning && !_coordinator.IsQueued && !HasTranscript;

    public bool CanTranscribeAgain =>
        HasSession && _sessionSettled && _hostReady && !_shuttingDown &&
        SelectedAsrModel.Usable && !IsWorking && !_coordinator.IsRunning && !_coordinator.IsQueued && HasTranscript;

    public bool CanCancel => IsWorking && !_shuttingDown;

    public bool CanExport => HasTranscript && !_shuttingDown && !IsWorking;

    public bool CanRunModelComparison =>
        HasSession && _sessionSettled && _hostReady && !_shuttingDown && !IsWorking
        && !_coordinator.IsRunning && !_coordinator.IsQueued
        && ComparisonModels.Count(choice => choice.IsSelected && choice.IsEnabled) >= 2;

    public bool CanCompareRevisions =>
        ComparisonLeftRevision is { } left && ComparisonRightRevision is { } right
        && left.Revision != right.Revision && !_shuttingDown && !IsWorking;

    /// <summary>
    /// Production preparation is available for a settled recording, when nothing else is running
    /// and this build can do it at all. On a machine with no worker runtime the buttons simply
    /// are not there, and recording is unaffected.
    /// </summary>
    public bool CanPrepare =>
        HasSession && _sessionSettled && _hostReady && !_shuttingDown &&
        _coordinator.SupportsProductionProfiles && !IsPreparing &&
        !IsWorking && !_coordinator.IsRunning && !_coordinator.IsQueued;

    public bool SupportsProduction => _coordinator.SupportsProductionProfiles;

    public bool IsPreparing { get; private set; }

    /// <summary>
    /// Where production readiness stands, in the words the window shows.
    ///
    /// <para>
    /// Distinct from the transcription stage on purpose: this build can have a finished
    /// placeholder transcript and no production models at all, and saying so plainly is the whole
    /// point of the surface.
    /// </para>
    /// </summary>
    public string ProductionStatus { get; private set; } = string.Empty;

    public bool HasProductionStatus => !string.IsNullOrWhiteSpace(ProductionStatus);

    /// <summary>Set once preparation has produced a plan.</summary>
    public string ProductionPlanSummary { get; private set; } = string.Empty;

    // -- what to run, and how ------------------------------------------------------------------

    public ObservableCollection<AsrModelOption> AsrModels { get; } = [];

    private AsrModelOption? _selectedAsrModel;

    /// <summary>Model identity is independent of backend and compute.</summary>
    public AsrModelOption SelectedAsrModel
    {
        get => _selectedAsrModel ??= ChooseInitialModel();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_selectedAsrModel, value))
            {
                return;
            }

            _selectedAsrModel = value;
            _selectedBackend = Backends.FirstOrDefault(option =>
                string.Equals(option.Id, value.Definition.BackendId, StringComparison.Ordinal))
                ?? Backends[0];
            CoerceSelectionsForModel(value.Definition);
            PersistInferenceSettings(modelId: value.Id);
            OnChanged();
            OnChanged(nameof(SelectedBackend));
            OnChanged(nameof(IsProductionSelected));
            OnChanged(nameof(IsPlaceholderBackend));
            OnChanged(nameof(PlaceholderWarning));
            OnChanged(nameof(SupportsGlossary));
            OnChanged(nameof(ModelCapabilitySummary));
            RaiseCommands();
        }
    }

    public bool SupportsGlossary => SelectedAsrModel.Definition.SupportsGlossaryPrompt;

    public string ModelCapabilitySummary
    {
        get
        {
            AsrModelDefinition model = SelectedAsrModel.Definition;
            string maturity = model.Maturity == ModelMaturity.Experimental ? "Experimental" : "Production";
            return $"{model.ShortDescription}  {maturity} · {model.BackendId} · {model.TimestampPrecision}.";
        }
    }

    public ObservableCollection<VadModeOption> VadModes { get; } =
    [
        new(VadMode.Accuracy, "Accuracy", "Whisper receives every sample; no destructive VAD filtering."),
        new(VadMode.Balanced, "Balanced", "Permissive VAD removes only sustained silence."),
        new(VadMode.Fast, "Fast", "More aggressive silence removal for throughput."),
        new(VadMode.Off, "No VAD / diagnostic", "Every planned sample reaches the ASR backend."),
    ];

    private VadModeOption? _selectedVadMode;

    public VadModeOption SelectedVadMode
    {
        get => _selectedVadMode ??= VadModes.First(option => option.Mode == DefaultVadMode(SelectedAsrModel.Id));
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SelectedAsrModel.Definition.SupportedVadModes.Contains(value.Mode))
            {
                return;
            }

            _selectedVadMode = value;
            PersistInferenceSettings(vadMode: Wire(value.Mode));
            OnChanged();
        }
    }

    /// <summary>
    /// The backends this build can offer. The placeholder is always present and always says
    /// what it is; production appears only where its artifacts and runtime exist.
    /// </summary>
    public ObservableCollection<BackendOption> Backends { get; } =
    [
        new(WorkerProtocol.MockBackend, "Deterministic placeholder (no speech recognition)", RecognizesSpeech: false),
        new("faster-whisper", "faster-whisper (real speech recognition)", RecognizesSpeech: true),
        new(AsrBackendIds.Nemo, "NVIDIA NeMo (real speech recognition; isolated runtime required)", RecognizesSpeech: true),
    ];

    private BackendOption? _selectedBackend;

    /// <summary>
    /// Real speech recognition where its models are actually on disk, the placeholder otherwise.
    ///
    /// <para>
    /// Resolved lazily rather than in the constructor, because preparation discovers what is
    /// installed after this view model exists. Once the user picks a backend their choice stands,
    /// including a deliberate choice of the placeholder.
    /// </para>
    /// </summary>
    public BackendOption SelectedBackend
    {
        get => _selectedBackend ??= Backends.FirstOrDefault(option =>
            string.Equals(option.Id, SelectedAsrModel.Definition.BackendId, StringComparison.Ordinal)) ?? Backends[0];
        set
        {
            _selectedBackend = value;
            AsrModelOption? corresponding = AsrModels.FirstOrDefault(model =>
                string.Equals(model.Definition.BackendId, value.Id, StringComparison.Ordinal)
                && (model.Installed || !value.RecognizesSpeech));
            if (corresponding is not null)
            {
                _selectedAsrModel = corresponding;
                CoerceSelectionsForModel(corresponding.Definition);
                PersistInferenceSettings(modelId: corresponding.Id);
                OnChanged(nameof(SelectedAsrModel));
                OnChanged(nameof(SupportsGlossary));
                OnChanged(nameof(ModelCapabilitySummary));
            }
            OnChanged();
            OnChanged(nameof(IsProductionSelected));
            OnChanged(nameof(IsPlaceholderBackend));
            OnChanged(nameof(PlaceholderWarning));
            RaiseCommands();
        }
    }

    /// <summary>
    /// True when a production profile's models are actually on disk and usable.
    ///
    /// <para>
    /// Profiles that declare no artifacts are excluded deliberately. A profile with nothing to
    /// install is trivially "ready", so counting those would report a machine with no speech model
    /// at all as production-ready and default the picker to a backend whose first click fails.
    /// </para>
    /// </summary>
    public bool ProductionInstalled =>
        AsrModels.Any(model => model.Definition.Maturity == ModelMaturity.Production
                               && model.Definition.BackendId != AsrBackendIds.Mock
                               && model.Installed);

    public bool IsProductionSelected => SelectedBackend.RecognizesSpeech;

    public ObservableCollection<ComputeProfileOption> ComputeProfiles { get; } =
    [
        new(ProcessingProfile.CpuInt8, "CPU INT8 — works everywhere, slowest"),
        new(ProcessingProfile.CudaInt8Float16, "GPU INT8/FP16 — lower memory"),
        new(ProcessingProfile.CudaFp16, "GPU FP16 — full precision"),
        new(ComputeProfileIds.CudaBFloat16, "GPU BF16 — NeMo models"),
    ];

    private ComputeProfileOption? _selectedComputeProfile;

    public ComputeProfileOption SelectedComputeProfile
    {
        get => _selectedComputeProfile ??= ResolveInitialComputeProfile();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SelectedAsrModel.Definition.SupportedComputeProfiles.Contains(value.Id, StringComparer.Ordinal))
            {
                return;
            }

            _selectedComputeProfile = value;
            PersistInferenceSettings(computeProfile: value.Id);
            OnChanged();
        }
    }

    /// <summary>
    /// Automatic detection, or a language chosen outright. Automatic is the default because a
    /// wrong forced language is far worse than a detection that occasionally hesitates.
    /// </summary>
    public ObservableCollection<LanguageOption> Languages { get; } =
    [
        new(null, "Detect automatically"),
        new("en", "English"),
        new("de", "German"),
        new("es", "Spanish"),
        new("fr", "French"),
        new("it", "Italian"),
        new("nl", "Dutch"),
        new("pt", "Portuguese"),
        new("ja", "Japanese"),
        new("zh", "Chinese"),
    ];

    private LanguageOption? _selectedLanguage;

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage ??= Languages[0];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SelectedAsrModel.Definition.Languages.Count == 1
                && string.Equals(SelectedAsrModel.Definition.Languages[0], "en", StringComparison.Ordinal)
                && !string.Equals(value.Code, "en", StringComparison.Ordinal))
            {
                return;
            }

            _selectedLanguage = value;
            PersistInferenceSettings(language: value.Code ?? "auto");
            OnChanged();
        }
    }

    private string _glossary = string.Empty;

    /// <summary>
    /// Names, jargon and acronyms, comma separated. Seeded as an initial prompt, which biases
    /// the recogniser rather than guaranteeing anything - and the label says so.
    /// </summary>
    public string Glossary
    {
        get => _glossary;
        set
        {
            _glossary = value ?? string.Empty;
            OnChanged();
            if (_settings is not null)
            {
                AppSettings current = _settings.Load();
                _settings.Save(current with { TranscriptionGlossary = GlossaryTerms(_glossary) });
            }
        }
    }

    /// <summary>
    /// Set when a run did not use the profile it was asked for. Shown prominently: a job that
    /// asked for the GPU and finished on the CPU took far longer for a reason worth knowing.
    /// </summary>
    public string? FallbackNotice { get; private set; }

    public bool HasFallbackNotice => !string.IsNullOrWhiteSpace(FallbackNotice);

    /// <summary>
    /// Pushed by the main view model on every refresh, so this view model never has to observe
    /// the recorder itself and the two can never disagree about whether capture is live.
    /// </summary>
    public void UpdateHost(
        string? sessionId,
        bool sessionSettled,
        bool recordingActive,
        bool hostReady,
        bool shuttingDown)
    {
        bool sessionChanged = !string.Equals(_sessionId, sessionId, StringComparison.Ordinal);

        if (!sessionChanged &&
            _sessionSettled == sessionSettled &&
            _recordingActive == recordingActive &&
            _hostReady == hostReady &&
            _shuttingDown == shuttingDown)
        {
            return;
        }

        _sessionId = sessionId;
        _sessionSettled = sessionSettled;
        _recordingActive = recordingActive;
        _hostReady = hostReady;
        _shuttingDown = shuttingDown;

        if (sessionChanged)
        {
            Error = null;
            Notice = null;
            ProgressPercent = 0;
            ProgressDescription = string.Empty;
        }

        RefreshFromCoordinator();
    }

    // -- commands ------------------------------------------------------------------------------

    /// <summary>
    /// The backend, profile, language and glossary currently chosen.
    ///
    /// <para>
    /// Shared with reprocessing from the library, so "transcribe again" on a stored meeting means
    /// the same thing as pressing the button here. A second set of defaults living somewhere else
    /// would eventually run a meeting through the placeholder because nobody noticed.
    /// </para>
    /// </summary>
    public TranscriptionOptions CurrentOptions() => new()
    {
        Backend = SelectedAsrModel.Definition.BackendId,
        ModelId = SelectedAsrModel.Id,
        ComputeProfile = SelectedComputeProfile.Id,
        Language = SelectedLanguage.Code,
        VadMode = SelectedVadMode.Mode,
        VadFilter = SelectedVadMode.Mode is not (VadMode.Accuracy or VadMode.Off),
        Glossary = SupportsGlossary
            ? GlossaryTerms(Glossary)
            : [],
    };

    private async Task TranscribeAsync()
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }

        Error = null;
        Notice = null;
        FallbackNotice = null;
        OnChanged(nameof(FallbackNotice));
        OnChanged(nameof(HasFallbackNotice));

        TranscriptionOptions options = CurrentOptions();

        // Request verifies every source chunk against its recorded digest, which means hashing
        // the audio. That is not something the UI thread may do.
        TranscriptionTicket ticket = await Task.Run(() => _coordinator.Request(sessionId, options)).ConfigureAwait(true);

        if (!ticket.Accepted)
        {
            Error = ticket.Message;
            RefreshFromCoordinator();
            return;
        }

        Notice = ticket.Message;
        RefreshFromCoordinator();

        TranscriptionRunResult result = await ticket.Completion.ConfigureAwait(true);

        if (result.Succeeded)
        {
            Notice = result.Message;
            Error = null;
        }
        else if (result.State == ProcessingStageState.Cancelled)
        {
            Notice = result.Message;
        }
        else
        {
            Error = result.Message;
            Notice = null;
        }

        ProgressPercent = result.Succeeded ? 100 : 0;
        ProgressDescription = string.Empty;
        RefreshFromCoordinator();
    }

    /// <summary>
    /// Runs every selected model one at a time. Each coordinator call owns one short-lived worker,
    /// and the next call is not made until that process has exited and its revision is durable.
    /// </summary>
    private async Task RunModelComparisonAsync()
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }

        List<AsrModelOption> models =
        [
            .. ComparisonModels
                .Where(choice => choice.IsSelected && choice.IsEnabled)
                .Select(choice => choice.Model)
        ];
        if (models.Count < 2)
        {
            Error = "Select at least two installed, usable transcription models.";
            return;
        }

        Error = null;
        Notice = $"Running 1 of {models.Count}: {models[0].Definition.DisplayName}.";
        List<int> revisions = [];

        for (int index = 0; index < models.Count; index++)
        {
            AsrModelDefinition model = models[index].Definition;
            Notice = $"Running {index + 1} of {models.Count}: {model.DisplayName}. Models are loaded sequentially.";

            string compute = model.SupportedComputeProfiles.Contains(
                    SelectedComputeProfile.Id, StringComparer.Ordinal)
                ? SelectedComputeProfile.Id
                : model.SupportedComputeProfiles.Contains(ProcessingProfile.CudaFp16, StringComparer.Ordinal)
                    ? ProcessingProfile.CudaFp16
                    : model.SupportedComputeProfiles[0];
            VadMode vad = model.SupportedVadModes.Contains(SelectedVadMode.Mode)
                ? SelectedVadMode.Mode
                : model.SupportedVadModes.Contains(VadMode.Accuracy) ? VadMode.Accuracy : model.SupportedVadModes[0];
            string? language = model.Languages.Count == 1 && model.Languages[0] == "en"
                ? "en"
                : SelectedLanguage.Code;

            TranscriptionOptions options = CurrentOptions() with
            {
                Backend = model.BackendId,
                ModelId = model.Id,
                ComputeProfile = compute,
                VadMode = vad,
                VadFilter = vad is not (VadMode.Accuracy or VadMode.Off),
                Language = language,
                Glossary = model.SupportsGlossaryPrompt ? CurrentOptions().Glossary : [],
            };

            TranscriptionTicket ticket = await Task
                .Run(() => _coordinator.Request(sessionId, options))
                .ConfigureAwait(true);
            if (!ticket.Accepted)
            {
                Error = ticket.Message;
                break;
            }

            TranscriptionRunResult result = await ticket.Completion.ConfigureAwait(true);
            if (!result.Succeeded)
            {
                if (result.State == ProcessingStageState.Cancelled)
                {
                    Notice = $"Comparison cancelled after {revisions.Count} completed model runs.";
                }
                else
                {
                    Error = $"{model.DisplayName}: {result.Message}";
                }
                break;
            }

            if (result.Revision is { } revision)
            {
                revisions.Add(revision);
            }
            RefreshFromCoordinator();
        }

        if (revisions.Count == models.Count)
        {
            Notice = $"Comparison runs complete. {revisions.Count} immutable transcript revisions were saved.";
            RebuildRevisions();
            ComparisonLeftRevision = Revisions.FirstOrDefault(option => option.Revision == revisions[^2]);
            ComparisonRightRevision = Revisions.FirstOrDefault(option => option.Revision == revisions[^1]);
        }

        RefreshFromCoordinator();
    }

    private async Task CompareRevisionsAsync()
    {
        if (_sessionId is not { } sessionId
            || ComparisonLeftRevision is not { } left
            || ComparisonRightRevision is not { } right)
        {
            return;
        }

        TranscriptComparisonResult? comparison = await Task.Run(() =>
        {
            TranscriptDocument? leftDocument = _coordinator.ReadTranscript(sessionId, left.Revision);
            TranscriptDocument? rightDocument = _coordinator.ReadTranscript(sessionId, right.Revision);
            return leftDocument is null || rightDocument is null
                ? null
                : TranscriptComparer.Compare(leftDocument, rightDocument);
        }).ConfigureAwait(true);

        if (comparison is null)
        {
            Error = "One of those transcript revisions could not be read.";
            return;
        }

        ShowComparison?.Invoke(comparison);
    }

    private Task CancelAsync()
    {
        Notice = null;
        return Task.Run(_coordinator.Cancel);
    }

    /// <summary>
    /// Prepares a production profile: verify or install the pinned artifacts, build the 16 kHz
    /// derivatives, and plan the windows. It stops before any recogniser runs, and says so.
    /// </summary>
    private async Task PrepareAsync(bool installMissing)
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }

        Error = null;
        IsPreparing = true;
        ProductionStatus = "Checking installed models…";
        OnChanged(nameof(IsPreparing));
        OnChanged(nameof(ProductionStatus));
        OnChanged(nameof(HasProductionStatus));
        RaiseCommands();

        try
        {
            PreparationResult result = await _coordinator
                .PrepareAsync(sessionId, ArtifactProfileFor(SelectedAsrModel.Definition), installMissing)
                .ConfigureAwait(true);

            ProductionStatus = result.Message;

            if (result.IsReady && result.Plan is { } plan)
            {
                ProductionPlanSummary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{plan.Windows.Count} windows · {plan.WindowSeconds:0} s each · {plan.OverlapSeconds:0} s overlap");
            }
            else if (result.Stage is PreparationStage.Failed or PreparationStage.Blocked)
            {
                Error = result.Message;
            }
        }
        finally
        {
            RebuildModels();
            IsPreparing = false;
            OnChanged(nameof(IsPreparing));
            OnChanged(nameof(ProductionStatus));
            OnChanged(nameof(HasProductionStatus));
            OnChanged(nameof(ProductionPlanSummary));
            RefreshFromCoordinator();
        }
    }

    private void OnPreparationProgress(object? sender, PreparationProgressEventArgs e) => Dispatch(() =>
    {
        ProductionStatus = e.Detail;
        ProgressPercent = Math.Round(e.Fraction * 100, 1);
        OnChanged(nameof(ProductionStatus));
        OnChanged(nameof(HasProductionStatus));
    });

    private async Task ExportAsync()
    {
        if (_sessionId is not { } sessionId || _state.Selected is not { } selected)
        {
            return;
        }

        Error = null;

        TranscriptDocument? transcript = await Task
            .Run(() => _coordinator.ReadTranscript(sessionId, selected.Revision))
            .ConfigureAwait(true);

        if (transcript is null)
        {
            Error = "That transcript could not be read. It may have been moved or changed.";
            return;
        }

        TranscriptExportFormat format = SelectedExportFormat.Format;

        // The prompt is the user's choice of destination, so it happens on the UI thread.
        ExportDestination? destination = _exportPrompt.Ask(
            TranscriptExporter.SuggestFileName(transcript, format), format);

        if (destination is null)
        {
            return;
        }

        string canonical = _coordinator.RevisionPath(sessionId, selected.Revision);

        ExportResult result = await Task
            .Run(() => TranscriptExporter.Export(transcript, canonical, format, destination.Path, destination.OverwriteConfirmed))
            .ConfigureAwait(true);

        if (result.Succeeded)
        {
            Notice = $"Exported as {TranscriptExporter.Describe(format)}.";
        }
        else
        {
            Error = result.Message;
        }
    }

    // -- coordinator plumbing --------------------------------------------------------------------

    private void OnCoordinatorStateChanged(object? sender, EventArgs e) => Dispatch(RefreshFromCoordinator);

    private void OnCoordinatorProgress(object? sender, TranscriptionProgressEventArgs e) => Dispatch(() =>
    {
        if (!string.Equals(e.SessionId, _sessionId, StringComparison.Ordinal))
        {
            return;
        }

        ProgressPercent = Math.Round(e.Fraction * 100, 1);
        ProgressDescription = DescribeStage(e);
    });

    private static string DescribeStage(TranscriptionProgressEventArgs e)
    {
        string what = e.Stage switch
        {
            "preparing" => "Preparing",
            "reading_audio" => "Reading audio",
            "transcribing_microphone" => "Transcribing your microphone",
            "transcribing_system" => "Transcribing the meeting audio",
            "merging" => "Merging the timeline",
            "validating" => "Checking the transcript",
            "writing_output" => "Saving the transcript",
            "finished" => "Finished",
            _ => "Working",
        };

        return e.TotalUnits <= 0
            ? what
            : string.Create(CultureInfo.InvariantCulture, $"{what} — chunk {e.CompletedUnits} of {e.TotalUnits}");
    }

    /// <summary>Re-reads the durable state and rebuilds everything the window binds to.</summary>
    private void RefreshFromCoordinator()
    {
        _state = _sessionId is null ? TranscriptionState.Empty : _coordinator.StateFor(_sessionId);

        if (_state.Stage == ProcessingStageState.Failed &&
            _state.CurrentJob?.FailureSummary is { } summary &&
            !IsWorking)
        {
            Error = summary;
        }

        RebuildRevisions();

        TranscriptRevisionRecord? selected = _state.Selected;
        FallbackNotice = selected is not null
                         && !string.IsNullOrWhiteSpace(selected.RequestedComputeProfile)
                         && !string.IsNullOrWhiteSpace(selected.ActualComputeProfile)
                         && !string.Equals(
                             selected.RequestedComputeProfile,
                             selected.ActualComputeProfile,
                             StringComparison.Ordinal)
            ? $"Compute fallback: requested {selected.RequestedComputeProfile}, actually ran {selected.ActualComputeProfile}."
            : null;

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
        List<TranscriptRevisionOption> wanted =
        [
            .. _state.Revisions
                .Where(r => r.FileExists)
                .OrderByDescending(r => r.Revision)
                .Select(r => new TranscriptRevisionOption(
                    r.Revision,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Version {r.Revision} · {r.ModelId} · {r.ActualComputeProfile} · {r.VadMode} · " +
                        $"{r.CreatedUtc.ToLocalTime():MMM d, h:mm tt} · {r.SegmentCount} segments" +
                        $"{(r.RecognizesSpeech ? string.Empty : " · placeholder")}"),
                    r.RecognizesSpeech))
        ];

        if (Revisions.SequenceEqual(wanted))
        {
            return;
        }

        Revisions.Clear();
        foreach (TranscriptRevisionOption option in wanted)
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
        string? selectedId = _selectedAsrModel?.Id;
        HashSet<string> compared =
        [
            .. ComparisonModels
                .Where(choice => choice.IsSelected)
                .Select(choice => choice.Model.Id)
        ];
        List<AsrModelOption> wanted = [];

        foreach (AsrModelDefinition definition in InferenceModelRegistry.AsrModels)
        {
            bool installed = IsModelInstalled(definition);
            bool usable = definition.BackendId switch
            {
                AsrBackendIds.Mock => true,
                AsrBackendIds.FasterWhisper => installed,
                // NeMo remains isolated from faster-whisper. It is usable only when the host has
                // an explicit WSL2 worker launch in addition to verified model artifacts.
                AsrBackendIds.Nemo => installed && _coordinator.SupportsBackend(AsrBackendIds.Nemo),
                _ => false,
            };

            string status = usable
                ? "Installed"
                : installed
                    ? "Installed; isolated runtime unavailable"
                    : definition.BackendId == AsrBackendIds.Mock ? "Available" : "Install";
            wanted.Add(new AsrModelOption(definition, installed, usable, status));
        }

        AsrModels.Clear();
        foreach (AsrModelOption model in wanted)
        {
            AsrModels.Add(model);
        }

        if (selectedId is not null)
        {
            _selectedAsrModel = AsrModels.FirstOrDefault(model => model.Id == selectedId)
                ?? _selectedAsrModel;
        }

        foreach (AsrComparisonChoice choice in ComparisonModels)
        {
            choice.PropertyChanged -= OnComparisonChoiceChanged;
        }

        ComparisonModels.Clear();
        foreach (AsrModelOption model in AsrModels.Where(model => model.Definition.BackendId != AsrBackendIds.Mock))
        {
            AsrComparisonChoice choice = new(model) { IsSelected = compared.Contains(model.Id) };
            choice.PropertyChanged += OnComparisonChoiceChanged;
            ComparisonModels.Add(choice);
        }

        OnChanged(nameof(AsrModels));
        OnChanged(nameof(ComparisonModels));
        OnChanged(nameof(ProductionInstalled));
    }

    private void OnComparisonChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AsrComparisonChoice.IsSelected))
        {
            OnChanged(nameof(CanRunModelComparison));
            RaiseCommands();
        }
    }

    private bool IsModelInstalled(AsrModelDefinition definition)
    {
        if (definition.BackendId == AsrBackendIds.Mock)
        {
            return true;
        }

        if (_coordinator.Preparation is not { Registry: { } registry })
        {
            return false;
        }

        if (registry.Profile(definition.ArtifactProfileId) is { } profile
            && profile.Artifacts.Any(artifact => artifact.Kind == "speech-model"))
        {
            return registry.IsProfileReady(profile);
        }

        // Manifests written before model/compute separation place Turbo in each compute profile.
        return definition.Id == AsrModelIds.WhisperLargeV3Turbo
               && registry.Profiles().Any(profile =>
                   profile.Id is ProcessingProfile.CpuInt8
                       or ProcessingProfile.CudaFp16
                       or ProcessingProfile.CudaInt8Float16
                   && profile.Artifacts.Any(artifact => artifact.Kind == "speech-model")
                   && registry.IsProfileReady(profile));
    }

    private AsrModelOption ChooseInitialModel()
    {
        string? remembered = _settings?.Load().AsrModelId;
        if (!string.IsNullOrWhiteSpace(remembered)
            && AsrModels.FirstOrDefault(model => model.Id == remembered) is { } chosen)
        {
            return chosen;
        }

        if (!string.IsNullOrWhiteSpace(_recommendedModelId)
            && AsrModels.FirstOrDefault(model => model.Id == _recommendedModelId && model.Usable) is { } recommended)
        {
            return recommended;
        }

        return AsrModels.FirstOrDefault(model =>
                   model.Id == AsrModelIds.WhisperLargeV3Turbo && model.Usable)
               ?? AsrModels.First(model => model.Id == AsrModelIds.Mock);
    }

    private void RestoreInferenceSelections()
    {
        _selectedAsrModel = ChooseInitialModel();
        _selectedBackend = Backends.FirstOrDefault(option =>
            option.Id == _selectedAsrModel.Definition.BackendId) ?? Backends[0];
        _selectedComputeProfile = ResolveInitialComputeProfile();

        AppSettings? settings = _settings?.Load();
        VadMode desiredVad = ParseVad(settings?.TranscriptionVadMode)
                             ?? DefaultVadMode(_selectedAsrModel.Id);
        _selectedVadMode = VadModes.First(option =>
            option.Mode == (_selectedAsrModel.Definition.SupportedVadModes.Contains(desiredVad)
                ? desiredVad
                : DefaultVadMode(_selectedAsrModel.Id)));

        string? desiredLanguage = settings?.TranscriptionLanguage;
        if (_selectedAsrModel.Definition.Languages.Count == 1
            && _selectedAsrModel.Definition.Languages[0] == "en")
        {
            desiredLanguage = "en";
        }

        _selectedLanguage = Languages.FirstOrDefault(option => option.Code == desiredLanguage) ?? Languages[0];
        _glossary = string.Join(", ", settings?.TranscriptionGlossary ?? []);
    }

    private ComputeProfileOption ResolveInitialComputeProfile()
    {
        AsrModelDefinition model = _selectedAsrModel?.Definition ?? ChooseInitialModel().Definition;
        string? remembered = _settings?.Load().TranscriptionComputeProfile;
        string? desired = !string.IsNullOrWhiteSpace(remembered) ? remembered : _recommendedComputeProfile;

        return ComputeProfiles.FirstOrDefault(option =>
                   option.Id == desired && model.SupportedComputeProfiles.Contains(option.Id, StringComparer.Ordinal))
               ?? ComputeProfiles.FirstOrDefault(option =>
                   option.Id == ProcessingProfile.CudaFp16
                   && model.SupportedComputeProfiles.Contains(option.Id, StringComparer.Ordinal)
                   && string.Equals(_recommendedComputeProfile, ProcessingProfile.CudaFp16, StringComparison.Ordinal))
               ?? ComputeProfiles.First(option => model.SupportedComputeProfiles.Contains(option.Id, StringComparer.Ordinal));
    }

    private void CoerceSelectionsForModel(AsrModelDefinition model)
    {
        if (_selectedComputeProfile is null
            || !model.SupportedComputeProfiles.Contains(_selectedComputeProfile.Id, StringComparer.Ordinal))
        {
            _selectedComputeProfile = ResolveInitialComputeProfile();
            OnChanged(nameof(SelectedComputeProfile));
        }

        if (_selectedVadMode is null || !model.SupportedVadModes.Contains(_selectedVadMode.Mode))
        {
            _selectedVadMode = VadModes.First(option => option.Mode == DefaultVadMode(model.Id));
            OnChanged(nameof(SelectedVadMode));
        }

        if (model.Languages.Count == 1 && model.Languages[0] == "en")
        {
            _selectedLanguage = Languages.First(option => option.Code == "en");
            OnChanged(nameof(SelectedLanguage));
        }
    }

    private string ArtifactProfileFor(AsrModelDefinition model) =>
        _coordinator.Preparation?.Registry.Profile(model.ArtifactProfileId) is not null
            ? model.ArtifactProfileId
            : model.Id == AsrModelIds.WhisperLargeV3Turbo
                ? SelectedComputeProfile.Id
                : model.ArtifactProfileId;

    private void PersistInferenceSettings(
        string? modelId = null,
        string? computeProfile = null,
        string? vadMode = null,
        string? language = null)
    {
        if (_settings is null)
        {
            return;
        }

        AppSettings current = _settings.Load();
        _settings.Save(current with
        {
            AsrModelId = modelId ?? current.AsrModelId,
            TranscriptionComputeProfile = computeProfile ?? current.TranscriptionComputeProfile,
            TranscriptionVadMode = vadMode ?? current.TranscriptionVadMode,
            TranscriptionLanguage = language == "auto"
                ? null
                : language ?? current.TranscriptionLanguage,
        });
    }

    private static VadMode DefaultVadMode(string modelId) => modelId switch
    {
        AsrModelIds.Mock => VadMode.Off,
        AsrModelIds.WhisperLargeV3Turbo => VadMode.Balanced,
        _ => VadMode.Accuracy,
    };

    private static VadMode? ParseVad(string? value) => value?.ToLowerInvariant() switch
    {
        "accuracy" => VadMode.Accuracy,
        "balanced" => VadMode.Balanced,
        "fast" => VadMode.Fast,
        "off" => VadMode.Off,
        _ => null,
    };

    private static string Wire(VadMode mode) => mode.ToString().ToLowerInvariant();

    private static IReadOnlyList<string> GlossaryTerms(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static readonly string[] RefreshedProperties =
    [
        nameof(Stage), nameof(StageText), nameof(IsWorking), nameof(HasTranscript), nameof(TranscriptSummary),
        nameof(IsPlaceholderBackend), nameof(PlaceholderWarning), nameof(BackendSummary),
        nameof(HasSession), nameof(SelectedRevision),
        nameof(CanTranscribe), nameof(CanTranscribeAgain), nameof(CanCancel), nameof(CanExport),
        nameof(CanPrepare), nameof(SupportsProduction), nameof(ProductionInstalled), nameof(IsPreparing),
        nameof(ProductionStatus), nameof(HasProductionStatus), nameof(ProductionPlanSummary),
        nameof(FallbackNotice), nameof(HasFallbackNotice), nameof(IsProductionSelected),
        nameof(SelectedAsrModel), nameof(AsrModels), nameof(SelectedVadMode), nameof(SupportsGlossary),
        nameof(ModelCapabilitySummary), nameof(SelectedComputeProfile), nameof(SelectedLanguage),
        nameof(ComparisonLeftRevision), nameof(ComparisonRightRevision),
        nameof(CanRunModelComparison), nameof(CanCompareRevisions),
    ];

    private void RaiseCommands()
    {
        TranscribeCommand.RaiseCanExecuteChanged();
        TranscribeAgainCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        RunModelComparisonCommand.RaiseCanExecuteChanged();
        CompareRevisionsCommand.RaiseCanExecuteChanged();
        PrepareProductionCommand.RaiseCanExecuteChanged();
        InstallModelsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Marshals onto the dispatcher when the coordinator raises from a worker thread.</summary>
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
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _coordinator.ProgressChanged -= OnCoordinatorProgress;
        _coordinator.PreparationProgress -= OnPreparationProgress;
        foreach (AsrComparisonChoice choice in ComparisonModels)
        {
            choice.PropertyChanged -= OnComparisonChoiceChanged;
        }
    }
}
