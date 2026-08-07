using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Infrastructure.Setup;

namespace EchoForge.Infrastructure.Diagnostics;

/// <summary>What was written, and where.</summary>
public sealed record DiagnosticsResult(bool Succeeded, string? Path, string Message);

/// <summary>
/// A support file describing the machine and the installation, and nothing else.
///
/// <para>
/// <b>The hard rule: no meeting content, ever.</b> Not a transcript line, not a summary sentence,
/// not a prompt, not a meeting title, not a session's audio path. A diagnostics bundle is a file
/// people email to somebody they have never met, and the entire value of a local-first meeting
/// recorder evaporates the first time one of them carries a sentence somebody said in a private
/// conversation.
/// </para>
///
/// <para>
/// So this is built from an allow-list rather than filtered. It names the fields it collects one at
/// a time — versions, statuses, counts, digests, hardware — and there is no path through it that
/// reads a transcript revision, a summary revision, a journal, or a session title. Redaction by
/// exclusion is the only kind that stays correct when somebody later adds a field.
/// </para>
///
/// <para>
/// Session identifiers are included because they are opaque ULIDs a user chose nothing about, and
/// without them a support conversation cannot refer to a specific meeting at all. Absolute paths
/// under the user's profile are reduced to the parts that matter, so a bundle does not carry a
/// person's name in every second line.
/// </para>
///
/// <para>
/// Writing one is always an explicit action, and nothing uploads it. There is no telemetry in
/// EchoForge.
/// </para>
/// </summary>
public sealed class DiagnosticsBundle
{
    private readonly AppLayout _layout;
    private readonly SetupServices? _setup;
    private readonly IHardwareProbe? _hardware;
    private readonly TimeProvider _clock;

