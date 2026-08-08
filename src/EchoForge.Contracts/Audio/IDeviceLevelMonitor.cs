namespace EchoForge.Contracts.Audio;

/// <summary>
/// Opens a microphone and a system endpoint just to report their live levels, for the "Test devices"
/// action. It records nothing: no session, no chunk, no canonical audio — only a peak level per
/// track while it runs.
///
/// <para>
/// Behind an interface so the recording screen can drive a device test without depending on the audio
/// stack, and so the logic is testable with a fake.
/// </para>
/// </summary>
public interface IDeviceLevelMonitor : IDisposable
{
    /// <summary>Opens both endpoints and begins reporting levels. Safe to call when already stopped.</summary>
    void Start(string captureEndpointId, string renderEndpointId);

    /// <summary>Closes both endpoints. The test writes nothing, so there is nothing to finalize.</summary>
    void Stop();

    bool IsRunning { get; }

    /// <summary>Latest microphone peak, 0..1.</summary>
    float YouLevel { get; }

    /// <summary>Latest system peak, 0..1.</summary>
    float RemoteLevel { get; }

    /// <summary>True when the microphone opened and is delivering audio.</summary>
    bool YouWorking { get; }

    /// <summary>True when the system endpoint opened and is delivering audio.</summary>
    bool RemoteWorking { get; }

    /// <summary>A safe diagnostic string when a track could not be opened, or null.</summary>
    string? YouFault { get; }

    string? RemoteFault { get; }
}
