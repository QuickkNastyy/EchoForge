using System.Windows;

namespace EchoForge.App;

/// <summary>
/// The compact, always-on-top recorder. A view over the main view model, not a second recorder:
/// whoever constructs it passes the live <see cref="MainViewModel"/> as the data context.
/// </summary>
public partial class CompactRecorderWindow : Window
{
    public CompactRecorderWindow() => InitializeComponent();
}
