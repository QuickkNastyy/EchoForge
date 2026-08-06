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
/// The two tracks are never mixed and never merged by arrival order. Each carries its own device
/// clock and its own chunk series; both share the epoch's QPC origin so t=0 means the same instant
/// on each. Closing an epoch means disposing the engine — which is what guarantees a finalized
/// chunk is never appended to when recording resumes.
/// </para>
///
/// <para>
/// This type captures. It does not write journals or manifests; session persistence belongs to
/// the recording state machine above it.
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

    public IReadOnlyList<AudioChunkMetadata> CompletedChunks =>
        [.. _tracks.SelectMany(t => t.Writer.CompletedChunks).OrderBy(c => c.Index)];

    /// <summary>The index the next chunk would take, so the next epoch can continue the numbering.</summary>
    public int NextChunkIndex => _tracks.Count == 0 ? Request.FirstChunkIndex : _tracks.Max(t => t.Writer.NextChunkIndex);

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

        return new RecorderStatus(_started && !_stopped, elapsed, bytes, tracks);
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
        return new TrackPipeline(device, endpointId, track, loopback, trackDirectory, Request, _options);
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
        private bool _started;
        private bool _stopped;
        private bool _disposed;

        public TrackPipeline(
            MMDevice device,
            string endpointId,
            SourceTrack track,
            bool loopback,
            string trackDirectory,
            CaptureRequest request,
            RecorderOptions options)
        {
            _device = device;
            EndpointId = endpointId;
            DeviceName = device.FriendlyName;
            Track = track;

            Capture = new WasapiPacketCapture(device, loopback, OnPacket);
            Queue = new BoundedAudioQueue(Capture.Format, options.QueueCapacity);
            Writer = new PcmChunkWriter(
                trackDirectory,
                track,
                Capture.Format,
                request.EpochQpc,
                options.ChunkDuration,
                options.FlushInterval,
                request.FirstChunkIndex);
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

        /// <summary>Set when the endpoint stops producing audio unexpectedly.</summary>
        public string? Fault { get; private set; }

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
                // The endpoint refused to open. Record it rather than pretending the track is live;
                // the other track keeps recording and the session goes degraded.
                Fault = ex.Message;
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

            Capture.Stop();
            _cts?.Cancel();
            _writerThread?.Join(TimeSpan.FromSeconds(10));
            _writerThread = null;

            if (_started)
            {
                Writer.AdvanceTo(stopQpc, exact: true);
            }

            Writer.Complete();
        }

        public TrackLiveStatus Snapshot() => new(
            Track,
            EndpointId,
            DeviceName,
            Capture.Format,
            _started && !_stopped,
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

            PeakLevel = Math.Max(peak / (double)short.MaxValue, PeakLevel * 0.85);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop(CaptureClock.Now());

            Capture.Dispose();
            Writer.Dispose();
            Queue.Dispose();
            _cts?.Dispose();
            _device.Dispose();
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
