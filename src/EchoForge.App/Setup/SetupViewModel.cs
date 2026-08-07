using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Setup;
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
    private string? _status;
    private string? _progressText;
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

    public ObservableCollection<string> HardwareFacts { get; } = [];

    public ObservableCollection<string> RecommendationReasons { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand InstallRecommendedCommand { get; }

    public AsyncRelayCommand InstallSelectedCommand { get; }

    public AsyncRelayCommand RepairSelectedCommand { get; }

    public RelayCommand CancelCommand { get; }

    public ComponentRow? SelectedComponent
    {
        get => _selected;
        set { _selected = value; Changed(); RaiseCommands(); }
    }

    public string RecommendationSummary => _recommendation is null
        ? "Checking this machine…"
        : _recommendation.Transcription.Summary + "  " + _recommendation.Summarization.Summary;

    public string TranscriptionProfileId => _recommendation?.Transcription.ProfileId ?? ProcessingProfile.Mock;

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

        _snapshot = await Task.Run(() => _services.Runtimes.Snapshot(transcription, summary)).ConfigureAwait(true);

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

        _selected = keep is null
            ? null
            : Components.FirstOrDefault(c => string.Equals(c.Id.ToString(), keep, StringComparison.Ordinal));

        foreach (string name in (string[])
        [
            nameof(SelectedComponent), nameof(OutstandingSummary), nameof(CanRecordNow),
        ])
        {
            Changed(name);
        }

        RaiseCommands();
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
            nameof(RecommendationSummary), nameof(TranscriptionProfileId), nameof(SummaryProfileId),
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
            foreach (RuntimeComponentId id in required)
            {
                token.ThrowIfCancellationRequested();

                await _services.Runtimes
                    .InstallAsync(id, TranscriptionProfileId, SummaryProfileId, progress, token)
                    .ConfigureAwait(false);
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
            .InstallAsync(row.Id, TranscriptionProfileId, SummaryProfileId, progress, token)).ConfigureAwait(true);
    }

    private async Task RepairAsync(ComponentRow? row)
    {
        if (row is null)
        {
            return;
        }

        Status = "Repairing " + row.Name + ". Anything already downloaded is checked before anything is fetched.";

        await RunAsync((token, progress) => _services.Runtimes
            .RepairAsync(row.Id, TranscriptionProfileId, SummaryProfileId, progress, token)).ConfigureAwait(true);
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
            Progress = 0;
            IsBusy = false;

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
