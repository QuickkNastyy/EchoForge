using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Playback;
using EchoForge.Contracts.Sessions;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Playback;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.App.Library;

/// <summary>
/// Asks the user to confirm a deletion.
///
/// <para>
/// An interface so the decision to destroy something is testable without a window, and so there is
/// exactly one place in the application that can answer yes.
/// </para>
/// </summary>
public interface IDeleteConfirmation
{
    bool Confirm(DeletionEligibility eligibility);
}

/// <summary>
/// The optional halves of the library: playing, reprocessing, deleting, and keeping the index up
/// to date.
///
/// <para>
/// Optional because each of them depends on something the application may not have — an output
/// device, a Python runtime, a shell. A machine without them still gets a library it can read,
/// search and export from, which is the same rule transcription itself follows.
/// </para>
/// </summary>
public sealed record LibraryServices
{
    public PlaybackPreparer? Playback { get; init; }

    public Func<IPlaybackDevice>? Devices { get; init; }

    public IMeetingReprocessor? Reprocessor { get; init; }

    public SessionDeletionService? Deletion { get; init; }

    public IDeleteConfirmation? Confirmation { get; init; }

    public LibraryIndexMaintainer? Index { get; init; }

    public FileMeetingTitleStore? Titles { get; init; }
}

/// <summary>One row in the meeting list.</summary>
public sealed record MeetingRow(LibraryEntry Entry)
{
    public string SessionId => Entry.SessionId;

    public string Title => Entry.Title;

    public string When => (Entry.StartedUtc ?? Entry.CreatedUtc).ToLocalTime()
        .ToString("ddd, MMM d, yyyy · h:mm tt", CultureInfo.CurrentCulture);

    public string Length => FormatDuration(Entry.Duration);

    public string DurationLabel => Length;

    private static string FormatDuration(TimeSpan duration)
    {
        int seconds = Math.Max(0, (int)Math.Round(duration.TotalSeconds));
        if (seconds < 60)
        {
            return $"{seconds} sec";
        }

        int minutes = seconds / 60;
        int remainder = seconds % 60;
        if (minutes < 60)
        {
            return remainder == 0 ? $"{minutes} min" : $"{minutes} min {remainder} sec";
        }

        int hours = minutes / 60;
        int minuteRemainder = minutes % 60;
        return minuteRemainder == 0 ? $"{hours} hr" : $"{hours} hr {minuteRemainder} min";
    }

    public string Status => Entry.NeedsAttention ? "Needs attention" : Entry.State.ToString();

