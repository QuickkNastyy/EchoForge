using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Library;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.App.Library;

/// <summary>One row in the meeting list.</summary>
public sealed record MeetingRow(LibraryEntry Entry)
{
    public string SessionId => Entry.SessionId;

    public string Title => Entry.Title;

    public string When => Entry.CreatedUtc.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);

    public string Length => Entry.Duration >= TimeSpan.FromHours(1)
        ? Entry.Duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : Entry.Duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public string Status => Entry.NeedsAttention ? "Needs attention" : Entry.State.ToString();

    public bool HasTranscript => Entry.HasTranscript;

    public bool HasSummary => Entry.HasSummary;

    public bool SummaryIsStale => Entry.SummaryIsStale;

    public bool NeedsAttention => Entry.NeedsAttention;

    public string? AttentionReason => Entry.AttentionReason;
}

/// <summary>One search result row.</summary>
public sealed record SearchRow(SearchHit Hit, string MeetingTitle)
{
    public string Where => Hit.Kind switch
    {
        SearchHitKind.TranscriptSegment => Hit.SpeakerName is { Length: > 0 } speaker
            ? speaker + " · " + (Hit.StartSeconds ?? 0).ToTimestamp()
            : "Transcript",
        SearchHitKind.SummaryOverview => "Summary · overview",
        SearchHitKind.SummaryKeyPoint => "Summary · key point",
        SearchHitKind.SummaryDecision => "Summary · decision",
        SearchHitKind.SummaryAction => "Summary · action",
        SearchHitKind.SummaryQuestion => "Summary · question",
        SearchHitKind.SummaryRisk => "Summary · risk",
        SearchHitKind.SummaryBlocker => "Summary · blocker",
        _ => "Result",
    };

    public string Text => Hit.Text;
}

