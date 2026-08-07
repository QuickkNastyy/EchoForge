using System.Windows;
using EchoForge.Core.Exports;

namespace EchoForge.App.Library;

/// <summary>
/// The manual-handoff dialog. It shows the exact payload, lets the user edit it, and copies or saves
/// it only when the user says so. Cancelling copies nothing.
/// </summary>
public partial class ManualHandoffWindow : Window
{
    private readonly ManualHandoffViewModel _model;

    public ManualHandoffWindow(ManualHandoffViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = _model;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        // The one and only path to the clipboard, and only on this click.
        _model.Copy();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "Save handoff",
            FileName = _model.SuggestedFileName,
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // The dialog already prompts before replacing an existing file, so a confirmed choice here
        // may overwrite; an unconfirmed one never reaches this code.
        ExportResult result = _model.Save(dialog.FileName, overwrite: true);
        if (!result.Succeeded)
        {
            System.Windows.MessageBox.Show(this, result.Message, "EchoForge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
