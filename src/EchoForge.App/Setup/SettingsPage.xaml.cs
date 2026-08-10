using System.IO;
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

    // -- where recordings are kept ----------------------------------------------------------------

    /// <summary>
    /// Picks a new folder for recordings.
    ///
    /// <para>
    /// The dialog lives here rather than in the view model for the usual reason: a view model that
    /// opens windows cannot be tested without a desktop. It hands back a path; deciding whether
    /// that path can be used, and remembering it, is the view model's job.
    /// </para>
    /// </summary>
    private void OnChooseRecordingsFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel host)
        {
            return;
        }

        Microsoft.Win32.OpenFolderDialog dialog = new()
        {
            Title = "Choose where EchoForge keeps recordings",
            Multiselect = false,
            InitialDirectory = Directory.Exists(host.RecordingsFolderInUse) ? host.RecordingsFolderInUse : string.Empty,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        if (host.ChooseRecordingsFolder(dialog.FolderName) is { } problem)
        {
            System.Windows.MessageBox.Show(
                Window.GetWindow(this), problem, "EchoForge",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Puts recordings back under the standard location beside the rest of the data.</summary>
    private void OnResetRecordingsFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel host)
        {
            host.ChooseRecordingsFolder(null);
        }
    }

    /// <summary>Opens the folder recordings are actually being written to.</summary>
    private void OnOpenRecordingsFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { RecordingsFolderInUse: { Length: > 0 } folder })
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                "That folder could not be opened:" + Environment.NewLine + Environment.NewLine + folder,
                "EchoForge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
