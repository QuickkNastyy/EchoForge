using System.ComponentModel;
using System.Windows;

namespace EchoForge.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// Closing the window while capture is live would either lose the recording or hide the fact
    /// that it is still running. Neither is acceptable, so the user is asked.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel { IsRecording: true } viewModel)
        {
            MessageBoxResult answer = System.Windows.MessageBox.Show(
                "A recording is still running. Stop it and save, or keep recording?",
                "EchoForge is recording",
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
