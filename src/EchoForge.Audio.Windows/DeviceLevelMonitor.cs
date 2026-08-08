using EchoForge.Contracts.Audio;
using NAudio.CoreAudioApi;

namespace EchoForge.Audio.Windows;

/// <summary>
/// The "Test devices" monitor: opens the chosen microphone and system endpoint through EchoForge's
/// own capture loop, reads a peak level off each packet, and throws the audio away.
///
/// <para>
/// It reuses <see cref="WasapiPacketCapture"/> — the same safe capture the recorder uses — but with a
/// sink that only measures loudness and keeps nothing. There is no session, no chunk, no file, and no
/// canonical audio; stopping it simply closes the endpoints. Each track is opened independently, so a
/// microphone that fails to open still leaves the system meter working, and the reverse.
/// </para>
/// </summary>
public sealed class DeviceLevelMonitor : IDeviceLevelMonitor
{
    private readonly AudioDeviceCatalog _catalog;

    private WasapiPacketCapture? _mic;
    private WasapiPacketCapture? _system;
    private MMDevice? _micDevice;
    private MMDevice? _systemDevice;

    private float _youLevel;
    private float _remoteLevel;
    private string? _youFault;
    private string? _remoteFault;
    private bool _disposed;

    public DeviceLevelMonitor(AudioDeviceCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public bool IsRunning { get; private set; }

    public float YouLevel => Volatile.Read(ref _youLevel);

    public float RemoteLevel => Volatile.Read(ref _remoteLevel);

    public bool YouWorking => _mic is { IsHealthy: true, PacketCount: > 0 };

    public bool RemoteWorking => _system is { IsHealthy: true, PacketCount: > 0 };

    public string? YouFault => _youFault;

    public string? RemoteFault => _remoteFault;

    public void Start(string captureEndpointId, string renderEndpointId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        _youFault = null;
        _remoteFault = null;
        Volatile.Write(ref _youLevel, 0);
        Volatile.Write(ref _remoteLevel, 0);

        // Each track opens on its own. One bad endpoint must not stop the other from being tested.
        try
        {
            _micDevice = _catalog.OpenDevice(captureEndpointId);
            _mic = new WasapiPacketCapture(_micDevice, loopback: false, MicSink);
            _mic.Start();
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            _youFault = ex.GetType().Name;
            DisposeTrack(ref _mic, ref _micDevice);
        }

        try
        {
            _systemDevice = _catalog.OpenDevice(renderEndpointId);
            _system = new WasapiPacketCapture(_systemDevice, loopback: true, SystemSink);
            _system.Start();
        }
        catch (Exception ex) when (IsAudioFailure(ex))
        {
            _remoteFault = ex.GetType().Name;
            DisposeTrack(ref _system, ref _systemDevice);
        }

        IsRunning = true;
    }

    public void Stop()
    {
        IsRunning = false;
        DisposeTrack(ref _mic, ref _micDevice);
        DisposeTrack(ref _system, ref _systemDevice);
        Volatile.Write(ref _youLevel, 0);
        Volatile.Write(ref _remoteLevel, 0);
    }

    private void MicSink(in PacketHeader header, ReadOnlySpan<byte> pcm16) =>
        Volatile.Write(ref _youLevel, Peak(pcm16));

    private void SystemSink(in PacketHeader header, ReadOnlySpan<byte> pcm16) =>
        Volatile.Write(ref _remoteLevel, Peak(pcm16));

    private static float Peak(ReadOnlySpan<byte> pcm16)
    {
        int max = 0;
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            short sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            int magnitude = Math.Abs((int)sample);
            if (magnitude > max)
            {
                max = magnitude;
            }
        }

        return max / 32768f;
    }

    private static void DisposeTrack(ref WasapiPacketCapture? capture, ref MMDevice? device)
    {
        try { capture?.Dispose(); }
        catch (Exception ex) when (IsAudioFailure(ex)) { }
        capture = null;

        try { device?.Dispose(); }
        catch (Exception ex) when (IsAudioFailure(ex)) { }
        device = null;
    }

    private static bool IsAudioFailure(Exception ex) =>
        ex is InvalidOperationException or System.Runtime.InteropServices.COMException
            or ObjectDisposedException or ArgumentException or NotSupportedException;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
