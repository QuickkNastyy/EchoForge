using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Artifacts;
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
    private readonly LlamaRuntimeStager? _runtime;
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
        TimeProvider? clock = null,
        LlamaRuntimeStager? runtime = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _summaries = summaries ?? throw new ArgumentNullException(nameof(summaries));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _captureGate = captureGate ?? NoRecordingInProgressGate.Instance;
        _otherJobRunning = otherJobRunning ?? (static () => false);
        _clock = clock ?? TimeProvider.System;
        _runtime = runtime;
    }

    public event EventHandler<SummaryProgressEventArgs>? ProgressChanged;

    public event EventHandler? StateChanged;

    /// <summary>
    /// The same news, naming the session it happened to, for a listener that has to know which
    /// meeting changed. Raised after the change is already durable, so nothing subscribed to it
    /// can undo one.
    /// </summary>
    public event EventHandler<string>? SessionChanged;

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
            SessionChanged?.Invoke(this, sessionId);
        }

        return selected;
    }

    public int DiscardOrphanStaging(string sessionId) => _summaries.DiscardOrphanStaging(sessionId);

    /// <summary>True when a local model could actually run. False is a normal, expected state.</summary>
    public bool ProductionAvailable => ResolveRuntime(new SummaryOptions { Backend = SummaryOptions.ProductionBackend }) is not null;

    public bool ModelAvailable(string backend) => !SummaryOptions.LocalModelBackends.Contains(backend, StringComparer.Ordinal)
        || ResolveRuntime(new SummaryOptions { Backend = backend }) is not null;

    /// <summary>What the production backend still needs, for the UI to show.</summary>
    public SummaryRuntimeStatus? RuntimeStatus(string? profileId = null)
    {
        if (_runtime is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(profileId))
        {
            return _runtime.Status(profileId);
        }

        // The best profile this machine has, which is the one a run would pick.
        SummaryRuntimeStatus? best = null;
        foreach (string candidate in ProcessingProfile.SummaryProfiles)
        {
            SummaryRuntimeStatus status = _runtime.Status(candidate);
            if (status.Ready)
            {
                return status;
            }

            best ??= status;
            if (status.BytesInstalled > best.BytesInstalled)
            {
                best = status;
            }
        }

        return best;
    }

    /// <summary>Downloads and unpacks the summary runtime. Long, cancellable, and never implicit.</summary>
    public async Task<bool> InstallProductionAsync(
        string? profileId = null,
        IProgress<ArtifactProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_runtime is null)
        {
            return false;
        }

        string target = profileId ?? ProcessingProfile.SummaryCudaQ4;
        LlamaRuntimePaths? paths = await _runtime.EnsureAsync(target, progress, cancellationToken).ConfigureAwait(false);

        StateChanged?.Invoke(this, EventArgs.Empty);
        return paths is not null;
    }

    /// <summary>
    /// The verified runtime a production run would use, or null.
    ///
    /// <para>
    /// Only ever a path the registry has hashed against the manifest. There is deliberately no
    /// branch that looks for a llama.cpp already on the machine: an unverified binary is not a
    /// degraded runtime, it is an unknown one, and running a meeting through it would be exactly
    /// the silent dependency the artifact gate exists to prevent.
    /// </para>
    /// </summary>
    private LlamaRuntimePaths? ResolveRuntime(SummaryOptions options)
    {
        if (_runtime is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(options.SummaryProfile))
        {
            return _runtime.TryResolve(options.SummaryProfile);
        }

        if (string.Equals(options.Backend, SummaryOptions.GptOssBackend, StringComparison.Ordinal))
        {
            return _runtime.TryResolve(ProcessingProfile.SummaryGptOss20B);
        }

        if (string.Equals(options.Backend, SummaryOptions.ComparisonBackend, StringComparison.Ordinal))
        {
            return _runtime.TryResolve(ProcessingProfile.SummaryBakeoff);
        }

        foreach (string candidate in ProcessingProfile.SummaryProfiles)
        {
            if (_runtime.TryResolve(candidate) is { } resolved)
            {
                return resolved;
            }
        }

        return null;
    }

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
        int? requestedTranscript = options.TranscriptRevision ?? transcription.SelectedRevision;
        if (requestedTranscript is not { } transcriptRevision
            || !transcription.Revisions.Any(revision => revision.Revision == transcriptRevision && revision.FileExists))
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

        // Resolved before an attempt exists, so a machine with no summary model refuses cheaply
        // and leaves no failed revision behind for something it was never able to start.
        LlamaRuntimePaths? runtime = null;
        if (options.IsProduction)
        {
            runtime = ResolveRuntime(options);
            if (runtime is null)
            {
                return Refuse(
                    "summary_model_missing",
                    "The local summary model is not installed yet, so this run would have nothing to summarise with. "
                    + "Download it first, or switch to the deterministic placeholder.");
            }
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
            return await RunAsync(running, transcript, transcriptPath, transcriptDigest, options, runtime, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _running = null;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            SessionChanged?.Invoke(this, sessionId);
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
        LlamaRuntimePaths? runtime,
        CancellationToken cancellationToken)
    {
        SummaryAttempt attempt = running.Attempt;
        SessionPaths paths = _sessions.Resolve(running.SessionId);

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, running.Cancellation.Token);

        IReadOnlyList<SummaryChunk> chunks = TranscriptChunker.Plan(transcript, options);

        _summaries.MarkStarted(attempt, Now);
        StateChanged?.Invoke(this, EventArgs.Empty);

        InlineProgress progress = new(update =>
        {
            _summaries.RecordProgress(attempt, update.Stage, update.CompletedUnits, update.TotalUnits);
            ProgressChanged?.Invoke(this, new SummaryProgressEventArgs(
                running.SessionId, update.Stage, update.CompletedUnits, update.TotalUnits));
        });

        SummaryRejection? rejected = null;

        // At most one repair, and the loop is the only place that decides so. A worker cannot
        // re-ask itself, and a rejection cannot lower the bar it failed to clear.
        for (int repairAttempt = 0; repairAttempt <= SummaryValidator.MaxRepairAttempts; repairAttempt++)
        {
            if (repairAttempt > 0)
            {
                progress.Report(new ProgressMessage
                {
                    JobId = attempt.JobId,
                    Stage = RepairingStage,
                    CompletedUnits = 0,
                    TotalUnits = chunks.Count,
                });
            }

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
                Chunks = chunks,
                SynthesisGroupSize = options.SynthesisGroupSize,
                LlamaBinaryPath = runtime?.ServerBinary ?? string.Empty,
                ModelPath = runtime?.ModelPath ?? string.Empty,
                SummaryProfile = runtime?.ProfileId ?? string.Empty,
                Seed = options.Seed,
                RepairAttempt = repairAttempt,
                RejectionReasons = rejected?.Problems ?? [],
                TestMode = options.TestMode,
                TestDelaySeconds = options.TestDelaySeconds,
            };

            WorkerRunResult worker = await _supervisor
                .SummarizeAsync(attempt.JobId, request, progress, linked.Token)
                .ConfigureAwait(false);

            if (worker.Outcome is WorkerOutcome.Cancelled or WorkerOutcome.Busy)
            {
                return Cancelled(attempt);
            }

            if (worker.Outcome != WorkerOutcome.Succeeded)
            {
                // A crashed or timed-out worker is a broken run, not a badly worded answer.
                // Re-asking it would be retrying the failure, which is not what repair is for.
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

            SummaryAcceptance acceptance = Accept(attempt, transcript, worker, repairAttempt);
            if (acceptance.Result is { } settled)
            {
                return settled;
            }

            rejected = acceptance.Rejection!;

            if (!rejected.Repairable)
            {
                break;
            }

            if (linked.IsCancellationRequested)
            {
                return Cancelled(attempt);
            }
        }

        return Reject(attempt, rejected!);
    }

    /// <summary>The stage the UI shows while the one re-ask is in flight.</summary>
    public const string RepairingStage = "repairing";

    private SummaryRunResult Cancelled(SummaryAttempt attempt)
    {
        _summaries.MarkCancelled(attempt, Now);
        return new SummaryRunResult(ProcessingStageState.Cancelled, null, null,
            "Summarising was cancelled. Your transcript and any earlier summary are unchanged.");
    }


    /// <summary>Why one attempt's output was refused, and whether re-asking could help.</summary>
    /// <param name="Repairable">
    /// True for an answer that was badly formed or unsupported, which a re-ask might get right.
    /// False for a refusal that has nothing to do with what the model said — a digest mismatch or
    /// a revision that already exists will not be fixed by generating better prose.
    /// </param>
    private sealed record SummaryRejection(string Code, bool Repairable, IReadOnlyList<string> Problems);

    /// <summary>A settled result, or the reason there is not one yet.</summary>
    private sealed record SummaryAcceptance(SummaryRunResult? Result, SummaryRejection? Rejection);

    /// <summary>
    /// Validates the staged summary against the transcript, then activates it.
    ///
    /// <para>
    /// This is where a summary stops being model output and becomes something a user will act on,
    /// so it is the last point a refusal costs nothing. A document that cites a segment the
    /// transcript does not contain never reaches disk under a revision name.
    /// </para>
    ///
    /// <para>
    /// The checks here are identical on a first attempt and on a repair. That is the whole
    /// discipline: a re-ask is another chance to answer, never a lower bar to clear.
    /// </para>
    /// </summary>
    private SummaryAcceptance Accept(
        SummaryAttempt attempt,
        TranscriptDocument transcript,
        WorkerRunResult worker,
        int repairAttempt)
    {
        SummaryDocument? summary;
        try
        {
            using FileStream stream = File.OpenRead(attempt.StagingPath);
            summary = JsonSerializer.Deserialize<SummaryDocument>(stream, SummaryDocument.Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Refused("summary_unreadable", true, [$"the summary could not be read ({ex.GetType().Name})"]);
        }

        if (summary is null)
        {
            return Refused("summary_unreadable", true, ["the summary file held nothing"]);
        }

        SummaryVerdict verdict = SummaryValidator.Validate(summary, transcript);
        if (!verdict.IsValid)
        {
            return Refused("summary_invalid", true, verdict.Problems);
        }

        // The worker is told which attempt it is on; a document disagreeing about that is a
        // document from somewhere other than the run being settled.
        if (summary.RepairAttempt != repairAttempt)
        {
            return Refused("summary_invalid", true,
                [$"the summary reports repair attempt {summary.RepairAttempt.ToString(CultureInfo.InvariantCulture)}, but this was attempt {repairAttempt.ToString(CultureInfo.InvariantCulture)}"]);
        }

        SummaryActivation activation = _summaries.Activate(attempt, worker.Output!.Sha256, summary, Now);
        if (!activation.Activated)
        {
            return Refused("activation_refused", false, [activation.Refusal ?? "the summary could not be activated"]);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        SessionChanged?.Invoke(this, attempt.SessionId);

        string ready = summary.Model.ProducesSummaries
            ? "Summary ready."
            : "Summary ready. This run used the deterministic placeholder, which groups and quotes what was said rather than summarising it.";

        if (repairAttempt > 0)
        {
            ready += " The first attempt was not supported by the transcript and was refused, so it was generated again.";
        }

        if (summary.Run?.FellBack == true)
        {
            ready += " A runtime or evidence-preserving fallback was used and is recorded on this revision.";
        }

        // A reduced context is never silent. The user is told the model would not start at the
        // size EchoForge asked for, and the revision records what it actually ran at.
        foreach (WarningMessage warning in worker.Warnings)
        {
            if (string.Equals(warning.Code, "summary_context_reduced", StringComparison.Ordinal))
            {
                ready += " This run used a reduced context because the model would not load at the full size on this machine.";
                break;
            }
        }

        return new SummaryAcceptance(
            new SummaryRunResult(ProcessingStageState.Succeeded, activation.Revision!.Revision, null, ready),
            null);

        static SummaryAcceptance Refused(string code, bool repairable, IReadOnlyList<string> problems) =>
            new(null, new SummaryRejection(code, repairable, Bounded(problems)));
    }

    /// <summary>
    /// The rejection reasons a re-ask is told about, capped.
    ///
    /// <para>
    /// A summary that got everything wrong produces a problem per item, and sending all of them
    /// would let a bad answer decide how big the next request is.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> Bounded(IReadOnlyList<string> problems) =>
        [.. problems.Take(10).Select(problem => problem.Length <= 300 ? problem : problem[..300])];

    private SummaryRunResult Reject(SummaryAttempt attempt, SummaryRejection rejection)
    {
        // Named so the difference is visible afterwards: refused once is a bad answer, refused
        // twice is a backend that could not produce a supported one.
        string code = rejection.Repairable ? rejection.Code + "_after_repair" : rejection.Code;

        string message = rejection.Repairable
            ? "The summary EchoForge received was not supported by the transcript. It was generated once more and " +
              "was still not supported, so nothing was changed. Your transcript and any earlier summary are untouched."
            : "The summary EchoForge received could not be stored, so nothing was changed. Your transcript and any " +
              "earlier summary are untouched.";

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
