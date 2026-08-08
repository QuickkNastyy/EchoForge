using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EchoForge.Contracts.Library;
using EchoForge.Core.Exports;

namespace EchoForge.App.Library;

/// <summary>
/// The recordings window: a full-width, day-grouped list that opens one recording into a workspace
/// where its summary and transcript sit side by side.
///
/// <para>
/// Deliberately thin. Everything that decides anything lives in the view models; this file moves
/// clicks to them, scrolls a list, and turns a click on the timeline into a seek.
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

        // Group the recordings by their local day. The default view is what the list already binds
        // to, so grouping it here needs no second collection.
        if (CollectionViewSource.GetDefaultView(library.Meetings) is { } view && view.GroupDescriptions.Count == 0)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MeetingRow.DayLabel)));
        }

        // The view model outlives this window so reopening the library is instant. The transport
        // must not: closing the window releases the audio device and the prepared file.
        Closed += (_, _) => _library.CloseOpenMeeting();
    }

    /// <summary>Returns from a recording to the list.</summary>
    private void OnBackToList(object sender, RoutedEventArgs e) => _library.CloseOpenMeeting();

    /// <summary>
    /// The rail's Record destination. The recorder is its own window here rather than a page in
    /// this one, so the destination brings it forward — including from minimised — instead of
    /// building a second view over the same controller.
    /// </summary>
    private void OnGoToRecorder(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current?.MainWindow is not { } recorder)
        {
            return;
        }

        recorder.Show();
        if (recorder.WindowState == WindowState.Minimized)
        {
            recorder.WindowState = WindowState.Normal;
        }

        recorder.Activate();
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
    /// Selecting a summary claim follows its evidence: it opens the exact transcript revision the
    /// summary cites — never the selected one — scrolls the transcript to the segment, highlights it,
    /// and moves the audio to that moment. The two panes are already side by side, so there is no tab
    /// to switch.
    /// </summary>
    private void OnSummarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_library.OpenMeeting is not { } meeting)
        {
            return;
        }

        // Deselecting puts the transcript back the way it was. A reader who clicked away should
        // not be left with two thirds of the transcript dimmed and no way to see why.
        if (SummaryList.SelectedItem is not SummaryLine summary || summary.Evidence.Count == 0)
        {
            meeting.FocusEvidence(null);
            return;
        }

        EvidenceLocation first = summary.Evidence[0];
        MeetingViewModel.EvidenceFollow follow = meeting.FollowEvidence(first);

        // Mark it on the transcript and on the ribbon before scrolling, so the line a reader
        // arrives at is already the one wearing the accent.
        meeting.FocusEvidence(follow.Line is null ? null : first);

        // The audio moves either way. A citation whose transcript version is gone can still be heard
        // at the time stored with it, and the transport says the position is approximate.
        meeting.Playback?.Cue(follow.Request);

        Reveal(follow.Line);
    }

    /// <summary>Double-clicking a transcript line plays from that moment.</summary>
    private void OnTranscriptLineActivated(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TranscriptList.SelectedItem is not TranscriptLine line || _library.OpenMeeting is not { } meeting)
        {
            return;
        }

        meeting.Playback?.Cue(meeting.RequestPlayback(line));
    }

    /// <summary>A click on the timeline ribbon seeks to that fraction of the recording.</summary>
    private void OnTimelineRibbonClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_library.OpenMeeting?.Playback is not { } playback || sender is not IInputElement element)
        {
            return;
        }

        double width = TimelineRibbon.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        double x = e.GetPosition(element).X;
        double fraction = Math.Clamp(x / width, 0, 1);
        playback.SeekTo(fraction * playback.DurationSeconds);
    }

    /// <summary>Finds the first transcript line that contains the query, and reveals it.</summary>
    private void OnTranscriptSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box || box.Text.Trim() is not { Length: > 0 } query || _library.OpenMeeting is null)
        {
            return;
        }

        TranscriptLine? match = null;
        foreach (object item in TranscriptList.Items)
        {
            if (item is TranscriptLine line && line.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                match = line;
                break;
            }
        }

        Reveal(match);
    }

    private void Reveal(TranscriptLine? line)
    {
        if (line is null)
        {
            return;
        }

        TranscriptList.SelectedItem = line;
        TranscriptList.ScrollIntoView(line);
    }

    private async void OnRegenerateSummary(object sender, RoutedEventArgs e)
    {
        if (_library.OpenMeeting is not { } meeting || _regenerate is null)
        {
            System.Windows.MessageBox.Show(
                this,
                "Use Generate summary above, on the recording that is open.",
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

        Report(meeting.ExportTranscript(format, dialog.FileName, overwrite: true));
    }

    private void OnCopyForAssistant(object sender, RoutedEventArgs e)
    {
        if (_library.OpenMeeting is not { } meeting)
        {
            return;
        }

        if (!meeting.HasTranscript)
        {
            System.Windows.MessageBox.Show(
                this, "There is no transcript to copy yet.", "EchoForge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ManualHandoffViewModel model = new(
            meeting.BuildManualHandoff, new WpfClipboardWriter(), meeting.SuggestManualHandoffFileName());

        ManualHandoffWindow window = new(model) { Owner = this };
        window.ShowDialog();
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
