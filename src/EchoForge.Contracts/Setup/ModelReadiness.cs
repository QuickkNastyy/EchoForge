using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Setup;

/// <summary>
/// Where one model has actually got to.
///
/// <para>
/// The states exist because "installed" and "usable" are different facts and EchoForge kept
/// conflating them. Weights on disk with no runtime that can load them is not a model somebody can
/// use, and showing it as installed sends a person into a meeting believing they have a
/// transcriber they do not have. Nothing reaches <see cref="Ready"/> without having produced real
/// output from the real runtime on this machine.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelReadinessState>))]
public enum ModelReadinessState
{
    NotInstalled,

    /// <summary>Provisioning the isolated environment the model needs — WSL, an interpreter.</summary>
    InstallingRuntime,

    /// <summary>Installing the pinned dependency closure into that environment.</summary>
    InstallingDependencies,

    DownloadingModel,

    /// <summary>Re-hashing what is on disk against the pinned digests.</summary>
    Verifying,

    /// <summary>Loading the model and asking it to produce something.</summary>
    Testing,

    Ready,

    /// <summary>A Windows feature was enabled and the machine has to restart before it works.</summary>
    RestartRequired,

    Failed,

    /// <summary>Installed once, but no longer qualifies. Repair re-runs only what is missing.</summary>
    RepairAvailable,
}

/// <summary>
/// The record of a model having been proved to work here, once.
///
/// <para>
/// Bound to the exact revision it qualified. A record that names a different revision from the one
/// on disk is not evidence about the one on disk, so it is discarded rather than trusted — which
/// is what stops a model looking ready because an earlier version of it once was.
/// </para>
/// </summary>
public sealed record ModelQualification
{
    [JsonPropertyName("model_id")]
    public required string ModelId { get; init; }

    [JsonPropertyName("revision")]
    public required string Revision { get; init; }

    [JsonPropertyName("state")]
    public required ModelReadinessState State { get; init; }

    [JsonPropertyName("qualified_at_utc")]
    public DateTimeOffset? QualifiedUtc { get; init; }

    /// <summary>What happened, in one sentence, whether it worked or not.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;

    /// <summary>What the runtime identified itself as when it ran the test.</summary>
    [JsonPropertyName("runtime")]
    public string Runtime { get; init; } = string.Empty;

    /// <summary>Peak dedicated device memory observed during the test, when it was measurable.</summary>
    [JsonPropertyName("peak_vram_bytes")]
    public long PeakVramBytes { get; init; }

    /// <summary>
    /// False when the test ran, but on the CPU.
    ///
    /// <para>
    /// Reported rather than hidden. A summariser that quietly fell back to system memory still
    /// produces a brief; it produces it twenty times slower, and somebody watching a progress bar
    /// deserves to know which of those two things is happening.
    /// </para>
    /// </summary>
    [JsonPropertyName("used_gpu")]
    public bool UsedGpu { get; init; }

    /// <summary>True when the runtime process was confirmed gone after the test.</summary>
    [JsonPropertyName("runtime_exited")]
    public bool RuntimeExited { get; init; }

    public bool IsReady => State == ModelReadinessState.Ready;

    /// <summary>True when this record describes the revision actually installed.</summary>
    public bool Describes(string revision) =>
        string.Equals(Revision, revision, StringComparison.Ordinal);
}