    public DiagnosticsBundle(
        AppLayout? layout = null,
        SetupServices? setup = null,
        IHardwareProbe? hardware = null,
        TimeProvider? clock = null)
    {
        _layout = layout ?? AppLayout.Current;
        _setup = setup;
        _hardware = hardware;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The application version, from the assembly rather than a second copy of it.</summary>
    public static string Version =>
        typeof(DiagnosticsBundle).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(DiagnosticsBundle).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Collects everything. Never throws; a section that fails is recorded as failed.</summary>
    public async Task<DiagnosticsReport> CollectAsync(
        string transcriptionProfileId = ProcessingProfile.CpuInt8,
        string summaryProfileId = ProcessingProfile.SummaryCpuQ4,
        CancellationToken cancellationToken = default)
    {
        List<string> problems = [];

        HardwareReport hardware;
        try
        {
            hardware = _hardware is null
                ? HardwareReport.Unavailable
                : Describe(await _hardware.ProbeAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            hardware = HardwareReport.Unavailable;
            problems.Add("hardware detection failed: " + ex.GetType().Name);
        }

        List<ComponentReport> components = [];
        List<ArtifactReport> artifacts = [];
        string? interpreter = null;
        string? packages = null;

        if (_setup is not null)
        {
            try
            {
                SetupSnapshot snapshot = _setup.Runtimes.Snapshot(transcriptionProfileId, summaryProfileId);

                components =
                [
                    .. snapshot.Components.Select(c => new ComponentReport
                    {
                        Component = c.Id.ToString(),
                        Status = c.Status.ToString(),
                        // The component's own detail is written by EchoForge from a fixed set of
                        // sentences. None of them can contain user content.
                        Detail = c.Detail,
                        BytesInstalled = c.BytesInstalled,
                        BytesRequired = c.BytesRequired,
                    })
                ];

                artifacts =
                [
                    .. _setup.Artifacts.Artifacts.Select(entry =>
                    {
                        ArtifactState state = _setup.Artifacts.Status(entry);
                        return new ArtifactReport
                        {
                            ArtifactId = entry.ArtifactId,
                            Revision = entry.Revision,
                            Status = state.Status.ToString(),
                            SizeBytes = entry.SizeBytes,
                            BytesOnDisk = state.BytesOnDisk,
                            Sha256 = entry.Sha256,
                        };
                    })
                ];

                interpreter = _setup.Python.TryResolve()?.Version;
                packages = _setup.WorkerEnvironment.TryResolve()?.PackageSummary;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add("installation status could not be read: " + ex.GetType().Name);
            }
        }
        else
        {
            problems.Add("the pinned manifest could not be opened, so nothing may be downloaded");
        }

        return new DiagnosticsReport
        {
            SchemaVersion = 1,
            GeneratedUtc = _clock.GetUtcNow(),
            Version = Version,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            IsPublishedLayout = _layout.LooksPublished,
            Hardware = hardware,
            TranscriptionProfile = transcriptionProfileId,
            SummaryProfile = summaryProfileId,
            InterpreterVersion = interpreter,
            WorkerPackages = packages,
            Components = components,
            Artifacts = artifacts,
            Library = ReadLibrary(problems),
            OfflineVariables = [.. OfflineEnvironment.Variables.Select(v => v.Key + "=" + v.Value)],
            Problems = problems,
        };
    }

    /// <summary>
    /// Writes the report where the user asked for it.
    ///
    /// <para>
    /// Always an explicit action, and nothing sends it anywhere. Written atomically, so a bundle
    /// that failed halfway is not left looking complete.
    /// </para>
    /// </summary>
    public async Task<DiagnosticsResult> WriteAsync(
        string? destination = null,
        string transcriptionProfileId = ProcessingProfile.CpuInt8,
        string summaryProfileId = ProcessingProfile.SummaryCpuQ4,
        CancellationToken cancellationToken = default)
    {
        DiagnosticsReport report;

        try
        {
            report = await CollectAsync(transcriptionProfileId, summaryProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new DiagnosticsResult(false, null, "The diagnostics could not be collected. Nothing was changed.");
        }

        string path = destination ?? Path.Combine(
            _layout.DiagnosticsRoot,
            string.Create(CultureInfo.InvariantCulture, $"echoforge-diagnostics-{_clock.GetUtcNow():yyyyMMdd-HHmmss}.json"));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temporary = path + ".partial";
            await File.WriteAllBytesAsync(
                temporary, JsonSerializer.SerializeToUtf8Bytes(report, DiagnosticsJson), cancellationToken)
                .ConfigureAwait(false);

            File.Move(temporary, path, overwrite: true);

            return new DiagnosticsResult(true, path, "Diagnostics written to " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed bundle changes nothing about the application. Saying so matters: somebody
            // generating diagnostics is already having a bad day.
            return new DiagnosticsResult(
                false, null, $"The diagnostics file could not be written ({ex.GetType().Name}). Nothing else was changed.");
        }
    }

    // -- sections ------------------------------------------------------------------------------------

    private static HardwareReport Describe(HardwareSnapshot hardware) => new()
    {
        OperatingSystem = hardware.OperatingSystem,
        Architecture = hardware.Architecture,
        Cpu = hardware.CpuName,
        LogicalCores = hardware.LogicalCores,
        Avx2 = hardware.HasAvx2,
        Avx512 = hardware.HasAvx512,
        TotalMemoryBytes = hardware.TotalMemoryBytes,
        AvailableDiskBytes = hardware.AvailableDiskBytes,
        Cuda = hardware.Cuda.ToString(),

        // Adapter model and driver, which are machine facts. Nothing about the endpoints beyond
        // how many there are: a device is named after the person's headset, or their company.
        Gpus =
        [
            .. hardware.Gpus.Select(g => new GpuReport
            {
                Vendor = g.Vendor,
                Model = g.Model,
                DedicatedMemoryBytes = g.DedicatedMemoryBytes,
                DriverVersion = g.DriverVersion,
                IsSoftware = g.IsSoftware,
            })
        ],

        InputDeviceCount = hardware.InputDevices.Count,
        OutputDeviceCount = hardware.OutputDevices.Count,
        CouldNotRead = hardware.Unavailable,
    };

    /// <summary>
    /// How much is in the library, and whether its index is healthy.
    ///
    /// <para>
    /// Counts and identifiers only. The session folders are counted by looking for the files that
    /// make a folder a session; none of them is opened, so there is no path by which a title or a
    /// transcript line could reach the bundle.
    /// </para>
    /// </summary>
    private LibraryReport ReadLibrary(List<string> problems)
    {
        try
        {
            if (!Directory.Exists(_layout.SessionsRoot))
            {
                return new LibraryReport { SessionCount = 0, IndexPresent = false };
            }

            int sessions = 0;
            foreach (string directory in Directory.EnumerateDirectories(_layout.SessionsRoot, "*", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(directory, "events.jsonl")) ||
                    File.Exists(Path.Combine(directory, "session.json")))
                {
                    sessions++;
                }
            }

            FileInfo index = new(_layout.IndexPath);

            return new LibraryReport
            {
                SessionCount = sessions,
                IndexPresent = index.Exists,
                IndexBytes = index.Exists ? index.Length : 0,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add("the library could not be counted: " + ex.GetType().Name);
            return new LibraryReport { SessionCount = -1, IndexPresent = false };
        }
    }

    internal static readonly JsonSerializerOptions DiagnosticsJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>The bundle's contents. Every field here is a machine fact or an installation fact.</summary>
public sealed record DiagnosticsReport
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("generated_utc")]
    public DateTimeOffset GeneratedUtc { get; init; }

    [JsonPropertyName("echoforge_version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("dotnet_runtime")]
    public string Runtime { get; init; } = string.Empty;

    [JsonPropertyName("published_layout")]
    public bool IsPublishedLayout { get; init; }

    [JsonPropertyName("hardware")]
    public HardwareReport Hardware { get; init; } = HardwareReport.Unavailable;

    [JsonPropertyName("transcription_profile")]
    public string TranscriptionProfile { get; init; } = string.Empty;

    [JsonPropertyName("summary_profile")]
    public string SummaryProfile { get; init; } = string.Empty;

    [JsonPropertyName("interpreter_version")]
    public string? InterpreterVersion { get; init; }

    [JsonPropertyName("worker_packages")]
    public string? WorkerPackages { get; init; }

    [JsonPropertyName("components")]
    public IReadOnlyList<ComponentReport> Components { get; init; } = [];

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<ArtifactReport> Artifacts { get; init; } = [];

    [JsonPropertyName("library")]
    public LibraryReport Library { get; init; } = new();

    [JsonPropertyName("offline_environment")]
    public IReadOnlyList<string> OfflineVariables { get; init; } = [];

    /// <summary>What could not be collected. Type names only, never a message off the filesystem.</summary>
    [JsonPropertyName("problems")]
    public IReadOnlyList<string> Problems { get; init; } = [];
}

public sealed record HardwareReport
{
    /// <summary>What a machine that would not answer looks like: every field explicitly absent.</summary>
    public static HardwareReport Unavailable { get; } = new();

    [JsonPropertyName("operating_system")]
    public string? OperatingSystem { get; init; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; init; }

    [JsonPropertyName("cpu")]
    public string? Cpu { get; init; }

    [JsonPropertyName("logical_cores")]
    public int LogicalCores { get; init; }

    [JsonPropertyName("avx2")]
    public bool? Avx2 { get; init; }

    [JsonPropertyName("avx512")]
    public bool? Avx512 { get; init; }

    [JsonPropertyName("total_memory_bytes")]
    public long? TotalMemoryBytes { get; init; }

    [JsonPropertyName("available_disk_bytes")]
    public long? AvailableDiskBytes { get; init; }

    [JsonPropertyName("cuda")]
    public string? Cuda { get; init; }

    [JsonPropertyName("gpus")]
    public IReadOnlyList<GpuReport> Gpus { get; init; } = [];

    /// <summary>Counts, not names. An endpoint is named after somebody's headset or their company.</summary>
    [JsonPropertyName("input_device_count")]
    public int InputDeviceCount { get; init; }

    [JsonPropertyName("output_device_count")]
    public int OutputDeviceCount { get; init; }

    [JsonPropertyName("unavailable")]
    public IReadOnlyList<string> CouldNotRead { get; init; } = [];
}

public sealed record GpuReport
{
    [JsonPropertyName("vendor")]
    public string Vendor { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("dedicated_memory_bytes")]
    public long? DedicatedMemoryBytes { get; init; }

    [JsonPropertyName("driver_version")]
    public string? DriverVersion { get; init; }

    [JsonPropertyName("software")]
    public bool IsSoftware { get; init; }
}

public sealed record ComponentReport
{
    [JsonPropertyName("component")]
    public string Component { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;

    [JsonPropertyName("bytes_installed")]
    public long BytesInstalled { get; init; }

    [JsonPropertyName("bytes_required")]
    public long BytesRequired { get; init; }
}

public sealed record ArtifactReport
{
    [JsonPropertyName("artifact_id")]
    public string ArtifactId { get; init; } = string.Empty;

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("bytes_on_disk")]
    public long BytesOnDisk { get; init; }

    /// <summary>The pinned digest, which is public information and the point of the whole manifest.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record LibraryReport
{
    /// <summary>How many meetings exist. Never which, never what they are called.</summary>
    [JsonPropertyName("session_count")]
    public int SessionCount { get; init; }

    [JsonPropertyName("index_present")]
    public bool IndexPresent { get; init; }

    [JsonPropertyName("index_bytes")]
    public long IndexBytes { get; init; }
}
