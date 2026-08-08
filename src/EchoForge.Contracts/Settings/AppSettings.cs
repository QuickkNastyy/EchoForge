namespace EchoForge.Contracts.Settings;

/// <summary>
/// User choices that survive a restart. Device selections are stored as stable endpoint IDs;
/// EchoForge re-validates them at startup and never silently falls back to a different device.
/// </summary>
public sealed record AppSettings
{
    public string? RenderEndpointId { get; init; }

    public string? CaptureEndpointId { get; init; }

    /// <summary>Whether the consent reminder has been acknowledged at least once.</summary>
    public bool ConsentAcknowledged { get; init; }

    /// <summary>Keep the compact recorder above other windows while capturing.</summary>
    public bool KeepRecorderOnTop { get; init; } = true;

    /// <summary>
    /// Which palette to paint in: "dark" or "light". Anything else, including a settings file
    /// written before this existed, reads as dark, which is the design's own default.
    /// </summary>
    public string Theme { get; init; } = "dark";

    public int SchemaVersion { get; init; } = 1;
}

/// <summary>Loads and saves <see cref="AppSettings"/>. Injected so the UI can be tested.</summary>
public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}
