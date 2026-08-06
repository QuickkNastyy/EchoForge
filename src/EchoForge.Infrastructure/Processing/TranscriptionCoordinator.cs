using System.Globalization;
using System.Text.Json;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.Infrastructure.Processing;

/// <summary>Whether a request was taken, deferred, or refused.</summary>
public enum TranscriptionAcceptance
{
    /// <summary>A worker is starting now.</summary>
    Started,

    /// <summary>Accepted and durably queued. It runs when recording stops.</summary>
    Deferred,

    /// <summary>Nothing was queued: the session cannot be processed as it stands.</summary>
    Rejected,

    /// <summary>Another job already holds the coordinator. Only one heavy job runs at a time.</summary>
    Busy,
}

/// <summary>How one transcription request ended.</summary>
public sealed record TranscriptionRunResult(
    ProcessingStageState State,
    int? Revision,
    string? FailureCode,
    string Message,
    WorkerOutcome? Outcome = null)
{
    public bool Succeeded => State == ProcessingStageState.Succeeded;
}

/// <summary>
/// A request that was accepted. <see cref="Completion"/> finishes when the work finally does,
/// which may be after several deferrals if recording keeps taking priority.
/// </summary>
public sealed record TranscriptionTicket(
    TranscriptionAcceptance Acceptance,
    string? JobId,
    int? Revision,
    string Message,
    Task<TranscriptionRunResult> Completion)
{
    public bool Accepted => Acceptance is TranscriptionAcceptance.Started or TranscriptionAcceptance.Deferred;
}

/// <summary>Progress for the UI. Carries no transcript content.</summary>
public sealed class TranscriptionProgressEventArgs(
    string sessionId,
    string jobId,
    string stage,
    int completedUnits,
    int totalUnits) : EventArgs
{
    public string SessionId { get; } = sessionId;

    public string JobId { get; } = jobId;

    public string Stage { get; } = stage;

    public int CompletedUnits { get; } = completedUnits;

    public int TotalUnits { get; } = totalUnits;

    public double Fraction => TotalUnits <= 0 ? 0 : Math.Clamp((double)CompletedUnits / TotalUnits, 0, 1);
}

/// <summary>
/// The one place transcription jobs are decided on, started, and settled.
///
/// <para>
/// <b>Recording always has priority, and the policy is cancel-and-requeue.</b> A request made
/// while capture is live is queued rather than run. If capture starts while a worker is running,
/// that worker is cancelled at a safe boundary and the request is queued again as a fresh
/// attempt. The user's request is never dropped: it waits, and it runs when the machine is theirs
/// again. Suspending the worker instead was rejected — a suspended Python process still holds its
/// memory and, later, its GPU allocation, so it would keep exactly the resources the policy
/// exists to release.
/// </para>
///
/// <para>
/// Only one job runs at a time, across all sessions. Transcription is the heavy stage, and two of
/// them would compete for the same GPU while telling the user that both are progressing.
/// </para>
/// </summary>
public sealed class TranscriptionCoordinator : IDisposable
{
    /// <summary>How often progress reaches the disk. The UI is updated every time regardless.</summary>
    private static readonly TimeSpan ProgressPersistInterval = TimeSpan.FromMilliseconds(500);

    private readonly ISessionStore _sessions;
    private readonly ITranscriptionStore _transcripts;
    private readonly WorkerSupervisor _supervisor;
    private readonly ICaptureActivityGate _captureGate;
    private readonly TimeProvider _clock;
    private readonly Lock _sync = new();

    private PendingRequest? _pending;
    private RunningAttempt? _running;
    private bool _preparing;
    private bool _disposed;

    public TranscriptionCoordinator(
        ISessionStore sessions,
        ITranscriptionStore transcripts,
        WorkerSupervisor supervisor,
        ICaptureActivityGate? captureGate = null,
        TimeProvider? clock = null,
        ProcessingPreparation? preparation = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _captureGate = captureGate ?? NoRecordingInProgressGate.Instance;
        _clock = clock ?? TimeProvider.System;
        _preparation = preparation;
    }

    private readonly ProcessingPreparation? _preparation;

    /// <summary>
    /// True when this build can prepare for a production profile at all. False leaves the
    /// placeholder path working exactly as before, which is what keeps a machine with no models
    /// fully usable.
    /// </summary>
    public bool SupportsProductionProfiles => _preparation is not null;

    public ProcessingPreparation? Preparation => _preparation;

    public event EventHandler<PreparationProgressEventArgs>? PreparationProgress;

