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
/// a finalized WAV is never appended to.
/// </para>
/// </summary>
public sealed class RecordingController : IDisposable
{
    private readonly ISessionStore _store;
    private readonly ICaptureEngineFactory _engineFactory;
    private readonly ICaptureClock _clock;
    private readonly IDiskSpaceProbe _disk;
    private readonly DiskPolicy _policy;

    private readonly List<SessionEpoch> _epochs = [];
    private readonly Dictionary<SourceTrack, TrackAccumulator> _tracks = [];
    private readonly Lock _gate = new();

    private ICaptureEngine? _engine;
    private RecordingRequest? _request;
    private SessionPaths? _paths;
    private DateTimeOffset _createdUtc;
    private DateTimeOffset? _startedUtc;
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
    }

    public SessionState State { get; private set; } = SessionState.New;

    public string? SessionId { get; private set; }

    /// <summary>Epochs opened so far, including the one currently running.</summary>
    public IReadOnlyList<SessionEpoch> Epochs => _epochs;

    /// <summary>True while audio is being captured. Pause makes this false without ending the session.</summary>
    public bool IsCapturing => State is SessionState.Recording or SessionState.Degraded;

    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    /// <summary>Whether a recording could start right now, and why not when it could not.</summary>
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

            _request = request;
            _epochs.Clear();
            _tracks.Clear();
            _nextChunkIndex = 1;
            _epochIndex = 0;
            _diskWarned = false;

            SessionId = Guid.NewGuid().ToString("n");
            _createdUtc = _clock.UtcNow();
            _paths = _store.Create(SessionId);

            (bool allowed, string? reason) = _policy.CanStart(_disk.AvailableBytes(_paths.Root));
            if (!allowed)
            {
                SetState(SessionState.Failed, reason);
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
                SetState(SessionState.Failed, ex.Message);
                throw;
            }
        }
    }

    /// <summary>
    /// Closes the current epoch. Capture stops, every active chunk is finalized, and the gap is
    /// recorded. All audio captured so far is preserved.
    /// </summary>
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

    /// <summary>Opens a new epoch. Never appends to a chunk finalized before the pause.</summary>
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

            _store.Append(SessionId!, JournalEvent.Create(
                JournalEventTypes.SessionStopped, _clock.UtcNow(), ("session_id", SessionId!)));

            SetState(SessionState.Recorded, null);
            WriteSnapshot();
        }
    }

    /// <summary>
    /// Called on the UI cadence. Surfaces a failed track as a degraded session and applies the
    /// disk thresholds. Returns the current live status.
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
                SetState(SessionState.Failed, "every track stopped capturing");
                WriteSnapshot();
                return status;
            }

            if (status.IsDegraded && State is not SessionState.Degraded)
            {
                foreach (TrackLiveStatus track in status.Tracks.Where(t => !t.IsHealthy))
                {
                    JournalTrackFailure(track);
                }

                SetState(SessionState.Degraded, "one track stopped capturing");
            }

            ApplyDiskPolicy(status);
            return status;
        }
    }

    /// <summary>Live counters from the capture engine, or an empty status when idle.</summary>
    public RecorderStatus Status() =>
        _engine?.Status() ?? new RecorderStatus(false, TimeSpan.Zero, 0, []);

    /// <summary>Current free space and what the policy makes of it.</summary>
    public DiskStatus Disk()
    {
        string path = _paths?.Root ?? Path.GetTempPath();
        return _policy.Evaluate(_disk.AvailableBytes(path), _policy.WorstCaseBytesPerSecond);
    }

    /// <summary>The session as it stands, built from what has actually been finalized.</summary>
    public SessionSnapshot Snapshot() => new(
        SessionId ?? string.Empty,
        State,
        _createdUtc,
        _startedUtc,
        State is SessionState.Recorded ? _clock.UtcNow() : null,
        [.. _epochs],
        [.. _tracks.Values
            .OrderBy(t => t.Track)
            .Select(t => new SessionTrack(t.Track, t.DeviceId, t.DeviceName, t.Format, [.. t.Chunks]))]);

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
            _nextChunkIndex);

        ICaptureEngine engine = _engineFactory.Create(captureRequest);

        try
        {
            engine.Start();
        }
        catch
        {
            engine.Dispose();
            _epochIndex--;
            throw;
        }

        _engine = engine;
        _epochs.Add(new SessionEpoch(_epochIndex, now, null, epochQpc, null, EpochEndReason.Running));

        _store.Append(SessionId!, JournalEvent.Create(
            JournalEventTypes.EpochStarted, now,
            ("epoch", _epochIndex.ToString(CultureInfo.InvariantCulture)),
            ("start_qpc", epochQpc.ToString(CultureInfo.InvariantCulture)),
            ("first_chunk_index", _nextChunkIndex.ToString(CultureInfo.InvariantCulture))));

        foreach (TrackLiveStatus track in engine.Status().Tracks)
        {
            Remember(track);
            _store.Append(SessionId!, JournalEvent.Create(
                JournalEventTypes.TrackOpened, now,
                ("track", track.Track.ToString()),
                ("device_id", track.DeviceId),
                ("device_name", track.DeviceName),
                ("sample_rate", track.Format.SampleRate.ToString(CultureInfo.InvariantCulture)),
                ("channels", track.Format.Channels.ToString(CultureInfo.InvariantCulture)),
                ("epoch", _epochIndex.ToString(CultureInfo.InvariantCulture))));
        }

        SetState(SessionState.Recording, null);
    }

    private void CloseEpoch(EpochEndReason reason)
    {
        if (_engine is null)
        {
            return;
        }

        long endQpc = _clock.NowQpc();
        DateTimeOffset now = _clock.UtcNow();

        _engine.Stop(endQpc);
        HarvestChunks(now);
        _engine.Dispose();
        _engine = null;

        int index = _epochs.FindIndex(e => e.Index == _epochIndex);
        if (index >= 0)
        {
            _epochs[index] = _epochs[index] with { EndedUtc = now, EndQpc = endQpc, EndReason = reason };
        }

        _store.Append(SessionId!, JournalEvent.Create(
            JournalEventTypes.EpochEnded, now,
            ("epoch", _epochIndex.ToString(CultureInfo.InvariantCulture)),
            ("end_qpc", endQpc.ToString(CultureInfo.InvariantCulture)),
            ("reason", reason.ToString())));
    }

    /// <summary>Records everything the closing epoch finalized, and advances the chunk numbering.</summary>
    private void HarvestChunks(DateTimeOffset now)
    {
        foreach (AudioChunkMetadata chunk in _engine!.CompletedChunks)
        {
            if (!_tracks.TryGetValue(chunk.Track, out TrackAccumulator? accumulator))
            {
                continue;
            }

            accumulator.Chunks.Add(chunk with { EpochIndex = _epochIndex });
            _nextChunkIndex = Math.Max(_nextChunkIndex, chunk.Index + 1);

            _store.Append(SessionId!, JournalEvent.Create(
                JournalEventTypes.ChunkCompleted, now,
                ("track", chunk.Track.ToString()),
                ("index", chunk.Index.ToString(CultureInfo.InvariantCulture)),
                ("epoch", _epochIndex.ToString(CultureInfo.InvariantCulture)),
                ("frames", chunk.SampleFrames.ToString(CultureInfo.InvariantCulture)),
                ("start_seconds", chunk.StartSeconds.ToString("0.####", CultureInfo.InvariantCulture)),
                ("sample_rate", chunk.SampleRate.ToString(CultureInfo.InvariantCulture)),
                ("channels", chunk.Channels.ToString(CultureInfo.InvariantCulture)),
                ("sha256", chunk.Sha256)));
        }
    }

    private void ApplyDiskPolicy(RecorderStatus status)
    {
        DiskStatus disk = _policy.Evaluate(_disk.AvailableBytes(_paths!.Root), _policy.WorstCaseBytesPerSecond);

        switch (disk.Action)
        {
            case DiskAction.ControlledStop:
                _store.Append(SessionId!, JournalEvent.Create(
                    JournalEventTypes.DiskControlledStop, _clock.UtcNow(),
                    ("available_bytes", disk.AvailableBytes.ToString(CultureInfo.InvariantCulture))));

                SetState(SessionState.Finalizing, "free space fell below the safe minimum");
                CloseEpoch(EpochEndReason.Stopped);
                _store.Append(SessionId!, JournalEvent.Create(
                    JournalEventTypes.SessionStopped, _clock.UtcNow(), ("session_id", SessionId!)));
                SetState(SessionState.Recorded, "stopped to protect the recording");
                WriteSnapshot();
                break;

            case DiskAction.Warn when !_diskWarned:
                _diskWarned = true;
                _store.Append(SessionId!, JournalEvent.Create(
                    JournalEventTypes.DiskWarning, _clock.UtcNow(),
                    ("available_bytes", disk.AvailableBytes.ToString(CultureInfo.InvariantCulture))));
                break;

            default:
                break;
        }

        _ = status;
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
            // Not fatal: the reserve is a cushion, not a precondition. Preflight already
            // confirmed there is enough room to record.
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
                Stop();
            }

            _engine?.Dispose();
            _engine = null;
        }
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
