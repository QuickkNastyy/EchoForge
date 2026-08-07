using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.App;

/// <summary>One selectable summary revision.</summary>
public sealed record SummaryRevisionOption(int Revision, string Label, bool ProducesSummaries, bool IsStale);

/// <summary>
/// The summary surface.
///
/// <para>
/// It holds no truth of its own: stage, revisions and progress all come from the coordinator,
/// which reads them from the journal. Everything slow — chunking a transcript, launching a
/// worker — runs off the UI thread, because this is the thread painting the recording indicator.
/// </para>
/// </summary>
public sealed class SummaryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SummaryCoordinator _coordinator;
    private readonly OperationGate _gate = new();

    private string? _sessionId;
    private bool _hasTranscript;
    private bool _hostReady;
    private bool _recordingActive;
    private bool _shuttingDown;
    private bool _disposed;

    private SummaryState _state = SummaryState.Empty;
    private string? _notice;
    private string? _error;

    public SummaryViewModel(SummaryCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        GenerateCommand = new AsyncRelayCommand(GenerateAsync, _gate, () => CanGenerate, m => Error = m);
        GenerateAgainCommand = new AsyncRelayCommand(GenerateAsync, _gate, () => CanGenerateAgain, m => Error = m);
        CancelCommand = new AsyncRelayCommand(CancelAsync, _gate, () => CanCancel, m => Error = m);

        _gate.Changed += (_, _) => Dispatch(RaiseCommands);

        _coordinator.StateChanged += OnStateChanged;
        _coordinator.ProgressChanged += OnProgress;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncRelayCommand GenerateCommand { get; }

    public AsyncRelayCommand GenerateAgainCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public ObservableCollection<SummaryRevisionOption> Revisions { get; } = [];

    public SummaryRevisionOption? SelectedRevision
    {
        get => Revisions.FirstOrDefault(r => r.Revision == _state.SelectedRevision);
        set
        {
            if (value is null || _sessionId is null || value.Revision == _state.SelectedRevision)
            {
                return;
            }

            if (!_coordinator.SelectRevision(_sessionId, value.Revision))
            {
                Error = "That summary version is no longer available.";
            }

            Refresh();
        }
    }

    // -- what the window shows -------------------------------------------------------------

    public string StageText => _state.Stage switch
    {
        ProcessingStageState.NotRequested => _hasTranscript ? "Not summarised" : "No transcript yet",
        ProcessingStageState.Queued => "Queued",
        ProcessingStageState.Running => "Summarising",
        ProcessingStageState.Succeeded => "Summarised",
        ProcessingStageState.Failed => "Summarising failed",
        ProcessingStageState.Cancelled => "Summarising cancelled",
        _ => "—",
    };

    public bool IsWorking => _state.Stage is ProcessingStageState.Queued or ProcessingStageState.Running;

    public double ProgressPercent { get; private set; }

    public string ProgressDescription { get; private set; } = string.Empty;

    public bool HasSummary => _state.Selected is not null;

    public string SummarySummary => _state.Selected is { } selected
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"Version {selected.Revision} · {selected.DecisionCount} decisions · {selected.ActionCount} actions · from transcript v{selected.TranscriptRevision}")
        : string.Empty;

    /// <summary>
    /// True whenever the summary on show was not written by a real summariser — and before
    /// anything has run, because the placeholder is what would run.
    /// </summary>
    public bool IsPlaceholderBackend => _state.Selected is null || !_state.Selected.ProducesSummaries;

    public string PlaceholderWarning => _state.Selected is { ProducesSummaries: false } selected
        ? $"Version {selected.Revision.ToString(CultureInfo.InvariantCulture)} was produced by a deterministic " +
          "placeholder. It read the real transcript and cites real segments, but it groups and quotes what " +
          "was said rather than understanding it — so it is not a summary and should not be read as one."
        : "This build summarises with a deterministic placeholder. It reads the real transcript and " +
          "cites real segments, but it groups and quotes what was said rather than understanding it — " +
          "so what it produces is not a summary and should not be read as one.";

    /// <summary>
    /// A summary is stale when the session's selected transcript is no longer the one it was
    /// written from. It stays fully readable, and its evidence still resolves, because it still
    /// points at its own revision.
    /// </summary>
    public bool IsStale => _state.Selected?.IsStaleAgainst(_transcriptRevision) ?? false;

    public string StaleNotice =>
        IsStale
            ? "This summary was written from an earlier transcript version. It is still accurate about that version; generate again to bring it up to date."
            : string.Empty;

    public string? Notice
    {
        get => _notice;
        private set { _notice = value; OnChanged(); OnChanged(nameof(HasNotice)); }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    public string? Error
    {
        get => _error;
        private set { _error = value; OnChanged(); OnChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    private int? _transcriptRevision;

    // -- availability -----------------------------------------------------------------------

    public bool CanGenerate =>
        _sessionId is not null && _hasTranscript && _hostReady && !_shuttingDown &&
        !_recordingActive && !IsWorking && !_coordinator.IsRunning && !HasSummary;

    public bool CanGenerateAgain =>
        _sessionId is not null && _hasTranscript && _hostReady && !_shuttingDown &&
        !_recordingActive && !IsWorking && !_coordinator.IsRunning && HasSummary;

    public bool CanCancel => IsWorking && !_shuttingDown;

    /// <summary>
    /// Pushed by the main view model, so there is one place that reads recorder state and the
    /// panels cannot disagree about whether capture is live.
    /// </summary>
    public void UpdateHost(
        string? sessionId,
        int? transcriptRevision,
        bool recordingActive,
        bool hostReady,
        bool shuttingDown)
    {
        bool sessionChanged = !string.Equals(_sessionId, sessionId, StringComparison.Ordinal);

        if (!sessionChanged &&
            _transcriptRevision == transcriptRevision &&
            _recordingActive == recordingActive &&
            _hostReady == hostReady &&
            _shuttingDown == shuttingDown)
        {
            return;
        }

        _sessionId = sessionId;
        _transcriptRevision = transcriptRevision;
        _hasTranscript = transcriptRevision is not null;
        _recordingActive = recordingActive;
        _hostReady = hostReady;
        _shuttingDown = shuttingDown;

        if (sessionChanged)
        {
            Error = null;
            Notice = null;
            ProgressPercent = 0;
        }

        Refresh();
    }

    // -- commands ---------------------------------------------------------------------------

    private async Task GenerateAsync()
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }

        Error = null;
        Notice = null;

        // Chunking walks the whole transcript and the worker launch blocks; neither belongs on
        // the thread that paints the window.
        SummaryRunResult result = await Task
            .Run(() => _coordinator.SummarizeAsync(sessionId))
            .ConfigureAwait(true);

        if (result.Succeeded)
        {
            Notice = result.Message;
        }
        else if (result.State == ProcessingStageState.Cancelled)
        {
            Notice = result.Message;
        }
        else
        {
            Error = result.Message;
        }

        ProgressPercent = result.Succeeded ? 100 : 0;
        ProgressDescription = string.Empty;
        Refresh();
    }

    private Task CancelAsync()
    {
        Notice = null;
        return Task.Run(_coordinator.Cancel);
    }

    // -- coordinator plumbing ------------------------------------------------------------------

    private void OnStateChanged(object? sender, EventArgs e) => Dispatch(Refresh);

    private void OnProgress(object? sender, SummaryProgressEventArgs e) => Dispatch(() =>
    {
        if (!string.Equals(e.SessionId, _sessionId, StringComparison.Ordinal))
        {
            return;
        }

        ProgressPercent = Math.Round(e.Fraction * 100, 1);
        ProgressDescription = e.TotalUnits <= 0
            ? "Working"
            : string.Create(CultureInfo.InvariantCulture, $"Reading the transcript — part {e.CompletedUnits} of {e.TotalUnits}");

        OnChanged(nameof(ProgressPercent));
        OnChanged(nameof(ProgressDescription));
    });

    private void Refresh()
    {
        _state = _sessionId is null ? SummaryState.Empty : _coordinator.StateFor(_sessionId);

        if (_state.Stage == ProcessingStageState.Failed &&
            _state.CurrentJob?.FailureSummary is { } summary &&
            !IsWorking)
        {
            Error = summary;
        }

        RebuildRevisions();

        foreach (string name in RefreshedProperties)
        {
            OnChanged(name);
        }

        RaiseCommands();
    }

    private void RebuildRevisions()
    {
        List<SummaryRevisionOption> wanted =
        [
            .. _state.Revisions
                .Where(r => r.FileExists)
                .OrderByDescending(r => r.Revision)
                .Select(r => new SummaryRevisionOption(
                    r.Revision,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Version {r.Revision} · {r.CreatedUtc.ToLocalTime():d MMM HH:mm} · transcript v{r.TranscriptRevision}{(r.ProducesSummaries ? string.Empty : " · placeholder")}"),
                    r.ProducesSummaries,
                    r.IsStaleAgainst(_transcriptRevision)))
        ];

        if (Revisions.SequenceEqual(wanted))
        {
            return;
        }

        Revisions.Clear();
        foreach (SummaryRevisionOption option in wanted)
        {
            Revisions.Add(option);
        }
    }

    private static readonly string[] RefreshedProperties =
    [
        nameof(StageText), nameof(IsWorking), nameof(HasSummary), nameof(SummarySummary),
        nameof(IsPlaceholderBackend), nameof(PlaceholderWarning), nameof(IsStale), nameof(StaleNotice),
        nameof(SelectedRevision), nameof(CanGenerate), nameof(CanGenerateAgain), nameof(CanCancel),
        nameof(ProgressPercent), nameof(ProgressDescription),
    ];

    private void RaiseCommands()
    {
        GenerateCommand.RaiseCanExecuteChanged();
        GenerateAgainCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
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

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.StateChanged -= OnStateChanged;
        _coordinator.ProgressChanged -= OnProgress;
    }
}