    /// <summary>
    /// Installs artifacts, builds the 16 kHz derivatives, and plans transcription windows for a
    /// production profile — and stops there.
    ///
    /// <para>
    /// Deliberately separate from <see cref="Request"/>. Nothing here loads a model or produces a
    /// transcript; the deterministic placeholder remains the only thing that does. Recording still
    /// wins: preparation is refused while capture is live, and cancelling it leaves sources and
    /// every previous revision untouched.
    /// </para>
    /// </summary>
    public async Task<PreparationResult> PrepareAsync(
        string sessionId,
        string profileId,
        bool installMissing = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_preparation is null)
        {
            return new PreparationResult(
                PreparationStage.Failed,
                "This installation cannot prepare production transcription.",
                "preparation_unavailable");
        }

        if (_captureGate.IsCaptureActive)
        {
            return new PreparationResult(
                PreparationStage.Blocked,
                "Preparation is waiting because a recording is in progress. Recording always has priority.",
                "recording_active");
        }

        lock (_sync)
        {
            if (_running is not null || _preparing)
            {
                return new PreparationResult(
                    PreparationStage.Blocked,
                    "Another processing job is already running. Only one runs at a time.",
                    "busy");
            }

            _preparing = true;
        }

        try
        {
            SessionSnapshot? snapshot = _sessions.ReadSnapshot(sessionId);
            SessionPaths paths = _sessions.Resolve(sessionId);

            if (snapshot is null)
            {
                return new PreparationResult(PreparationStage.Failed, "That recording could not be read.", "session_unreadable");
            }

            SourceVerification verification = SourceChunkVerifier.Verify(snapshot, paths.Root);
            if (!verification.Ok)
            {
                return new PreparationResult(
                    PreparationStage.Failed, DescribeRefusal(verification.Code!), verification.Code);
            }

            RequestBuildResult built = BuildRequest(
                snapshot, paths, new TranscriptionOptions(), 1, paths.TranscriptStagingPath(1));

            if (!built.Succeeded)
            {
                return new PreparationResult(
                    PreparationStage.Failed,
                    "EchoForge could not describe this recording to the preparation pipeline.",
                    built.Failure?.Code);
            }

            void Forward(object? sender, PreparationProgressEventArgs e) => PreparationProgress?.Invoke(this, e);

            _preparation.Progress += Forward;
            try
            {
                return await _preparation
                    .PrepareAsync(built.Request!, profileId, installMissing, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _preparation.Progress -= Forward;
            }
        }
        finally
        {
            lock (_sync)
            {
                _preparing = false;
            }

            RaiseStateChanged();
        }
    }

    public event EventHandler<TranscriptionProgressEventArgs>? ProgressChanged;

    /// <summary>Raised whenever the durable state changed enough for the UI to re-read it.</summary>
    public event EventHandler? StateChanged;

    public bool IsRunning
    {
        get { lock (_sync) { return _running is not null; } }
    }

    public bool IsQueued
    {
        get { lock (_sync) { return _pending is not null && _running is null; } }
    }

    public string? ActiveSessionId
    {
        get { lock (_sync) { return _pending?.SessionId; } }
    }

    /// <summary>
    /// The durable state, corrected for the job this coordinator is actually running.
    ///
    /// <para>
    /// The store reconstructs from the journal, and from there an attempt with no terminal event
    /// is indistinguishable from one whose process died — so it reports it as interrupted, which
    /// is the right answer after a restart and the wrong one while the work is under way. Only
    /// the coordinator knows a job is alive, so only the coordinator can say so.
    /// </para>
    /// </summary>
    public TranscriptionState StateFor(string sessionId)
    {
        TranscriptionState state = _transcripts.Read(sessionId);

        PendingRequest? pending;
        bool running;
        lock (_sync)
        {
            pending = _pending;
            running = _running is not null;
        }

        if (pending is null ||
            !string.Equals(pending.SessionId, sessionId, StringComparison.Ordinal) ||
            state.CurrentJob is not { } job ||
            !string.Equals(job.JobId, pending.Attempt.JobId, StringComparison.Ordinal))
        {
            return state;
        }

        ProcessingStageState live = running ? ProcessingStageState.Running : ProcessingStageState.Queued;

        return state with
        {
            Stage = live,
            CurrentJob = job with { State = live, FailureCode = null, FailureSummary = null },
        };
    }

    /// <summary>
    /// What the last worker to complete a handshake said about itself, or null before one has.
    /// Reported by the worker rather than guessed at by the host.
    /// </summary>
    public WorkerEnvironment? LastWorkerEnvironment { get; private set; }

