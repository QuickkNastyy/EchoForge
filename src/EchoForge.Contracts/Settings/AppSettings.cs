namespace EchoForge.Contracts.Settings;

/// <summary>
/// User choices that survive a restart. Device selections are stored as stable endpoint IDs;
/// EchoForge re-validates them at startup and never silently falls back to a different device.
/// </summary>
public sealed record AppSettings
{
    public string? RenderEndpointId { get; init; }

    public string? CaptureEndpointId { get; init; }

    /// <summary>Keep the compact recorder above other windows while capturing.</summary>
    public bool KeepRecorderOnTop { get; init; } = true;

    /// <summary>
    /// Which palette to paint in: "dark" or "light". Anything else, including a settings file
    /// written before this existed, reads as dark, which is the design's own default.
    /// </summary>
    public string Theme { get; init; } = "dark";

    /// <summary>Explicit model choice. Null means hardware recommendation may choose initially.</summary>
    public string? AsrModelId { get; init; }

    /// <summary>Explicit compute choice. Null means hardware recommendation may choose initially.</summary>
    public string? TranscriptionComputeProfile { get; init; }

    public string? TranscriptionVadMode { get; init; }

    /// <summary>Null means automatic language detection.</summary>
    public string? TranscriptionLanguage { get; init; }

    /// <summary>Optional names, companies, acronyms, products, and locations used for ASR biasing.</summary>
    public IReadOnlyList<string> TranscriptionGlossary { get; init; } = [];

    public string? SummaryModelId { get; init; }

    public int SchemaVersion { get; init; } = 2;
}

/// <summary>Loads and saves <see cref="AppSettings"/>. Injected so the UI can be tested.</summary>
public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}
