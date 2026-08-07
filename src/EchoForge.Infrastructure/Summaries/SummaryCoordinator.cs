using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Summaries;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.Infrastructure.Summaries;

/// <summary>How one summary request ended.</summary>
public sealed record SummaryRunResult(
    ProcessingStageState State,
    int? Revision,
    string? FailureCode,
    string Message)
{
    public bool Succeeded => State == ProcessingStageState.Succeeded;
}

/// <summary>Progress for the UI. Carries no summary content.</summary>
public sealed class SummaryProgressEventArgs(string sessionId, string stage, int completed, int total) : EventArgs
{
    public string SessionId { get; } = sessionId;

    public string Stage { get; } = stage;

    public int CompletedUnits { get; } = completed;

    public int TotalUnits { get; } = total;

    public double Fraction => TotalUnits <= 0 ? 0 : Math.Clamp((double)CompletedUnits / TotalUnits, 0, 1);
}

/// <summary>
/// The one place summary jobs are decided on, run, and settled.
///
/// <para>
/// Summarisation is the second heavy inference stage, and the plan is explicit that only one runs
/// at a time and that recording outranks both. So this shares the transcription coordinator's
/// gate rather than keeping its own: two coordinators each politely checking their own state
/// would still start two jobs.
/// </para>
///
/// <para>
/// It requires a selected, readable transcript revision. Summarising a session with no transcript
/// is not a degraded case to handle gracefully; there is nothing to summarise.
/// </para>
/// </summary>
public sealed class SummaryCoordinator : IDisposable
{
    private readonly ISessionStore _sessions;
    private readonly FileSummaryStore _summaries;
    private readonly ITranscriptionStore _transcripts;
    private readonly WorkerSupervisor _supervisor;
    private readonly ICaptureActivityGate _captureGate;
    private readonly Func<bool> _otherJobRunning;
    private readonly TimeProvider _clock;
    private readonly Lock _sync = new();

    private RunningSummary? _running;
    private bool _disposed;

    public SummaryCoordinator(
        ISessionStore sessions,
        FileSummaryStore summaries,
        ITranscriptionStore transcripts,
        WorkerSupervisor supervisor,
        ICaptureActivityGate? captureGate = null,
        Func<bool>? otherJobRunning = null,
        TimeProvider? clock = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _summaries = summaries ?? throw new ArgumentNullException(nameof(summaries));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _captureGate = captureGate ?? NoRecordingInProgressGate.Instance;
        _otherJobRunning = otherJobRunning ?? (static () => false);
        _clock = clock ?? TimeProvider.System;
    }

    public event EventHandler<SummaryProgressEventArgs>? ProgressChanged;

    public event EventHandler? StateChanged;

    public bool IsRunning
    {
        get { lock (_sync) { return _running is not null; } }
    }

    public SummaryState StateFor(string sessionId)
    {
        SummaryState state = _summaries.Read(sessionId);

        RunningSummary? running;
        lock (_sync)
        {
            running = _running;
        }

        // The store reconstructs from the journal and cannot tell a live attempt from one whose
        // process died, so it reports the latter. Only the coordinator knows a job is alive.
        if (running is null ||
            !string.Equals(running.SessionId, sessionId, StringComparison.Ordinal) ||
            state.CurrentJob is not { } job ||
            !string.Equals(job.JobId, running.Attempt.JobId, StringComparison.Ordinal))
        {
            return state;
        }

        return state with
        {
            Stage = ProcessingStageState.Running,
            CurrentJob = job with { State = ProcessingStageState.Running, FailureCode = null, FailureSummary = null },
        };
    }

    public SummaryDocument? ReadSummary(string sessionId, int revision) => _summaries.ReadSummary(sessionId, revision);

    public bool SelectRevision(string sessionId, int revision)
    {
        bool selected = _summaries.SelectRevision(sessionId, revision, Now);
        if (selected)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return selected;
    }

    public int DiscardOrphanStaging(string sessionId) => _summaries.DiscardOrphanStaging(sessionId);