    /// <summary>Chooses an existing revision as the active one.</summary>
    public bool SelectRevision(string sessionId, int revision)
    {
        bool selected = _transcripts.SelectRevision(sessionId, revision, Now);
        if (selected)
        {
            RaiseStateChanged();
        }

        return selected;
    }

    /// <summary>Reads an activated revision, verified against the digest it was activated under.</summary>
    public TranscriptDocument? ReadTranscript(string sessionId, int revision) =>
        _transcripts.ReadTranscript(sessionId, revision);

    /// <summary>The file behind a revision, for an export that copies canonical bytes.</summary>
    public string RevisionPath(string sessionId, int revision) =>
        _transcripts.RevisionPath(sessionId, revision);

    /// <summary>
    /// Settles anything a previous run left staged. Called at startup, beside recovery: a staged
    /// transcript is the visible remains of an attempt whose process died.
    /// </summary>
    public int DiscardOrphanStaging(string sessionId) => _transcripts.DiscardOrphanStaging(sessionId, Now);

    /// <summary>
    /// Asks for a transcript.
    ///
    /// <para>
    /// Validation happens before a revision number is allocated, so a session that cannot be
    /// processed leaves no trace of a job that never existed.
    /// </para>
    /// </summary>
    public TranscriptionTicket Request(string sessionId, TranscriptionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        options ??= new TranscriptionOptions();

        lock (_sync)
        {
            if (_pending is not null)
            {
                return Refused(
                    TranscriptionAcceptance.Busy,
                    "Another transcription is already in progress. Only one runs at a time.");
            }
        }

        SessionSnapshot? snapshot = _sessions.ReadSnapshot(sessionId);
        if (snapshot is null)
        {
            return Refused(TranscriptionAcceptance.Rejected, "That recording could not be read.");
        }

        SessionPaths paths = _sessions.Resolve(sessionId);
        SourceVerification verification = SourceChunkVerifier.Verify(snapshot, paths.Root);
        if (!verification.Ok)
        {
            return Refused(
                TranscriptionAcceptance.Rejected,
                DescribeRefusal(verification.Code!),
                verification.Code);
        }

        // Built once here purely to derive the source manifest digest: the attempt is recorded
        // against that digest, so it has to exist before the attempt does. The request itself is
        // discarded and rebuilt against the real staging path once a revision is allocated.
        RequestBuildResult built = BuildRequest(
            snapshot, paths, options, revision: 1, stagingPath: paths.TranscriptStagingPath(1));
        if (!built.Succeeded)
        {
            return Refused(TranscriptionAcceptance.Rejected, "EchoForge could not describe this recording to the worker.", built.Failure?.Code);
        }

        string manifest = TranscriptionRequestBuilder.SourceManifestSha256(built.Request!);
        string jobId = NewJobId();

        TranscriptionAttempt attempt = _transcripts.BeginAttempt(sessionId, jobId, manifest, options, Now);

        TaskCompletionSource<TranscriptionRunResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            _pending = new PendingRequest(sessionId, options, manifest, completion) { Attempt = attempt };
        }

        RaiseStateChanged();
        bool started = Pump();

