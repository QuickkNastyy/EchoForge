using System.Globalization;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Recording;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Storage;

namespace EchoForge.Core.Recording;

/// <summary>The endpoints a recording is pinned to, chosen before Start and fixed for the session.</summary>
public sealed record RecordingRequest(
    string RenderEndpointId,
    string RenderDeviceName,
    string CaptureEndpointId,
    string CaptureDeviceName);

/// <summary>
/// What the capture hardware is doing, as distinct from what the session is.
///
/// <para>
/// Stop is not instantaneous: the capture threads have to be joined, the final chunks finalized
/// and hashed, and the snapshot written. The red indicator is bound to this, so it keeps telling
/// the truth for as long as a capture source may still be live rather than vanishing the moment
/// the button is pressed.
/// </para>
/// </summary>
public enum CapturePhase
{
    /// <summary>Nothing is open.</summary>
    Idle,

    /// <summary>Audio is being captured right now.</summary>
    Capturing,

    /// <summary>Stop has been asked for; capture threads are still being wound down.</summary>
    StoppingCapture,

    /// <summary>Capture has stopped; chunks and the snapshot are still being written.</summary>
    Saving,

    /// <summary>Everything is durable.</summary>
    Stopped,
}

/// <summary>Raised whenever the authoritative recording state changes.</summary>
public sealed class RecordingStateChangedEventArgs(SessionState state, string? reason) : EventArgs
{
    public SessionState State { get; } = state;

    public string? Reason { get; } = reason;
}

/// <summary>
/// Cumulative session totals, as opposed to the current epoch's counters.
/// </summary>
/// <param name="ActiveDuration">
/// Time spent actually capturing, summed across every epoch. Pause gaps are excluded.
/// </param>
/// <param name="PausedDuration">Time spent between epochs, kept separate from recorded time.</param>
public sealed record SessionTotals(
    TimeSpan ActiveDuration,
    TimeSpan PausedDuration,
    long BytesWritten,
    int ChunkCount,
    IReadOnlyDictionary<SourceTrack, int> ChunksPerTrack);

/// <summary>
/// The authoritative recording state machine.
///
/// <para>
/// This lives below the view model by design. The UI observes it and sends it commands; it never
/// owns capture truth, so a tray icon or indicator cannot disagree with what is actually being
/// captured — both read the same state from here.
/// </para>
///
/// <para>
/// Pause closes the current epoch: capture stops, every active chunk is finalized, and the gap is
/// journalled. Resume opens a new epoch with new chunks that continue the session's numbering, so
/// a finalized WAV is never appended to. Totals reported to the UI are cumulative across epochs
/// and do not reset when an epoch closes.
/// </para>
/// </summary>
public sealed class RecordingController : IDisposable
{
    private readonly ISessionStore _store;
    private readonly ICaptureEngineFactory _engineFactory;
    private readonly ICaptureClock _clock;
    private readonly IDiskSpaceProbe _disk;
    private readonly DiskPolicy _policy;
    private readonly JournalPersistenceQueue _persistence;

    private readonly List<SessionEpoch> _epochs = [];
    private readonly Dictionary<SourceTrack, TrackAccumulator> _tracks = [];
    private readonly HashSet<(SourceTrack Track, int Index)> _journalledChunks = [];
    private readonly HashSet<(long Generation, int Epoch, SourceTrack Track)> _journalledFaults = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<ChunkNotification> _chunkNotifications = new();
    private readonly Lock _gate = new();

    /// <summary>Bumped on every Start, so callbacks from a previous session can be discarded.</summary>
    private long _generation;

    private ICaptureEngine? _engine;
    private Action? _detachEngineHandlers;
    private RecordingRequest? _request;
    private SessionPaths? _paths;
    private DateTimeOffset _createdUtc;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _endedUtc;
    private DateTimeOffset? _intendedEndUtc;
    private SessionState? _intendedOutcome;
    private bool _terminalEventWritten;
    private TimeSpan _completedEpochDuration;
    private long _completedBytes;
    private DateTimeOffset? _currentEpochStartedUtc;
    private int _nextChunkIndex = 1;
    private int _epochIndex;
    private bool _diskWarned;
    private bool _disposed;

    private readonly IEndpointMonitor? _endpoints;
    private readonly IPowerMonitor? _power;
    private readonly ISessionLeaseProvider? _leases;
    private readonly LifecycleSignalQueue _signals;
    private readonly HashSet<string> _lostEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private ISessionLease? _lease;