    /// <summary>The local calendar day, as a heading: Today, Yesterday, or the weekday and date.</summary>
    public string DayLabel
    {
        get
        {
            DateTime local = Entry.CreatedUtc.ToLocalTime().DateTime;
            DateTime day = local.Date;
            DateTime today = DateTime.Today;

            if (day == today) { return "Today"; }
            if (day == today.AddDays(-1)) { return "Yesterday"; }
            return local.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>The compact row's second line.</summary>
    public string Sub => DurationLabel;

    /// <summary>A short status word for the row's chip.</summary>
    public string Chip => Entry.NeedsAttention ? "Needs attention"
        : Entry.HasSummary ? "Ready"
        : Entry.HasTranscript ? "Transcribed"
        : Entry.State == SessionState.Recorded ? "Recorded"
        : Entry.State.ToString();

    public bool ChipIsWarn => Entry.NeedsAttention;

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
    private readonly LibraryServices _services;
    private readonly OperationGate _gate = new();

    // A small cache of the two-lane conversation shapes the library rows draw, keyed by session.
    // Derived from the transcript, bounded per meeting, and dropped when a meeting is re-read.
    private readonly Dictionary<string, ConversationShape> _shapes = new(StringComparer.Ordinal);

    private MeetingRow? _selected;
    private MeetingViewModel? _open;
    private string _searchText = string.Empty;
    private string? _status;
    private DateTime? _from;
    private DateTime? _to;
    private bool _busy;
    private bool _initialised;
    private bool _disposed;

    public LibraryViewModel(
        SqliteLibraryIndex index,
        LibraryProjection projection,
        FileTranscriptionStore transcripts,
        FileSummaryStore summaries,
        FileSpeakerAliasStore aliases,
        LibraryServices? services = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _summaries = summaries ?? throw new ArgumentNullException(nameof(summaries));
        _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
        _services = services ?? new LibraryServices();

        SearchCommand = new AsyncRelayCommand(SearchAsync, _gate, () => !_busy, m => Status = m);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, _gate, () => !_busy, m => Status = m);
        RebuildCommand = new AsyncRelayCommand(RebuildAsync, _gate, () => !_busy, m => Status = m);
        ClearSearchCommand = new RelayCommand(ClearSearch, () => Results.Count > 0 || SearchText.Length > 0);
        ClearDatesCommand = new RelayCommand(ClearDates, () => _from is not null || _to is not null);

        TranscribeAgainCommand = new AsyncRelayCommand(
            () => ReprocessAsync(transcribe: true), _gate, () => CanReprocess(transcribe: true), m => Status = m);

        SummarizeAgainCommand = new AsyncRelayCommand(
            () => ReprocessAsync(transcribe: false), _gate, () => CanReprocess(transcribe: false), m => Status = m);

        ProcessMeetingCommand = new AsyncRelayCommand(
            ProcessAsync, _gate, () => CanProcess, m => Status = m);

        CancelProcessingCommand = new RelayCommand(CancelProcessing, () => _processing is not null && !_cancellationRequested);

        DeleteMeetingCommand = new AsyncRelayCommand(
            DeleteAsync, _gate, () => _services.Deletion is not null && _selected is not null, m => Status = m);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MeetingRow> Meetings { get; } = [];

    public ObservableCollection<SearchRow> Results { get; } = [];

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand RebuildCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    public RelayCommand ClearDatesCommand { get; }

    public AsyncRelayCommand TranscribeAgainCommand { get; }

    public AsyncRelayCommand SummarizeAgainCommand { get; }

    public AsyncRelayCommand DeleteMeetingCommand { get; }

    /// <summary>Recording to brief, as one action, using the defaults chosen in Settings.</summary>
    public AsyncRelayCommand ProcessMeetingCommand { get; }

    public RelayCommand CancelProcessingCommand { get; }

    private CancellationTokenSource? _processing;
    private bool _cancellationRequested;
    private string _processingStage = string.Empty;

    public bool CanProcess =>
        !_busy && _selected is not null && _services.Reprocessor is { CanTranscribe: true };

    public bool IsProcessing => _processing is not null;

    /// <summary>
    /// What is happening, in words rather than in stage names.
    ///
    /// <para>
    /// An hour of audio takes a long time to read, and a bar that says nothing for twenty minutes
    /// is indistinguishable from one that has stopped. The internal chunk arithmetic stays where it
    /// belongs — diagnostics — and this says which of the few things a person cares about is under
    /// way.
    /// </para>
    /// </summary>
    public string ProcessingStage => _processingStage;

    /// <summary>
    /// What the primary action on an unprocessed meeting says.
    ///
    /// <para>
    /// One button. It used to be two, in an order the user had to know, and a meeting could sit
    /// transcribed and unsummarised because nobody realised there was a second step.
    /// </para>
    /// </summary>
    public string ProcessActionLabel => _open?.HasSummary == true
        ? "Reprocess meeting"
        : _open?.HasTranscript == true ? "Finish processing" : "Process meeting";

    /// <summary>
    /// Loads the miniature conversation shape for one meeting, off the UI thread, and caches it.
    ///
    /// <para>
    /// Derived from the meeting's selected transcript, not its audio, so it is cheap and reads no WAV.
    /// A meeting with no transcript yields an empty shape and a quiet ribbon. The library rows pass
    /// this in as an inherited provider, so a virtualised list only loads the shapes it actually shows.
    /// </para>
    /// </summary>
    public Func<string, Task<ConversationShape>> LoadShapeProvider => LoadShapeAsync;

    private async Task<ConversationShape> LoadShapeAsync(string sessionId)
    {
        if (_shapes.TryGetValue(sessionId, out ConversationShape? cached))
        {
            return cached;
        }

        ConversationShape shape = await Task.Run(() =>
        {
            try
            {
                Contracts.Processing.TranscriptionState state = _transcripts.Read(sessionId);
                if (state.SelectedRevision is not { } revision)
                {
                    return ConversationShape.Empty;
                }

                Contracts.Transcripts.TranscriptDocument? transcript = _transcripts.ReadTranscript(sessionId, revision);
                return transcript is null ? ConversationShape.Empty : ConversationShape.FromTranscript(transcript);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return ConversationShape.Empty;
            }
        }).ConfigureAwait(true);

        _shapes[sessionId] = shape;
        return shape;
    }

    public bool CanReprocessHere => _services.Reprocessor is not null;

    public bool CanDeleteHere => _services.Deletion is not null;

    /// <summary>
    /// What the transcription action should read for the open meeting: the first run is
    /// <c>Transcribe</c>, and only a meeting that already has a transcript offers to do it
    /// <c>again</c>. Calling a first-ever transcription "Transcribe again" is exactly the kind of
    /// small lie that makes a workflow feel untrustworthy.
    /// </summary>
    public string TranscribeActionLabel => _open?.HasTranscript == true ? "Transcribe again" : "Transcribe";

    /// <summary>The same rule for the summary: <c>Generate summary</c>, then <c>… again</c>.</summary>
    public string SummarizeActionLabel => _open?.HasSummary == true ? "Generate summary again" : "Generate summary";

    /// <summary>
    /// Re-evaluates whether reprocessing is possible right now.
    ///
    /// <para>
    /// The library is composed before it is known whether this machine can process anything, and a
    /// runtime installed from Setup while the window is open must light the actions up without a
    /// restart. This is how the composition root tells an already-open library that the answer has
    /// changed.
    /// </para>
    /// </summary>
    public void ProcessingAvailabilityChanged() => Dispatch(() =>
    {
        Changed(nameof(CanReprocessHere));
        RaiseCommands();
    });

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value ?? string.Empty; Changed(); }
    }

