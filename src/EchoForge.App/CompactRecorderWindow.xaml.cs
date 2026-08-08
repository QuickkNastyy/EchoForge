using System.Windows;
using System.Windows.Input;

namespace EchoForge.App;

/// <summary>
/// The compact, always-on-top recorder. A view over the main view model, not a second recorder:
/// whoever constructs it passes the live <see cref="MainViewModel"/> as the data context.
/// </summary>
public partial class CompactRecorderWindow : Window
{
    public CompactRecorderWindow() => InitializeComponent();

    /// <summary>
    /// The card has no title bar to drag, so it drags by its own surface.
    ///
    /// <para>
    /// Only when the press was not already handled, so the pause and stop buttons keep working:
    /// a click that a button took is not a drag, and starting one would swallow it.
    /// </para>
    /// </summary>
    private void OnDragged(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the drag began. Nothing to move, nothing to report.
        }
    }
}