/// <summary>
/// The meeting library: what exists, and how to find something in it.
///
/// <para>
/// Everything slow happens off the UI thread. Rebuilding an index over a few hundred meetings
/// reads every transcript on disk, and this is the thread that paints the recording indicator —
/// a library that froze the app while it caught up would be worse than one that took longer.
/// </para>
/// </summary>
public sealed class LibraryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SqliteLibraryIndex _index;
    private readonly LibraryProjection _projection;
    private readonly FileTranscriptionStore _transcripts;
    private readonly FileSummaryStore _summaries;
    private readonly FileSpeakerAliasStore _aliases;
    private readonly OperationGate _gate = new();

    private MeetingRow? _selected;
    private MeetingViewModel? _open;
    private string _searchText = string.Empty;
    private string? _status;
    private bool _busy;
    private bool _disposed;

    public LibraryViewModel(
        SqliteLibraryIndex index,
        LibraryProjection projection,
        FileTranscriptionStore transcripts,
        FileSummaryStore summaries,
        FileSpeakerAliasStore aliases)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _summaries = summaries ?? throw new ArgumentNullException(nameof(summaries));
        _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));

        SearchCommand = new AsyncRelayCommand(SearchAsync, _gate, () => !_busy, m => Status = m);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, _gate, () => !_busy, m => Status = m);
        RebuildCommand = new AsyncRelayCommand(RebuildAsync, _gate, () => !_busy, m => Status = m);
        ClearSearchCommand = new RelayCommand(ClearSearch, () => Results.Count > 0 || SearchText.Length > 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MeetingRow> Meetings { get; } = [];

    public ObservableCollection<SearchRow> Results { get; } = [];

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand RebuildCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value ?? string.Empty; Changed(); }
    }

    public string? Status
    {
        get => _status;
        private set { _status = value; Changed(); Changed(nameof(HasStatus)); }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public bool IsBusy
    {
        get => _busy;
        private set { _busy = value; Changed(); RaiseCommands(); }
    }

    public bool HasResults => Results.Count > 0;

    public MeetingRow? SelectedMeeting
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            OpenMeeting = value is null
                ? null
                : new MeetingViewModel(value.Entry, _transcripts, _summaries, _aliases);

            Changed();
        }
    }

    public MeetingViewModel? OpenMeeting
    {
        get => _open;
        private set { _open = value; Changed(); Changed(nameof(HasOpenMeeting)); }
    }

    public bool HasOpenMeeting => _open is not null;

    // -- commands ------------------------------------------------------------------------------

    /// <summary>Opens the index, rebuilding it if that is what opening requires.</summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;

        try
        {
            IndexHealth health = await Task.Run(() => _index.EnsureReadyAsync()).ConfigureAwait(true);

            Status = health switch
            {
                { Usable: false } => "The search index could not be built. Your recordings are unaffected; try rebuilding.",
                { Rebuilt: true } => "The search index was rebuilt from your recordings.",
                _ => null,
            };

            LoadMeetings();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;

        try
        {
            await Task.Run(() => _index.EnsureReadyAsync()).ConfigureAwait(true);
            LoadMeetings();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RebuildAsync()
    {
        IsBusy = true;
        Status = "Rebuilding the search index…";

        try
        {
            IndexHealth health = await Task.Run(() => _index.RebuildAsync()).ConfigureAwait(true);

            Status = health.Usable
                ? "The search index was rebuilt."
                : "The search index could not be rebuilt. Your recordings are unaffected.";

            LoadMeetings();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SearchAsync()
    {
        string text = SearchText;

        if (string.IsNullOrWhiteSpace(text))
        {
            ClearSearch();
            return;
        }

        IsBusy = true;

        try
        {
            // Off the UI thread: a search over a large library reads the index, and this thread
            // has a recording indicator to paint.
            SearchResults found = await Task
                .Run(() => _index.Search(new SearchQuery { Text = text, Limit = 300 }))
                .ConfigureAwait(true);

            Dictionary<string, string> titles = Meetings.ToDictionary(m => m.SessionId, m => m.Title, StringComparer.Ordinal);

            Results.Clear();
            foreach (SearchHit hit in found.Hits)
            {
                Results.Add(new SearchRow(hit, titles.GetValueOrDefault(hit.SessionId, hit.SessionId)));
            }

            // Emptiness and inability to answer are different statements, and a user acts on
            // them differently: one means try other words, the other means rebuild.
            Status = found.IndexUnavailable || found.Notice is not null
                ? found.Notice
                : found.Hits.Count == 0
                    ? "Nothing matched that."
                    : string.Create(CultureInfo.CurrentCulture, $"{found.Hits.Count} result(s).");

            Changed(nameof(HasResults));
            ClearSearchCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearSearch()
    {
        SearchText = string.Empty;
        Results.Clear();
        Status = null;
        Changed(nameof(HasResults));
        ClearSearchCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Opens the meeting a search result came from, and points at the hit.</summary>
    public TranscriptLine? OpenResult(SearchRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        MeetingRow? meeting = Meetings.FirstOrDefault(m => string.Equals(m.SessionId, row.Hit.SessionId, StringComparison.Ordinal));
        if (meeting is null)
        {
            return null;
        }

        SelectedMeeting = meeting;

        if (row.Hit.TranscriptRevision is { } revision && row.Hit.SegmentId is { } segmentId
            && row.Hit.Kind == SearchHitKind.TranscriptSegment)
        {
            return OpenMeeting?.LocateSegment(revision, segmentId);
        }

        return null;
    }

    /// <summary>Re-reads one meeting from disk and re-indexes it.</summary>
    public async Task RefreshMeetingAsync(string sessionId)
    {
        await _index.UpdateAsync(sessionId).ConfigureAwait(true);

        LibraryDocument? document = _projection.Build(sessionId);
        if (document is null)
        {
            return;
        }

        for (int i = 0; i < Meetings.Count; i++)
        {
            if (string.Equals(Meetings[i].SessionId, sessionId, StringComparison.Ordinal))
            {
                Meetings[i] = new MeetingRow(document.Entry);
                break;
            }
        }

        if (OpenMeeting is { } open && string.Equals(open.SessionId, sessionId, StringComparison.Ordinal))
        {
            open.Reload(document.Entry);
        }
    }

    private void LoadMeetings()
    {
        string? keep = _selected?.SessionId;

        Meetings.Clear();
        foreach (LibraryEntry entry in _index.Meetings())
        {
            Meetings.Add(new MeetingRow(entry));
        }

        if (keep is not null)
        {
            _selected = Meetings.FirstOrDefault(m => string.Equals(m.SessionId, keep, StringComparison.Ordinal));
            Changed(nameof(SelectedMeeting));
        }
    }

    private void RaiseCommands()
    {
        SearchCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        RebuildCommand.RaiseCanExecuteChanged();
    }

    private static void Dispatch(Action action)
    {
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void Changed([CallerMemberName] string? name = null) =>
        Dispatch(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _index.Dispose();
    }
}
