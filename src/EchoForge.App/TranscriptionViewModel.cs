using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Exports;
using EchoForge.Infrastructure.Processing;

namespace EchoForge.App;

/// <summary>Where an export should go. Null from the prompt means the user cancelled.</summary>
public sealed record ExportDestination(string Path, bool OverwriteConfirmed);

/// <summary>
/// Asks the user where to save an export. Abstracted so export behaviour can be tested without
/// showing a dialog.
/// </summary>
public interface IExportDestinationPrompt
{
    ExportDestination? Ask(string suggestedFileName, TranscriptExportFormat format);
}

/// <summary>One selectable revision, described for a list.</summary>
public sealed record TranscriptRevisionOption(int Revision, string Label, bool RecognizesSpeech);

/// <summary>One export format, with the name a person would recognise.</summary>
public sealed record ExportFormatOption(TranscriptExportFormat Format, string Label);

/// <summary>
/// The transcription surface: ask for a transcript, watch it happen, choose which revision is
/// current, and export it.
///
/// <para>
/// It holds no processing truth of its own. Stage, progress, revisions, and the selected revision
/// are all read from the coordinator, which reads them from the journal — so the window cannot
/// disagree with what was actually activated. Everything slow (hashing source chunks, launching a
/// worker, writing an export) happens off the UI thread.
/// </para>
/// </summary>
public sealed class TranscriptionViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly TranscriptionCoordinator _coordinator;
    private readonly IExportDestinationPrompt _exportPrompt;
    private readonly OperationGate _gate = new();

    private string? _sessionId;
    private bool _sessionSettled;
    private bool _recordingActive;
    private bool _hostReady;
    private bool _shuttingDown;
    private bool _disposed;

    private TranscriptionState _state = TranscriptionState.Empty;
    private string? _notice;
    private string? _error;
    private string _progressDescription = string.Empty;
    private double _progressPercent;

    public TranscriptionViewModel(TranscriptionCoordinator coordinator, IExportDestinationPrompt exportPrompt)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(exportPrompt);

        _coordinator = coordinator;
        _exportPrompt = exportPrompt;

        TranscribeCommand = new AsyncRelayCommand(TranscribeAsync, _gate, () => CanTranscribe, m => Error = m);
        TranscribeAgainCommand = new AsyncRelayCommand(TranscribeAsync, _gate, () => CanTranscribeAgain, m => Error = m);
        CancelCommand = new AsyncRelayCommand(CancelAsync, _gate, () => CanCancel, m => Error = m);
        ExportCommand = new AsyncRelayCommand(ExportAsync, _gate, () => CanExport, m => Error = m);

        _gate.Changed += (_, _) => Dispatch(RaiseCommands);

        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.ProgressChanged += OnCoordinatorProgress;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncRelayCommand TranscribeCommand { get; }

    public AsyncRelayCommand TranscribeAgainCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public ObservableCollection<TranscriptRevisionOption> Revisions { get; } = [];

    public ObservableCollection<ExportFormatOption> ExportFormats { get; } =
    [
        new(TranscriptExportFormat.Text, TranscriptExporter.Describe(TranscriptExportFormat.Text)),
        new(TranscriptExportFormat.Json, TranscriptExporter.Describe(TranscriptExportFormat.Json)),
        new(TranscriptExportFormat.Srt, TranscriptExporter.Describe(TranscriptExportFormat.Srt)),
        new(TranscriptExportFormat.Vtt, TranscriptExporter.Describe(TranscriptExportFormat.Vtt)),
    ];

    private ExportFormatOption? _selectedExportFormat;

    public ExportFormatOption SelectedExportFormat
    {
        get => _selectedExportFormat ??= ExportFormats[0];
        set { _selectedExportFormat = value; OnChanged(); }
    }

    /// <summary>
    /// Which revision the app treats as current. Setting it records the choice durably; a
    /// selection the store refuses is reverted rather than shown as if it had taken.
    /// </summary>
    public TranscriptRevisionOption? SelectedRevision
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
                Error = "That transcript version is no longer available.";
            }

            RefreshFromCoordinator();
        }
    }

    // -- what the window shows ---------------------------------------------------------------

    public ProcessingStageState Stage => _state.Stage;

    public string StageText => _state.Stage switch
    {
        ProcessingStageState.NotRequested => HasSession ? "Not transcribed" : "No recording selected",
        ProcessingStageState.Queued => "Queued — waiting for recording to finish",
        ProcessingStageState.Running => "Transcribing",
        ProcessingStageState.Succeeded => "Transcribed",
        ProcessingStageState.Failed => "Transcription failed",
        ProcessingStageState.Cancelled => "Transcription cancelled",
        _ => "—",
    };

    public bool IsWorking => _state.Stage is ProcessingStageState.Queued or ProcessingStageState.Running;

    public double ProgressPercent
    {
        get => _progressPercent;
        private set { _progressPercent = value; OnChanged(); }
    }

    /// <summary>Which track and how far through it, in words rather than internal stage names.</summary>
    public string ProgressDescription
    {
        get => _progressDescription;
        private set { _progressDescription = value; OnChanged(); }
    }

    public bool HasTranscript => _state.Selected is not null;

    public string TranscriptSummary
    {
        get
        {
            TranscriptRevisionRecord? selected = _state.Selected;
            if (selected is null)
            {
                return string.Empty;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"Version {selected.Revision} · {selected.SegmentCount} segments · {TimeSpan.FromSeconds(selected.DurationSeconds):hh\\:mm\\:ss}");
        }
    }

    /// <summary>
    /// True whenever the transcript on show was not produced by real speech recognition.
    ///
    /// <para>
    /// It is also true before anything has run, because the backend that would run is the
    /// placeholder. The warning is about what the user is about to get, not only about what they
    /// already have.
    /// </para>
    /// </summary>
    public bool IsPlaceholderBackend => _state.Selected is null || !_state.Selected.RecognizesSpeech;

    public string PlaceholderWarning => _state.Selected is { RecognizesSpeech: false } selected
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"Version {selected.Revision} was produced by a deterministic placeholder. It read the real " +
            $"audio but performed no speech recognition, so the words are not a record of what was said.")
        : "This build transcribes with a deterministic placeholder. It reads the real audio but " +
          "performs no speech recognition, so the words are not a record of what was said.";

    /// <summary>What actually ran, as reported by the worker rather than inferred here.</summary>
    public string BackendSummary
    {
        get
        {
            string worker = _coordinator.LastWorkerEnvironment?.Summary ?? "worker not started yet";
            TranscriptRevisionRecord? selected = _state.Selected;

            return selected is null
                ? worker
                : $"{selected.Backend} · {selected.ModelId} · {worker}";
        }
    }

    public string? Notice
    {
        get => _notice;
        private set { _notice = value; OnChanged(); OnChanged(nameof(HasNotice)); }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    /// <summary>An actionable, safe message. Never worker text and never a path.</summary>
    public string? Error
    {
        get => _error;
        private set { _error = value; OnChanged(); OnChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool HasSession => _sessionId is not null;

    // -- availability -------------------------------------------------------------------------

    /// <summary>
    /// True only when there is a finished recording, startup has settled, nothing is shutting
    /// down, and no other processing job holds the coordinator.
    /// </summary>
    public bool CanTranscribe =>
        HasSession && _sessionSettled && _hostReady && !_shuttingDown &&
        !IsWorking && !_coordinator.IsRunning && !_coordinator.IsQueued && !HasTranscript;

    public bool CanTranscribeAgain =>
        HasSession && _sessionSettled && _hostReady && !_shuttingDown &&
        !IsWorking && !_coordinator.IsRunning && !_coordinator.IsQueued && HasTranscript;

    public bool CanCancel => IsWorking && !_shuttingDown;

    public bool CanExport => HasTranscript && !_shuttingDown && !IsWorking;

    /// <summary>
    /// Pushed by the main view model on every refresh, so this view model never has to observe
    /// the recorder itself and the two can never disagree about whether capture is live.
    /// </summary>
    public void UpdateHost(
        string? sessionId,
        bool sessionSettled,
        bool recordingActive,
        bool hostReady,
        bool shuttingDown)
    {
        bool sessionChanged = !string.Equals(_sessionId, sessionId, StringComparison.Ordinal);

        if (!sessionChanged &&
            _sessionSettled == sessionSettled &&
            _recordingActive == recordingActive &&
            _hostReady == hostReady &&
            _shuttingDown == shuttingDown)
        {
            return;
        }

        _sessionId = sessionId;
        _sessionSettled = sessionSettled;
        _recordingActive = recordingActive;
        _hostReady = hostReady;
        _shuttingDown = shuttingDown;

        if (sessionChanged)
        {
            Error = null;
            Notice = null;
            ProgressPercent = 0;
            ProgressDescription = string.Empty;
        }

        RefreshFromCoordinator();
    }

    // -- commands ------------------------------------------------------------------------------

    private async Task TranscribeAsync()
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }

        Error = null;
        Notice = null;

        // Request verifies every source chunk against its recorded digest, which means hashing
        // the audio. That is not something the UI thread may do.
        TranscriptionTicket ticket = await Task.Run(() => _coordinator.Request(sessionId)).ConfigureAwait(true);

        if (!ticket.Accepted)
        {
            Error = ticket.Message;
            RefreshFromCoordinator();
            return;
        }

        Notice = ticket.Message;
        RefreshFromCoordinator();

        TranscriptionRunResult result = await ticket.Completion.ConfigureAwait(true);

        if (result.Succeeded)
        {
            Notice = result.Message;
            Error = null;
        }
        else if (result.State == ProcessingStageState.Cancelled)
        {
            Notice = result.Message;
        }
        else
        {
            Error = result.Message;
            Notice = null;
        }

        ProgressPercent = result.Succeeded ? 100 : 0;
        ProgressDescription = string.Empty;
        RefreshFromCoordinator();
    }

    private Task CancelAsync()
    {
        Notice = null;
        return Task.Run(_coordinator.Cancel);
    }

    private async Task ExportAsync()
    {
        if (_sessionId is not { } sessionId || _state.Selected is not { } selected)
        {
            return;
        }

        Error = null;

        TranscriptDocument? transcript = await Task
            .Run(() => _coordinator.ReadTranscript(sessionId, selected.Revision))
            .ConfigureAwait(true);

        if (transcript is null)
        {
            Error = "That transcript could not be read. It may have been moved or changed.";
            return;
        }

        TranscriptExportFormat format = SelectedExportFormat.Format;

        // The prompt is the user's choice of destination, so it happens on the UI thread.
        ExportDestination? destination = _exportPrompt.Ask(
            TranscriptExporter.SuggestFileName(transcript, format), format);

        if (destination is null)
        {
            return;
        }

        string canonical = _coordinator.RevisionPath(sessionId, selected.Revision);

        ExportResult result = await Task
            .Run(() => TranscriptExporter.Export(transcript, canonical, format, destination.Path, destination.OverwriteConfirmed))
            .ConfigureAwait(true);

        if (result.Succeeded)
        {
            Notice = $"Exported as {TranscriptExporter.Describe(format)}.";
        }
        else
        {
            Error = result.Message;
        }
    }

    // -- coordinator plumbing --------------------------------------------------------------------

    private void OnCoordinatorStateChanged(object? sender, EventArgs e) => Dispatch(RefreshFromCoordinator);

    private void OnCoordinatorProgress(object? sender, TranscriptionProgressEventArgs e) => Dispatch(() =>
    {
        if (!string.Equals(e.SessionId, _sessionId, StringComparison.Ordinal))
        {
            return;
        }

        ProgressPercent = Math.Round(e.Fraction * 100, 1);
        ProgressDescription = DescribeStage(e);
    });

    private static string DescribeStage(TranscriptionProgressEventArgs e)
    {
        string what = e.Stage switch
        {
            "preparing" => "Preparing",
            "reading_audio" => "Reading audio",
            "transcribing_microphone" => "Transcribing your microphone",
            "transcribing_system" => "Transcribing the meeting audio",
            "merging" => "Merging the timeline",
            "validating" => "Checking the transcript",
            "writing_output" => "Saving the transcript",
            "finished" => "Finished",
            _ => "Working",
        };

        return e.TotalUnits <= 0
            ? what
            : string.Create(CultureInfo.InvariantCulture, $"{what} — chunk {e.CompletedUnits} of {e.TotalUnits}");
    }

    /// <summary>Re-reads the durable state and rebuilds everything the window binds to.</summary>
    private void RefreshFromCoordinator()
    {
        _state = _sessionId is null ? TranscriptionState.Empty : _coordinator.StateFor(_sessionId);

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
        List<TranscriptRevisionOption> wanted =
        [
            .. _state.Revisions
                .Where(r => r.FileExists)
                .OrderByDescending(r => r.Revision)
                .Select(r => new TranscriptRevisionOption(
                    r.Revision,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Version {r.Revision} · {r.CreatedUtc.ToLocalTime():d MMM HH:mm} · {r.SegmentCount} segments{(r.RecognizesSpeech ? string.Empty : " · placeholder")}"),
                    r.RecognizesSpeech))
        ];

        if (Revisions.SequenceEqual(wanted))
        {
            return;
        }

        Revisions.Clear();
        foreach (TranscriptRevisionOption option in wanted)
        {
            Revisions.Add(option);
        }
    }

    private static readonly string[] RefreshedProperties =
    [
        nameof(Stage), nameof(StageText), nameof(IsWorking), nameof(HasTranscript), nameof(TranscriptSummary),
        nameof(IsPlaceholderBackend), nameof(PlaceholderWarning), nameof(BackendSummary),
        nameof(HasSession), nameof(SelectedRevision),
        nameof(CanTranscribe), nameof(CanTranscribeAgain), nameof(CanCancel), nameof(CanExport),
    ];

    private void RaiseCommands()
    {
        TranscribeCommand.RaiseCanExecuteChanged();
        TranscribeAgainCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Marshals onto the dispatcher when the coordinator raises from a worker thread.</summary>
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
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _coordinator.ProgressChanged -= OnCoordinatorProgress;
    }
}
