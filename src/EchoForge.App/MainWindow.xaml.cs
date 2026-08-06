using System.ComponentModel;
using System.Windows;

namespace EchoForge.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// Closing while a session is live or paused would either lose audio or hide the fact that a
    /// recording is still open. Both need an explicit choice.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && (viewModel.IsRecording || viewModel.IsPaused))
        {
            string message = viewModel.IsRecording
                ? "A recording is still running. Stop it and save, or keep recording?"
                : "This recording is paused and has not been saved. Stop it and save, or leave it open?";

            string caption = viewModel.IsRecording ? "EchoForge is recording" : "EchoForge is paused";

            MessageBoxResult answer = System.Windows.MessageBox.Show(
                this, message, caption,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (answer != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }

            viewModel.StopCommand.Execute(null);
        }

        base.OnClosing(e);
    }
}
