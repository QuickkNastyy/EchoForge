using EchoForge.Contracts.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EchoForge.Audio.Windows;

/// <summary>
/// Enumerates Windows audio endpoints through Core Audio, reporting the stable endpoint ID
/// and the format the endpoint actually offers rather than one EchoForge would prefer.
/// </summary>
public sealed class AudioDeviceCatalog : IAudioDeviceCatalog, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _disposed;

    public IReadOnlyList<AudioEndpointInfo> GetRenderEndpoints() => Enumerate(DataFlow.Render);

    public IReadOnlyList<AudioEndpointInfo> GetCaptureEndpoints() => Enumerate(DataFlow.Capture);

    public AudioEndpointInfo? FindById(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            using MMDevice device = _enumerator.GetDevice(endpointId);
            return Describe(device, isDefault: false);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The endpoint is gone. EchoForge never silently substitutes another device.
            return null;
        }
    }

    /// <summary>Opens the underlying device for capture. The caller owns the returned instance.</summary>
    public MMDevice OpenDevice(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _enumerator.GetDevice(endpointId);
    }

    private List<AudioEndpointInfo> Enumerate(DataFlow flow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? defaultId = null;
        try
        {
            using MMDevice defaultDevice = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // No default endpoint for this flow. Not an error; the list is simply unmarked.
        }

        List<AudioEndpointInfo> results = [];
        foreach (MMDevice device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                results.Add(Describe(device, device.ID == defaultId));
            }
        }

        return results;
    }

    private static AudioEndpointInfo Describe(MMDevice device, bool isDefault)
    {
        WaveFormat mix = device.AudioClient.MixFormat;
        return new AudioEndpointInfo(
            device.ID,
            device.FriendlyName,
            isDefault,
            new CaptureFormat(mix.SampleRate, mix.Channels, 16));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _enumerator.Dispose();
        _disposed = true;
    }
}
