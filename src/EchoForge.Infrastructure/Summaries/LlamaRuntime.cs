using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using EchoForge.Contracts.Artifacts;
using EchoForge.Infrastructure.Artifacts;

namespace EchoForge.Infrastructure.Summaries;

/// <summary>Where the verified runtime and model ended up, once there is one.</summary>
public sealed record LlamaRuntimePaths(string ServerBinary, string ModelPath, string ProfileId, string ModelRevision);

/// <summary>What the summary runtime needs before it can run, and how much of it is here.</summary>
public sealed record SummaryRuntimeStatus(
    string ProfileId,
    bool Ready,
    long BytesInstalled,
    long BytesRequired,
    string Description)
{
    public double Fraction => BytesRequired <= 0 ? 1 : Math.Clamp((double)BytesInstalled / BytesRequired, 0, 1);

    public bool AnythingToDownload => BytesInstalled < BytesRequired;
}

/// <summary>
/// Turns verified artifacts into a runnable llama.cpp.
///
/// <para>
/// The registry's job ends at "these exact bytes are on disk and their digest matched". llama.cpp
/// ships as a zip, so something has to unpack it, and that something must not become a second way
/// of getting binaries onto the machine. Everything here starts from an artifact the registry has
/// already verified: nothing is extracted that was not first hashed against the manifest, and a
/// staged directory whose source digests no longer match is rebuilt rather than trusted.
/// </para>
///
/// <para>
/// The CUDA build and the CUDA runtime libraries unpack into the <b>same</b> directory on purpose.
/// <c>llama-server.exe</c> loads its backends from DLLs beside itself, so separating them would
/// mean either copying files around at launch or setting a search path — both of which are ways of
/// running a binary against libraries nobody checked.
/// </para>
/// </summary>
public sealed class LlamaRuntimeStager
{
    /// <summary>The one binary EchoForge launches. Named explicitly; there is no search.</summary>
    public const string ServerBinaryName = "llama-server.exe";

    private const string StampFileName = "staged.json";

    private readonly ArtifactRegistry _registry;
    private readonly Lock _sync = new();

