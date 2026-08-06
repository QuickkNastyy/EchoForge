using EchoForge.Contracts.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EchoForge.Audio.Windows;

/// <summary>Receives one packet synchronously on the capture thread.</summary>
public delegate void PacketSink(in PacketHeader header, ReadOnlySpan<byte> pcm16);

/// <summary>
/// EchoForge's own WASAPI capture loop.
///
/// <para>
/// This deliberately does not use <c>WasapiCapture</c>/<c>WasapiLoopbackCapture</c> and their
/// <c>DataAvailable</c> event. That event hands over bytes without the per-packet positions
/// the timeline depends on, and the moment managed code sees a packet is not a clock. Instead
/// the loop drains <see cref="AudioCaptureClient.GetBuffer(out int, out AudioClientBufferFlags,
/// out long, out long)"/>, which reports the frame count, buffer flags, device position, and
/// QPC position for every packet. Those positions are the only clock anchors EchoForge trusts.
/// </para>
///
/// <para>
/// The sink is invoked on the capture thread and must not block: no disk, no hashing, no UI.
/// </para>
/// </summary>
public sealed class WasapiPacketCapture : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly MMDevice _device;
    private readonly bool _loopback;
    private readonly PacketSink _sink;
    private readonly AudioClient _audioClient;
    private readonly WaveFormat _mixFormat;
    private readonly AutoResetEvent? _packetEvent;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private byte[] _scratch = [];
    private string? _fault;
    private bool _started;
    private bool _stopRequested;
    private bool _disposed;

    /// <param name="device">The endpoint to capture. Ownership stays with the caller.</param>
    /// <param name="loopback">True to capture a render endpoint's mix rather than a microphone.</param>
    /// <param name="sink">Called once per packet on the capture thread.</param>
    public WasapiPacketCapture(MMDevice device, bool loopback, PacketSink sink)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(sink);

        _device = device;
        _loopback = loopback;
        _sink = sink;
        _audioClient = device.AudioClient;
        _mixFormat = _audioClient.MixFormat;

        Format = new CaptureFormat(_mixFormat.SampleRate, _mixFormat.Channels, 16);
        SourceEncoding = DescribeEncoding(_mixFormat);

        AudioClientStreamFlags flags = loopback
            ? AudioClientStreamFlags.Loopback
            : AudioClientStreamFlags.EventCallback;

        // 200 ms of engine buffer leaves room for a scheduling hiccup without dropping data.
        _audioClient.Initialize(
            AudioClientShareMode.Shared,
            flags,
            2_000_000,
            0,
            _mixFormat,
            Guid.Empty);

        if (!loopback)
        {
            // Loopback endpoints do not raise the event while the endpoint is silent, so the
            // loopback path polls instead and lets the device position describe the gap.
            _packetEvent = new AutoResetEvent(false);
            _audioClient.SetEventHandle(_packetEvent.SafeWaitHandle.DangerousGetHandle());
        }
    }

    /// <summary>The PCM16 format packets are delivered in after conversion.</summary>
    public CaptureFormat Format { get; }

    /// <summary>The endpoint's native mix encoding, recorded so the report states what was really captured.</summary>
    public string SourceEncoding { get; }

    /// <summary>Packets delivered to the sink.</summary>
    public long PacketCount { get; private set; }

    /// <summary>Frames delivered to the sink.</summary>
    public long FrameCount { get; private set; }

    /// <summary>The QPC position of the first packet, which anchors this track on the timeline.</summary>
    public long? FirstPacketQpc { get; private set; }

    /// <summary>
    /// A safe diagnostic description of the fault that stopped this capture, or null while
    /// healthy. Carries the exception type and HRESULT where available; never meeting content
    /// and never a full private path.
    /// </summary>
    public string? Fault => Volatile.Read(ref _fault);

    /// <summary>
    /// True only while the capture thread is genuinely alive and unfaulted. This is deliberately
    /// not "Start was called once": a removed endpoint or a COM failure must show as unhealthy.
    /// </summary>
    public bool IsHealthy => _started && Fault is null && !_stopRequested && (_thread?.IsAlive ?? false);

    /// <summary>Raised once, off the capture thread's hot path, when the thread faults.</summary>
    public event EventHandler<string>? Faulted;

    /// <summary>Starts the capture thread.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            throw new InvalidOperationException("Capture has already been started.");
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _audioClient.Start();
        _started = true;
        _thread = new Thread(() => Run(token))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = _loopback ? "EchoForge system capture" : "EchoForge microphone capture",
        };
        _thread.Start();
    }

    /// <summary>
    /// Stops the capture thread and the audio client.
    /// </summary>
    /// <returns>
    /// False when the thread would not stop within the grace period. The caller must not dispose
    /// resources the thread may still be touching, so this is surfaced rather than swallowed.
    /// </returns>
    public bool Stop()
    {
        _stopRequested = true;

        Thread? thread = _thread;
        if (thread is null)
        {
            return true;
        }

        _cts?.Cancel();
        _packetEvent?.Set();

        bool stopped = thread.Join(TimeSpan.FromSeconds(5));
        if (stopped)
        {
            _thread = null;
        }
        else
        {
            SetFault("capture thread did not stop within 5 s");
        }

        try
        {
            _audioClient.Stop();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The endpoint may already be gone. Stopping is best effort.
        }

        return stopped;
    }

    /// <summary>
    /// The capture thread's outer boundary.
    ///
    /// <para>
    /// Every exception is caught here. A COM error, a removed endpoint, an unsupported format, or
    /// a disposed resource must degrade the session, not terminate the process — an unhandled
    /// exception on a background thread would take the whole app down and lose the recording.
    /// </para>
    /// </summary>
    private void Run(CancellationToken token)
    {
        try
        {
            AudioCaptureClient capture = _audioClient.AudioCaptureClient;

            while (!token.IsCancellationRequested)
            {
                if (_packetEvent is not null)
                {
                    _packetEvent.WaitOne(PollInterval);
                }
                else
                {
                    Thread.Sleep(PollInterval);
                }

                Drain(capture, token);
            }

            // One last drain so frames already in the engine buffer are not lost on stop.
            Drain(capture, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            SetFault(Describe(ex));
        }
    }

    /// <summary>
    /// Records a fault once and notifies. Silence is never a fault: an endpoint that simply has
    /// nothing to play produces no packets and stays healthy.
    /// </summary>
    private void SetFault(string message)
    {
        if (Interlocked.CompareExchange(ref _fault, message, null) is not null)
        {
            return;
        }

        try
        {
            Faulted?.Invoke(this, message);
        }
        catch (Exception)
        {
            // A handler must never be able to escalate a track fault into a process crash.
        }
    }

    /// <summary>Builds a diagnostic string with no meeting content and no private paths.</summary>
    private static string Describe(Exception ex)
    {
        string detail = ex is System.Runtime.InteropServices.COMException com
            ? $"HRESULT 0x{com.HResult:X8}"
            : $"HRESULT 0x{ex.HResult:X8}";

        return $"{ex.GetType().Name} ({detail})";
    }

    private void Drain(AudioCaptureClient capture, CancellationToken token)
    {
        while (!token.IsCancellationRequested && capture.GetNextPacketSize() > 0)
        {
            IntPtr buffer = capture.GetBuffer(
                out int framesRead,
                out AudioClientBufferFlags bufferFlags,
                out long devicePosition,
                out long qpcPosition);

            if (framesRead == 0)
            {
                capture.ReleaseBuffer(0);
                continue;
            }

            try
            {
                AudioPacketConditions flags = TranslateFlags(bufferFlags);
                PacketHeader header = new(devicePosition, qpcPosition, framesRead, flags);

                FirstPacketQpc ??= qpcPosition;
                PacketCount++;
                FrameCount += framesRead;

                if ((flags & AudioPacketConditions.Silent) != 0)
                {
                    _sink(header, ReadOnlySpan<byte>.Empty);
                }
                else
                {
                    ReadOnlySpan<byte> pcm16 = Convert(buffer, framesRead);
                    _sink(header, pcm16);
                }
            }
            finally
            {
                capture.ReleaseBuffer(framesRead);
            }
        }
    }

    private unsafe ReadOnlySpan<byte> Convert(IntPtr source, int frames)
    {
        int channels = _mixFormat.Channels;
        int samples = frames * channels;
        int neededBytes = samples * 2;

        if (_scratch.Length < neededBytes)
        {
            _scratch = new byte[neededBytes];
        }

        Span<short> destination = System.Runtime.InteropServices.MemoryMarshal
            .Cast<byte, short>(_scratch.AsSpan(0, neededBytes));

        switch (_mixFormat.BitsPerSample)
        {
            case 32:
            {
                // Shared-mode mix formats are 32-bit IEEE float in practice.
                ReadOnlySpan<float> input = new((void*)source, samples);
                for (int i = 0; i < samples; i++)
                {
                    float clamped = Math.Clamp(input[i], -1f, 1f);
                    destination[i] = (short)Math.Round(clamped * short.MaxValue, MidpointRounding.AwayFromZero);
                }

                break;
            }

            case 16:
            {
                ReadOnlySpan<short> input = new((void*)source, samples);
                input.CopyTo(destination);
                break;
            }

            default:
                throw new NotSupportedException(
                    $"Endpoint mix format is {_mixFormat.BitsPerSample}-bit {_mixFormat.Encoding}, " +
                    "which Phase 0 does not convert. Record the format and extend the converter.");
        }

        return _scratch.AsSpan(0, neededBytes);
    }

    private static AudioPacketConditions TranslateFlags(AudioClientBufferFlags flags)
    {
        AudioPacketConditions result = AudioPacketConditions.None;
        if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
        {
            result |= AudioPacketConditions.DataDiscontinuity;
        }

        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            result |= AudioPacketConditions.Silent;
        }

        if ((flags & AudioClientBufferFlags.TimestampError) != 0)
        {
            result |= AudioPacketConditions.TimestampError;
        }

        return result;
    }

    private static string DescribeEncoding(WaveFormat format) =>
        $"{format.Encoding} {format.BitsPerSample}-bit {format.SampleRate} Hz {format.Channels} ch";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _cts?.Dispose();
        _packetEvent?.Dispose();
        _disposed = true;
    }
}

