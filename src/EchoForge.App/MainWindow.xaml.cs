using System.ComponentModel;
using System.Windows;

namespace EchoForge.App;

public partial class MainWindow : Window
{
    private ShutdownCoordinator? _shutdown;

    public MainWindow() => InitializeComponent();

    /// <summary>Supplied by the composition root once the view model exists.</summary>
    public void UseShutdownCoordinator(ShutdownCoordinator coordinator) => _shutdown = coordinator;

    /// <summary>
    /// Closing is asynchronous because saving is.
    ///
    /// <para>
    /// The first close is always cancelled: WPF cannot await here, and letting it proceed would
    /// tear the window down — and with it the process — while chunks were still being finalized.
    /// The coordinator asks the user, awaits a durable save, and only then closes for real. A
    /// failed save keeps the window open.
    /// </para>
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_shutdown is null || _shutdown.IsShuttingDown)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        BeginShutdown();
    }

    /// <summary>
    /// The one permitted async void: a framework event adapter. Every exception inside the
    /// coordinator is already turned into a notice.
    /// </summary>
    private async void BeginShutdown()
    {
        if (_shutdown is null)
        {
            return;
        }

        if (await _shutdown.TryShutdownAsync().ConfigureAwait(true))
        {
            // IsShuttingDown is set, so this pass runs straight through OnClosing.
            Close();
        }
    }
}
