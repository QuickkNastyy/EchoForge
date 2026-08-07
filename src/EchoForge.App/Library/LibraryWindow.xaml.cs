using System.Windows;
using System.Windows.Controls;
using EchoForge.Contracts.Library;
using EchoForge.Core.Exports;

namespace EchoForge.App.Library;

/// <summary>
/// The meeting library window.
///
/// <para>
/// Deliberately thin. Everything that decides anything lives in the view models; this file moves
/// clicks to them and scrolls a list, so the behaviour worth testing is testable without a window.
/// </para>
/// </summary>
public partial class LibraryWindow : Window
{
    private readonly LibraryViewModel _library;
    private readonly Func<string, Task>? _regenerate;

    public LibraryWindow(LibraryViewModel library, Func<string, Task>? regenerate = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _regenerate = regenerate;

        InitializeComponent();
        DataContext = library;
    }

    /// <summary>Opens the meeting a result came from and scrolls to the line it matched.</summary>
    private void OnResultActivated(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not SearchRow row)
        {
            return;
        }

        TranscriptLine? line = _library.OpenResult(row);
        Reveal(line);
    }

    /// <summary>
    /// Follows a citation into the transcript.
    ///
    /// <para>
    /// Resolution happens in the view model against the revision the citation names, so a summary
    /// written from an older transcript opens that transcript rather than the selected one.
    /// </para>
    /// </summary>
    private void OnEvidenceActivated(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SummaryList.SelectedItem is not SummaryLine summary || _library.OpenMeeting is not { } meeting)
        {
            return;
        }

        if (summary.Evidence.Count == 0)
        {
            return;
        }

        EvidenceLocation first = summary.Evidence[0];

        TranscriptLine? line = meeting.LocateEvidence(first);

        if (line is not null)
        {
            // Switch to the transcript so the reader lands where the citation points.
            if (FindTabControl() is { } tabs)
            {
                tabs.SelectedIndex = 0;
            }

            Reveal(line);
        }
    }

    private System.Windows.Controls.TabControl? FindTabControl() =>
        VisualTreeHelperFind(this) as System.Windows.Controls.TabControl;

    private static object? VisualTreeHelperFind(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is System.Windows.Controls.TabControl found)
            {
                return found;
            }

            if (VisualTreeHelperFind(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void Reveal(TranscriptLine? line)
    {
        if (line is null)
        {
            return;
        }

        TranscriptList.SelectedItem = line;
        TranscriptList.ScrollIntoView(line);
        TranscriptList.Focus();
    }

    private async void OnRegenerateSummary(object sender, RoutedEventArgs e)
    {
        if (_library.OpenMeeting is not { } meeting || _regenerate is null)
        {
            System.Windows.MessageBox.Show(
                this,
                "Generating a summary is done from the main window, on the recording that is open there.",
                "EchoForge",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await _regenerate(meeting.SessionId);
        await _library.RefreshMeetingAsync(meeting.SessionId);
    }

    private void OnExportTranscript(object sender, RoutedEventArgs e)
    {
        if (_library.OpenMeeting is not { } meeting)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Filter = "Canonical JSON|*.json|Plain text|*.txt|SubRip|*.srt|WebVTT|*.vtt",
            FileName = meeting.SuggestTranscriptFileName(TranscriptExportFormat.Text),
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        TranscriptExportFormat format = dialog.FilterIndex switch
        {
            1 => TranscriptExportFormat.Json,
            3 => TranscriptExportFormat.Srt,
            4 => TranscriptExportFormat.Vtt,
            _ => TranscriptExportFormat.Text,
        };

        // The dialog already asked about replacing, so overwrite is the user's answer rather
        // than a default.
        Report(meeting.ExportTranscript(format, dialog.FileName, overwrite: true));
    }

    private void OnExportSummary(object sender, RoutedEventArgs e)
    {
        if (_library.OpenMeeting is not { } meeting)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Filter = "Markdown|*.md|Plain text|*.txt|Canonical JSON|*.json",
            FileName = meeting.SuggestSummaryFileName(SummaryExportFormat.Markdown),
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SummaryExportFormat format = dialog.FilterIndex switch
        {
            2 => SummaryExportFormat.Text,
            3 => SummaryExportFormat.Json,
            _ => SummaryExportFormat.Markdown,
        };

        Report(meeting.ExportSummary(format, dialog.FileName, overwrite: true));
    }

    private void Report(ExportResult result) => System.Windows.MessageBox.Show(
        this,
        result.Succeeded ? "Exported to " + result.Path : result.Message,
        "EchoForge",
        MessageBoxButton.OK,
        result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);

    private void OnSpeakerNameCommitted(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || sender is not System.Windows.Controls.TextBox box || box.Tag is not string speakerId)
        {
            return;
        }

        _library.OpenMeeting?.Rename(speakerId, box.Text);
        e.Handled = true;
    }

    private void OnResetSpeaker(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpeakerRow row })
        {
            _library.OpenMeeting?.Rename(row.SpeakerId, null);
        }
    }
}