    public RecordingController(
        ISessionStore store,
        ICaptureEngineFactory engineFactory,
        ICaptureClock clock,
        IDiskSpaceProbe disk,
        DiskPolicy? policy = null,
        IEndpointMonitor? endpoints = null,
        IPowerMonitor? power = null,
        ISessionLeaseProvider? leases = null)
    {
        _leases = leases;
        _signals = new LifecycleSignalQueue(detail => Notice?.Invoke(this, detail));
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engineFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(disk);

        _store = store;
        _engineFactory = engineFactory;
        _clock = clock;
        _disk = disk;
        _policy = policy ?? new DiskPolicy();
        _persistence = new JournalPersistenceQueue(store, OnPersistenceFailure);

        _endpoints = endpoints;
        if (_endpoints is not null)
        {
            _endpoints.EndpointLost += OnEndpointLost;
            _endpoints.DefaultChanged += OnDefaultEndpointChanged;
            _endpoints.Start();
        }

        _power = power;
        if (_power is not null)
        {
            _power.Suspending += OnSuspending;
            _power.Resumed += OnResumed;
            _power.Start();
        }
    }

    /// <summary>
    /// True after a resume, until the user explicitly starts a new epoch. EchoForge never
    /// restarts capture on its own after the machine wakes.
    /// </summary>
    public bool AwaitingResumeAfterSuspend { get; private set; }

    /// <summary>Endpoints Windows has reported as lost during this session.</summary>
    public IReadOnlyCollection<string> LostEndpoints => _lostEndpoints;

    /// <summary>
    /// Windows reported an endpoint gone. Only the two pinned for this session matter; an
    /// unrelated device change must not disturb a running recording.
    /// </summary>
    /// <summary>
    /// Runs on a COM callback thread. Posts and returns; it must never join a thread, hash a
    /// file, fsync the journal, write a snapshot, or wait on the lifecycle lock.
    /// </summary>
    private void OnEndpointLost(object? sender, EndpointChangedEventArgs e) =>
        _signals.Post(() => HandleEndpointLost(e));

    private void HandleEndpointLost(EndpointChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_request is null || !IsCapturing)
            {
                return;
            }

            bool isRender = string.Equals(e.EndpointId, _request.RenderEndpointId, StringComparison.OrdinalIgnoreCase);
            bool isCapture = string.Equals(e.EndpointId, _request.CaptureEndpointId, StringComparison.OrdinalIgnoreCase);

            if (!isRender && !isCapture)
            {
                return;
            }

            _lostEndpoints.Add(e.EndpointId);
            SourceTrack track = isRender ? SourceTrack.System : SourceTrack.Microphone;

            EnqueueTrackFault(SessionId!, _generation, _epochIndex, track,
                $"endpoint {e.Change.ToString().ToLowerInvariant()}");

            bool bothLost =
                _lostEndpoints.Contains(_request.RenderEndpointId) &&
                _lostEndpoints.Contains(_request.CaptureEndpointId);

            if (bothLost)
            {
                CloseEpoch(EpochEndReason.DeviceLost);
                FinalizeSession(SessionState.Failed, "both endpoints were lost");
                return;
            }

