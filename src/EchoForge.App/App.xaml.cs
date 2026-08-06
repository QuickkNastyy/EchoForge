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
            power: _power);

        MainWindow window = new();
        _viewModel = new MainViewModel(_controller, _catalog, settings, new DialogConsentPrompt(window));
        window.DataContext = _viewModel;
        MainWindow = window;

        _tray = new TrayIndicator(_viewModel, () =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        });

        window.Show();

        // Recovery walks every session folder and hashes files, so it runs off the UI thread.
        _ = RunRecoveryScanAsync(store, window);
    }

    /// <summary>
    /// Settles interrupted sessions in the background and reports what actually happened, by
    /// session rather than by note count.
    /// </summary>
    private async Task RunRecoveryScanAsync(FileSessionStore store, Window window)
    {
        try
        {
            IReadOnlyList<RecoveryOutcome> outcomes = await Task.Run(() =>
                new SessionRecoveryService(store, new WavChunkRepairer()).ScanAll()).ConfigureAwait(true);

            if (outcomes.Count == 0)
            {
                return;
            }

            int needsAttention = outcomes.Count(o => o.State == Contracts.Sessions.SessionState.NeedsAttention);
            int chunks = outcomes.Sum(o => o.ChunksRecovered + o.ChunksReconciled);

            string summary = $"Recovered {Plural(outcomes.Count, "interrupted recording")} on startup" +
                (chunks > 0 ? $", restoring {Plural(chunks, "audio chunk")}" : string.Empty) +
                (needsAttention > 0 ? $". {Plural(needsAttention, "session")} needs attention." : ".");

            window.Dispatcher.Invoke(() => _viewModel?.ShowRecoverySummary(summary));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            window.Dispatcher.Invoke(() =>
                _viewModel?.ShowRecoverySummary($"Recovery could not finish: {ex.GetType().Name}"));
        }
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

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
