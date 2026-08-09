using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Inference;
using EchoForge.Contracts.Setup;
using EchoForge.Core.Inference;
using EchoForge.Core.Setup;
using EchoForge.Infrastructure.Setup;

namespace EchoForge.App.Setup;

/// <summary>One installable component, as the setup screen shows it.</summary>
public sealed record ComponentRow(RuntimeComponentState State)
{
    public RuntimeComponentId Id => State.Id;

    public string Name => State.Id switch
    {
        RuntimeComponentId.PythonRuntime => "Python runtime",
        RuntimeComponentId.WorkerEnvironment => "Speech-recognition packages",
        RuntimeComponentId.SpeechModel => "Speech model",
        RuntimeComponentId.SummaryRuntime => "Summary runtime",
        RuntimeComponentId.SummaryModel => "Summary model",
        RuntimeComponentId.BenchmarkModel => "Comparison model (optional)",
        _ => State.Id.ToString(),
    };

    public string Status => State.Status switch
    {
        RuntimeComponentStatus.NotInstalled => "Not installed",
        RuntimeComponentStatus.Downloading => "Downloading",
        RuntimeComponentStatus.Verifying => "Verifying",
        RuntimeComponentStatus.Installing => "Installing",
        RuntimeComponentStatus.Ready => "Ready",
        RuntimeComponentStatus.Corrupt => "Needs repair",
        RuntimeComponentStatus.Incompatible => "Not supported here",
        RuntimeComponentStatus.NotNeeded => "Not needed",
        _ => State.Status.ToString(),
    };

    public string Detail => State.Detail;

    public bool IsReady => State.IsReady;

    public bool CanInstall => State.NeedsAction || State.Status == RuntimeComponentStatus.NotNeeded;

    public bool CanRepair => State.Status is RuntimeComponentStatus.Corrupt or RuntimeComponentStatus.Ready
        or RuntimeComponentStatus.Installing;

    public string Size => State.BytesRequired > 0 ? RuntimeRegistry.Describe(State.BytesRequired) : string.Empty;
}

/// <summary>One capability, and whether it is available yet.</summary>
public sealed record CapabilityRow(CapabilityState State)
{
    public string Name => State.Level switch
    {
        CapabilityLevel.Recording => "Record and review meetings",
        CapabilityLevel.Transcription => "Turn speech into text",
        CapabilityLevel.Summarization => "Summarise locally",
        CapabilityLevel.Benchmarking => "Compare summary models",
        _ => State.Level.ToString(),
    };

    public bool Available => State.Available;

    public string Detail => State.Detail;

    public bool IsOptional => State.IsOptional;
}

/// <summary>
/// One model, and what it would take to be able to use it.
///
/// <para>
/// <b>Readiness here is a capability, never a file listing.</b> The row that said "Installed" for
/// a model whose runtime could not load it is the specific failure this record exists to make
/// impossible: <see cref="Status"/> is derived from <see cref="Readiness"/>, and nothing reaches
/// <see cref="ModelReadinessState.Ready"/> without the model having produced real output on this
/// machine.
/// </para>
/// </summary>
public sealed record ModelManagementRow(
    string Category,
    string Id,
    string Name,
    string ProfileId,
    string Detail,
    ModelReadinessState Readiness,
    bool FilesPresent,
    bool Experimental,
    bool Recommended,
    long BytesRequired,
    string Qualification)
{
    public string Status => Readiness switch
    {
        ModelReadinessState.Ready => Recommended ? "Ready · recommended" : "Ready",
        ModelReadinessState.InstallingRuntime => "Installing runtime…",
        ModelReadinessState.InstallingDependencies => "Installing dependencies…",
        ModelReadinessState.DownloadingModel => "Downloading…",
        ModelReadinessState.Verifying => "Verifying…",
        ModelReadinessState.Testing => "Testing…",
        ModelReadinessState.RestartRequired => "Restart required",
        ModelReadinessState.Failed => "Failed",
        ModelReadinessState.RepairAvailable => "Downloaded · not usable yet",
        _ => Recommended ? "Not installed · recommended" : "Not installed",
    };

    public bool IsReady => Readiness == ModelReadinessState.Ready;

    public string Size => BytesRequired > 0 ? RuntimeRegistry.Describe(BytesRequired) : string.Empty;

    public bool CanInstall => Readiness is not (ModelReadinessState.Ready
        or ModelReadinessState.InstallingRuntime
        or ModelReadinessState.InstallingDependencies
        or ModelReadinessState.DownloadingModel
        or ModelReadinessState.Verifying
        or ModelReadinessState.Testing);

    /// <summary>Repair is for something that was installed and stopped qualifying.</summary>
    public bool CanRepair => FilesPresent &&
        Readiness is ModelReadinessState.RepairAvailable or ModelReadinessState.Failed or ModelReadinessState.Ready;
}

