using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EchoForge.Contracts.Audio;
using NAudio.CoreAudioApi;

namespace EchoForge.Audio.Windows;

/// <summary>Tunables for a Phase 0 capture run.</summary>
public sealed record RecorderOptions
{
    public TimeSpan ChunkDuration { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan QueueCapacity { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Live counters for one track, for the console display and the run report.</summary>
public sealed record TrackStatus(
    SourceTrack Track,
    string DeviceName,
    CaptureFormat Format,
    string SourceEncoding,
    long Packets,
    long SessionFrames,
    long SilenceFrames,
    int CompletedChunks,
    long QueuedFrames,
    long DroppedFrames,
    long PeakQueuedFrames,
    double PeakLevel,
    double? DriftMillisecondsPerHour,
    double LastDriftMilliseconds);

/// <summary>
/// Captures the selected render endpoint and microphone at the same time into two
/// independent chunk series on one shared timeline.
///
/// <para>
/// The tracks are never mixed and never merged by arrival order. Each carries its own
/// device clock, its own chunk series, and its own drift estimate; alignment is computed
/// from the packet positions afterwards.
/// </para>
/// </summary>
public sealed class DualTrackRecorder : IDisposable
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly List<TrackPipeline> _tracks = [];
    private readonly string _sessionDirectory;
    private readonly RecorderOptions _options;
    private readonly Lock _journalLock = new();

    private StreamWriter? _journal;
    private CaptureClock? _clock;
    private bool _running;
    private bool _stopped;
    private bool _disposed;

    public DualTrackRecorder(
        AudioDeviceCatalog catalog,
        string sessionDirectory,
        string renderEndpointId,
        string captureEndpointId,
        RecorderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderEndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureEndpointId);

        _sessionDirectory = sessionDirectory;
        _options = options ?? new RecorderOptions();

        Directory.CreateDirectory(_sessionDirectory);
        SessionId = Guid.NewGuid().ToString("n");

        // One epoch, fixed before either endpoint opens, shared by both tracks. This is what
        // makes t=0 the same instant on each and therefore what makes them alignable.
        _clock = CaptureClock.StartAt(CaptureClock.Now());

        _tracks.Add(CreatePipeline(catalog, renderEndpointId, SourceTrack.System, loopback: true));
        _tracks.Add(CreatePipeline(catalog, captureEndpointId, SourceTrack.Microphone, loopback: false));
    }

    public string SessionId { get; }

    public DateTimeOffset StartedUtc { get; private set; }

    /// <summary>Seconds since the shared timeline epoch, from the performance counter.</summary>
    public double ElapsedSeconds => _clock is null ? 0 : _clock.SecondsSinceEpoch(CaptureClock.Now());

    /// <summary>Starts both endpoints against one epoch.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            throw new InvalidOperationException("Recording is already running.");
        }