    public LlamaRuntimeStager(ArtifactRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>
    /// The model a given profile uses.
    ///
    /// <para>
    /// Profile-scoped rather than "the first summary model in the manifest", because there is now
    /// more than one: taking the first would hand a Gemma run the Ministral weights the moment a
    /// comparison candidate was pinned, and it would look like a quality result rather than a
    /// wiring mistake.
    /// </para>
    /// </summary>
    public ArtifactEntry? ModelEntryFor(string profileId) =>
        _registry.Profile(profileId)?.Artifacts
            .FirstOrDefault(a => string.Equals(a.Kind, "summary-model", StringComparison.Ordinal));

    /// <summary>The default profile's model. Kept for callers that only ever mean production.</summary>
    public ArtifactEntry? ModelEntry => ModelEntryFor(ProcessingProfile.SummaryCudaQ4);

    public string StagingRoot(string profileId) =>
        Path.Combine(_registry.ModelRoot, "summary-runtime", profileId);

    /// <summary>What is present, cheaply enough to ask on every UI refresh.</summary>
    public SummaryRuntimeStatus Status(string profileId)
    {
        ProcessingProfile? profile = _registry.Profile(profileId);
        if (profile is null || profile.Artifacts.Count == 0)
        {
            return new SummaryRuntimeStatus(profileId, false, 0, 0, "That summary profile is not in the manifest.");
        }

        IReadOnlyList<ArtifactState> states = _registry.Status(profile);
        long required = profile.TotalBytes;
        long installed = states.Sum(s => s.IsUsable ? s.Entry.SizeBytes : s.BytesOnDisk);

        if (!states.All(s => s.IsUsable))
        {
            bool started = installed > 0;
            return new SummaryRuntimeStatus(
                profileId, false, installed, required,
                started ? "The summary model is still downloading." : "The summary model has not been downloaded yet.");
        }

        return TryResolve(profileId) is null
            ? new SummaryRuntimeStatus(profileId, false, installed, required, "The summary runtime still has to be unpacked.")
            : new SummaryRuntimeStatus(profileId, true, installed, required, "Ready.");
    }

    /// <summary>
    /// The paths a job needs, or null when the profile is not ready.
    ///
    /// <para>
    /// Returns null rather than throwing, and never falls back to a binary that happens to be on
    /// the machine. An unverified llama.cpp is not a degraded runtime, it is an unknown one.
    /// </para>
    /// </summary>
    public LlamaRuntimePaths? TryResolve(string profileId)
    {
        ProcessingProfile? profile = _registry.Profile(profileId);
        if (profile is null || ModelEntryFor(profileId) is not { } model)
        {
            return null;
        }

        if (_registry.Status(model).Status != ArtifactStatus.Installed)
        {
            return null;
        }

        string binary = Path.Combine(StagingRoot(profileId), ServerBinaryName);
        if (!File.Exists(binary) || !StampMatches(profileId, profile))
        {
            return null;
        }

        return new LlamaRuntimePaths(binary, _registry.InstallPath(model), profileId, model.Revision);
    }

    /// <summary>Downloads whatever is missing, then unpacks it. Safe to call when already ready.</summary>
    public async Task<LlamaRuntimePaths?> EnsureAsync(
        string profileId,
        IProgress<ArtifactProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ProcessingProfile? profile = _registry.Profile(profileId);
        if (profile is null)
        {
            return null;
        }

        IReadOnlyList<ArtifactState> states =
            await _registry.EnsureProfileAsync(profileId, progress, cancellationToken).ConfigureAwait(false);

        if (!states.All(s => s.IsUsable))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        Stage(profileId, profile);
        return TryResolve(profileId);
    }

    /// <summary>
    /// Unpacks the archives into one directory, replacing anything stale.
    ///
    /// <para>
    /// Extraction is done to a fresh neighbour and then swapped in, so an interrupted unpack
    /// leaves the previous working runtime alone rather than a half-populated directory that
    /// looks complete because <c>llama-server.exe</c> happens to have been written first.
    /// </para>
    /// </summary>
    private void Stage(string profileId, ProcessingProfile profile)
    {
        lock (_sync)
        {
            if (StampMatches(profileId, profile) && File.Exists(Path.Combine(StagingRoot(profileId), ServerBinaryName)))
            {
                return;
            }

            string destination = StagingRoot(profileId);
            string building = destination + ".building";

            if (Directory.Exists(building))
            {
                Directory.Delete(building, recursive: true);
            }

            Directory.CreateDirectory(building);

            foreach (ArtifactEntry entry in profile.Artifacts.Where(a => a.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                // Only ever an archive the registry verified.
                if (_registry.Status(entry).Status != ArtifactStatus.Installed)
                {
                    Directory.Delete(building, recursive: true);
                    return;
                }

                ExtractFlattened(_registry.InstallPath(entry), building);
            }

            if (!File.Exists(Path.Combine(building, ServerBinaryName)))
            {
                Directory.Delete(building, recursive: true);
                return;
            }

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(building, destination);

            WriteStamp(profileId, profile);
        }
    }

    /// <summary>
    /// Unpacks an archive, discarding the directory structure inside it.
    ///
    /// <para>
    /// llama.cpp's archives put the binaries at the root and the CUDA archive puts its DLLs in a
    /// nested folder; the server needs them side by side. Flattening also removes any question of
    /// a path inside an archive escaping the destination, because only the file name is ever used.
    /// </para>
    /// </summary>
    private static void ExtractFlattened(string archivePath, string destination)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string target = Path.Combine(destination, entry.Name);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private string StampPath(string profileId) => Path.Combine(StagingRoot(profileId), StampFileName);

    private bool StampMatches(string profileId, ProcessingProfile profile)
    {
        try
        {
            if (!File.Exists(StampPath(profileId)))
            {
                return false;
            }

            using FileStream stream = File.OpenRead(StampPath(profileId));
            StagingStamp? stamp = JsonSerializer.Deserialize(stream, StagingStampContext.Default.StagingStamp);

            return stamp is not null && stamp.Sources.SequenceEqual(SourceDigests(profile), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private void WriteStamp(string profileId, ProcessingProfile profile)
    {
        StagingStamp stamp = new()
        {
            ProfileId = profileId,
            StagedUtc = DateTimeOffset.UtcNow,
            Sources = [.. SourceDigests(profile)],
        };

        try
        {
            using FileStream stream = File.Create(StampPath(profileId));
            JsonSerializer.Serialize(stream, stamp, StagingStampContext.Default.StagingStamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the stamp costs one re-extraction, never correctness.
        }
    }

    /// <summary>The digests this staging was built from, so a re-pin invalidates it.</summary>
    private static IReadOnlyList<string> SourceDigests(ProcessingProfile profile) =>
    [
        .. profile.Artifacts
            .Where(a => a.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.ArtifactId, StringComparer.Ordinal)
            .Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.ArtifactId}:{a.Sha256}"))
    ];

}

/// <summary>What a staged runtime directory was built from, so a re-pin invalidates it.</summary>
internal sealed record StagingStamp
{
    [JsonPropertyName("profile_id")]
    public string ProfileId { get; init; } = string.Empty;

    [JsonPropertyName("staged_utc")]
    public DateTimeOffset StagedUtc { get; init; }

    [JsonPropertyName("sources")]
    public IReadOnlyList<string> Sources { get; init; } = [];
}

[JsonSerializable(typeof(StagingStamp))]
internal sealed partial class StagingStampContext : JsonSerializerContext;
