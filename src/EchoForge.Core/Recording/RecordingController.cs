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
    private readonly Lock _gate = new();

    private ICaptureEngine? _engine;
    private RecordingRequest? _request;
    private SessionPaths? _paths;
    private DateTimeOffset _createdUtc;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _endedUtc;
    private TimeSpan _completedEpochDuration;
    private long _completedBytes;
    private DateTimeOffset? _currentEpochStartedUtc;
    private int _nextChunkIndex = 1;
    private int _epochIndex;
    private bool _diskWarned;
    private bool _disposed;

    public RecordingController(
        ISessionStore store,
        ICaptureEngineFactory engineFactory,
        ICaptureClock clock,
        IDiskSpaceProbe disk,
        DiskPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engineFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(disk);

        _store = store;
        _engineFactory = engineFactory;
        _clock = clock;
        _disk = disk;
        _policy = policy ?? new DiskPolicy();
        _persistence = new JournalPersistenceQueue(store);
    }

    public SessionState State { get; private set; } = SessionState.New;

    public string? SessionId { get; private set; }

    public IReadOnlyList<SessionEpoch> Epochs => _epochs;

    public bool IsCapturing => State is SessionState.Recording or SessionState.Degraded;

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
            _request = request;

            SessionId = Guid.NewGuid().ToString("n");
            _createdUtc = _clock.UtcNow();
            _paths = _store.Create(SessionId);

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

            OpenEpoch();
        }
    }

    /// <summary>Finalizes the session. Idempotent.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (State is not (SessionState.Recording or SessionState.Degraded or SessionState.Paused))
            {
                return;
            }

            SetState(SessionState.Finalizing, null);
            CloseEpoch(EpochEndReason.Stopped);
            FinishSession("session stopped");
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
            RecorderStatus status = Status();

            if (State is not (SessionState.Recording or SessionState.Degraded))
            {
                return status;
            }

            if (status.AllTracksFailed)
            {
                foreach (TrackLiveStatus track in status.Tracks.Where(t => !t.IsHealthy))
                {
                    JournalTrackFailure(track);
                }

                CloseEpoch(EpochEndReason.Failed);
                FinishSession("every track stopped capturing");
                SetState(SessionState.Failed, "every track stopped capturing");
                return status;
            }

            if (status.IsDegraded && State is not SessionState.Degraded)
            {
                foreach (TrackLiveStatus track in status.Tracks.Where(t => !t.IsHealthy))
                {
                    JournalTrackFailure(track);
                }

                SetState(SessionState.Degraded, DescribeDegraded(status));
            }

            ApplyDiskPolicy();
            return status;
        }
    }

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
            return new SessionSnapshot(
                SessionId ?? string.Empty,
                State,
                _createdUtc,
                _startedUtc,
                _endedUtc,
                [.. _epochs],
                [.. _tracks.Values
                    .OrderBy(t => t.Track)
                    .Select(t => new SessionTrack(t.Track, t.DeviceId, t.DeviceName, t.Format, [.. t.Chunks.OrderBy(c => c.Index)]))]);
        }
    }

    private void ResetSessionState()
    {
        _epochs.Clear();
        _tracks.Clear();
        _journalledChunks.Clear();
        _nextChunkIndex = 1;
        _epochIndex = 0;
        _diskWarned = false;
        _endedUtc = null;
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
        engine.ChunkFinalized += OnChunkFinalized;
        engine.TrackFaulted += OnTrackFaulted;

        try
        {
            engine.Start();
        }
        catch
        {
            engine.ChunkFinalized -= OnChunkFinalized;
            engine.TrackFaulted -= OnTrackFaulted;
            engine.Dispose();
            _epochIndex--;
            throw;
        }

        _engine = engine;
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
        }

        SetState(SessionState.Recording, null);
    }

    /// <summary>
    /// Records a chunk the moment it becomes durable. Raised on a writer thread, so the journal
    /// append is handed to the persistence queue rather than performed here.
    /// </summary>
    private void OnChunkFinalized(object? sender, ChunkFinalizedEventArgs e)
    {
        AudioChunkMetadata chunk = e.Chunk;

        lock (_gate)
        {
            if (!_journalledChunks.Add((chunk.Track, chunk.Index)))
            {
                return;
            }

            if (_tracks.TryGetValue(chunk.Track, out TrackAccumulator? accumulator))
            {
                accumulator.Chunks.Add(chunk);
            }

            _nextChunkIndex = Math.Max(_nextChunkIndex, chunk.Index + 1);
        }

        _persistence.Enqueue(SessionId!, ChunkEvent(chunk, _clock.UtcNow()));
    }

    private void OnTrackFaulted(object? sender, TrackFaultedEventArgs e)
    {
        // Raised from a background thread. Only record it; the poll loop promotes the session
        // state so the transition happens on one thread.
        _persistence.Enqueue(SessionId!, JournalEvent.Create(
            JournalEventTypes.TrackFailed, _clock.UtcNow(),
            ("track", e.Track.ToString()),
            ("fault", e.Fault)));
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

        _engine.Stop(endQpc);

        // Anything finalized during Stop arrives through ChunkFinalized; wait for those writes.
        _persistence.Drain(TimeSpan.FromSeconds(5));

        _completedBytes += _engine.Status().BytesWritten;
        _engine.ChunkFinalized -= OnChunkFinalized;
        _engine.TrackFaulted -= OnTrackFaulted;
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

    /// <summary>Writes the terminal session events exactly once and freezes the end timestamp.</summary>
    private void FinishSession(string reason)
    {
        if (_endedUtc is not null)
        {
            return;
        }

        _endedUtc = _clock.UtcNow();

        _store.Append(SessionId!, JournalEvent.Create(
            JournalEventTypes.SessionStopped, _endedUtc.Value,
            ("session_id", SessionId!),
            ("reason", reason)));

        SetState(SessionState.Recorded, reason);
        WriteSnapshot();
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
            _engine.ChunkFinalized -= OnChunkFinalized;
            _engine.TrackFaulted -= OnTrackFaulted;
            _engine.Dispose();
            _engine = null;
        }

        _endedUtc = _clock.UtcNow();

        if (journalSession && SessionId is not null)
        {
            _store.Append(SessionId, JournalEvent.Create(
                JournalEventTypes.SessionStartFailed, _endedUtc.Value,
                ("session_id", SessionId),
                ("reason", reason)));
        }

        SetState(SessionState.Failed, reason);

        if (SessionId is not null)
        {
            _store.WriteSnapshot(Snapshot());
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
                FinishSession("stopped to protect the recording");
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

    private void JournalTrackFailure(TrackLiveStatus track) =>
        _store.Append(SessionId!, JournalEvent.Create(
            JournalEventTypes.TrackFailed, _clock.UtcNow(),
            ("track", track.Track.ToString()),
            ("device_id", track.DeviceId),
            ("fault", track.Fault ?? "stopped capturing")));

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
                FinishSession("application closing");
            }

            _engine?.Dispose();
            _engine = null;
        }

        _persistence.Drain(TimeSpan.FromSeconds(5));
        _persistence.Dispose();
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