    /// <summary>Asks for a summary. Returns when the job has settled.</summary>
    public async Task<SummaryRunResult> SummarizeAsync(
        string sessionId,
        SummaryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        options ??= new SummaryOptions();

        if (_captureGate.IsCaptureActive)
        {
            return Refuse("recording_active",
                "Summarising is waiting because a recording is in progress. Recording always has priority.");
        }

        lock (_sync)
        {
            if (_running is not null || _otherJobRunning())
            {
                return Refuse("busy", "Another processing job is already running. Only one runs at a time.");
            }
        }

        // A transcript that is selected, present, and passes its own validator. Anything less
        // and there is nothing to summarise rather than something to summarise badly.
        TranscriptionState transcription = _transcripts.Read(sessionId);
        if (transcription.SelectedRevision is not { } transcriptRevision)
        {
            return Refuse("no_transcript", "That recording has not been transcribed yet.");
        }

        TranscriptDocument? transcript = _transcripts.ReadTranscript(sessionId, transcriptRevision);
        if (transcript is null)
        {
            return Refuse("transcript_unreadable", "That recording's transcript could not be read.");
        }

        TranscriptVerdict verdict = TranscriptValidator.Validate(transcript);
        if (!verdict.IsValid)
        {
            return Refuse("transcript_invalid", "That recording's transcript did not pass validation, so it was not summarised.");
        }

        string transcriptPath = _transcripts.RevisionPath(sessionId, transcriptRevision);
        string transcriptDigest = Digest(transcriptPath);

        IReadOnlyList<SummaryChunk> chunks = TranscriptChunker.Plan(transcript, options);
        if (chunks.Count == 0)
        {
            return Refuse("transcript_empty", "That transcript has no segments, so there is nothing to summarise.");
        }

        SummaryAttempt attempt = _summaries.BeginAttempt(
            sessionId, Guid.NewGuid().ToString("n"), transcriptRevision, transcriptDigest, options, Now);

        RunningSummary running = new(sessionId, attempt);
        lock (_sync)
        {
            _running = running;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            return await RunAsync(running, transcript, transcriptPath, transcriptDigest, options, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _running = null;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Cancel()
    {
        RunningSummary? running;
        lock (_sync)
        {
            running = _running;
        }

        running?.Cancellation.Cancel();
    }

    private async Task<SummaryRunResult> RunAsync(
        RunningSummary running,
        TranscriptDocument transcript,
        string transcriptPath,
        string transcriptDigest,
        SummaryOptions options,
        CancellationToken cancellationToken)
    {
        SummaryAttempt attempt = running.Attempt;
        SessionPaths paths = _sessions.Resolve(running.SessionId);

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, running.Cancellation.Token);

        SummaryRequest request = new()
        {
            SessionId = running.SessionId,
            SummaryRevision = attempt.Revision,
            TranscriptRevision = transcript.TranscriptRevision,
            TranscriptSha256 = transcriptDigest,
            TranscriptPath = transcriptPath,
            SessionRoot = paths.Root,
            OutputPath = attempt.StagingPath,
            CreatedAtUtc = Now,
            MeetingDate = options.MeetingDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            PromptVersion = options.PromptVersion,
            InferOwners = options.InferOwners,
            InferDueDates = options.InferDueDates,
            Backend = options.Backend,
            Chunks = TranscriptChunker.Plan(transcript, options),
            TestMode = options.TestMode,
            TestDelaySeconds = options.TestDelaySeconds,
        };

        _summaries.MarkStarted(attempt, Now);
        StateChanged?.Invoke(this, EventArgs.Empty);

        InlineProgress progress = new(update =>
        {
            _summaries.RecordProgress(attempt, update.Stage, update.CompletedUnits, update.TotalUnits);
            ProgressChanged?.Invoke(this, new SummaryProgressEventArgs(
                running.SessionId, update.Stage, update.CompletedUnits, update.TotalUnits));
        });

        WorkerRunResult worker = await _supervisor
            .SummarizeAsync(attempt.JobId, request, progress, linked.Token)
            .ConfigureAwait(false);

        switch (worker.Outcome)
        {
            case WorkerOutcome.Succeeded:
                return Activate(attempt, transcript, worker);

            case WorkerOutcome.Cancelled or WorkerOutcome.Busy:
                _summaries.MarkCancelled(attempt, Now);
                return new SummaryRunResult(ProcessingStageState.Cancelled, null, null,
                    "Summarising was cancelled. Your transcript and any earlier summary are unchanged.");

            default:
            {
                string code = worker.Outcome switch
                {
                    WorkerOutcome.TimedOut => "timeout",
                    WorkerOutcome.Crashed => "worker_crashed",
                    WorkerOutcome.ProtocolError => "protocol_error",
                    WorkerOutcome.LaunchFailed => "worker_unavailable",
                    _ => worker.Error?.Code ?? "worker_failed",
                };

                _summaries.MarkFailed(attempt, code, worker.UserMessage, Now);
                return new SummaryRunResult(ProcessingStageState.Failed, null, code, worker.UserMessage);
            }
        }
    }

    /// <summary>
    /// Validates the staged summary against the transcript, then activates it.
    ///
    /// <para>
    /// This is where a summary stops being model output and becomes something a user will act on,
    /// so it is the last point a refusal costs nothing. A document that cites a segment the
    /// transcript does not contain never reaches disk under a revision name.
    /// </para>
    /// </summary>
    private SummaryRunResult Activate(SummaryAttempt attempt, TranscriptDocument transcript, WorkerRunResult worker)
    {
        SummaryDocument? summary;
        try
        {
            using FileStream stream = File.OpenRead(attempt.StagingPath);
            summary = JsonSerializer.Deserialize<SummaryDocument>(stream, SummaryDocument.Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Reject(attempt, "summary_unreadable");
        }

        if (summary is null)
        {
            return Reject(attempt, "summary_unreadable");
        }

        SummaryVerdict verdict = SummaryValidator.Validate(summary, transcript);
        if (!verdict.IsValid)
        {
            return Reject(attempt, "summary_invalid");
        }

        SummaryActivation activation = _summaries.Activate(attempt, worker.Output!.Sha256, summary, Now);
        if (!activation.Activated)
        {
            return Reject(attempt, "activation_refused");
        }

        StateChanged?.Invoke(this, EventArgs.Empty);

        return new SummaryRunResult(
            ProcessingStageState.Succeeded,
            activation.Revision!.Revision,
            null,
            summary.Model.ProducesSummaries
                ? "Summary ready."
                : "Summary ready. This run used the deterministic placeholder, which groups and quotes what was said rather than summarising it.");
    }

    private SummaryRunResult Reject(SummaryAttempt attempt, string code)
    {
        const string message =
            "The summary EchoForge received was not supported by the transcript, so nothing was changed. " +
            "Your transcript and any earlier summary are untouched.";

        _summaries.MarkFailed(attempt, code, message, Now);
        return new SummaryRunResult(ProcessingStageState.Failed, null, code, message);
    }

    private static SummaryRunResult Refuse(string code, string message) =>
        new(ProcessingStageState.NotRequested, null, code, message);

    private static string Digest(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new string('0', 64);
        }
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    public void Dispose()
    {
        RunningSummary? running;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            running = _running;
        }

        running?.Cancellation.Cancel();
    }

    private sealed class RunningSummary(string sessionId, SummaryAttempt attempt)
    {
        public string SessionId { get; } = sessionId;

        public SummaryAttempt Attempt { get; } = attempt;

        public CancellationTokenSource Cancellation { get; } = new();
    }

    /// <summary>Ordered progress: the supervisor's read loop is ordered, so keep it that way.</summary>
    private sealed class InlineProgress(Action<ProgressMessage> handle) : IProgress<ProgressMessage>
    {
        public void Report(ProgressMessage value) => handle(value);
    }
}
