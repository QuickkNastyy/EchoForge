namespace EchoForge.App.Recording;

/// <summary>
/// The Record page.
///
/// <para>
/// Deliberately thin, like every other view here: it moves clicks to the main view model and owns
/// no state. The compact recorder is opened from the rail, which the main window owns.
/// </para>
/// </summary>
public partial class RecordPage : System.Windows.Controls.UserControl
{
    public RecordPage() => InitializeComponent();
}
