using System.IO;
using System.Windows;
using EchoForge.Audio.Windows;
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

        // Recovery scan before anything can start a new recording, so an interrupted session is
        // settled before its folder is touched again.
        IReadOnlyList<string> notices = RunRecoveryScan(store);

        _controller = new RecordingController(
            store,
            new DualTrackCaptureEngineFactory(_catalog),
            new SystemCaptureClock(),
            new VolumeDiskSpaceProbe());

        _viewModel = new MainViewModel(_controller, _catalog, settings, notices);

        MainWindow window = new() { DataContext = _viewModel };
        MainWindow = window;

        _tray = new TrayIndicator(_viewModel, () =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        });

        window.Show();
    }

    private static IReadOnlyList<string> RunRecoveryScan(FileSessionStore store)
    {
        try
        {
            SessionRecoveryService recovery = new(store, new WavChunkRepairer());
            return [.. recovery.ScanAll().SelectMany(o => o.Notes)];
        }
        catch (IOException ex)
        {
            return [$"Recovery could not finish: {ex.Message}"];
        }
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
