namespace EchoForge.Contracts.Audio;

/// <summary>
/// Enumerates render and capture endpoints with stable IDs and their actual formats.
/// Injected so device behaviour can be faked in tests.
/// </summary>
public interface IAudioDeviceCatalog
{
    /// <summary>Active render endpoints, the loopback sources.</summary>
    IReadOnlyList<AudioEndpointInfo> GetRenderEndpoints();

    /// <summary>Active capture endpoints, the microphones.</summary>
    IReadOnlyList<AudioEndpointInfo> GetCaptureEndpoints();

    /// <summary>Looks up one endpoint by its stable ID, or null if it is gone.</summary>
    AudioEndpointInfo? FindById(string endpointId);
}