        StartedUtc = DateTimeOffset.UtcNow;
        _journal = new StreamWriter(
            new FileStream(Path.Combine(_sessionDirectory, "events.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };

        WriteJournal("session_started", $"\"session_id\":\"{SessionId}\",\"epoch_qpc\":{_clock!.EpochQpc}");

        foreach (TrackPipeline track in _tracks)
        {
            track.Start();
            WriteJournal("track_started",
                $"\"track\":\"{track.Track}\",\"device_id\":\"{Escape(track.EndpointId)}\",\"format\":\"{track.Capture.Format}\",\"source_encoding\":\"{track.Capture.SourceEncoding}\"");
        }

        _running = true;
    }

    /// <summary>
    /// Stops both endpoints, finalizes every chunk, and writes the manifest.
    ///
    /// <para>
    /// Idempotent by contract, not by accident. After the first successful stop no further
    /// frames, silence, chunks, hashes, journal lines, or manifest data are produced — a
    /// second <see cref="Stop"/>, or a <see cref="Dispose"/> afterwards, leaves the session
    /// exactly as validation found it.
    /// </para>
    /// </summary>
    public void Stop()
    {
        if (_stopped || !_running)
        {
            return;
        }

        _stopped = true;

        // One stop instant for both tracks. Stopping them sequentially and letting each pad
        // to its own "now" would leave the tracks different lengths for no physical reason.
        long stopQpc = CaptureClock.Now();

        foreach (TrackPipeline track in _tracks)
        {
            track.Stop(stopQpc);
            WriteJournal("track_stopped",
                $"\"track\":\"{track.Track}\",\"chunks\":{track.Writer.CompletedChunks.Count},\"session_frames\":{track.Writer.SessionFrames}");
        }

        WriteJournal("session_stopped", $"\"session_id\":\"{SessionId}\"");
        _running = false;

        WriteManifest();

        _journal?.Dispose();
        _journal = null;
    }

    /// <summary>Current counters for both tracks.</summary>
    public IReadOnlyList<TrackStatus> Snapshot() => [.. _tracks.Select(t => t.Snapshot())];

    /// <summary>
    /// <b>Estimate only.</b> How far the two tracks' delivered audio has diverged from each
    /// other relative to the shared performance counter, in milliseconds.
    ///
    /// <para>
    /// This is a packet-level estimate, not an end-to-end alignment measurement. It cannot see
    /// analogue latency in either direction, so it must never be reported as satisfying the
    /// alignment gate. Real qualification needs the signal-based chirp harness, which is
    /// deferred hardening work; see <see cref="AlignmentQualification"/>.
    /// </para>
    /// </summary>
    public double? EstimatedOffsetMilliseconds()
    {
        if (_tracks.Count != 2)
        {
            return null;
        }

        double a = _tracks[0].Drift.LastDriftMilliseconds;
        double b = _tracks[1].Drift.LastDriftMilliseconds;
        return _tracks[0].Drift.AnchorCount > 1 && _tracks[1].Drift.AnchorCount > 1 ? a - b : null;
    }

    /// <summary>
    /// <b>Estimate only.</b> Relative drift rate between the two tracks in milliseconds per
    /// hour, fitted from packet timestamps. Not a substitute for the signal-based drift gate.
    /// </summary>
    public double? EstimatedRelativeDriftMillisecondsPerHour()
    {
        if (_tracks.Count != 2)
        {
            return null;
        }

        double? a = _tracks[0].Drift.MillisecondsPerHour();
        double? b = _tracks[1].Drift.MillisecondsPerHour();
        return a is null || b is null ? null : a - b;
    }

    private TrackPipeline CreatePipeline(AudioDeviceCatalog catalog, string endpointId, SourceTrack track, bool loopback)
    {
        MMDevice device = catalog.OpenDevice(endpointId);
        string trackDirectory = Path.Combine(_sessionDirectory, "tracks", track.ToString().ToLowerInvariant());
        return new TrackPipeline(device, endpointId, track, loopback, trackDirectory, _clock!.EpochQpc, _options);
    }

    private void WriteJournal(string type, string fields)
    {
        lock (_journalLock)
        {
            _journal?.WriteLine(
                $"{{\"ts\":\"{DateTimeOffset.UtcNow:O}\",\"type\":\"{type}\",{fields}}}");
        }
    }

    private void WriteManifest()
    {
        var manifest = new
        {
            schema_version = 1,
            session_id = SessionId,
            state = "recorded",
            started_at_utc = StartedUtc.ToString("O", CultureInfo.InvariantCulture),
            ended_at_utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            timeline = new
            {
                qpc_units_per_second = CaptureClock.UnitsPerSecond,
                epoch_qpc = _clock?.EpochQpc ?? 0,
            },
            tracks = _tracks.Select(t => new
            {
                source_track = t.Track.ToString().ToLowerInvariant(),
                device_id = t.EndpointId,
                device_name = t.DeviceName,
                source_encoding = t.Capture.SourceEncoding,
                recorded_format = new
                {
                    sample_rate = t.Capture.Format.SampleRate,
                    channels = t.Capture.Format.Channels,
                    bits_per_sample = t.Capture.Format.BitsPerSample,
                },
                packets = t.Capture.PacketCount,
                silence_frames_inserted = t.Writer.SilenceFramesInserted,
                dropped_frames = t.Queue.DroppedFrames,
                peak_queued_frames = t.Queue.PeakQueuedFrames,
                estimated_drift_ms_per_hour = t.Drift.MillisecondsPerHour(),
                chunks = t.Writer.CompletedChunks.Select(c => new
                {
                    index = c.Index,
                    relative_path = c.RelativePath,
                    start_seconds = c.StartSeconds,
                    end_seconds = c.EndSeconds,
                    sample_rate = c.SampleRate,
                    channels = c.Channels,
                    sample_frames = c.SampleFrames,
                    sha256 = c.Sha256,
                    discontinuities = c.Discontinuities.Select(d => new
                    {
                        kind = d.Kind.ToString().ToLowerInvariant(),
                        at_device_position = d.AtDevicePosition,
                        frame_count = d.FrameCount,
                        detail = d.Detail,
                    }),
                }),
            }),
        };

        string temporary = Path.Combine(_sessionDirectory, "session.json.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, ManifestJson));
        File.Move(temporary, Path.Combine(_sessionDirectory, "session.json"), overwrite: true);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        foreach (TrackPipeline track in _tracks)
        {
            track.Dispose();
        }

        _journal?.Dispose();
        _disposed = true;
    }

    /// <summary>Capture, queue, writer, and drift estimate for one track.</summary>
    private sealed class TrackPipeline : IDisposable
    {
        private readonly MMDevice _device;
        private readonly RecorderOptions _options;
        private Thread? _writerThread;
        private CancellationTokenSource? _cts;
        private long _reportedDroppedFrames;
        private bool _started;
        private bool _pipelineStopped;
        private bool _pipelineDisposed;

        public TrackPipeline(
            MMDevice device,
            string endpointId,
            SourceTrack track,
            bool loopback,
            string trackDirectory,
            long epochQpc,
            RecorderOptions options)
        {
            _device = device;
            _options = options;
            EndpointId = endpointId;
            DeviceName = device.FriendlyName;
            Track = track;

            Capture = new WasapiPacketCapture(device, loopback, OnPacket);
            Queue = new BoundedAudioQueue(Capture.Format, options.QueueCapacity);
            Writer = new PcmChunkWriter(
                trackDirectory, track, Capture.Format, epochQpc, options.ChunkDuration, options.FlushInterval);
            Drift = new DriftEstimator(Capture.Format.SampleRate);
        }

        public string EndpointId { get; }

        public string DeviceName { get; }

        public SourceTrack Track { get; }

        public WasapiPacketCapture Capture { get; }

        public BoundedAudioQueue Queue { get; }

        public PcmChunkWriter Writer { get; }

        public DriftEstimator Drift { get; }

        public double PeakLevel { get; private set; }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            _writerThread = new Thread(() => WriterLoop(token))
            {
                IsBackground = true,
                Name = $"EchoForge {Track} writer",
            };
            _writerThread.Start();
            Capture.Start();
            _started = true;
        }

        /// <summary>
        /// Stops capture and finalizes this track. Safe to call twice, and safe to call on a
        /// pipeline whose <see cref="Start"/> failed part way through.
        /// </summary>
        public void Stop(long stopQpc)
        {
            if (_pipelineStopped)
            {
                return;
            }

            _pipelineStopped = true;

            Capture.Stop();
            _cts?.Cancel();
            _writerThread?.Join(TimeSpan.FromSeconds(10));
            _writerThread = null;

            // Pad to the shared end of the session, but only for a track that actually ran.
            // Advancing a never-started track would manufacture a chunk of pure silence.
            if (_started)
            {
                Writer.AdvanceTo(stopQpc, exact: true);
            }

            // Sealing here is what makes a later Dispose a no-op rather than a chance to
            // append silence and a fresh chunk after the manifest has already been written.
            Writer.Complete();
        }

        public TrackStatus Snapshot() => new(
            Track,
            DeviceName,
            Capture.Format,
            Capture.SourceEncoding,
            Capture.PacketCount,
            Writer.SessionFrames,
            Writer.SilenceFramesInserted,
            Writer.CompletedChunks.Count,
            Queue.QueuedFrames,
            Queue.DroppedFrames,
            Queue.PeakQueuedFrames,
            PeakLevel,
            Drift.MillisecondsPerHour(),
            Drift.LastDriftMilliseconds);

        /// <summary>
        /// Runs on the capture thread. Copies the packet and returns immediately: no disk,
        /// no hashing, no allocation beyond a pooled buffer.
        /// </summary>
        private void OnPacket(in PacketHeader header, ReadOnlySpan<byte> pcm16)
        {
            Drift.Add(header);
            Queue.TryEnqueue(header, pcm16);
        }

        private void WriterLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!DrainOnce(TimeSpan.FromMilliseconds(20)))
                {
                    // Nothing arrived. Keep the timeline moving so a stalled or silent
                    // endpoint produces recorded silence instead of a short track.
                    Writer.AdvanceTo(CaptureClock.Now());
                }
            }

            // Drain whatever is still queued after the capture thread has stopped.
            while (DrainOnce(TimeSpan.Zero))
            {
                // Intentionally empty; DrainOnce reports whether it made progress.
            }
        }