        return new TranscriptionTicket(
            started ? TranscriptionAcceptance.Started : TranscriptionAcceptance.Deferred,
            jobId,
            attempt.Revision,
            started
                ? "Transcribing."
                : "Transcription is queued. It will start when recording stops, because recording has priority.",
            completion.Task);
    }

    /// <summary>Cancels whatever is running or queued. The user's decision, so it is not requeued.</summary>
    public void Cancel()
    {
        RunningAttempt? running;
        PendingRequest? pending;

        lock (_sync)
        {
            running = _running;
            pending = _pending;

            if (running is not null)
            {
                running.RequeueOnCancel = false;
            }
        }

        if (running is not null)
        {
            running.Cancellation.Cancel();
            return;
        }

        // Queued but never started: settle it here, since no worker will report anything.
        if (pending is not null)
        {
            _transcripts.MarkCancelled(pending.Attempt, Now);
            Settle(pending, new TranscriptionRunResult(
                ProcessingStageState.Cancelled,
                null,
                null,
                "Transcription was cancelled. Your recording is unchanged."));
        }
    }

    /// <summary>
    /// Tells the coordinator that capture may have started or stopped.
    ///
    /// <para>
    /// Driven by the recorder's own state changes rather than polled, so the reaction is
    /// immediate and deterministic: a test can make capture start and know exactly when the
    /// running worker was asked to stop.
    /// </para>
    /// </summary>
    public void CaptureStateChanged()
    {
        RunningAttempt? toCancel = null;

        lock (_sync)
        {
            if (_captureGate.IsCaptureActive)
            {
                if (_running is { } running)
                {
                    running.RequeueOnCancel = true;
                    toCancel = running;
                }
            }
        }

        if (toCancel is not null)
        {
            toCancel.Cancellation.Cancel();
            return;
        }

        Pump();
    }

    /// <summary>Waits for any in-flight job to settle. Used by shutdown and by tests.</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        Task? work;
        lock (_sync)
        {
            work = _running?.Work;
        }

        if (work is null)
        {
            return true;
        }

        Task finished = await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false);
        return ReferenceEquals(finished, work);
    }

    // -- the pump ----------------------------------------------------------------------------

    /// <summary>Starts the pending request if nothing forbids it. Returns true if it started.</summary>
    private bool Pump()
    {
        PendingRequest pending;
        RunningAttempt running;

        lock (_sync)
        {
            // Preparation counts as the one heavy job too: it hashes gigabytes, resamples hours
            // of audio, and would otherwise run alongside a worker doing the same thing.
            if (_disposed || _running is not null || _preparing || _pending is null)
            {
                return false;
            }

            if (_captureGate.IsCaptureActive)
            {
                return false;
            }

            pending = _pending;
            running = new RunningAttempt(pending.Attempt);
            _running = running;
        }

        running.Work = Task.Run(() => ExecuteAsync(pending, running));
        return true;
    }

    private async Task ExecuteAsync(PendingRequest pending, RunningAttempt running)
    {
        TranscriptionAttempt attempt = running.Attempt;
        TranscriptionRunResult result;

        try
        {
            result = await RunWorkerAsync(pending, running).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            _transcripts.MarkFailed(attempt, "coordinator_error", SafeFailureMessage, Now);
            result = new TranscriptionRunResult(ProcessingStageState.Failed, null, "coordinator_error", SafeFailureMessage);
        }

        bool requeue;
        lock (_sync)
        {
            _running = null;
            requeue = running.RequeueOnCancel && result.State == ProcessingStageState.Cancelled;
        }

        if (requeue)
        {
            // A fresh attempt, with its own revision number. Reusing the cancelled one would
            // make two different runs indistinguishable in the journal.
            TranscriptionAttempt next = _transcripts.BeginAttempt(
                pending.SessionId, NewJobId(), pending.SourceManifestSha256, pending.Options, Now);

            lock (_sync)
            {
                pending.Attempt = next;
            }

            RaiseStateChanged();
            Pump();
            return;
        }

        Settle(pending, result);
    }

    private async Task<TranscriptionRunResult> RunWorkerAsync(PendingRequest pending, RunningAttempt running)
    {
        TranscriptionAttempt attempt = running.Attempt;

        SessionSnapshot? snapshot = _sessions.ReadSnapshot(pending.SessionId);
        SessionPaths paths = _sessions.Resolve(pending.SessionId);

        if (snapshot is null)
        {
            _transcripts.MarkFailed(attempt, "session_unreadable", SafeFailureMessage, Now);
            return new TranscriptionRunResult(ProcessingStageState.Failed, null, "session_unreadable", SafeFailureMessage);
        }

        // Re-verified immediately before the worker runs. The check at request time may be old
        // by the time a deferred job finally starts.
        SourceVerification verification = SourceChunkVerifier.Verify(snapshot, paths.Root);
        if (!verification.Ok)
        {
            string message = DescribeRefusal(verification.Code!);
            _transcripts.MarkFailed(attempt, verification.Code!, message, Now);
            return new TranscriptionRunResult(ProcessingStageState.Failed, null, verification.Code, message);
        }

        RequestBuildResult built = BuildRequest(snapshot, paths, pending.Options, attempt.Revision, attempt.StagingPath);
        if (!built.Succeeded)
        {
            _transcripts.MarkFailed(attempt, built.Failure!.Code, SafeFailureMessage, Now);
            return new TranscriptionRunResult(ProcessingStageState.Failed, null, built.Failure.Code, SafeFailureMessage);
        }

        TranscriptionRequest request = built.Request!;
        _transcripts.MarkStarted(attempt, pending.Options, Now);
        RaiseStateChanged();

        DateTimeOffset lastPersist = DateTimeOffset.MinValue;

        // Deliberately not Progress<T>. That posts each report as its own work item, so two
        // updates can be handled out of order and a progress bar can jump backwards. The
        // supervisor's read loop is strictly ordered; handling reports inline on it keeps that
        // ordering all the way to the UI, and the view model does its own dispatching anyway.
        InlineProgress progress = new(update =>
        {
            DateTimeOffset now = Now;
            if (now - lastPersist >= ProgressPersistInterval || update.CompletedUnits == update.TotalUnits)
            {
                lastPersist = now;
                _transcripts.RecordProgress(attempt, update.Stage, update.CompletedUnits, update.TotalUnits);
            }

            ProgressChanged?.Invoke(this, new TranscriptionProgressEventArgs(
                pending.SessionId, attempt.JobId, update.Stage, update.CompletedUnits, update.TotalUnits));
        });

        WorkerRunResult worker = await _supervisor
            .TranscribeAsync(attempt.JobId, request, progress, running.Cancellation.Token)
            .ConfigureAwait(false);

        if (worker.Environment is not null)
        {
            LastWorkerEnvironment = worker.Environment;
        }

        return Settle(attempt, request, worker);
    }

    /// <summary>Turns a worker outcome into a durable result. Every path leaves prior work intact.</summary>
    private TranscriptionRunResult Settle(
        TranscriptionAttempt attempt,
        TranscriptionRequest request,
        WorkerRunResult worker)
    {
        switch (worker.Outcome)
        {
            case WorkerOutcome.Succeeded:
                return Activate(attempt, request, worker);

            case WorkerOutcome.Cancelled:
                _transcripts.MarkCancelled(attempt, Now);
                return new TranscriptionRunResult(
                    ProcessingStageState.Cancelled, null, null, worker.UserMessage, worker.Outcome);

            case WorkerOutcome.Busy:
                // The gate closed between the pump and the launch. Treat it as a cancellation so
                // the requeue path takes over and the request is not lost.
                _transcripts.MarkCancelled(attempt, Now);
                return new TranscriptionRunResult(
                    ProcessingStageState.Cancelled, null, null, worker.UserMessage, worker.Outcome);

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

                _transcripts.MarkFailed(attempt, code, worker.UserMessage, Now);
                return new TranscriptionRunResult(
                    ProcessingStageState.Failed, null, code, worker.UserMessage, worker.Outcome);
            }
        }
    }

    /// <summary>
    /// Validates the staged transcript and activates it.
    ///
    /// <para>
    /// The worker already checked its own output and the supervisor already verified the digest.
    /// This is the third check, and it is the one that matters: it is the last point before a
    /// file becomes the canonical transcript of a meeting, and the only one that can still refuse
    /// without anything having been retracted.
    /// </para>
    /// </summary>
    private TranscriptionRunResult Activate(
        TranscriptionAttempt attempt,
        TranscriptionRequest request,
        WorkerRunResult worker)
    {
        TranscriptionOutput output = worker.Output!;

        TranscriptDocument? transcript;
        try
        {
            using FileStream stream = File.OpenRead(attempt.StagingPath);
            transcript = JsonSerializer.Deserialize<TranscriptDocument>(stream, TranscriptDocument.Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Reject(attempt, "transcript_unreadable", ex.GetType().Name);
        }

        if (transcript is null)
        {
            return Reject(attempt, "transcript_unreadable", "empty document");
        }

        if (!string.Equals(transcript.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            return Reject(attempt, "transcript_wrong_session", "session mismatch");
        }

        string expectedManifest = TranscriptionRequestBuilder.SourceManifestSha256(request);
        if (!string.Equals(transcript.SourceManifestSha256, expectedManifest, StringComparison.Ordinal))
        {
            // The transcript names audio other than the audio it was given.
            return Reject(attempt, "transcript_source_mismatch", "source manifest mismatch");
        }

        TranscriptVerdict verdict = TranscriptValidator.Validate(transcript);
        if (!verdict.IsValid)
        {
            return Reject(attempt, "transcript_invalid", string.Join("; ", verdict.Problems.Take(3)));
        }

        ActivationOutcome activation = _transcripts.Activate(
            new ActivationRequest(attempt, output.Sha256, transcript, WorkerProtocol.Version, request.Options.Profile),
            Now);

        if (!activation.Activated)
        {
            return Reject(attempt, "activation_refused", activation.Refusal ?? "refused");
        }

        RaiseStateChanged();

        return new TranscriptionRunResult(
            ProcessingStageState.Succeeded,
            activation.Revision!.Revision,
            null,
            transcript.Model.RecognizesSpeech
                ? "Transcription finished."
                : "Transcription finished. This run used the deterministic placeholder backend, which does not recognise speech.",
            WorkerOutcome.Succeeded);
    }

    private TranscriptionRunResult Reject(TranscriptionAttempt attempt, string code, string detail)
    {
        // The detail goes nowhere near the user. The message is fixed text chosen by code.
        _ = detail;
        const string message = "The transcript EchoForge received was not usable, so nothing was changed. " +
                               "Your recording and any earlier transcript are untouched.";

        _transcripts.MarkFailed(attempt, code, message, Now);
        return new TranscriptionRunResult(ProcessingStageState.Failed, null, code, message);
    }

    // -- plumbing -----------------------------------------------------------------------------

    private RequestBuildResult BuildRequest(
        SessionSnapshot snapshot,
        SessionPaths paths,
        TranscriptionOptions options,
        int revision,
        string stagingPath) =>
        TranscriptionRequestBuilder.Build(
            snapshot,
            paths.Root,
            stagingPath,
            Math.Max(1, revision),
            Now,
            new RequestOptions
            {
                Backend = options.Backend,
                Profile = options.Profile,
                Language = options.Language,
                SegmentSeconds = options.SegmentSeconds,
                TestMode = options.TestMode,
                TestDelaySeconds = options.TestDelaySeconds,
            });

    private void Settle(PendingRequest pending, TranscriptionRunResult result)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
            }
        }

        RaiseStateChanged();
        pending.Completion.TrySetResult(result);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private DateTimeOffset Now => _clock.GetUtcNow();

    private static string NewJobId() => Guid.NewGuid().ToString("n");

    private static TranscriptionTicket Refused(TranscriptionAcceptance acceptance, string message, string? code = null) =>
        new(
            acceptance,
            null,
            null,
            message,
            Task.FromResult(new TranscriptionRunResult(
                acceptance == TranscriptionAcceptance.Busy ? ProcessingStageState.Queued : ProcessingStageState.NotRequested,
                null,
                code,
                message)));

    /// <summary>
    /// Safe sentences for a refused session, chosen by code. None of them can contain a path or
    /// anything a worker printed.
    /// </summary>
    private static string DescribeRefusal(string code) => code switch
    {
        "session_not_settled" => "That recording has not finished saving yet. Try again once it has.",
        "no_audio" => "That recording has no saved audio to transcribe.",
        "no_epochs" or "epoch_open" => "That recording did not finish cleanly, so its timeline is incomplete.",
        "chunk_missing" => "Some of that recording's audio is missing. Nothing has been changed or deleted.",
        "chunk_changed" => "That recording's audio no longer matches what was saved, so it will not be transcribed.",
        "duplicate_chunk" or "chunk_format_invalid" or "chunk_path_invalid" or "chunk_path_escapes" =>
            "That recording's saved audio could not be verified, so it will not be transcribed.",
        _ => "That recording cannot be transcribed as it stands.",
    };

    private const string SafeFailureMessage =
        "Transcription could not be completed. Your recording is unchanged and you can try again.";

    public void Dispose()
    {
        RunningAttempt? running;
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

    private sealed class PendingRequest(
        string sessionId,
        TranscriptionOptions options,
        string sourceManifestSha256,
        TaskCompletionSource<TranscriptionRunResult> completion)
    {
        public string SessionId { get; } = sessionId;

        public TranscriptionOptions Options { get; } = options;

        public string SourceManifestSha256 { get; } = sourceManifestSha256;

        public TaskCompletionSource<TranscriptionRunResult> Completion { get; } = completion;

        /// <summary>Replaced on a requeue: each attempt owns its own revision number.</summary>
        public required TranscriptionAttempt Attempt { get; set; }
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that runs its handler on the reporting thread, so reports
    /// arrive in the order they were made.
    /// </summary>
    private sealed class InlineProgress(Action<ProgressMessage> handle) : IProgress<ProgressMessage>
    {
        public void Report(ProgressMessage value) => handle(value);
    }

    private sealed class RunningAttempt(TranscriptionAttempt attempt)
    {
        public TranscriptionAttempt Attempt { get; } = attempt;

        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>
        /// True when the cancellation came from recording taking priority rather than from the
        /// user. The request is then queued again instead of being reported as cancelled.
        /// </summary>
        public bool RequeueOnCancel { get; set; }

        public Task? Work { get; set; }
    }
}
