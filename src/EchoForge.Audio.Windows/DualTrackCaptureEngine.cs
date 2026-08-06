using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Recording;
using NAudio.CoreAudioApi;

namespace EchoForge.Audio.Windows;

/// <summary>Tunables for a capture epoch.</summary>
public sealed record RecorderOptions
{
    public TimeSpan ChunkDuration { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan QueueCapacity { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Captures the selected render endpoint and microphone simultaneously for **one epoch**.
///
/// <para>
/// The two tracks are never mixed and never merged by arrival order. Both share the epoch's QPC
/// origin so t=0 means the same instant on each. Closing an epoch means disposing the engine,
/// which is what guarantees a finalized chunk is never appended to when recording resumes.
/// </para>
///
/// <para>
/// This type captures. It does not write journals or manifests; session persistence belongs to
/// the recording state machine above it, which subscribes to <see cref="ChunkFinalized"/>.
/// </para>
/// </summary>
public sealed class DualTrackCaptureEngine : ICaptureEngine
{
    private readonly List<TrackPipeline> _tracks = [];
    private readonly RecorderOptions _options;
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public DualTrackCaptureEngine(
        AudioDeviceCatalog catalog,
        CaptureRequest request,
        RecorderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);

        _options = options ?? new RecorderOptions();
        Request = request;

        Directory.CreateDirectory(request.TracksRoot);

        try
        {
            _tracks.Add(CreatePipeline(catalog, request.RenderEndpointId, SourceTrack.System, loopback: true));
            _tracks.Add(CreatePipeline(catalog, request.CaptureEndpointId, SourceTrack.Microphone, loopback: false));
        }
        catch
        {
            // A partially constructed engine still owns real COM objects and file handles.
            foreach (TrackPipeline created in _tracks)
            {
                created.Dispose();
            }

            _tracks.Clear();
            throw;
        }
    }

    public CaptureRequest Request { get; }

    public event EventHandler<ChunkFinalizedEventArgs>? ChunkFinalized;

    public event EventHandler<TrackFaultedEventArgs>? TrackFaulted;

    public IReadOnlyList<AudioChunkMetadata> CompletedChunks =>
        [.. _tracks.SelectMany(t => t.Writer.CompletedChunks).OrderBy(c => c.Index)];

    /// <summary>The index the next chunk would take, so the next epoch can continue the numbering.</summary>
    public int NextChunkIndex => _tracks.Count == 0 ? Request.FirstChunkIndex : _tracks.Max(t => t.Writer.NextChunkIndex);

    /// <summary>True when at least one track is capturing healthily.</summary>
    public bool IsHealthy => _tracks.Any(t => t.IsHealthy);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("This capture epoch has already been started.");
        }

        _started = true;

        foreach (TrackPipeline track in _tracks)
        {
            track.Start();
        }
    }

    /// <summary>
    /// Stops capture and finalizes every chunk. Idempotent: after the first stop no further
    /// frames, silence, chunks, or hashes are produced.
    /// </summary>
    public void Stop(long stopQpc)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;

