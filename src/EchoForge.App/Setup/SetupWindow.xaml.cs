using System.Windows;
using EchoForge.Contracts.Audio;
using EchoForge.Infrastructure.Diagnostics;
using EchoForge.Infrastructure.Setup;

namespace EchoForge.App.Setup;

/// <summary>
/// The setup window.
///
/// <para>
/// Thin, like the library window: everything that decides anything lives in the view model, and
/// this moves clicks to it. The one thing it owns is the file dialog for diagnostics, because
/// writing a support file is an explicit user action and asking where to put it is what makes it
/// one.
/// </para>
/// </summary>
public partial class SetupWindow : Window
{
    private readonly SetupViewModel _setup;
    private readonly SetupServices _services;
    private readonly IAudioDeviceCatalog? _audio;

    public SetupWindow(SetupViewModel setup, SetupServices services, IAudioDeviceCatalog? audio = null)
    {
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _audio = audio;

        InitializeComponent();
        DataContext = setup;

        Loaded += async (_, _) => await _setup.RefreshAsync();
    }

    /// <summary>
    /// Writes a support file, having asked where.
    ///
    /// <para>
    /// It contains no transcript, no summary, no prompt and no meeting title — that is enforced
    /// where the bundle is built, by naming every field it collects rather than by filtering what
    /// it happens to have picked up. Nothing uploads it.
    /// </para>
    /// </summary>
    private async void OnSaveDiagnostics(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            FileName = "echoforge-diagnostics.json",
            DefaultExt = ".json",
            Filter = "Diagnostics|*.json|All files|*.*",
            OverwritePrompt = true,
            AddExtension = true,
            Title = "Save diagnostics",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        DiagnosticsBundle bundle = new(
            _services.Layout, _services, _services.HardwareProbe(_audio));

        DiagnosticsResult result = await bundle.WriteAsync(
            dialog.FileName, _setup.TranscriptionProfileId, _setup.SummaryProfileId);

        System.Windows.MessageBox.Show(
            this,
            result.Succeeded
                ? result.Message + "\n\nIt describes this machine and what is installed. It contains no " +
                  "transcript, summary or meeting content."
                : result.Message,
            "EchoForge",
            MessageBoxButton.OK,
            result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