        private bool DrainOnce(TimeSpan timeout)
        {
            CapturedPacket? packet = Queue.TryDequeue(timeout);
            if (packet is null)
            {
                return false;
            }

            try
            {
                long dropped = Queue.DroppedFrames;
                if (dropped > _reportedDroppedFrames)
                {
                    Writer.RecordOverflow(dropped - _reportedDroppedFrames,
                        "writer fell behind; bounded queue dropped packets");
                    _reportedDroppedFrames = dropped;
                }

                Writer.Write(packet.Header, packet.Payload);
                UpdateLevel(packet.Payload);
            }
            finally
            {
                BoundedAudioQueue.Release(packet);
            }

            return true;
        }

        private void UpdateLevel(ReadOnlySpan<byte> pcm16)
        {
            if (pcm16.IsEmpty)
            {
                PeakLevel *= 0.85;
                return;
            }

            ReadOnlySpan<short> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm16);
            int peak = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                int magnitude = Math.Abs((int)samples[i]);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            double level = peak / (double)short.MaxValue;
            PeakLevel = Math.Max(level, PeakLevel * 0.85);
        }

        public void Dispose()
        {
            if (_pipelineDisposed)
            {
                return;
            }

            _pipelineDisposed = true;

            // Only stops if it has not already stopped; a stopped pipeline is sealed and
            // this call cannot add anything to the session.
            Stop(CaptureClock.Now());

            Capture.Dispose();
            Writer.Dispose();
            Queue.Dispose();
            _cts?.Dispose();
            _device.Dispose();
        }
    }
}
