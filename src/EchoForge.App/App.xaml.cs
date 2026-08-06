using System.IO;
using System.Windows;
using EchoForge.Audio.Windows;
using EchoForge.Contracts.Recording;
using EchoForge.Contracts.Settings;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Settings;
using EchoForge.Infrastructure.Storage;

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
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

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
