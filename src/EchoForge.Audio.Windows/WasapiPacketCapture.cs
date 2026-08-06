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
        _thread = new Thread(() => Run(token))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = _loopback ? "EchoForge system capture" : "EchoForge microphone capture",
        };
        _thread.Start();
    }

    /// <summary>Stops the capture thread and the audio client.</summary>
    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _cts?.Cancel();
        _packetEvent?.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;

        try
        {
            _audioClient.Stop();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The endpoint may already be gone. Stopping is best effort.
        }
    }

    private void Run(CancellationToken token)
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