    // -- date range ----------------------------------------------------------------------------

    /// <summary>
    /// The first day to include, as a local calendar date.
    ///
    /// <para>
    /// A date, not an instant. Meetings are stored in UTC and remembered in local time, and the
    /// conversion between the two is done once, in <see cref="LibraryFilter.ForLocalDates"/> —
    /// which is what stops an evening meeting being filed under the following day.
    /// </para>
    /// </summary>
    public DateTime? FromDate
    {
        get => _from;
        set { _from = value?.Date; Changed(); ApplyDates(); }
    }

    public DateTime? ToDate
    {
        get => _to;
        set { _to = value?.Date; Changed(); ApplyDates(); }
    }

    public bool HasDateFilter => _from is not null || _to is not null;

    /// <summary>The filter the list and every search are read through.</summary>
    public LibraryFilter Filter => LibraryFilter.ForLocalDates(
        _from is { } from ? DateOnly.FromDateTime(from) : null,
        _to is { } to ? DateOnly.FromDateTime(to) : null);

    /// <summary>
    /// Re-reads the list through the new range.
    ///
    /// <para>
    /// A query, never a rebuild. The index already knows when every meeting was; discarding it to
    /// ask a narrower question would turn picking a date into a full re-read of every transcript
    /// on disk.
    /// </para>
    /// </summary>
    private void ApplyDates()
    {
        Changed(nameof(HasDateFilter));
        Changed(nameof(Filter));
        ClearDatesCommand.RaiseCanExecuteChanged();

        LoadMeetings();

        if (Filter.IsReversed)
        {
            // Swapping them silently would show results for a range nobody asked for.
            Status = "The first date is after the last one, so nothing can match. Swap them, or clear the dates.";
        }
        else if (Meetings.Count == 0 && HasDateFilter)
        {
            Status = "No meetings in that date range.";
        }
        else if (Status is not null && Status.StartsWith("No meetings in that date range", StringComparison.Ordinal))
        {
            Status = null;
        }
    }