        foreach (TrackPipeline track in _tracks)
        {
            track.Stop(stopQpc);
        }
    }

    public RecorderStatus Status()
    {
        List<TrackLiveStatus> tracks = [.. _tracks.Select(t => t.Snapshot())];
        long bytes = _tracks.Sum(t => t.Writer.SessionFrames * t.Capture.Format.BytesPerFrame);
        TimeSpan elapsed = _tracks.Count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(_tracks.Max(t => (double)t.Writer.SessionFrames / t.Capture.Format.SampleRate));

        return new RecorderStatus(_started && !_stopped && IsHealthy, elapsed, bytes, tracks);
    }

    /// <summary>
    /// <b>Estimate only.</b> Relative drift between the tracks in milliseconds per hour, fitted
    /// from packet timestamps. Never gate evidence; see <see cref="AlignmentQualification"/>.
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

    /// <summary><b>Estimate only.</b> Packet-level offset between the tracks, in milliseconds.</summary>
    public double? EstimatedOffsetMilliseconds()
    {
        if (_tracks.Count != 2 || _tracks[0].Drift.AnchorCount < 2 || _tracks[1].Drift.AnchorCount < 2)
        {
            return null;
        }

        return _tracks[0].Drift.LastDriftMilliseconds - _tracks[1].Drift.LastDriftMilliseconds;
    }

    private TrackPipeline CreatePipeline(AudioDeviceCatalog catalog, string endpointId, SourceTrack track, bool loopback)
    {
        MMDevice device = catalog.OpenDevice(endpointId);
        string trackDirectory = Path.Combine(Request.TracksRoot, track.ToString().ToLowerInvariant());
        string sessionRoot = string.IsNullOrEmpty(Request.SessionRoot)
            ? Path.GetDirectoryName(Request.TracksRoot)!
            : Request.SessionRoot;

        TrackPipeline pipeline = new(
            device, endpointId, track, loopback, trackDirectory, sessionRoot, Request, _options);

        pipeline.ChunkFinalized += chunk => ChunkFinalized?.Invoke(this, new ChunkFinalizedEventArgs(chunk));
        pipeline.Faulted += fault => TrackFaulted?.Invoke(this, new TrackFaultedEventArgs(track, fault));
        return pipeline;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop(CaptureClock.Now());

        foreach (TrackPipeline track in _tracks)
        {
            track.Dispose();
        }
    }

    /// <summary>Capture, queue, writer, and drift estimate for one track.</summary>
    private sealed class TrackPipeline : IDisposable
    {
        private readonly MMDevice _device;
        private Thread? _writerThread;
        private CancellationTokenSource? _cts;
        private long _reportedDroppedFrames;
        private string? _writerFault;
        private bool _started;
        private bool _stopped;
        private bool _writerStoppedCleanly = true;
        private bool _disposed;

        public TrackPipeline(
            MMDevice device,
            string endpointId,
            SourceTrack track,
            bool loopback,
            string trackDirectory,
            string sessionRoot,
            CaptureRequest request,
            RecorderOptions options)
        {
            _device = device;
            EndpointId = endpointId;
            DeviceName = device.FriendlyName;
            Track = track;

            Capture = new WasapiPacketCapture(device, loopback, OnPacket);
            Capture.Faulted += (_, message) => RaiseFault(message);

            Queue = new BoundedAudioQueue(Capture.Format, options.QueueCapacity);
            Writer = new PcmChunkWriter(
                trackDirectory,
                track,
                Capture.Format,
                request.EpochQpc,
                options.ChunkDuration,
                options.FlushInterval,
                request.FirstChunkIndex,
                request.EpochIndex,
                sessionRoot,
                chunk => ChunkFinalized?.Invoke(chunk));

            Drift = new DriftEstimator(Capture.Format.SampleRate);
        }

        public event Action<AudioChunkMetadata>? ChunkFinalized;

        public event Action<string>? Faulted;

        public string EndpointId { get; }

        public string DeviceName { get; }

        public SourceTrack Track { get; }

        public WasapiPacketCapture Capture { get; }

        public BoundedAudioQueue Queue { get; }

        public PcmChunkWriter Writer { get; }

        public DriftEstimator Drift { get; }

        public double PeakLevel { get; private set; }

        /// <summary>Combined capture-thread and writer-thread fault, or null while healthy.</summary>
        public string? Fault => Capture.Fault ?? Volatile.Read(ref _writerFault);

        /// <summary>
        /// Real health, not "Start was called". A faulted capture or writer thread reads as
        /// unhealthy so the session goes degraded instead of silently recording nothing.
        /// </summary>
        public bool IsHealthy => _started && !_stopped && Fault is null && Capture.IsHealthy;

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

            try
            {
                Capture.Start();
                _started = true;
            }
            catch (Exception ex)
            {
                RaiseFault($"{ex.GetType().Name} (HRESULT 0x{ex.HResult:X8})");
                throw;
            }
        }

        public void Stop(long stopQpc)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;

            bool captureStopped = Capture.Stop();
            _cts?.Cancel();

            Thread? writer = _writerThread;
            if (writer is not null)
            {
                _writerStoppedCleanly = writer.Join(TimeSpan.FromSeconds(10));
                if (_writerStoppedCleanly)
                {
                    _writerThread = null;
                }
                else
                {
                    RaiseFault("writer thread did not stop within 10 s");
                }
            }

            if (!captureStopped)
            {
                RaiseFault("capture thread did not stop within 5 s");
            }

            // Only finalize once no thread can still be writing.
            if (_writerStoppedCleanly)
            {
                if (_started)
                {
                    Writer.AdvanceTo(stopQpc, exact: true);
                }

                Writer.Complete();
            }
        }

        public TrackLiveStatus Snapshot() => new(
            Track,
            EndpointId,
            DeviceName,
            Capture.Format,
            IsHealthy,
            Fault,
            PeakLevel,
            Writer.SessionFrames,
            Writer.CompletedChunks.Count,
            Queue.QueuedFrames,
            Queue.DroppedFrames);

        /// <summary>
        /// Runs on the capture thread. Copies the packet and returns immediately: no disk,
        /// no hashing, no allocation beyond a pooled buffer.
        /// </summary>
        private void OnPacket(in PacketHeader header, ReadOnlySpan<byte> pcm16)
        {
            Drift.Add(header);
            Queue.TryEnqueue(header, pcm16);
        }

        /// <summary>
        /// The writer thread's outer boundary. A disk failure or disposed resource degrades the
        /// track rather than terminating the process.
        /// </summary>
        private void WriterLoop(CancellationToken token)
        {
            try
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

                while (DrainOnce(TimeSpan.Zero))
                {
                    // Intentionally empty; DrainOnce reports whether it made progress.
                }
            }
            catch (OperationCanceledException)
            {
                // Ordinary shutdown.
            }
            catch (Exception ex)
            {
                RaiseFault($"{ex.GetType().Name} (HRESULT 0x{ex.HResult:X8})");
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

            PeakLevel = Math.Max(peak / (double)short.MaxValue, PeakLevel * 0.85);
        }

        private void RaiseFault(string message)
        {
            Interlocked.CompareExchange(ref _writerFault, message, null);

            try
            {
                Faulted?.Invoke(message);
            }
            catch (Exception)
            {
                // A handler must never escalate a track fault into a process crash.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop(CaptureClock.Now());

            // A thread that refused to stop may still touch these; leaking is safer than a
            // use-after-dispose inside the audio engine.
            if (_writerStoppedCleanly)
            {
                Capture.Dispose();
                Writer.Dispose();
                Queue.Dispose();
                _cts?.Dispose();
                _device.Dispose();
            }
        }
    }
}

/// <summary>Creates real WASAPI capture engines for the recording state machine.</summary>
public sealed class DualTrackCaptureEngineFactory : ICaptureEngineFactory
{
    private readonly AudioDeviceCatalog _catalog;
    private readonly RecorderOptions _options;

    public DualTrackCaptureEngineFactory(AudioDeviceCatalog catalog, RecorderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _options = options ?? new RecorderOptions();
    }

    public ICaptureEngine Create(CaptureRequest request) => new DualTrackCaptureEngine(_catalog, request, _options);
}

/// <summary>The real performance counter, wrapped so tests can drive time deterministically.</summary>
public sealed class SystemCaptureClock : ICaptureClock
{
    public long NowQpc() => CaptureClock.Now();

    public DateTimeOffset UtcNow() => DateTimeOffset.UtcNow;
}
