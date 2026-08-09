using System.Windows;
using EchoForge.Infrastructure.Diagnostics;
using EchoForge.Infrastructure.Setup;

namespace EchoForge.App.Setup;

/// <summary>
/// Settings, models and runtime, as a page in the main window.
///
/// <para>
/// This used to be a second application with its own title bar, which is how downloads, GPU facts
/// and a machine report came to feel like a product of their own rather than a preference somebody
/// adjusts twice a year. Nothing about what it can do changed; where it lives did.
/// </para>
/// </summary>
public partial class SettingsPage : System.Windows.Controls.UserControl
{
    public SettingsPage() => InitializeComponent();

    /// <summary>
    /// Writes a support bundle, having said where it went.
    ///
    /// <para>
    /// The bundle contains no transcript, summary or meeting content — only what EchoForge itself
    /// did — and it is written on request rather than collected in the background.
    /// </para>
    /// </summary>
    private async void OnSaveDiagnostics(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { SetupServices: { } services, Setup: { } setup })
        {
            return;
        }

        // The bundle never throws — a section it cannot collect is recorded as uncollected — so
        // there is nothing to catch here, only a result to report.
        DiagnosticsResult result = await new DiagnosticsBundle(services.Layout, services)
            .WriteAsync(
                transcriptionProfileId: setup.TranscriptionProfileId,
                summaryProfileId: setup.SummaryProfileId)
            .ConfigureAwait(true);

        System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            result.Succeeded
                ? "Diagnostics written to\n\n" + result.Path +
                  "\n\nIt contains no transcript, summary or meeting content."
                : result.Message,
            "EchoForge",
            MessageBoxButton.OK,
            result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
