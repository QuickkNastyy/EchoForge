using System.IO;
using System.Windows;
using EchoForge.Audio.Windows;
using EchoForge.Contracts.Recording;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Contracts.Settings;
using EchoForge.Core.Exports;
using EchoForge.Core.Recording;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Settings;
using EchoForge.Infrastructure.Summaries;
using EchoForge.Infrastructure.Storage;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.App;

/// <summary>
/// The composition root. Everything is constructed here once and passed down; there is no service
/// locator and no global mutable state.
/// </summary>
public partial class App : System.Windows.Application, IDisposable
{
    private const string InstanceMutexName = @"Local\EchoForge.SingleInstance";

    private Mutex? _instanceMutex;
    private AudioDeviceCatalog? _catalog;
    private MmDeviceEndpointMonitor? _endpoints;
    private SystemPowerMonitor? _power;
    private RecordingController? _controller;
    private MainViewModel? _viewModel;
    private TrayIndicator? _tray;
    private ShutdownCoordinator? _shutdown;
    private TranscriptionCoordinator? _coordinator;
    private FileTranscriptionStore? _transcripts;
    private ArtifactRegistry? _registry;
    private FileSummaryStore? _summaries;
    private SummaryCoordinator? _summaryCoordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance. Two recorders sharing one session root would corrupt each other's
        // journals, so the second copy hands over to the first rather than starting.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                "EchoForge is already running. Use the tray icon to open it.",
                "EchoForge", MessageBoxButton.OK, MessageBoxImage.Information);

            Shutdown();
            return;
        }

        base.OnStartup(e);

        FileSessionStore store = new();
        FileSessionLeaseProvider leases = new(store);
        ISettingsStore settings = new JsonSettingsStore();
        _catalog = new AudioDeviceCatalog();
        _endpoints = new MmDeviceEndpointMonitor();
        _power = new SystemPowerMonitor();

        _controller = new RecordingController(
            store,
            new DualTrackCaptureEngineFactory(_catalog),
            new SystemCaptureClock(),
            new VolumeDiskSpaceProbe(),
            policy: null,
            endpoints: _endpoints,
            power: _power,
            leases: leases);

        MainWindow window = new();
        _viewModel = new MainViewModel(_controller, _catalog, settings, new DialogConsentPrompt(window));
        window.DataContext = _viewModel;
        MainWindow = window;

        // Every exit path — the close button, tray Exit, and Windows shutting down — goes
        // through one coordinator so none of them can close over an unsaved recording.
        _shutdown = new ShutdownCoordinator(
            _viewModel,
            new DialogShutdownPrompt(window),
            message => System.Windows.MessageBox.Show(window, message, "EchoForge",
                MessageBoxButton.OK, MessageBoxImage.Warning));

        window.UseShutdownCoordinator(_shutdown);

        _tray = new TrayIndicator(_viewModel, () =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        })
        { Shutdown = _shutdown };

        window.Show();

        // Recovery walks every session folder and hashes files, so it runs off the UI thread.
        // Start stays disabled until it finishes; the window is responsive throughout.
        _ = RunRecoveryScanAsync(store, leases);
    }

    /// <summary>
    /// Settles interrupted sessions in the background, then opens the readiness gate.
    ///
    /// <para>
    /// The gate opens whether or not recovery succeeded: a recovery problem must not lock the
    /// user out of recording. It does say what happened, counted by session rather than by note.
    /// </para>
    /// </summary>
    private async Task RunRecoveryScanAsync(FileSessionStore store, FileSessionLeaseProvider leases)
    {
        try
        {
            IReadOnlyList<RecoveryOutcome> outcomes = await Task.Run(() =>
                new SessionRecoveryService(store, new WavChunkRepairer(), null, leases).ScanAll())
                .ConfigureAwait(true);

            string? summary = null;
            if (outcomes.Count > 0)
            {
                int needsAttention = outcomes.Count(o => o.State == Contracts.Sessions.SessionState.NeedsAttention);
                int chunks = outcomes.Sum(o => o.ChunksRecovered + o.ChunksReconciled);

                summary = $"Recovered {Plural(outcomes.Count, "interrupted recording")} on startup" +
                    (chunks > 0 ? $", restoring {Plural(chunks, "audio chunk")}" : string.Empty) +
                    (needsAttention > 0 ? $". {Plural(needsAttention, "session")} needs attention." : ".");
            }

            // Unfinished recordings are offered back, never resumed automatically.
            IReadOnlyList<Contracts.Sessions.RecoveryCandidate> continuable = await Task.Run(() =>
                new SessionRecoveryService(store, new WavChunkRepairer(), null, leases)
                    .FindContinuationCandidates()).ConfigureAwait(true);

            Contracts.Sessions.RecoveryCandidate? candidate = continuable.Count > 0 ? continuable[0] : null;

            _viewModel?.OfferContinuation(candidate);
            _viewModel?.MarkReady(summary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel?.MarkReady(
                warning: $"EchoForge could not finish checking earlier recordings ({ex.GetType().Name}). " +
                         "You can still record, but any interrupted sessions may need attention.");
        }

        // Recording is usable from here. Processing sets itself up afterwards, because finding a
        // Python runtime means starting processes and the recorder must never wait on that.
        await InitialiseProcessingAsync(store).ConfigureAwait(true);
    }

    /// <summary>
    /// Composes the transcription surface, if this machine can run a worker at all.
    ///
    /// <para>
    /// Transcription is optional composition on purpose. Without a usable Python runtime the
    /// recorder still works completely and the panel simply does not appear; a missing processing
    /// dependency must not stop anyone from recording a meeting.
    /// </para>
    /// </summary>
    private async Task InitialiseProcessingAsync(FileSessionStore store)
    {
        if (_controller is null || _viewModel is null || MainWindow is not Window window)
        {
            return;
        }

        WorkerLaunchOptions? options = await Task.Run(() =>
            WorkerLaunchOptions.Discover(Path.Combine(AppContext.BaseDirectory, "worker"))
            ?? WorkerLaunchOptions.Discover(Path.Combine(RepositoryWorkerRoot(), "worker")))
            .ConfigureAwait(true);

        if (options is null)
        {
            return;
        }

        _transcripts = new FileTranscriptionStore(store);

        // Production preparation is composed only when the pinned manifest itself is sound. A
        // manifest that fails validation permits nothing rather than permitting less: the
        // placeholder path keeps working and no download can happen.
        _registry = ArtifactRegistry.TryOpen(
            Path.Combine(RepositoryWorkerRoot(), "artifacts", "manifest.json"),
            out IReadOnlyList<string> manifestProblems);

        ProcessingPreparation? preparation = _registry is null
            ? null
            : new ProcessingPreparation(store, _registry, new DerivativeBuilder(store));

        _coordinator = new TranscriptionCoordinator(
            store,
            _transcripts,
            new WorkerSupervisor(options, new RecordingCaptureGate(_controller)),
            new RecordingCaptureGate(_controller),
            preparation: preparation);

        _ = manifestProblems;

        // Recording always has priority, so the coordinator hears about capture the moment the
        // recorder does rather than discovering it on a poll.
        _controller.StateChanged += OnRecordingStateChangedForProcessing;

        _viewModel.AttachTranscription(new TranscriptionViewModel(_coordinator, new SaveFileExportPrompt(window)));

        // Summarisation shares the transcription coordinator's gate rather than keeping its own:
        // two coordinators each politely checking their own state would still start two jobs.
        _summaries = new FileSummaryStore(store);
        _summaryCoordinator = new SummaryCoordinator(
            store,
            _summaries,
            _transcripts,
            new WorkerSupervisor(options, new RecordingCaptureGate(_controller)),
            new RecordingCaptureGate(_controller),
            otherJobRunning: () => _coordinator?.IsRunning ?? false,
            // Null when the manifest did not validate, which leaves the placeholder working and
            // makes the production backend unavailable rather than unverified.
            runtime: _registry is null ? null : new LlamaRuntimeStager(_registry));

        _viewModel.AttachSummary(new SummaryViewModel(_summaryCoordinator));

        await Task.Run(() => DiscardOrphanStaging(store)).ConfigureAwait(true);
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private void OnRecordingStateChangedForProcessing(object? sender, RecordingStateChangedEventArgs e) =>
        _coordinator?.CaptureStateChanged();

    /// <summary>
    /// The worker package during development, where it sits beside the solution rather than in
    /// the publish output. Phase 6 replaces both lookups with an app-local runtime directory.
    /// </summary>
    private static string RepositoryWorkerRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EchoForge.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Clears anything a previous run left staged. A staged transcript is the remains of an
    /// attempt whose process died; keeping it would eventually make an unverified file look like
    /// a revision.
    /// </summary>
    private void DiscardOrphanStaging(FileSessionStore store)
    {
        if (_coordinator is null)
        {
            return;
        }

        foreach (string sessionId in store.EnumerateSessions())
        {
            try
            {
                _coordinator.DiscardOrphanStaging(sessionId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A session whose folder cannot be read is recovery's problem, not processing's.
            }
        }
    }

    /// <summary>
    /// Windows is ending the session. There is no opportunity to ask anything, so finalize as
    /// far as the time allows rather than letting the process die mid-write.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _viewModel?.FinalizeForShutdownAsync().GetAwaiter().GetResult();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Tears down in dependency order. Disposing the controller stops any live recording, which
    /// finalizes the active chunks and writes the manifest rather than abandoning them.
    /// </summary>
    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;

        _viewModel?.Dispose();
        _viewModel = null;

        // Before the controller: the coordinator holds a worker process, and it must be taken
        // down while the recorder it defers to still exists.
        if (_controller is not null)
        {
            _controller.StateChanged -= OnRecordingStateChangedForProcessing;
        }

        _summaryCoordinator?.Dispose();
        _summaryCoordinator = null;
        _summaries = null;

        _coordinator?.Dispose();
        _coordinator = null;
        _transcripts = null;

        _registry?.Dispose();
        _registry = null;

        _controller?.Dispose();
        _controller = null;

        _endpoints?.Dispose();
        _endpoints = null;

        _power?.Dispose();
        _power = null;

        _catalog?.Dispose();
        _catalog = null;

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owning thread, or never acquired. Nothing to release.
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Shows the per-recording consent reminder as a modal dialog.
///
/// <para>
/// The architecture requires a reminder before <em>every</em> recording. Clicking Start is not
/// consent; this asks for an affirmative answer each time, and cancelling is a real cancel.
/// </para>
/// </summary>
public sealed class DialogConsentPrompt(Window owner) : IConsentPrompt
{
    public Task<bool> ConfirmAsync()
    {
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            owner,
            "Everyone in this meeting should know it's being recorded.\n\n" +
            "Recording law varies by jurisdiction and by where each participant is. " +
            "Obtaining the consent required for this meeting is your responsibility.\n\n" +
            "Start recording now?",
            "Before you record",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.Cancel);

        return Task.FromResult(answer == MessageBoxResult.OK);
    }
}

/// <summary>
/// Asks where to save an export with the standard Windows dialog.
///
/// <para>
/// The dialog's own overwrite prompt is the confirmation. It is not bypassed: if the user picks
/// an existing file and confirms, that confirmation is what the exporter is told about, and
/// without it the exporter refuses to replace anything.
/// </para>
/// </summary>
public sealed class SaveFileExportPrompt(Window owner) : IExportDestinationPrompt
{
    public ExportDestination? Ask(string suggestedFileName, TranscriptExportFormat format)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            FileName = suggestedFileName,
            DefaultExt = TranscriptExporter.Extension(format),
            Filter = $"{TranscriptExporter.Describe(format)}|*{TranscriptExporter.Extension(format)}|All files|*.*",
            OverwritePrompt = true,
            AddExtension = true,
            Title = "Export transcript",
        };

        return dialog.ShowDialog(owner) == true
            ? new ExportDestination(dialog.FileName, OverwriteConfirmed: true)
            : null;
    }
}
