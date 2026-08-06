using EchoForge.Contracts.Recording;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace EchoForge.Audio.Windows;

/// <summary>
/// Watches Core Audio endpoint notifications through <see cref="IMMNotificationClient"/>.
///
/// <para>
/// This is the real failure detector. The UI poll loop keeps the display current, but a removed
/// or disabled endpoint is reported here as soon as Windows knows, without waiting for a tick.
/// A default-device change is reported and deliberately not acted on: EchoForge records the
/// endpoints pinned at Start and never silently follows the default somewhere else.
/// </para>
/// </summary>
public sealed class MmDeviceEndpointMonitor : IEndpointMonitor, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _started;
    private bool _disposed;

    public event EventHandler<EndpointChangedEventArgs>? EndpointLost;

    public event EventHandler<DefaultEndpointChangedEventArgs>? DefaultChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _enumerator.RegisterEndpointNotificationCallback(this);
        _started = true;
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (newState == DeviceState.Active)
        {
            return;
        }

        EndpointChange change = newState switch
        {
            DeviceState.Disabled => EndpointChange.Disabled,
            DeviceState.Unplugged => EndpointChange.Unplugged,
            DeviceState.NotPresent => EndpointChange.NotPresent,
            _ => EndpointChange.Removed,
        };

        Raise(deviceId, change);
    }

    void IMMNotificationClient.OnDeviceAdded(string deviceId)
    {
        // An unrelated device appearing must not disturb a running recording.
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Raise(deviceId, EndpointChange.Removed);

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role != Role.Multimedia)
        {
            return;
        }

        try
        {
            DefaultChanged?.Invoke(this, new DefaultEndpointChangedEventArgs(defaultDeviceId, flow == DataFlow.Render));
        }
        catch (Exception)
        {
            // A COM callback must never propagate a managed exception back into the audio stack.
        }
    }

    void IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key)
    {
        // Volume, name, and format property changes are not capture failures.
    }

    private void Raise(string deviceId, EndpointChange change)
    {
        try
        {
            EndpointLost?.Invoke(this, new EndpointChangedEventArgs(deviceId, change));
        }
        catch (Exception)
        {
            // Never let a handler fault unwind into the COM callback.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_started)
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Already torn down.
            }
        }

        _enumerator.Dispose();
    }
}