    private void ClearDates()
    {
        _from = null;
        _to = null;
        Changed(nameof(FromDate));
        Changed(nameof(ToDate));
        ApplyDates();
        Status = null;
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
            OpenMeeting = value is null ? null : Open(value.Entry);

            Changed();
            RaiseCommands();
        }
    }

    /// <summary>
    /// Opens a meeting, closing whatever was open first.
    ///
    /// <para>
    /// The closing is the important half. A meeting owns a transport, and a transport owns an
    /// audio device and an open file; leaving the previous one alive would claim an output for a
    /// meeting nobody is looking at and hold the derivative against the rebuild a reprocess needs.
    /// One opened meeting, one playback session.
    /// </para>
    /// </summary>
    private MeetingViewModel Open(LibraryEntry entry)
    {
        PlaybackViewModel? playback = _services is { Playback: { } preparer, Devices: { } devices }
            ? new PlaybackViewModel(entry.SessionId, preparer, devices)
            : null;

        MeetingViewModel meeting = new(entry, _transcripts, _summaries, _aliases, playback);
        meeting.CanonicalChanged += OnMeetingChanged;

        // Building the aligned audio reads every chunk the first time, so it is started rather
        // than awaited: the transcript is readable while the audio catches up.
        _ = playback?.PrepareAsync();

        return meeting;
    }

    private void OnMeetingChanged(object? sender, string sessionId) => _services.Index?.Invalidate(sessionId);

    /// <summary>
    /// Closes whatever meeting is open, and with it the audio device it held.
    ///
    /// <para>
    /// Called when the library window closes. The view model outlives the window so reopening it
    /// is instant, but an output device claimed for a window nobody can see is not something to
    /// hold onto for that.
    /// </para>
    /// </summary>
    public void CloseOpenMeeting() => SelectedMeeting = null;

    /// <summary>
    /// Opens one meeting by ID, loading the index first if this is the first look at it.
    ///
    /// <para>
    /// What the recorder calls when a recording has just finished: the meeting the user made two
    /// seconds ago is the one they want, and finding it in a list they have not opened yet is work
    /// they should not have to do.
    /// </para>
    /// </summary>
    public async Task OpenAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (Meetings.Count == 0)
        {
            await InitializeAsync().ConfigureAwait(true);
        }

        if (Meetings.FirstOrDefault(row => string.Equals(row.SessionId, sessionId, StringComparison.Ordinal))
            is not { } wanted)
        {
            await RefreshMeetingAsync(sessionId).ConfigureAwait(true);
            wanted = Meetings.FirstOrDefault(row => string.Equals(row.SessionId, sessionId, StringComparison.Ordinal))!;
        }

        if (wanted is not null)
        {
            SelectedMeeting = wanted;
        }
    }

    public MeetingViewModel? OpenMeeting
    {
        get => _open;
        private set
        {
            MeetingViewModel? previous = _open;
            _open = value;

            if (previous is not null && !ReferenceEquals(previous, value))
            {
                previous.CanonicalChanged -= OnMeetingChanged;
                previous.Dispose();
            }

            Changed();
            Changed(nameof(HasOpenMeeting));

            // The action words follow the newly opened meeting's state, not the last one's.
            Changed(nameof(TranscribeActionLabel));
            Changed(nameof(SummarizeActionLabel));
            Changed(nameof(ProcessActionLabel));
            Changed(nameof(WindowTitle));
        }
    }

    public bool HasOpenMeeting => _open is not null;

    /// <summary>
    /// What the window bar says: the destination, or the recording that is open in it. Named so a
    /// second window in the taskbar is identifiable without switching to it.
    /// </summary>
    public string WindowTitle => _open is { } meeting
        ? "EchoForge — " + meeting.Title
        : "EchoForge — all recordings";

    // -- commands ------------------------------------------------------------------------------

    /// <summary>Opens the index, rebuilding it if that is what opening requires.</summary>
    public async Task InitializeAsync()
    {
        // Once. Navigating to Settings and back used to run this again, and reloading the list
        // clears it - which drops the ListBox's selection, which closes the meeting the user was
        // reading. Losing your place every time you glance at Settings is not a refresh, it is a
        // bug wearing a refresh's clothes. Refresh is a button, and it is deliberately explicit.
        if (_initialised)
        {
            return;
        }

        _initialised = true;
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
            LibraryFilter range = Filter;

            SearchResults found = await Task
                .Run(() => _index.Search(new SearchQuery
                {
                    Text = text,
                    Limit = 300,
                    // Search and the date range apply together rather than one replacing the
                    // other: narrowing to a fortnight and then searching within it is the whole
                    // point of having both.
                    Since = range.Since,
                    Until = range.Until,
                }))
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

    /// <summary>Renames a recording through a presentation-only overlay, then refreshes its row.</summary>
    public async Task<bool> RenameMeetingAsync(MeetingRow row, string? title)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_services.Titles is not { } titles)
        {
            Status = "Renaming is not available in this build.";
            return false;
        }

        if (!titles.Rename(row.SessionId, title))
        {
            Status = "The recording name could not be saved.";
            return false;
        }

        bool refreshed = await RefreshMeetingAsync(row.SessionId).ConfigureAwait(true);
        Status = refreshed ? null : "The recording was renamed, but the list could not refresh yet.";
        return refreshed;
    }

    /// <summary>Plain, human-readable selected transcript for the row context menu.</summary>
    public string? BuildTranscriptText(MeetingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.HasTranscript)
        {
            return null;
        }

        using MeetingViewModel meeting = new(row.Entry, _transcripts, _summaries, _aliases);
        return string.Join(
            Environment.NewLine,
            meeting.Lines.Select(line => $"{line.Timestamp}  {line.Speaker}: {line.Text}"));
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

    /// <summary>
    /// Re-reads one meeting from disk and re-indexes it.
    ///
    /// <para>
    /// The index update goes through the maintainer when there is one, so several notifications
    /// about the same meeting in quick succession become one pass rather than several overlapping
    /// re-reads of the same folder. The meeting on screen is then rebuilt from the files either
    /// way: what the reader sees comes from the revision, never from the database.
    /// </para>
    /// </summary>
    public async Task<bool> RefreshMeetingAsync(string sessionId)
    {
        // The transcript may have changed, so its cached shape is no longer trustworthy.
        _shapes.Remove(sessionId);

        if (_services.Index is { } maintainer)
        {
            await maintainer.UpdateNowAsync(sessionId).ConfigureAwait(true);
        }
        else
        {
            await _index.UpdateAsync(sessionId).ConfigureAwait(true);
        }

        LibraryDocument? document = _projection.Build(sessionId);
        if (document is null)
        {
            return false;
        }

        MeetingRow row = new(document.Entry);
        int existing = IndexOf(sessionId);

        if (existing >= 0)
        {
            // Replacing the row removes the object the list had selected, so the selection is
            // pushed back as null and the meeting closes. That happens at the worst possible
            // moment - the instant processing finishes and the brief is finally worth reading -
            // so the selection is restored when the row that was replaced was the open one.
            bool wasSelected = string.Equals(_selected?.SessionId, sessionId, StringComparison.Ordinal);
            Meetings[existing] = row;

            // Through the property, not the field: the list has already pushed a null selection
            // back by this point, which closed the meeting, so this has to genuinely reopen it.
            // Reopening re-reads the canonical files, which is how the brief that was just written
            // arrives on screen without anybody pressing anything.
            if (wasSelected && !ReferenceEquals(_selected, row))
            {
                SelectedMeeting = row;
            }
        }
        else if (Filter.Includes(document.Entry.CreatedUtc))
        {
            // A recording this list has never seen. Replacing a row that is not there was the
            // whole of the old behaviour, which meant a meeting that had just been recorded
            // silently failed to appear and the user was left looking for it. Inserted in the
            // list's own order - newest first - rather than appended, so it lands where the day
            // grouping expects it.
            int at = 0;
            while (at < Meetings.Count && Meetings[at].Entry.CreatedUtc > document.Entry.CreatedUtc)
            {
                at++;
            }

            Meetings.Insert(at, row);
        }

        if (OpenMeeting is { } open && string.Equals(open.SessionId, sessionId, StringComparison.Ordinal))
        {
            open.Reload(document.Entry);

            // A first transcript or summary just landed, so the action that produced it should stop
            // saying "Transcribe" and start saying "Transcribe again".
            Changed(nameof(TranscribeActionLabel));
            Changed(nameof(SummarizeActionLabel));
            Changed(nameof(ProcessActionLabel));
        }

        return true;
    }

    // -- reprocessing --------------------------------------------------------------------------

    private bool CanReprocess(bool transcribe) =>
        !_busy && _selected is not null && _services.Reprocessor is { } reprocessor &&
        (transcribe ? reprocessor.CanTranscribe : reprocessor.CanSummarize);

    /// <summary>
    /// Runs transcription or summarisation again on the open meeting.
    ///
    /// <para>
    /// The coordinators decide everything: recording still outranks it, only one heavy job runs at
    /// a time, and a refusal arrives in the coordinator's own words. What happens here is what a
    /// library needs and a coordinator does not do — the list and the open meeting are re-read
    /// afterwards, so a new revision appears without anybody pressing Refresh.
    /// </para>
    /// </summary>
    private async Task ReprocessAsync(bool transcribe)
    {
        if (_selected is not { } row || _services.Reprocessor is not { } reprocessor)
        {
            return;
        }

        string sessionId = row.SessionId;

        IsBusy = true;
        Status = transcribe ? "Transcribing again…" : "Generating the summary again…";

        try
        {
            ReprocessOutcome outcome = transcribe
                ? await reprocessor.TranscribeAgainAsync(sessionId).ConfigureAwait(true)
                : await reprocessor.SummarizeAgainAsync(sessionId).ConfigureAwait(true);

            Status = outcome.Message;
        }
        finally
        {
            IsBusy = false;
        }

        // Whether it worked or not: a failed attempt still leaves a job record worth showing, and
        // a successful one leaves a revision the reader is waiting to see.
        await RefreshMeetingAsync(sessionId).ConfigureAwait(true);
    }

    /// <summary>
    /// The normal path: transcribe and summarise in one go, with the defaults from Settings.
    ///
    /// <para>
    /// Cancellation goes through the coordinators, which is what makes it safe — the worker process
    /// is stopped and the model unloaded by the same code that would have done it on success. A
    /// cancelled run leaves the previous revisions exactly as they were.
    /// </para>
    /// </summary>
    private async Task ProcessAsync()
    {
        if (_selected is not { } row || _services.Reprocessor is not { } reprocessor)
        {
            return;
        }

        string sessionId = row.SessionId;

        using CancellationTokenSource cancellation = new();
        _processing = cancellation;
        _cancellationRequested = false;
        IsBusy = true;
        _processingStage = "Preparing audio";
        Changed(nameof(ProcessingStage));
        Changed(nameof(IsProcessing));
        Status = null;
        CancelProcessingCommand.RaiseCanExecuteChanged();

        Progress<string> stage = new(text => Dispatch(() =>
        {
            _processingStage = text;
            Changed(nameof(ProcessingStage));
        }));

        try
        {
            ReprocessOutcome outcome = await reprocessor
                .ProcessMeetingAsync(sessionId, stage, cancellation.Token)
                .ConfigureAwait(true);

            Status = outcome.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "Processing was cancelled. Nothing was changed.";
        }
        finally
        {
            _processing = null;
            _cancellationRequested = false;
            _processingStage = string.Empty;
            IsBusy = false;
            Changed(nameof(ProcessingStage));
            Changed(nameof(IsProcessing));
            CancelProcessingCommand.RaiseCanExecuteChanged();
        }

        await RefreshMeetingAsync(sessionId).ConfigureAwait(true);
    }

    private void CancelProcessing()
    {
        if (_processing is null || _cancellationRequested)
        {
            return;
        }

        // Cancel both sides of the boundary. The token tells managed orchestration to stop, while
        // the reprocessor hook wakes the coordinator/worker that may currently be waiting on native
        // model work. The old button only did the former, which could leave "Cancel" looking dead.
        _cancellationRequested = true;
        _processingStage = "Cancelling…";
        Changed(nameof(ProcessingStage));
        CancelProcessingCommand.RaiseCanExecuteChanged();

        _processing.Cancel();
        _services.Reprocessor?.Cancel();
    }

    // -- deletion ------------------------------------------------------------------------------

    /// <summary>
    /// Deletes the open meeting, having asked first and re-checked afterwards.
    ///
    /// <para>
    /// The confirmation and the deletion are two separate decisions and the service treats them
    /// that way. Between them a recording can start; the service refuses at that point, and the
    /// user is told rather than losing a meeting to a race.
    /// </para>
    /// </summary>
    private async Task DeleteAsync()
    {
        if (_selected is not { } row || _services.Deletion is not { } deletion)
        {
            return;
        }

        string sessionId = row.SessionId;
        DeletionEligibility eligibility = deletion.Check(sessionId);

        if (!eligibility.Allowed)
        {
            Status = eligibility.Message;
            return;
        }

        if (_services.Confirmation is not { } confirmation || !confirmation.Confirm(eligibility))
        {
            // Cancelling leaves everything exactly as it was, including what is on screen.
            Status = null;
            return;
        }

        IsBusy = true;

        try
        {
            DeletionResult result = await Task.Run(() => deletion.DeleteAsync(sessionId)).ConfigureAwait(true);
            Status = result.Message;

            if (!result.Deleted)
            {
                return;
            }

            // Gone from the list, and the open meeting closed: leaving it on screen would let
            // somebody keep reading a transcript that is in the Recycle Bin.
            SelectedMeeting = null;

            for (int i = 0; i < Meetings.Count; i++)
            {
                if (string.Equals(Meetings[i].SessionId, sessionId, StringComparison.Ordinal))
                {
                    Meetings.RemoveAt(i);
                    break;
                }
            }

            for (int i = Results.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Results[i].Hit.SessionId, sessionId, StringComparison.Ordinal))
                {
                    Results.RemoveAt(i);
                }
            }

            Changed(nameof(HasResults));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private int IndexOf(string sessionId)
    {
        for (int i = 0; i < Meetings.Count; i++)
        {
            if (string.Equals(Meetings[i].SessionId, sessionId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void LoadMeetings()
    {
        string? keep = _selected?.SessionId;

        Meetings.Clear();
        foreach (LibraryEntry entry in _index.Meetings(Filter))
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
        TranscribeAgainCommand.RaiseCanExecuteChanged();
        SummarizeAgainCommand.RaiseCanExecuteChanged();
        ProcessMeetingCommand.RaiseCanExecuteChanged();
        CancelProcessingCommand.RaiseCanExecuteChanged();
        DeleteMeetingCommand.RaiseCanExecuteChanged();
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

        // The open meeting first: it holds the audio device and the prepared file, and closing
        // the window must not leave either claimed.
        OpenMeeting = null;
        _index.Dispose();
    }
}
