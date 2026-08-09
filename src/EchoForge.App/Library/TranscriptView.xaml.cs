using System.Windows;
using System.Windows.Controls;

namespace EchoForge.App.Library;

/// <summary>
/// The transcript pane.
///
/// <para>
/// It owns the list, so it owns revealing a line — the page above asks it to, whether that came
/// from a search box, a summary claim, or a double-click on a line.
/// </para>
/// </summary>
public partial class TranscriptView : System.Windows.Controls.UserControl
{
    public TranscriptView() => InitializeComponent();

    /// <summary>Raised when a line was double-clicked, so the page can cue the audio.</summary>
    public event EventHandler<TranscriptLine>? LineActivated;

    /// <summary>Selects a line and scrolls it into view. Null does nothing, on purpose.</summary>
    public void Reveal(TranscriptLine? line)
    {
        if (line is null)
        {
            return;
        }

        TranscriptList.SelectedItem = line;
        TranscriptList.ScrollIntoView(line);
    }

    private void OnTranscriptLineActivated(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TranscriptList.SelectedItem is TranscriptLine line)
        {
            LineActivated?.Invoke(this, line);
        }
    }

    /// <summary>Finds the first line containing the query, and reveals it.</summary>
    private void OnTranscriptSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box || box.Text.Trim() is not { Length: > 0 } query)
        {
            return;
        }

        foreach (object item in TranscriptList.Items)
        {
            if (item is TranscriptLine line && line.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Reveal(line);
                return;
            }
        }
    }
    private void OnSpeakerRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not MeetingViewModel meeting ||
            sender is not FrameworkElement { DataContext: TranscriptLine line } ||
            line.IsYou ||
            Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        string current = string.Equals(line.Speaker, "Remote", StringComparison.Ordinal)
            ? string.Empty
            : line.Speaker;

        string? name = TextPromptWindow.Ask(
            owner,
            "Name speaker",
            "What should this speaker be called?",
            current);

        if (name is null)
        {
            return;
        }

        meeting.Rename(line.SpeakerId, name);
        e.Handled = true;
    }

}