            SetState(SessionState.Degraded,
                $"{(isRender ? "system audio" : "the microphone")} was {e.Change.ToString().ToLowerInvariant()}; " +
                "the other track is still recording");
        }
    }

    /// <summary>Reported, never followed. The pinned endpoints stay pinned.</summary>
    private void OnDefaultEndpointChanged(object? sender, DefaultEndpointChangedEventArgs e)
    {
        // Deliberately does nothing to the recording. Recorded for diagnostics only.
        if (SessionId is not null && IsCapturing)
        {
            _persistence.Enqueue(SessionId, JournalEvent.Create(
                JournalEventTypes.DefaultEndpointChanged, _clock.UtcNow(),
                ("device_id", e.EndpointId),
                ("flow", e.IsRender ? "render" : "capture")));
        }
    }

    /// <summary>
    /// Finalizes both tracks and closes the epoch before the machine suspends. Audio during
    /// sleep is unrecoverable, so the missing period becomes an explicit gap rather than a
    /// pretence of continuity.
    /// </summary>
    private void OnSuspending(object? sender, EventArgs e) => _signals.Post(HandleSuspending);

    private void HandleSuspending()
    {
        lock (_gate)
        {
            if (State is not (SessionState.Recording or SessionState.Degraded))
            {
                return;
            }

            CloseEpoch(EpochEndReason.Suspended);
            SetState(SessionState.Paused, "the computer went to sleep; recording paused and everything so far is saved");
            AwaitingResumeAfterSuspend = true;
            WriteSnapshot();
        }
    }

    private void OnResumed(object? sender, EventArgs e) => _signals.Post(() =>
    {
        if (AwaitingResumeAfterSuspend)
        {
            Notice?.Invoke(this, "The computer woke up. Recording is paused — choose Resume to continue into a new segment.");
        }
    });

    /// <summary>Waits for queued OS signals to be processed. Used at boundaries and by tests.</summary>
    public bool WaitForSignals(TimeSpan timeout) => _signals.Drain(timeout);

    /// <summary>An advisory message for the user that does not change the recording state.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>
    /// A journal write failed or fell behind. The audio and its metadata records are already
    /// durable, so nothing is lost; the ledger simply needs reconciling on the next start.
    /// </summary>
    private void OnPersistenceFailure(string detail)
    {
        NeedsReconciliation = true;
        Notice?.Invoke(this, "Your audio is safe, but EchoForge could not keep its record file " +
            "up to date. This recording will be checked and repaired the next time EchoForge starts.");
        _ = detail;
    }

    public SessionState State { get; private set; } = SessionState.New;

    public string? SessionId { get; private set; }

    public IReadOnlyList<SessionEpoch> Epochs => _epochs;

    public bool IsCapturing => State is SessionState.Recording or SessionState.Degraded;

    /// <summary>What the hardware is doing. Drives the red indicator and the tray icon.</summary>
    public CapturePhase Phase { get; private set; } = CapturePhase.Idle;

    /// <summary>
    /// True while any capture source may still be live, including the window between asking to
    /// stop and the capture threads actually stopping.
    /// </summary>
    public bool CaptureMayBeLive => Phase is CapturePhase.Capturing or CapturePhase.StoppingCapture;

    /// <summary>The moment the session finished. Assigned exactly once, then never changes.</summary>
    public DateTimeOffset? EndedUtc => _endedUtc;

    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    public (bool Allowed, string? Reason) CanStart(string sessionRoot) =>
        _policy.CanStart(_disk.AvailableBytes(sessionRoot));

    public void Start(RecordingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (State is not (SessionState.New or SessionState.Recorded or SessionState.Failed))
            {
                throw new InvalidOperationException($"Cannot start a recording from state {State}.");
            }

            ResetSessionState();
            _lostEndpoints.Clear();
            _journalledFaults.Clear();
            _chunkNotifications.Clear();
            _generation++;
            AwaitingResumeAfterSuspend = false;
            _request = request;

            SessionId = Guid.NewGuid().ToString("n");
            _createdUtc = _clock.UtcNow();
            _paths = _store.Create(SessionId);

            // Claim the session for its whole lifetime, so startup recovery in this or another
            // process leaves it alone rather than repairing chunks still being written.
            if (_leases is not null)
            {
                _lease = _leases.TryAcquire(SessionId);
                if (_lease is null)
                {
                    const string Busy = "this session folder is already in use";
                    FailStart(Busy, journalSession: false);
                    throw new InvalidOperationException(Busy);
                }
            }

            (bool allowed, string? reason) = _policy.CanStart(_disk.AvailableBytes(_paths.Root));
            if (!allowed)
            {
                FailStart(reason ?? "not enough free space", journalSession: false);
                throw new InvalidOperationException(reason);
            }

            CreateRecoveryReserve(_paths);

            _store.Append(SessionId, JournalEvent.Create(
                JournalEventTypes.SessionCreated, _createdUtc, ("session_id", SessionId)));

            try
            {
                OpenEpoch();
                _startedUtc = _epochs[0].StartedUtc;
            }
            catch (Exception ex)
            {
                FailStart($"{ex.GetType().Name}: {ex.Message}", journalSession: true);
                throw;
            }
        }
    }

    /// <summary>
    /// Takes over a recording that was never finished, without starting capture.
    ///
    /// <para>
    /// The session is adopted whole: its ID, epochs, tracks, endpoint pins, and chunk numbering
    /// all carry over, and the controller lands in <see cref="SessionState.Paused"/>. Nothing is
    /// captured until the user explicitly resumes, and when they do the new epoch continues the
    /// numbering so no finalized WAV is ever appended to. Finalizing instead is equally valid.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The session is already claimed by another owner, or this controller is mid-session.
    /// </exception>
    public void AdoptRecoveredSession(RecoveryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (State is not (SessionState.New or SessionState.Recorded or SessionState.Failed or SessionState.NeedsAttention))
            {
                throw new InvalidOperationException($"Cannot adopt a recording from state {State}.");
            }

            ResetSessionState();
            _lostEndpoints.Clear();
            _journalledFaults.Clear();
            _chunkNotifications.Clear();
            _generation++;

            SessionId = candidate.SessionId;
            _paths = _store.Resolve(candidate.SessionId);
            _createdUtc = candidate.CreatedUtc;
            _startedUtc = candidate.StartedUtc;

            // Only one process may continue a session. Claiming the lease is what enforces it.
            if (_leases is not null)
            {
                _lease = _leases.TryAcquire(candidate.SessionId);
                if (_lease is null)
                {
                    SessionId = null;
                    throw new InvalidOperationException(
                        "This recording is already open somewhere else.");
                }
            }

            _request = new RecordingRequest(
                candidate.RenderEndpointId, candidate.RenderDeviceName,
                candidate.CaptureEndpointId, candidate.CaptureDeviceName);

            _epochs.AddRange(candidate.Epochs);
            foreach (SessionTrack track in candidate.Tracks)
            {
                TrackAccumulator accumulator = new()
                {
                    Track = track.Track,
                    DeviceId = track.DeviceId,
                    DeviceName = track.DeviceName,
                    Format = track.Format,
                };

                accumulator.Chunks.AddRange(track.Chunks);
                _tracks[track.Track] = accumulator;

                foreach (AudioChunkMetadata chunk in track.Chunks)
                {
                    // Already journalled by the original session; never write them again.
                    _journalledChunks.Add((chunk.Track, chunk.Index));
                }
            }

            _epochIndex = candidate.NextEpochIndex - 1;
            _nextChunkIndex = candidate.NextChunkIndex;
            AwaitingResumeAfterSuspend = candidate.Reason == SessionContinuationReason.Suspended;

            _store.Append(SessionId, JournalEvent.Create(
                JournalEventTypes.SessionAdopted, _clock.UtcNow(),
                ("session_id", SessionId),
                ("reason", candidate.Reason.ToString()),
                ("next_epoch", Text(candidate.NextEpochIndex)),
                ("next_chunk_index", Text(candidate.NextChunkIndex))));

            Phase = CapturePhase.Idle;
            SetState(SessionState.Paused, $"Continuing a recording that was {candidate.Describe()}.");
        }
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (State is not (SessionState.Recording or SessionState.Degraded))
            {
                return;
            }

            CloseEpoch(EpochEndReason.Paused);
            SetState(SessionState.Paused, null);
            WriteSnapshot();
        }
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (State is not SessionState.Paused)
            {
                return;
            }

            // Resuming re-opens the pinned endpoints. If one is still gone, OpenEpoch throws and
            // the session stays paused with its reason intact, rather than silently recording a
            // single track or switching to a different device. AwaitingResumeAfterSuspend is
            // cleared inside OpenEpoch, only once the new epoch is actually running.
            OpenEpoch();
        }
    }

    /// <summary>Finalizes the session. Idempotent.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            // A previous finalization that failed part way through left the session retryable.
            // Stopping again resumes it from where it got to rather than doing nothing.
            if (_intendedEndUtc is not null && _endedUtc is null && SessionId is not null)
            {
                FinalizeSession(_intendedOutcome ?? SessionState.NeedsAttention, "retrying the save");
                return;
            }

            if (State is not (SessionState.Recording or SessionState.Degraded or SessionState.Paused))
            {
                return;
            }

            SetState(SessionState.Finalizing, null);
            CloseEpoch(EpochEndReason.Stopped);
            FinalizeSession(SessionState.Recorded, "session stopped");
        }
    }

    /// <summary>
    /// Called on the UI cadence. Surfaces a failed track as a degraded session and applies the
    /// disk thresholds. Failure detection does not depend on this loop — the capture engine
    /// raises faults directly — but the poll keeps the displayed state current.
    /// </summary>
    public RecorderStatus Poll()
    {
        lock (_gate)
        {
            DrainChunkNotifications();
            RecorderStatus status = Status();

            if (State is not (SessionState.Recording or SessionState.Degraded))
            {
                return status;
            }

            if (status.AllTracksFailed)
            {
                foreach (TrackLiveStatus track in status.Tracks.Where(t => !t.IsHealthy))
                {
                    EnqueueTrackFault(SessionId!, _generation, _epochIndex, track.Track,
                        track.Fault ?? "stopped capturing");
                }

                CloseEpoch(EpochEndReason.Failed);
                FinalizeSession(SessionState.Failed, "every track stopped capturing");
                return status;
            }

            if (status.IsDegraded)
            {
                // Deduplicated, so a fault that persists across many ticks is journalled once.
                foreach (TrackLiveStatus track in status.Tracks.Where(t => !t.IsHealthy))
                {
                    EnqueueTrackFault(SessionId!, _generation, _epochIndex, track.Track,
                        track.Fault ?? "stopped capturing");
                }

                if (State is not SessionState.Degraded)
                {
                    SetState(SessionState.Degraded, DescribeDegraded(status));
                }
            }

            ApplyDiskPolicy();
            return status;
        }
    }

    /// <summary>
    /// Waits until every journal write accepted so far is durable.
    ///
    /// <para>
    /// Returns false on timeout, which means the ledger is behind the audio. The audio and its
    /// metadata records are still safe, so the session is marked as needing reconciliation rather
    /// than treated as lost.
    /// </para>
    /// </summary>
    public bool FlushPendingWrites(TimeSpan timeout) => _persistence.Drain(timeout);

    /// <summary>True when a journal write failed and the session should be reconciled on restart.</summary>
    public bool NeedsReconciliation { get; private set; }

    /// <summary>Live counters from the capture engine, or an empty status when idle.</summary>
    public RecorderStatus Status() =>
        _engine?.Status() ?? new RecorderStatus(false, TimeSpan.Zero, 0, []);

    /// <summary>
    /// Totals for the whole session rather than the current epoch, so the timer and chunk counts
    /// do not reset on Pause and Resume.
    /// </summary>
    public SessionTotals Totals()
    {
        lock (_gate)
        {
            DrainChunkNotifications();
            TimeSpan active = _completedEpochDuration;
            if (_currentEpochStartedUtc is { } epochStart && IsCapturing)
            {
                active += _clock.UtcNow() - epochStart;
            }

            long bytes = _completedBytes + (_engine?.Status().BytesWritten ?? 0);

            Dictionary<SourceTrack, int> perTrack = _tracks.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Chunks.Count);

            // Chunks from the running epoch are already accumulated as they finalize, so no
            // double counting here.
            TimeSpan paused = TimeSpan.Zero;
            for (int i = 1; i < _epochs.Count; i++)
            {
                if (_epochs[i - 1].EndedUtc is { } previousEnd)
                {
                    paused += _epochs[i].StartedUtc - previousEnd;
                }
            }

            return new SessionTotals(active, paused, bytes, perTrack.Values.Sum(), perTrack);
        }
    }

    public DiskStatus Disk()
    {
        string path = _paths?.Root ?? Path.GetTempPath();
        return _policy.Evaluate(_disk.AvailableBytes(path), EstimatedBytesPerSecond());
    }

    /// <summary>Storage rate derived from the formats actually being captured, not a constant.</summary>
    public double EstimatedBytesPerSecond()
    {
        RecorderStatus status = Status();
        if (status.Tracks.Count == 0)
        {
            return _policy.WorstCaseBytesPerSecond;
        }

        return status.Tracks.Sum(t => (double)t.Format.SampleRate * t.Format.BytesPerFrame);
    }

    public SessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            DrainChunkNotifications();
            return BuildSnapshot(State, _endedUtc);
        }
    }

    /// <summary>
    /// Builds a snapshot with explicit state and end time, so finalization can persist the
    /// intended terminal values before committing them to memory.
    /// </summary>
    private SessionSnapshot BuildSnapshot(SessionState state, DateTimeOffset? endedUtc) => new(
        SessionId ?? string.Empty,
        state,
        _createdUtc,
        _startedUtc,
        endedUtc,
        [.. _epochs],
        [.. _tracks.Values
            .OrderBy(t => t.Track)
            .Select(t => new SessionTrack(t.Track, t.DeviceId, t.DeviceName, t.Format, [.. t.Chunks.OrderBy(c => c.Index)]))]);

    private void ResetSessionState()
    {
        _epochs.Clear();
        _tracks.Clear();
        _journalledChunks.Clear();
        _nextChunkIndex = 1;
        _epochIndex = 0;
        _diskWarned = false;
        _endedUtc = null;
        _intendedEndUtc = null;
        _intendedOutcome = null;
        _terminalEventWritten = false;
        NeedsReconciliation = false;
        _startedUtc = null;
        _completedEpochDuration = TimeSpan.Zero;
        _completedBytes = 0;
        _currentEpochStartedUtc = null;
        _engine = null;
    }

    private void OpenEpoch()
    {
        _epochIndex++;
        long epochQpc = _clock.NowQpc();
        DateTimeOffset now = _clock.UtcNow();

        CaptureRequest captureRequest = new(
            _request!.RenderEndpointId,
            _request.CaptureEndpointId,
            _paths!.TracksRoot,
            epochQpc,
            _nextChunkIndex,
            _paths.Root,
            _epochIndex);

        ICaptureEngine engine = _engineFactory.Create(captureRequest);

        // Capture the identity this epoch's callbacks belong to, so a late one can be discarded.
        string sessionId = SessionId!;
        long generation = _generation;
        int epochIndex = _epochIndex;

        void OnChunk(object? sender, ChunkFinalizedEventArgs e) =>
            EnqueueFinalizedChunk(sessionId, generation, epochIndex, e.Chunk);

        void OnFault(object? sender, TrackFaultedEventArgs e) =>
            EnqueueTrackFault(sessionId, generation, epochIndex, e.Track, e.Fault);

        engine.ChunkFinalized += OnChunk;
        engine.TrackFaulted += OnFault;
        _detachEngineHandlers = () =>
        {
            engine.ChunkFinalized -= OnChunk;
            engine.TrackFaulted -= OnFault;
        };

        try
        {
            engine.Start();
        }
        catch
        {
            _detachEngineHandlers();
            _detachEngineHandlers = null;
            engine.Dispose();
            _epochIndex--;
            throw;
        }

        _engine = engine;
        Phase = CapturePhase.Capturing;
        _currentEpochStartedUtc = now;
        _epochs.Add(new SessionEpoch(_epochIndex, now, null, epochQpc, null, EpochEndReason.Running));

        _store.Append(SessionId!, JournalEvent.Create(
            JournalEventTypes.EpochStarted, now,
            ("epoch", Text(_epochIndex)),
            ("start_qpc", Text(epochQpc)),
            ("first_chunk_index", Text(_nextChunkIndex))));

        foreach (TrackLiveStatus track in engine.Status().Tracks)
        {
            Remember(track);
            _store.Append(SessionId!, JournalEvent.Create(
                JournalEventTypes.TrackOpened, now,
                ("track", track.Track.ToString()),
                ("device_id", track.DeviceId),
                ("device_name", track.DeviceName),
                ("sample_rate", Text(track.Format.SampleRate)),
                ("channels", Text(track.Format.Channels)),
                ("epoch", Text(_epochIndex))));

            // A loss that this epoch has just reopened is resolved. Only that one is cleared,
            // so an endpoint still missing keeps its recorded loss.
            if (_lostEndpoints.Remove(track.DeviceId))
            {
                _store.Append(SessionId!, JournalEvent.Create(
                    JournalEventTypes.TrackRestored, now,
                    ("track", track.Track.ToString()),
                    ("device_id", track.DeviceId),
                    ("epoch", Text(_epochIndex))));
            }
        }

        // Faults are per epoch: a track that failed in an earlier epoch and has now reopened must
        // be able to report a fresh failure later.
        _journalledFaults.RemoveWhere(f => f.Epoch != _epochIndex);

        // Only now that capture is genuinely running is the wake-up prompt satisfied.
        AwaitingResumeAfterSuspend = false;
        SetState(SessionState.Recording, null);
    }

    /// <summary>
    /// One chunk, stamped with the session and generation that produced it.
    ///
    /// <para>
    /// Immutable and self-identifying so a callback that arrives late — after the epoch closed, or
    /// even after a different session started — can be recognised and discarded instead of
    /// contaminating whatever is running now.
    /// </para>
    /// </summary>
    private readonly record struct ChunkNotification(
        string SessionId, long Generation, int EpochIndex, AudioChunkMetadata Chunk);

    /// <summary>
    /// Receives a finalized chunk on the writer thread.
    ///
    /// <para>
    /// <b>This must never take <c>_gate</c>.</b> The writer thread raises it synchronously from
    /// inside chunk finalization, while Pause and Stop hold <c>_gate</c> and join that very
    /// thread — taking the lock here deadlocks the app with a recording in progress. It only
    /// enqueues; the lifecycle path drains at a safe point.
    /// </para>
    /// </summary>
    private void EnqueueFinalizedChunk(string sessionId, long generation, int epochIndex, AudioChunkMetadata chunk)
    {
        _chunkNotifications.Enqueue(new ChunkNotification(sessionId, generation, epochIndex, chunk));
        _persistence.Enqueue(sessionId, ChunkEvent(chunk, _clock.UtcNow()));
    }

    /// <summary>
    /// Folds queued chunk notifications into session state. Always called with <c>_gate</c> held,
    /// and only from a lifecycle or poll path, never from a capture or writer thread.
    /// </summary>
    private void DrainChunkNotifications()
    {
        while (_chunkNotifications.TryDequeue(out ChunkNotification notification))
        {
            // Discard anything belonging to an older session or generation.
            if (notification.Generation != _generation ||
                !string.Equals(notification.SessionId, SessionId, StringComparison.Ordinal))
            {
                continue;
            }

            AudioChunkMetadata chunk = notification.Chunk;
            if (!_journalledChunks.Add((chunk.Track, chunk.Index)))
            {
                continue;
            }

            if (_tracks.TryGetValue(chunk.Track, out TrackAccumulator? accumulator))
            {
                accumulator.Chunks.Add(chunk);
            }

            _nextChunkIndex = Math.Max(_nextChunkIndex, chunk.Index + 1);
        }
    }

    /// <summary>
    /// Receives a track fault on a background thread. Like the chunk callback, it only enqueues.
    ///
    /// <para>
    /// Deduplicated per session, generation, epoch, and track, so a fault that persists across
    /// many poll ticks is journalled once rather than appended on every tick.
    /// </para>
    /// </summary>
    private void EnqueueTrackFault(string sessionId, long generation, int epochIndex, SourceTrack track, string fault)
    {
        if (!_journalledFaults.Add((generation, epochIndex, track)))
        {
            return;
        }

        _persistence.Enqueue(sessionId, JournalEvent.Create(
            JournalEventTypes.TrackFailed, _clock.UtcNow(),
            ("track", track.ToString()),
            ("epoch", Text(epochIndex)),
            ("fault", fault)));
    }

    private static JournalEvent ChunkEvent(AudioChunkMetadata chunk, DateTimeOffset at) =>
        JournalEvent.Create(
            JournalEventTypes.ChunkCompleted, at,
            ("track", chunk.Track.ToString()),
            ("index", Text(chunk.Index)),
            ("epoch", Text(chunk.EpochIndex)),
            ("frames", Text(chunk.SampleFrames)),
            ("start_seconds", chunk.StartSeconds.ToString("0.####", CultureInfo.InvariantCulture)),
            ("sample_rate", Text(chunk.SampleRate)),
            ("channels", Text(chunk.Channels)),
            ("sha256", chunk.Sha256));

    private void CloseEpoch(EpochEndReason reason)
    {
        if (_engine is null)
        {
            return;
        }

        long endQpc = _clock.NowQpc();
        DateTimeOffset now = _clock.UtcNow();

        // The threads are still running until Stop returns, so the indicator must stay lit.
        Phase = CapturePhase.StoppingCapture;
        _engine.Stop(endQpc);
        Phase = CapturePhase.Saving;

        // Stop has joined the writer threads, so every chunk this epoch will ever produce has
        // now been enqueued. Fold them in before the epoch is closed and the snapshot is built.
        DrainChunkNotifications();

        if (!_persistence.Drain(TimeSpan.FromSeconds(5)))
        {
            OnPersistenceFailure("the journal did not catch up before the segment closed");
        }

        _completedBytes += _engine.Status().BytesWritten;
        _detachEngineHandlers?.Invoke();
        _detachEngineHandlers = null;
        _engine.Dispose();
        _engine = null;

        if (_currentEpochStartedUtc is { } startedUtc)
        {
            _completedEpochDuration += now - startedUtc;
            _currentEpochStartedUtc = null;
        }

        int index = _epochs.FindIndex(e => e.Index == _epochIndex);
        if (index >= 0)
        {
            _epochs[index] = _epochs[index] with { EndedUtc = now, EndQpc = endQpc, EndReason = reason };
        }

        _store.Append(SessionId!, JournalEvent.Create(
            JournalEventTypes.EpochEnded, now,
            ("epoch", Text(_epochIndex)),
            ("end_qpc", Text(endQpc)),
            ("reason", reason.ToString())));
    }

    /// <summary>
    /// The one way a session ends.
    ///
    /// <para>
    /// Atomic and idempotent: it freezes <see cref="EndedUtc"/>, writes a single terminal journal
    /// event carrying the outcome, sets the in-memory state, and persists the snapshot — in that
    /// order, exactly once. Previously the failure paths wrote <c>Recorded</c> to disk and then
    /// changed only memory to <c>Failed</c>, so the snapshot and the recovered state disagreed
    /// with what the user had been told.
    /// </para>
    /// </summary>
    /// <param name="outcome">
    /// One of <see cref="SessionState.Recorded"/>, <see cref="SessionState.Failed"/>, or
    /// <see cref="SessionState.NeedsAttention"/>. A session whose journal fell behind is
    /// downgraded to <c>NeedsAttention</c>, because its ledger no longer describes its audio.
    /// </param>
    private void FinalizeSession(SessionState outcome, string reason)
    {
        if (_endedUtc is not null)
        {
            return;
        }

        if (outcome is not (SessionState.Recorded or SessionState.Failed or SessionState.NeedsAttention))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a terminal outcome.");
        }

        // One intended end time and one intended kind of ending, both kept across retries so a
        // second attempt neither moves the recorded end nor turns a failure into a success.
        _intendedEndUtc ??= _clock.UtcNow();
        _intendedOutcome ??= outcome;

        // The downgrade is re-evaluated every attempt: a session whose ledger fell behind — even
        // if a later retry got through — is one a human should look at.
        SessionState effective = _intendedOutcome.Value == SessionState.Failed
            ? SessionState.Failed
            : NeedsReconciliation ? SessionState.NeedsAttention : _intendedOutcome.Value;

        // Capture has stopped by this point, so the session is no longer live whatever happens
        // to the writes below. Releasing here means a finalization failure cannot strand the
        // lease and lock recovery out of the session forever.
        Phase = CapturePhase.Stopped;
        ReleaseLease();

        try
        {
            // Written synchronously, not through the queue: the terminal record must be durable
            // before the snapshot that claims it. Guarded so a retry cannot write it twice.
            if (!_terminalEventWritten)
            {
                _store.Append(SessionId!, JournalEvent.Create(
                    JournalEventTypes.SessionEnded, _intendedEndUtc.Value,
                    ("session_id", SessionId!),
                    ("outcome", effective.ToString()),
                    ("reason", reason)));

                _terminalEventWritten = true;
            }

            _store.WriteSnapshot(BuildSnapshot(effective, _intendedEndUtc));

            // Only now is finalization complete. Setting this earlier would make a failed
            // attempt look finished and block every retry.
            _endedUtc = _intendedEndUtc;
            SetState(effective, reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The audio and its per-chunk records are already durable; only the ledger and the
            // projection are behind. Leave the session retryable and say so.
            _endedUtc = null;
            NeedsReconciliation = true;
            SetState(SessionState.NeedsAttention, "the recording could not be fully saved");

            Notice?.Invoke(this,
                "Your audio is saved, but EchoForge could not finish writing this recording's " +
                $"record file ({ex.GetType().Name}). It will be checked and repaired the next time " +
                "EchoForge starts.");
        }
    }

    private void ReleaseLease()
    {
        _lease?.Dispose();
        _lease = null;
    }

    /// <summary>
    /// A start that never got both endpoints open must not leave a folder that looks like an
    /// interrupted recording. The failure is journalled and a Failed snapshot is written, so
    /// recovery reports it honestly instead of offering it as recovered audio.
    /// </summary>
    private void FailStart(string reason, bool journalSession)
    {
        if (_engine is not null)
        {
            _detachEngineHandlers?.Invoke();
            _detachEngineHandlers = null;
            _engine.Dispose();
            _engine = null;
        }

        if (journalSession && SessionId is not null)
        {
            // Marks the session as one that never captured anything, so recovery can tell it
            // apart from an interrupted recording before it looks for audio.
            _store.Append(SessionId, JournalEvent.Create(
                JournalEventTypes.SessionStartFailed, _clock.UtcNow(),
                ("session_id", SessionId),
                ("reason", reason)));
        }

        if (SessionId is not null)
        {
            FinalizeSession(SessionState.Failed, reason);
        }
        else
        {
            SetState(SessionState.Failed, reason);
        }
    }

    private void ApplyDiskPolicy()
    {
        DiskStatus disk = _policy.Evaluate(_disk.AvailableBytes(_paths!.Root), EstimatedBytesPerSecond());

        switch (disk.Action)
        {
            case DiskAction.ControlledStop:
                _store.Append(SessionId!, JournalEvent.Create(
                    JournalEventTypes.DiskControlledStop, _clock.UtcNow(),
                    ("available_bytes", Text(disk.AvailableBytes))));

                SetState(SessionState.Finalizing, "free space fell below the safe minimum");
                CloseEpoch(EpochEndReason.Stopped);
                FinalizeSession(SessionState.Recorded, "stopped to protect the recording");
                break;

            case DiskAction.Warn when !_diskWarned:
                _diskWarned = true;
                _store.Append(SessionId!, JournalEvent.Create(
                    JournalEventTypes.DiskWarning, _clock.UtcNow(),
                    ("available_bytes", Text(disk.AvailableBytes))));
                break;

            default:
                break;
        }
    }

    private static string DescribeDegraded(RecorderStatus status)
    {
        List<string> failed = [.. status.Tracks
            .Where(t => !t.IsHealthy)
            .Select(t => t.Track == SourceTrack.Microphone ? "microphone" : "system audio")];

        return $"{string.Join(" and ", failed)} stopped capturing; the other track is still recording";
    }

    private void Remember(TrackLiveStatus track)
    {
        if (!_tracks.ContainsKey(track.Track))
        {
            _tracks[track.Track] = new TrackAccumulator
            {
                Track = track.Track,
                DeviceId = track.DeviceId,
                DeviceName = track.DeviceName,
                Format = track.Format,
            };
        }
    }

    private void CreateRecoveryReserve(SessionPaths paths)
    {
        try
        {
            using FileStream stream = new(paths.RecoveryReservePath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.SetLength(_policy.RecoveryReserveBytes);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
            // Not fatal: the reserve is a cushion, not a precondition.
        }
    }

    private void WriteSnapshot()
    {
        if (SessionId is not null)
        {
            _store.WriteSnapshot(Snapshot());
        }
    }

    private void SetState(SessionState state, string? reason)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(state, reason));
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            if (State is SessionState.Recording or SessionState.Degraded or SessionState.Paused)
            {
                SetState(SessionState.Finalizing, null);
                CloseEpoch(EpochEndReason.Stopped);
                FinalizeSession(SessionState.Recorded, "application closing");
            }

            _engine?.Dispose();
            _engine = null;
        }

        if (_endpoints is not null)
        {
            _endpoints.EndpointLost -= OnEndpointLost;
            _endpoints.DefaultChanged -= OnDefaultEndpointChanged;
        }

        if (_power is not null)
        {
            _power.Suspending -= OnSuspending;
            _power.Resumed -= OnResumed;
        }

        _signals.Drain(TimeSpan.FromSeconds(5));
        _signals.Dispose();

        _persistence.Drain(TimeSpan.FromSeconds(5));
        _persistence.Dispose();
        ReleaseLease();
    }

    private sealed class TrackAccumulator
    {
        public SourceTrack Track { get; init; }

        public required string DeviceId { get; init; }

        public required string DeviceName { get; init; }

        public required CaptureFormat Format { get; init; }

        public List<AudioChunkMetadata> Chunks { get; } = [];
    }
}

