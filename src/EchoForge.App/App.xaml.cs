using System.IO;
using System.Windows;
using EchoForge.Audio.Windows;
using EchoForge.Audio.Windows.Playback;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Recording;
using EchoForge.Infrastructure.Playback;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Contracts.Settings;
using EchoForge.Contracts.Setup;
using EchoForge.Core.Exports;
using EchoForge.Core.Setup;
using EchoForge.Contracts.Inference;
using EchoForge.Contracts.Processing;
using EchoForge.Core.Recording;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Settings;
using EchoForge.App.Library;
using EchoForge.App.Setup;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Setup;
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
    private FileSpeakerAliasStore? _aliases;
    private SqliteLibraryIndex? _libraryIndex;

    /// <summary>Where this run keeps recordings: the chosen folder, or the default.</summary>
    private string? _sessionsRoot;

    /// <summary>
    /// The chosen recordings folder if it can be used, and the default if it cannot.
    ///
    /// <para>
    /// Creating the directory is the test, because being able to name a folder is not the same as
    /// being able to write in it — an external drive that is not plugged in today is the ordinary
    /// case. Falling back keeps the application usable; it does not rewrite the setting, so the
    /// folder starts being used again the moment it is reachable.
    /// </para>
    /// </summary>
    private static string ResolveSessionsRoot(string? chosen, string fallback)
    {
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return fallback;
        }

        try
        {
            string full = Path.GetFullPath(chosen);
            Directory.CreateDirectory(full);
            return full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return fallback;
        }
    }
    private LibraryIndexMaintainer? _indexMaintenance;
    private LibraryViewModel? _library;
    private FileSessionLeaseProvider? _leases;
    private AppLayout? _layout;
    private SetupServices? _setup;
    private SetupViewModel? _setupViewModel;
    private FileSessionStore? _store;
    private CoordinatorReprocessor? _reprocessor;
    private ISettingsStore? _settings;
    private SetupRecommendation? _recommendation;
    private bool _attachingProcessing;

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

        // Everything the application reads or writes is resolved here, from the executable and
        // from the user's profile. Nothing looks for the repository it was built in.
        _layout = AppLayout.Current;
        _layout.EnsureDataDirectories();

        JsonSettingsStore settings = new();
        _settings = settings;

        // Where recordings live is read before the store exists, because the store is the thing
        // it decides. A folder that cannot be created falls back to the default rather than
        // failing to start: an unreachable drive in a settings file must not cost the user their
        // recorder, and the Recordings section says which one is actually in use.
        _sessionsRoot = ResolveSessionsRoot(settings.Load().RecordingsRoot, _layout.SessionsRoot);

        FileSessionStore store = new(_sessionsRoot);
        _store = store;
        FileSessionLeaseProvider leases = new(store);
        _leases = leases;

        // Paint in the remembered palette before the first window exists, so nothing is ever seen
        // in the wrong one, and remember any later change. A settings file written before this
        // existed, or holding anything unrecognised, reads as dark.
        Theme.Apply(Theme.Parse(settings.Load().Theme));
        Theme.Changed += (_, _) => settings.Save(settings.Load() with { Theme = Theme.Name(Theme.Current) });

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
        _viewModel = new MainViewModel(
            _controller, _catalog, settings,
            levelMonitor: () => new DeviceLevelMonitor(_catalog));

        // The folder actually in use, which is the chosen one unless it could not be opened.
        _viewModel.DescribeStorage(_sessionsRoot ?? _layout.SessionsRoot, _layout.SessionsRoot);
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
    /// Composes the library first, then transcription and summarisation if this machine can run a
    /// worker.
    ///
    /// <para>
    /// The order is the point. The meeting library — seeing, opening, playing, searching, exporting
    /// and deleting recordings — never depends on a Python runtime, a model, or anything Setup
    /// installs. It is composed unconditionally. Transcription and summarisation are the optional
    /// half: without a usable worker they simply do not attach yet, and the recorder and the library
    /// both keep working. When Setup later installs a runtime, <see cref="TryAttachProcessingAsync"/>
    /// attaches them in place, with no restart.
    /// </para>
    /// </summary>
    private async Task InitialiseProcessingAsync(FileSessionStore store)
    {
        if (_controller is null || _viewModel is null || _layout is null || MainWindow is not Window window)
        {
            return;
        }

        // Open the pinned manifest. A manifest that fails validation permits no downloads and no
        // processing — but it does not stop the library from opening: a broken manifest is exactly
        // when somebody most needs to reach their recordings.
        _setup = await Task.Run(() => SetupServices.TryOpen(out IReadOnlyList<string> problems, _layout))
            .ConfigureAwait(true);

        _viewModel.AttachSetup(_setup, _catalog);

        if (_setup is { } services)
        {
            // Settings is a page in the main window now, so its view model is built once and
            // lives for the life of the application rather than for the life of a window.
            _setupViewModel = new SetupViewModel(services, _catalog);

            // Installing or repairing a runtime here can make transcription and summarisation
            // possible on a machine that had neither when it started. Re-evaluate and attach them
            // in place; nobody should have to restart EchoForge for a download to count.
            _setupViewModel.ComponentsChanged += OnSetupComponentsChanged;

            _viewModel.AttachSetupPage(_setupViewModel);
            _ = _setupViewModel.RefreshAsync();
        }

        try
        {
            if (_setup is { } setup)
            {
                HardwareSnapshot hardware = await setup.HardwareProbe(_catalog).ProbeAsync().ConfigureAwait(true);
                _recommendation = ProfileRecommender.Recommend(hardware);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _recommendation = null;
        }

        // The library, unconditionally. It reprocesses through a live seam that is empty until a
        // worker attaches, so a recording is reachable now and "Transcribe again" lights up later.
        ComposeLibrary(store, window);

        // Finishing a recording changes what the library should say about it.
        _controller.StateChanged += OnRecordingStateChangedForIndex;

        // Attach processing if a worker is already installed. If not, this returns quietly and the
        // library carries on; Setup can make it available without a restart.
        await TryAttachProcessingAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Builds the meeting library and its index. Depends on nothing Setup installs: the session
    /// store, the derivative stores, and a database that is a throwaway cache.
    /// </summary>
    private void ComposeLibrary(FileSessionStore store, Window window)
    {
        if (_library is not null || _viewModel is null || _layout is null)
        {
            return;
        }

        _transcripts = new FileTranscriptionStore(store);
        _summaries = new FileSummaryStore(store);
        _aliases = new FileSpeakerAliasStore(store);
        FileMeetingTitleStore titles = new(store);

        LibraryProjection projection = new(store, _transcripts, _summaries, _aliases, titles);
        // The index travels with the recordings it indexes. Leaving it at the default while the
        // sessions moved would have one folder's library describing another folder's meetings.
        _libraryIndex = new SqliteLibraryIndex(
            Path.Combine(_sessionsRoot ?? _layout.SessionsRoot, "library.db"), projection);

        // Keeps the index in step with the folders. Deliberately fire-and-forget: an index update
        // that fails must never be able to undo the transcript activation that triggered it.
        _indexMaintenance = new LibraryIndexMaintainer(_libraryIndex);

        _library = new LibraryViewModel(
            _libraryIndex,
            projection,
            _transcripts,
            _summaries,
            _aliases,
            new LibraryServices
            {
                Playback = new PlaybackPreparer(store),
                Devices = () => new NAudioPlaybackDevice(),
                FolderFor = sessionId => store.Resolve(sessionId).Root,
                // Reads whatever reprocessor currently exists. Null until a worker attaches and
                // non-null afterwards, so reprocessing follows the runtime rather than the state
                // the application happened to start in.
                Reprocessor = new LiveReprocessor(() => _reprocessor),
                Deletion = new SessionDeletionService(
                    store,
                    store.Root,
                    new CompositeDeletionAuthority(
                        new SessionStateDeletionAuthority(store),
                        new LeaseDeletionAuthority(_leases!),
                        // What only the running application knows: a live recorder and the two
                        // heavy jobs. Asked again at the moment of deletion, not just when the
                        // button was drawn.
                        new DelegateDeletionAuthority(RefuseWhileBusy)),
                    new WindowsRecycleBin(),
                    sessionId => _indexMaintenance?.UpdateNowAsync(sessionId) ?? Task.CompletedTask),
                Confirmation = new DialogDeleteConfirmation(window),
                Index = _indexMaintenance,
                Titles = titles,
            });

        _viewModel.AttachLibrary(_library);
    }

    /// <summary>
    /// Handles the Setup screen reporting that components changed: a runtime it just installed may
    /// now make processing possible.
    /// </summary>
    private void OnSetupComponentsChanged(object? sender, EventArgs e) => _ = TryAttachProcessingAsync();

    /// <summary>
    /// Attaches transcription and summarisation, once — if a worker can be resolved.
    ///
    /// <para>
    /// Idempotent and safe to call repeatedly, which is what the no-restart path needs: a second
    /// call after processing is attached does nothing, so there is never a duplicate coordinator, a
    /// doubled event subscription, or a leaked worker. Called at startup, and again every time Setup
    /// finishes installing or repairing something.
    /// </para>
    /// </summary>
    private async Task TryAttachProcessingAsync()
    {
        if (_controller is null || _viewModel is null || _setup is null || _store is null
            || MainWindow is not Window window)
        {
            return;
        }

        // Already attached, or an attach is in flight: either way there is nothing to build. The
        // guard is set synchronously, before the first await, so two rapid notifications cannot
        // both get past it.
        if (_coordinator is not null || _attachingProcessing)
        {
            return;
        }

        _attachingProcessing = true;

        try
        {
            // EchoForge's own interpreter and worker environment. There is deliberately no fall
            // back to a Python on the machine: the pinned wheel closure is built for one CPython
            // ABI, and a missing runtime is something the setup screen can install.
            WorkerLaunchOptions? options = await Task.Run(_setup.TryResolveWorkerLaunch).ConfigureAwait(true);

            if (options is null)
            {
                return;
            }

            FileSessionStore store = _store;
            _registry = _setup.Artifacts;

            ProcessingPreparation preparation = new(store, _registry, new DerivativeBuilder(store));

            WorkerLaunchOptions? nemoOptions = _setup.TryResolveNemoWorkerLaunch();
            _coordinator = new TranscriptionCoordinator(
                store,
                _transcripts!,
                new WorkerSupervisor(options, new RecordingCaptureGate(_controller), nemoOptions),
                new RecordingCaptureGate(_controller),
                preparation: preparation);

            // Recording always has priority, so the coordinator hears about capture the moment the
            // recorder does rather than discovering it on a poll.
            _controller.StateChanged += OnRecordingStateChangedForProcessing;

            string recommendedCompute = _recommendation?.Transcription.ProfileId ?? ProcessingProfile.CpuInt8;
            string recommendedModel = _recommendation?.Asr.ModelId ?? AsrModelIds.WhisperLargeV3Turbo;

            TranscriptionViewModel transcriptionViewModel = new(
                _coordinator,
                new SaveFileExportPrompt(window),
                _settings,
                recommendedModel,
                recommendedCompute);
            transcriptionViewModel.ShowComparison = comparison =>
                new TranscriptComparisonWindow(comparison) { Owner = window }.Show();

            _viewModel.AttachTranscription(transcriptionViewModel);

            // Summarisation shares the transcription coordinator's gate rather than keeping its own:
            // two coordinators each politely checking their own state would still start two jobs.
            _summaryCoordinator = new SummaryCoordinator(
                store,
                _summaries!,
                _transcripts!,
                new WorkerSupervisor(options, new RecordingCaptureGate(_controller)),
                new RecordingCaptureGate(_controller),
                otherJobRunning: () => _coordinator?.IsRunning ?? false,
                runtime: _setup.Llama);

            SummaryViewModel summaryViewModel = new(_summaryCoordinator, _settings);
            summaryViewModel.ShowComparison = comparison =>
                new SummaryComparisonWindow(comparison) { Owner = window }.Show();
            _viewModel.AttachSummary(summaryViewModel);

            // Keep the index in step as processing changes a session's canonical state.
            _coordinator.SessionChanged += OnSessionChangedForIndex;
            _summaryCoordinator.SessionChanged += OnSessionChangedForIndex;

            // The library's live reprocessor now has real coordinators to run, using the same
            // backend choices the main window shows rather than a second default.
            _reprocessor = new CoordinatorReprocessor(
                _coordinator,
                _summaryCoordinator,
                () => _viewModel?.Transcription?.CurrentOptions() ?? new Contracts.Processing.TranscriptionOptions(),
                () => _viewModel?.Summary?.CurrentOptions() ?? new Contracts.Summaries.SummaryOptions());

            // Reprocessing actions in an already-open library can enable now that a runtime exists.
            _library?.ProcessingAvailabilityChanged();

            await Task.Run(() => DiscardOrphanStaging(store)).ConfigureAwait(true);
        }
        finally
        {
            _attachingProcessing = false;
        }
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private void OnRecordingStateChangedForProcessing(object? sender, RecordingStateChangedEventArgs e) =>
        _coordinator?.CaptureStateChanged();

    /// <summary>
    /// A session's canonical state changed, so the index has something to catch up on.
    ///
    /// <para>
    /// Nothing here is awaited and nothing here can fail the operation that raised it. That is the
    /// whole rule: a transcript that activated stays activated even if the database is locked, out
    /// of space, or gone entirely.
    /// </para>
    /// </summary>
    private void OnSessionChangedForIndex(object? sender, string sessionId) =>
        _indexMaintenance?.Invalidate(sessionId);

    /// <summary>
    /// A recording that has settled is a meeting the library should be able to find, without
    /// anybody pressing Refresh.
    /// </summary>
    private void OnRecordingStateChangedForIndex(object? sender, RecordingStateChangedEventArgs e)
    {
        if (e.State is not (Contracts.Sessions.SessionState.Recorded
            or Contracts.Sessions.SessionState.NeedsAttention
            or Contracts.Sessions.SessionState.Failed))
        {
            return;
        }

        // The controller may already have advanced by the time this listener runs. The event owns
        // the identity of the transition; asking the controller afterwards creates a race where a
        // perfectly durable recording is invisible until the next full library refresh.
        if (e.SessionId is not { Length: > 0 } sessionId)
        {
            return;
        }

        _indexMaintenance?.Invalidate(sessionId);

        // Stop runs on a worker thread. The library owns an ObservableCollection bound to WPF, so
        // the incremental row refresh must begin on the application dispatcher. Starting the async
        // method there also makes its ConfigureAwait(true) continuations return to the UI thread.
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            _ = ShowNewRecordingAsync(sessionId);
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() => _ = ShowNewRecordingAsync(sessionId)));
        }
    }

    /// <summary>
    /// Brings a recording that has just finished into the meeting list.
    ///
    /// <para>
    /// Deliberately does not navigate. Somebody who stopped a recording to start another one
    /// should not be thrown onto a different page; the recording is simply there, at the top,
    /// when they go looking for it.
    /// </para>
    /// </summary>
    private async Task ShowNewRecordingAsync(string sessionId)
    {
        if (_indexMaintenance is { } maintenance)
        {
            try
            {
                await maintenance.UpdateNowAsync(sessionId).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // An index that could not be updated is a stale list, not a lost recording. The
                // refresh below still reads the folders.
            }
        }

        if (_library is not { } library)
        {
            return;
        }

        // The terminal state is raised only after the snapshot write, but filesystem/index
        // projection work can still be a beat behind on a busy machine. Refresh the one meeting
        // until the canonical projection is readable rather than turning that transient null into
        // a recording that only appears after restart. Bounded so a genuinely unreadable folder
        // does not leave a permanent background loop.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (await library.RefreshMeetingAsync(sessionId).ConfigureAwait(true))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Same settling case: the next pass re-reads the canonical files.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// What only the running application knows about whether a meeting is busy.
    ///
    /// <para>
    /// Consulted again immediately before a folder is moved, which is the point: a user can open
    /// the confirmation while nothing is happening and answer it after a recording has started.
    /// </para>
    /// </summary>
    private Contracts.Sessions.DeletionRefusal? RefuseWhileBusy(string sessionId)
    {
        if (_controller is { } controller &&
            string.Equals(controller.SessionId, sessionId, StringComparison.Ordinal) &&
            controller.CaptureMayBeLive)
        {
            return new Contracts.Sessions.DeletionRefusal(
                "recording_active",
                "That recording is running right now, so it was not deleted. Stop it first.");
        }

        if (_coordinator is { } transcription &&
            (transcription.IsRunning || transcription.IsQueued) &&
            string.Equals(transcription.ActiveSessionId, sessionId, StringComparison.Ordinal))
        {
            return new Contracts.Sessions.DeletionRefusal(
                "transcribing",
                "That recording is being transcribed right now, so it was not deleted. Wait for it to finish, or cancel it.");
        }

        if (_summaryCoordinator is { IsRunning: true } summaries &&
            summaries.StateFor(sessionId).CurrentJob is { State: Contracts.Transcripts.ProcessingStageState.Running })
        {
            return new Contracts.Sessions.DeletionRefusal(
                "summarizing",
                "That recording is being summarised right now, so it was not deleted. Wait for it to finish, or cancel it.");
        }

        return null;
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

        if (_setupViewModel is not null)
        {
            _setupViewModel.ComponentsChanged -= OnSetupComponentsChanged;
            _setupViewModel.Dispose();
            _setupViewModel = null;
        }

        // Before the index: the library owns the open meeting, and the open meeting owns an audio
        // device and a handle on its prepared audio.
        _library?.Dispose();
        _library = null;
        _libraryIndex = null;

        _indexMaintenance?.Dispose();
        _indexMaintenance = null;

        _viewModel?.Dispose();
        _viewModel = null;

        // Before the controller: the coordinator holds a worker process, and it must be taken
        // down while the recorder it defers to still exists.
        if (_controller is not null)
        {
            _controller.StateChanged -= OnRecordingStateChangedForProcessing;
            _controller.StateChanged -= OnRecordingStateChangedForIndex;
        }

        if (_coordinator is not null)
        {
            _coordinator.SessionChanged -= OnSessionChangedForIndex;
        }

        if (_summaryCoordinator is not null)
        {
            _summaryCoordinator.SessionChanged -= OnSessionChangedForIndex;
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
/// Asks before deleting a meeting.
///
/// <para>
/// The dialog names the meeting and its date rather than its session ID, because the whole safety
/// of an explicit confirmation rests on the user recognising which recording it is about. It says
/// what will happen to it — the Recycle Bin, not oblivion — and it defaults to No.
/// </para>
/// </summary>
public sealed class DialogDeleteConfirmation(Window owner) : IDeleteConfirmation
{
    public bool Confirm(EchoForge.Infrastructure.Sessions.DeletionEligibility eligibility)
    {
        ArgumentNullException.ThrowIfNull(eligibility);

        string when = eligibility.RecordedUtc is { } recorded
            ? recorded.ToLocalTime().ToString("ddd, MMM d, yyyy · h:mm tt", System.Globalization.CultureInfo.CurrentCulture)
            : "date unknown";

        string size = eligibility.ApproximateBytes > 0
            ? $"\n\nAbout {eligibility.ApproximateBytes / (1024.0 * 1024.0):F0} MB will be moved."
            : string.Empty;

        MessageBoxResult answer = System.Windows.MessageBox.Show(
            owner,
            $"Delete this meeting?\n\n{eligibility.Title}\n{when}\n\n" +
            "Its recording, transcript versions, summary versions and speaker names all go together, " +
            "to the Recycle Bin. You can restore them from there." + size,
            "Delete meeting",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
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