/// <summary>
/// First run, and every run after it that needs something installed.
///
/// <para>
/// <b>Nothing here is all-or-nothing.</b> Recording works with nothing downloaded, and the screen
/// says so first: refusing to let somebody record a meeting because a seven-gigabyte summariser has
/// not finished is absurd, and the meeting is the only thing here that cannot be downloaded again.
/// The capability list is therefore the headline and the component list is the detail.
/// </para>
///
/// <para>
/// The recommendation is always shown with its reasons. A recommendation a user cannot interrogate
/// is one they either accept blindly or ignore, and "EchoForge could not tell how much memory your
/// GPU has, so it chose the safe option" is a sentence that changes what a reasonable person does
/// next.
/// </para>
/// </summary>
public sealed class SetupViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SetupServices _services;
    private readonly IAudioDeviceCatalog? _audio;
    private readonly OperationGate _gate = new();

    private CancellationTokenSource? _work;
    private HardwareSnapshot _hardware = HardwareSnapshot.Unknown;
    private SetupRecommendation? _recommendation;
    private SetupSnapshot? _snapshot;
    private ComponentRow? _selected;
    private ModelManagementRow? _selectedModel;
    private string? _status;
    private string? _progressText;
    private string? _modelProgressText;
    private double _progress;
    private bool _busy;
    private bool _disposed;

    public SetupViewModel(SetupServices services, IAudioDeviceCatalog? audio = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _audio = audio;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, _gate, () => !_busy, m => Status = m);

        InstallRecommendedCommand = new AsyncRelayCommand(
            InstallRecommendedAsync, _gate, () => !_busy && _recommendation is not null, m => Status = m);

        InstallSelectedCommand = new AsyncRelayCommand(
            () => InstallAsync(_selected), _gate, () => !_busy && _selected is { CanInstall: true }, m => Status = m);

        RepairSelectedCommand = new AsyncRelayCommand(
            () => RepairAsync(_selected), _gate, () => !_busy && _selected is { CanRepair: true }, m => Status = m);

        InstallSelectedModelCommand = new AsyncRelayCommand(
            InstallSelectedModelAsync, _gate, () => !_busy && _selectedModel is { CanInstall: true }, m => Status = m);

        RepairSelectedModelCommand = new AsyncRelayCommand(
            RepairSelectedModelAsync, _gate, () => !_busy && _selectedModel is { CanRepair: true }, m => Status = m);

        FixProcessingCommand = new AsyncRelayCommand(
            FixProcessingAsync, _gate, () => !_busy && !ProcessingAvailable, m => Status = m);

        CancelCommand = new RelayCommand(() => _work?.Cancel(), () => _busy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after an install or repair has settled and the snapshot has been re-read.
    ///
    /// <para>
    /// This is how the composition root learns that a worker runtime it could not find at startup
    /// may now be present, so it can attach transcription and summarisation without the user having
    /// to close and reopen the application. It carries no payload: the listener re-evaluates the
    /// installed runtime itself rather than trusting a claim made here.
    /// </para>
    /// </summary>
    public event EventHandler? ComponentsChanged;

    public ObservableCollection<ComponentRow> Components { get; } = [];

    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];

    public ObservableCollection<ModelManagementRow> Models { get; } = [];

    public ObservableCollection<string> HardwareFacts { get; } = [];

    public ObservableCollection<string> RecommendationReasons { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand InstallRecommendedCommand { get; }

    public AsyncRelayCommand InstallSelectedCommand { get; }

    public AsyncRelayCommand RepairSelectedCommand { get; }

    public AsyncRelayCommand InstallSelectedModelCommand { get; }

    public AsyncRelayCommand RepairSelectedModelCommand { get; }

    /// <summary>
    /// What provisioning is doing, in words.
    ///
    /// <para>
    /// Separate from the byte counter above it. Building a Linux runtime and installing several
    /// gigabytes of PyTorch takes minutes during which no artifact is downloading at all, and a
    /// progress bar with nothing to say is how a working install looks like a hung one.
    /// </para>
    /// </summary>
    public string? ModelProgressText
    {
        get => _modelProgressText;
        private set { _modelProgressText = value; Changed(); Changed(nameof(HasModelProgress)); }
    }

    public bool HasModelProgress => !string.IsNullOrWhiteSpace(ModelProgressText);

    public RelayCommand CancelCommand { get; }

    public ComponentRow? SelectedComponent
    {
        get => _selected;
        set { _selected = value; Changed(); RaiseCommands(); }
    }

    public ModelManagementRow? SelectedModel
    {
        get => _selectedModel;
        set { _selectedModel = value; Changed(); RaiseCommands(); }
    }

    /// <summary>
    /// Whether a recording can be turned into anything at all right now.
    ///
    /// <para>
    /// Read from the components rather than from whether a view model happened to attach, because
    /// this has to be answerable at exactly the moment nothing attached.
    /// </para>
    /// </summary>
    public bool ProcessingAvailable =>
        _snapshot is { } snapshot && (snapshot.CanTranscribe || snapshot.CanSummarize);

    /// <summary>
    /// The first thing standing in the way, in the words the component itself used.
    ///
    /// <para>
    /// This exists because of a real installation that could record and could do nothing else, and
    /// said so nowhere a person would look: the pages that would have explained it are the ones
    /// that do not render until processing works. A screen that is empty precisely when something
    /// is wrong is worse than no screen.
    /// </para>
    /// </summary>
    public string ProcessingProblem
    {
        get
        {
            if (_snapshot is not { } snapshot)
            {
                return "EchoForge is still checking what this machine can do.";
            }

            ComponentRow? blocking = Components.FirstOrDefault(c => !c.IsReady && c.CanInstall || !c.IsReady && c.CanRepair);
            return blocking is null
                ? "Transcription and meeting briefs are not available yet."
                : blocking.Name + ": " + blocking.Detail;
        }
    }

    public bool HasProcessingProblem => !ProcessingAvailable;

    /// <summary>
    /// Repairs whatever is actually blocking processing, without making the user find it.
    ///
    /// <para>
    /// It repairs rather than reinstalls where it can, so nothing already downloaded is fetched
    /// again — the usual cause is an environment whose record of itself has fallen behind the
    /// packages it is supposed to contain, and rebuilding that is quick.
    /// </para>
    /// </summary>
    public AsyncRelayCommand FixProcessingCommand { get; }

    private async Task FixProcessingAsync()
    {
        ComponentRow? blocking = Components.FirstOrDefault(c => !c.IsReady && (c.CanRepair || c.CanInstall));
        if (blocking is null)
        {
            Status = "Nothing needs repairing; EchoForge could not tell what is missing.";
            return;
        }

        SelectedComponent = blocking;
        await (blocking.CanRepair ? RepairAsync(blocking) : InstallAsync(blocking)).ConfigureAwait(true);
    }

    public string RecommendationSummary => _recommendation is null
        ? "Checking this machine…"
        : _recommendation.Transcription.Summary + "  " + _recommendation.Summarization.Summary;

    public string TranscriptionProfileId => _recommendation?.Transcription.ProfileId ?? ProcessingProfile.Mock;

    public string AsrModelProfileId => _recommendation?.Asr.ArtifactProfileId ?? ProcessingProfile.Mock;

    public string SummaryProfileId => _recommendation?.Summarization.ProfileId ?? ProcessingProfile.Mock;

    /// <summary>
    /// What still has to be downloaded for the recommended setup, excluding anything optional.
    /// </summary>
    public string OutstandingSummary => _snapshot is null
        ? string.Empty
        : _snapshot.BytesOutstanding <= 0
            ? "Everything the recommended setup needs is installed."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{RuntimeRegistry.Describe(_snapshot.BytesOutstanding)} still to download.");

    public bool CanRecordNow => _snapshot?.Capability(CapabilityLevel.Recording).Available ?? true;

    public string? Status
    {
        get => _status;
        private set { _status = value; Changed(); Changed(nameof(HasStatus)); }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public string? ProgressText
    {
        get => _progressText;
        private set { _progressText = value; Changed(); Changed(nameof(HasProgress)); }
    }

    public bool HasProgress => !string.IsNullOrWhiteSpace(ProgressText);

    public double Progress
    {
        get => _progress;
        private set { _progress = value; Changed(); }
    }

    public bool IsBusy
    {
        get => _busy;
        private set { _busy = value; Changed(); RaiseCommands(); }
    }

    // -- reading the machine ----------------------------------------------------------------------

    /// <summary>
    /// Reads the machine and what is installed.
    ///
    /// <para>
    /// Off the UI thread, and it never throws: a probe that fails leaves the fields it could not
    /// fill explicitly unknown, and a machine with an unusual driver must not be able to stop
    /// somebody recording a meeting.
    /// </para>
    /// </summary>
    public async Task RefreshAsync()
    {
        IsBusy = true;

        try
        {
            _hardware = await Task.Run(() => _services.HardwareProbe(_audio).ProbeAsync()).ConfigureAwait(true);
            _recommendation = ProfileRecommender.Recommend(_hardware);

            BuildHardwareFacts();
            BuildRecommendation();
            await ReloadSnapshotAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadSnapshotAsync()
    {
        string transcription = TranscriptionProfileId;
        string summary = SummaryProfileId;

        _snapshot = await Task.Run(() => _services.Runtimes.Snapshot(
            transcription,
            summary,
            AsrModelProfileId)).ConfigureAwait(true);

        string? keep = _selected?.Id.ToString();

        Components.Clear();
        foreach (RuntimeComponentState component in _snapshot.Components)
        {
            Components.Add(new ComponentRow(component));
        }

        Capabilities.Clear();
        foreach (CapabilityState capability in _snapshot.Capabilities)
        {
            Capabilities.Add(new CapabilityRow(capability));
        }

        BuildModelRows();

        _selected = keep is null
            ? null
            : Components.FirstOrDefault(c => string.Equals(c.Id.ToString(), keep, StringComparison.Ordinal));

        foreach (string name in (string[])
        [
            nameof(SelectedComponent), nameof(OutstandingSummary), nameof(CanRecordNow),
            nameof(SelectedModel),
            nameof(ProcessingAvailable),
            nameof(HasProcessingProblem),
            nameof(ProcessingProblem),
        ])
        {
            Changed(name);
        }

        RaiseCommands();
    }

    private void BuildModelRows()
    {
        string? keep = _selectedModel?.Id;
        Models.Clear();

        foreach (AsrModelDefinition model in InferenceModelRegistry.AsrModels
                     .Where(model => model.BackendId != AsrBackendIds.Mock))
        {
            ProcessingProfile? profile = _services.Artifacts.Profile(model.ArtifactProfileId);
            bool filesPresent = profile is not null && _services.Artifacts.IsProfileReady(profile);

            // Never "installed because the files are there", and never "not usable" just because
            // this build has no qualification record for a model that has been working for weeks.
            // Whisper runs in the worker environment, whose readiness the components list already
            // establishes; the NVIDIA models need a Linux runtime that may not exist at all, so
            // for those nothing short of having run the model counts.
            bool nemo = model.BackendId == AsrBackendIds.Nemo;
            bool runtimeReady = nemo
                ? _services.TryResolveNemoWorkerLaunch() is not null
                : _services.WorkerEnvironment.TryResolve() is not null;

            ModelReadinessState readiness = _services.Provisioning.StateOf(
                model.Id, model.ArtifactProfileId, model.ModelRevision,
                runtimeReady, requiresQualification: nemo);

            Models.Add(new ModelManagementRow(
                "SPEECH",
                model.Id,
                model.DisplayName,
                model.ArtifactProfileId,
                model.ShortDescription,
                readiness,
                filesPresent,
                model.Maturity == ModelMaturity.Experimental,
                string.Equals(model.Id, _recommendation?.Asr.ModelId, StringComparison.Ordinal),
                profile?.TotalBytes ?? 0,
                _services.Provisioning.QualificationOf(model.Id, model.ModelRevision)?.Detail ?? string.Empty));
        }

        foreach (SummaryModelDefinition model in InferenceModelRegistry.SummaryModels)
        {
            ProcessingProfile? profile = _services.Artifacts.Profile(model.ArtifactProfileId);
            bool filesPresent = _services.Llama.TryResolve(model.ArtifactProfileId) is not null;
            ModelReadinessState readiness = _services.Provisioning.StateOf(
                model.Id, model.ArtifactProfileId, model.ModelRevision,
                runtimeReady: filesPresent, requiresQualification: false);

            // A summary model this build never selects on its own is a comparison model, and
            // saying so is the difference between "why is there a third summariser?" and a row
            // that explains itself.
            Models.Add(new ModelManagementRow(
                model.Maturity == ModelMaturity.Production ? "SUMMARY" : "COMPARE",
                model.Id,
                model.DisplayName,
                model.ArtifactProfileId,
                model.ShortDescription,
                readiness,
                filesPresent,
                model.Maturity == ModelMaturity.Experimental,
                false,
                profile?.TotalBytes ?? 0,
                _services.Provisioning.QualificationOf(model.Id, model.ModelRevision)?.Detail ?? string.Empty));
        }

        _selectedModel = keep is null ? null : Models.FirstOrDefault(model => model.Id == keep);
        Changed(nameof(Models));
    }

    private void BuildHardwareFacts()
    {
        HardwareFacts.Clear();

        HardwareFacts.Add(_hardware.OperatingSystem + "  ·  " + _hardware.Architecture);
        HardwareFacts.Add((_hardware.CpuName ?? "Processor unknown") +
            string.Create(CultureInfo.CurrentCulture, $"  ·  {_hardware.LogicalCores} logical cores"));

        HardwareFacts.Add(_hardware.TotalMemoryBytes is { } memory
            ? RuntimeRegistry.Describe(memory) + " memory"
            : "Memory unknown");

        HardwareFacts.Add(_hardware.AvailableDiskBytes is { } disk
            ? RuntimeRegistry.Describe(disk) + " free on " + (_hardware.DataVolume ?? "this drive")
            : "Free disk space unknown");

        foreach (GpuInfo gpu in _hardware.Gpus.Where(g => !g.IsSoftware))
        {
            HardwareFacts.Add(
                gpu.Vendor + " " + gpu.Model +
                (gpu.DedicatedMemoryBytes is { } vram ? "  ·  " + RuntimeRegistry.Describe(vram) : "  ·  memory unknown") +
                (gpu.DriverVersion is { } driver ? "  ·  driver " + driver : string.Empty));
        }

        HardwareFacts.Add(_hardware.Cuda switch
        {
            CudaAvailability.Available => "CUDA is available to the speech recogniser.",
            CudaAvailability.NoNvidiaAdapter => "No NVIDIA adapter, so the GPU profiles do not apply.",
            CudaAvailability.AdapterWithoutRuntime => "CUDA has not been confirmed yet.",
            _ => "CUDA support is unknown.",
        });

        HardwareFacts.Add(string.Create(
            CultureInfo.CurrentCulture,
            $"{_hardware.InputDevices.Count} microphone(s), {_hardware.OutputDevices.Count} playback device(s)"));
    }

    private void BuildRecommendation()
    {
        RecommendationReasons.Clear();
        Warnings.Clear();

        if (_recommendation is not { } recommendation)
        {
            return;
        }

        foreach (string reason in recommendation.Transcription.Reasons)
        {
            RecommendationReasons.Add(reason);
        }

        foreach (string reason in recommendation.Summarization.Reasons)
        {
            RecommendationReasons.Add(reason);
        }

        foreach (string warning in recommendation.Warnings)
        {
            Warnings.Add(warning);
        }

        foreach (string name in (string[])
        [
            nameof(RecommendationSummary), nameof(TranscriptionProfileId), nameof(AsrModelProfileId),
            nameof(SummaryProfileId),
        ])
        {
            Changed(name);
        }
    }

    // -- installing --------------------------------------------------------------------------------

    /// <summary>
    /// Installs everything the recommended setup needs, and nothing optional.
    ///
    /// <para>
    /// The comparison model is deliberately excluded. It exists to be measured against the
    /// default, and downloading eight gigabytes of it on first run would be recommending a
    /// benchmark as a product.
    /// </para>
    /// </summary>
    private async Task InstallRecommendedAsync()
    {
        RuntimeComponentId[] required =
        [
            RuntimeComponentId.PythonRuntime,
            RuntimeComponentId.WorkerEnvironment,
            RuntimeComponentId.SpeechModel,
            RuntimeComponentId.SummaryRuntime,
            RuntimeComponentId.SummaryModel,
        ];

        await RunAsync(async (token, progress) =>
        {
            string transcriptionProfile = TranscriptionProfileId;
            string summaryProfile = SummaryProfileId;
            string asrModelProfile = AsrModelProfileId;

            foreach (RuntimeComponentId id in required)
            {
                token.ThrowIfCancellationRequested();

                await _services.Runtimes
                    .InstallAsync(
                        id,
                        transcriptionProfile,
                        summaryProfile,
                        asrModelProfile,
                        progress,
                        token)
                    .ConfigureAwait(false);

                // On a new installation the first probe cannot ask CTranslate2 yet, so it makes a
                // conservative CPU recommendation. Once the verified worker and private CUDA
                // runtime exist, ask again before choosing which multi-gigabyte model to fetch.
                // This is a capability check, not GPU-brand inference: a driver/runtime mismatch
                // remains CPU and the setup never claims FP16 merely because an adapter exists.
                if (id == RuntimeComponentId.WorkerEnvironment)
                {
                    HardwareSnapshot refreshed = await _services.HardwareProbe(_audio)
                        .ProbeAsync(token)
                        .ConfigureAwait(false);
                    SetupRecommendation recommendation = ProfileRecommender.Recommend(refreshed);

                    _hardware = refreshed;
                    _recommendation = recommendation;
                    transcriptionProfile = recommendation.Transcription.ProfileId;
                    summaryProfile = recommendation.Summarization.ProfileId;
                    asrModelProfile = recommendation.Asr.ArtifactProfileId;
                }
            }
        }).ConfigureAwait(true);
    }

    private async Task InstallAsync(ComponentRow? row)
    {
        if (row is null)
        {
            return;
        }

        await RunAsync((token, progress) => _services.Runtimes
            .InstallAsync(
                row.Id,
                TranscriptionProfileId,
                SummaryProfileId,
                AsrModelProfileId,
                progress,
                token)).ConfigureAwait(true);
    }

    private async Task RepairAsync(ComponentRow? row)
    {
        if (row is null)
        {
            return;
        }

        Status = "Repairing " + row.Name + ". Anything already downloaded is checked before anything is fetched.";

        await RunAsync((token, progress) => _services.Runtimes
            .RepairAsync(
                row.Id,
                TranscriptionProfileId,
                SummaryProfileId,
                AsrModelProfileId,
                progress,
                token)).ConfigureAwait(true);
    }

    /// <summary>
    /// Install, which now means install <em>and prove</em>.
    ///
    /// <para>
    /// The provisioner does whatever this particular model needs - for the NVIDIA ones that is an
    /// entire isolated Linux runtime before a single byte of the checkpoint matters - and finishes
    /// by running it. What comes back is either a qualification or a reason, and the reason is
    /// what the row then says.
    /// </para>
    /// </summary>
    private async Task InstallSelectedModelAsync()
    {
        if (_selectedModel is not { } model)
        {
            return;
        }

        await RunAsync(async (token, bytes) =>
        {
            Progress<ProvisionProgress> steps = new(update => Dispatch(() => ModelProgressText = update.Detail));

            ModelQualification result = await _services.Provisioning
                .InstallAsync(model.Id, steps, bytes, token)
                .ConfigureAwait(false);

            Dispatch(() => Status = result.IsReady
                ? model.Name + " is ready. " + result.Detail
                : model.Name + " is not ready. " + result.Detail);
        }).ConfigureAwait(true);
    }

    /// <summary>Repair forgets the old verdict and asks the question again, from wherever it can.</summary>
    private async Task RepairSelectedModelAsync()
    {
        if (_selectedModel is not { } model)
        {
            return;
        }

        _services.Provisioning.Forget(model.Id);
        await InstallSelectedModelAsync().ConfigureAwait(true);
    }

    /// <summary>Runs one installation step with progress, cancellation, and a refresh afterwards.</summary>
    private async Task RunAsync(Func<CancellationToken, IProgress<ArtifactProgressEventArgs>, Task> work)
    {
        CancellationTokenSource cancellation = new();
        _work = cancellation;

        IsBusy = true;
        Progress = 0;

        Progress<ArtifactProgressEventArgs> progress = new(p => Dispatch(() =>
        {
            Progress = p.Fraction;
            ProgressText = string.Create(
                CultureInfo.CurrentCulture,
                $"{Describe(p.Status)} — {RuntimeRegistry.Describe(p.BytesCompleted)} of {RuntimeRegistry.Describe(p.TotalBytes)}");
        }));

        try
        {
            await Task.Run(() => work(cancellation.Token, progress), cancellation.Token).ConfigureAwait(true);
            Status = null;
        }
        catch (OperationCanceledException)
        {
            // Cancelling costs the time already spent, never the bytes already fetched: a partly
            // downloaded artifact resumes from where it stopped.
            Status = "Stopped. Anything already downloaded was kept, and it will continue from there.";
        }
        finally
        {
            _work = null;
            cancellation.Dispose();

            ProgressText = null;
            ModelProgressText = null;
            Progress = 0;
            IsBusy = false;

            // Install-recommended may have been able to perform the definitive CUDA probe only
            // after constructing the worker. Reflect that second, effective recommendation before
            // rebuilding component/model rows.
            BuildHardwareFacts();
            BuildRecommendation();
            await ReloadSnapshotAsync().ConfigureAwait(true);

            // An install or repair may have put a worker runtime on disk that was not there at
            // startup. Tell whoever is listening so processing can attach without a restart; the
            // listener re-checks the runtime itself rather than trusting anything asserted here.
            ComponentsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string Describe(ArtifactStatus status) => status switch
    {
        ArtifactStatus.Downloading => "Downloading",
        ArtifactStatus.Verifying => "Checking",
        ArtifactStatus.Installed => "Installed",
        _ => status.ToString(),
    };

    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        InstallRecommendedCommand.RaiseCanExecuteChanged();
        InstallSelectedCommand.RaiseCanExecuteChanged();
        RepairSelectedCommand.RaiseCanExecuteChanged();
        InstallSelectedModelCommand.RaiseCanExecuteChanged();
        RepairSelectedModelCommand.RaiseCanExecuteChanged();
        FixProcessingCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
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
        Dispatch(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _work?.Cancel();
    }
}
