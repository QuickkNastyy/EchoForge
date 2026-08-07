namespace EchoForge.Contracts.Setup;

/// <summary>
/// One graphics adapter, as far as the machine will say.
///
/// <para>
/// Every uncertain field is nullable and stays null when it is not known. A VRAM figure that was
/// guessed would be worse than none: the recommendation engine reads it, and inventing 16 GB on a
/// machine that has 4 produces a recommendation that fails halfway through somebody's first
/// meeting rather than one that says it cannot tell.
/// </para>
/// </summary>
public sealed record GpuInfo
{
    public required string Vendor { get; init; }

    public required string Model { get; init; }

    /// <summary>Dedicated video memory, when the adapter reports it.</summary>
    public long? DedicatedMemoryBytes { get; init; }

    /// <summary>The display driver version, when it can be read. NVIDIA only, in practice.</summary>
    public string? DriverVersion { get; init; }

    public bool IsNvidia => Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    /// <summary>A software adapter is not a GPU for these purposes, whatever it calls itself.</summary>
    public bool IsSoftware { get; init; }
}

/// <summary>Whether a CUDA-capable stack could actually run, and how that was established.</summary>
public enum CudaAvailability
{
    /// <summary>Nothing has asked yet, or asking failed. Never treated as "no".</summary>
    Unknown,

    /// <summary>No NVIDIA adapter was found.</summary>
    NoNvidiaAdapter,

    /// <summary>An NVIDIA adapter is present but the runtime could not be exercised.</summary>
    AdapterWithoutRuntime,

    /// <summary>CTranslate2 reported at least one usable CUDA device.</summary>
    Available,
}

/// <summary>
/// An audio endpoint the machine offers, reduced to what setup needs to say.
///
/// <para>
/// Deliberately not the recorder's own endpoint record: setup only has to answer "is there a
/// microphone" and "is there something to capture system audio from", and carrying the mix format
/// around would invite the setup screen to start making decisions the recorder owns.
/// </para>
/// </summary>
public sealed record AudioEndpointSummary(string Id, string Name, bool IsDefault);

/// <summary>
/// What this machine is, as far as it can be established without guessing.
///
/// <para>
/// Every field that could not be determined is null or <c>Unknown</c>, and nothing here is ever
/// filled in with a plausible default. The whole value of this record is that the recommendation
/// engine and the setup screen can tell the difference between "8 GB of VRAM" and "the machine
/// would not say", and can behave differently — cautiously — for the second.
/// </para>
/// </summary>
public sealed record HardwareSnapshot
{
    public static readonly HardwareSnapshot Unknown = new()
    {
        OperatingSystem = "unknown",
        Architecture = "unknown",
        CpuName = null,
    };

    public required string OperatingSystem { get; init; }

    public required string Architecture { get; init; }

    /// <summary>The processor's brand string, when it can be read.</summary>
    public required string? CpuName { get; init; }

    public int LogicalCores { get; init; }

    /// <summary>AVX2 matters: the CPU speech fallback is markedly slower without it.</summary>
    public bool? HasAvx2 { get; init; }

    public bool? HasAvx512 { get; init; }

    public long? TotalMemoryBytes { get; init; }

    public long? AvailableMemoryBytes { get; init; }

    /// <summary>Free space on the volume holding the data root, which is where downloads land.</summary>
    public long? AvailableDiskBytes { get; init; }

    public string? DataVolume { get; init; }

    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];

    public CudaAvailability Cuda { get; init; } = CudaAvailability.Unknown;

    public IReadOnlyList<AudioEndpointSummary> InputDevices { get; init; } = [];

    public IReadOnlyList<AudioEndpointSummary> OutputDevices { get; init; } = [];

    /// <summary>Anything that could not be determined, named so the UI can say so out loud.</summary>
    public IReadOnlyList<string> Unavailable { get; init; } = [];

    /// <summary>The most capable NVIDIA adapter, which is the one a profile would use.</summary>
    public GpuInfo? PrimaryNvidia => Gpus
        .Where(g => g.IsNvidia && !g.IsSoftware)
        .OrderByDescending(g => g.DedicatedMemoryBytes ?? 0)
        .FirstOrDefault();

    public bool HasMicrophone => InputDevices.Count > 0;

    /// <summary>System audio is captured through a playback endpoint's loopback.</summary>
    public bool HasLoopback => OutputDevices.Count > 0;
}

/// <summary>Reads the machine. An interface so every test can describe a machine instead of being on one.</summary>
public interface IHardwareProbe
{
    /// <summary>
    /// Everything that can be established. Never throws: a machine that will not answer produces
    /// a snapshot full of nulls, and startup carries on.
    /// </summary>
    Task<HardwareSnapshot> ProbeAsync(CancellationToken cancellationToken = default);
}
