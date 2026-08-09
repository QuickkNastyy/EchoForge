using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EchoForge.Contracts.Library;
using EchoForge.Core.Exports;

namespace EchoForge.App.Library;

/// <summary>
/// The recordings page: the list and one opened meeting, side by side in the main window.
///
/// <para>
/// Deliberately thin, like the window it replaced. Everything that decides anything lives in the
/// view models; this moves clicks to them, scrolls a list, turns a click on the timeline into a
/// seek, and owns the file dialogs — which are the one thing a view model should not.
/// </para>
/// </summary>
public partial class RecordingsPage : System.Windows.Controls.UserControl
{
    private LibraryViewModel? _library;

    public RecordingsPage()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        BriefView.EvidenceRequested += OnEvidenceRequested;
        TranscriptViewPane.LineActivated += OnTranscriptLineActivated;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _library = e.NewValue as LibraryViewModel;

        // Group the recordings by their local day. The default view is what the list already binds
        // to, so grouping it here needs no second collection.
        if (_library is not null &&
            CollectionViewSource.GetDefaultView(_library.Meetings) is { } view &&
            view.GroupDescriptions.Count == 0)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MeetingRow.DayLabel)));
        }
    }

    /// <summary>Opens the meeting a result came from and scrolls to the line it matched.</summary>
    private void OnResultActivated(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_library is null || ResultsList.SelectedItem is not SearchRow row)
        {
            return;
        }

        TranscriptLine? line = _library.OpenResult(row);
        ShowTranscript(line);
    }

    /// <summary>
    /// Following a claim to its source.
    ///
    /// <para>
    /// It opens the exact transcript revision the brief cites — never the selected one — scrolls to
    /// the segment, highlights it, and moves the audio to that moment. The audio moves either way:
    /// a citation whose transcript version is gone can still be heard at the time stored with it,
    /// and the transport says the position is approximate.
    /// </para>
    /// </summary>
    private void OnEvidenceRequested(object? sender, EvidenceLocation location)
    {
        if (_library?.OpenMeeting is not { } meeting)
        {
            return;
        }

        MeetingViewModel.EvidenceFollow follow = meeting.FollowEvidence(location);

        // Marked before scrolling, so the line a reader arrives at is already wearing the accent.
        meeting.FocusEvidence(follow.Line is null ? null : location);
        meeting.Playback?.Cue(follow.Request);

        ShowTranscript(follow.Line);
    }

    /// <summary>The audit view's own selection follows the same path a brief citation does.</summary>
    private void OnSummarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_library?.OpenMeeting is not { } meeting)
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

        OnEvidenceRequested(sender, summary.Evidence[0]);
    }

    private void OnTranscriptLineActivated(object? sender, TranscriptLine line) =>
        _library?.OpenMeeting?.Playback?.Cue(_library.OpenMeeting.RequestPlayback(line));

    /// <summary>Switches to the transcript tab and reveals a line there.</summary>
    private void ShowTranscript(TranscriptLine? line)
    {
        if (line is null)
        {
            return;
        }

        MeetingTabs.SelectedIndex = 1;
        TranscriptViewPane.Reveal(line);
    }

    /// <summary>A click on the timeline ribbon seeks to that fraction of the recording.</summary>
    private void OnTimelineRibbonClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_library?.OpenMeeting is not { } meeting ||
            meeting.Playback is not { } playback ||
            sender is not IInputElement element)
        {
            return;
        }

        double width = TimelineRibbon.ActualWidth;
        double duration = playback.DurationSeconds > 0 ? playback.DurationSeconds : meeting.TimelineSeconds;
        if (width <= 0 || duration <= 0)
        {
            return;
        }

        double x = e.GetPosition(element).X;
        double fraction = Math.Clamp(x / width, 0, 1);
        playback.SeekTo(fraction * duration);
    }

    /// <summary>
    /// Everything secondary, in one menu.
    ///
    /// <para>
    /// Re-transcribing, regenerating the brief, exporting, copying and deleting all live here and
    /// only here. They used to be five buttons across the top of a window, which made reprocessing
    /// look like the normal thing to do with a meeting rather than the exception.
    /// </para>
    /// </summary>
    private void OnMoreActions(object sender, RoutedEventArgs e)
    {
        if (_library is null || sender is not FrameworkElement anchor)
        {
            return;
        }

        // Filled first, opened last. A ContextMenu opened in its own initialiser measures itself
        // while it is still empty, and the items appended afterwards can arrive too late to be
        // laid out - which presents as a menu that opens onto nothing.
        System.Windows.Controls.ContextMenu menu = new()
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Background = (System.Windows.Media.Brush)FindResource("Raised"),
            Foreground = (System.Windows.Media.Brush)FindResource("Ink"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
        };

        menu.Items.Add(Item("Re-transcribe only", () => _library.TranscribeAgainCommand.Execute(null),
            _library.TranscribeAgainCommand.CanExecute(null)));
        menu.Items.Add(Item("Regenerate the brief only", () => _library.SummarizeAgainCommand.Execute(null),
            _library.SummarizeAgainCommand.CanExecute(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Export transcript…", ExportTranscript, true));
        menu.Items.Add(Item("Export brief…", ExportSummary, true));
        menu.Items.Add(Item("Copy for ChatGPT / Claude…", CopyForAssistant, true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete meeting…", () => _library.DeleteMeetingCommand.Execute(null),
            _library.DeleteMeetingCommand.CanExecute(null)));

        menu.IsOpen = true;

        static System.Windows.Controls.MenuItem Item(string header, Action action, bool enabled)
        {
            System.Windows.Controls.MenuItem item = new() { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void ExportTranscript()
    {
        if (_library?.OpenMeeting is not { } meeting)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Filter = "Canonical JSON|*.json|Plain text|*.txt|SubRip|*.srt|WebVTT|*.vtt",
            FileName = meeting.SuggestTranscriptFileName(TranscriptExportFormat.Text),
            OverwritePrompt = true,
            FilterIndex = 2,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
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

    private void ExportSummary()
    {
        if (_library?.OpenMeeting is not { } meeting)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Filter = "Markdown|*.md|Plain text|*.txt|Canonical JSON|*.json",
            FileName = meeting.SuggestSummaryFileName(SummaryExportFormat.Markdown),
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
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

    private void CopyForAssistant()
    {
        if (_library?.OpenMeeting is not { } meeting)
        {
            return;
        }

        if (!meeting.HasTranscript)
        {
            System.Windows.MessageBox.Show(
                Window.GetWindow(this), "There is no transcript to copy yet.", "EchoForge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ManualHandoffViewModel model = new(
            meeting.BuildManualHandoff, new WpfClipboardWriter(), meeting.SuggestManualHandoffFileName());

        ManualHandoffWindow window = new(model) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private void OnRecordingRowRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_library is null || sender is not ListBoxItem { DataContext: MeetingRow row } item)
        {
            return;
        }

        item.IsSelected = true;

        System.Windows.Controls.ContextMenu menu = new()
        {
            PlacementTarget = item,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            Background = (System.Windows.Media.Brush)FindResource("Raised"),
            Foreground = (System.Windows.Media.Brush)FindResource("Ink"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
        };

        System.Windows.Controls.MenuItem rename = new() { Header = "Rename recording…", DataContext = row };
        rename.Click += OnRenameRecording;
        menu.Items.Add(rename);

        System.Windows.Controls.MenuItem copy = new() { Header = "Copy transcript", DataContext = row, IsEnabled = row.HasTranscript };
        copy.Click += OnCopyRecordingTranscript;
        menu.Items.Add(copy);

        menu.Items.Add(new Separator());

        System.Windows.Controls.MenuItem delete = new() { Header = "Delete recording…", DataContext = row };
        delete.Click += OnDeleteRecording;
        menu.Items.Add(delete);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void OnRenameRecording(object sender, RoutedEventArgs e)
    {
        if (_library is null || sender is not FrameworkElement { DataContext: MeetingRow row } ||
            Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        string? name = TextPromptWindow.Ask(
            owner,
            "Rename recording",
            "Recording name",
            row.Title);

        if (name is null)
        {
            return;
        }

        await _library.RenameMeetingAsync(row, name).ConfigureAwait(true);
    }

    /// <summary>
    /// The pencil beside the open meeting's title. Same rename as the row's context menu, reached
    /// from where the name is actually read.
    /// </summary>
    private async void OnRenameOpenMeeting(object sender, RoutedEventArgs e)
    {
        if (_library is not { SelectedMeeting: { } row } || Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        string? name = TextPromptWindow.Ask(
            owner,
            "Rename recording",
            "Recording name",
            row.Title);

        if (name is null)
        {
            return;
        }

        await _library.RenameMeetingAsync(row, name).ConfigureAwait(true);
    }

    private void OnCopyRecordingTranscript(object sender, RoutedEventArgs e)
    {
        if (_library is null || sender is not FrameworkElement { DataContext: MeetingRow row })
        {
            return;
        }

        string? transcript = _library.BuildTranscriptText(row);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        System.Windows.Clipboard.SetText(transcript);
    }

    private void OnDeleteRecording(object sender, RoutedEventArgs e)
    {
        if (_library is null || sender is not FrameworkElement { DataContext: MeetingRow row })
        {
            return;
        }

        // The existing deletion service owns all safety checks and the Recycle Bin confirmation.
        _library.SelectedMeeting = row;
        if (_library.DeleteMeetingCommand.CanExecute(null))
        {
            _library.DeleteMeetingCommand.Execute(null);
        }
    }

    private void Report(ExportResult result) => System.Windows.MessageBox.Show(
        Window.GetWindow(this),
        result.Succeeded ? "Exported to " + result.Path : result.Message,
        "EchoForge",
        MessageBoxButton.OK,
        result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);


}
