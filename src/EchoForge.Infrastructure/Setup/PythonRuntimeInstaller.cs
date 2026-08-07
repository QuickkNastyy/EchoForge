using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Infrastructure.Artifacts;

namespace EchoForge.Infrastructure.Setup;

/// <summary>Where the app-local interpreter ended up, once there is one.</summary>
public sealed record AppLocalPython(string ExecutablePath, string HomeDirectory, string Revision, string Version);

/// <summary>
/// Unpacks the pinned CPython that EchoForge runs its workers with.
///
/// <para>
/// <b>EchoForge ships an interpreter rather than looking for one.</b> A Python found on PATH is a
/// Python somebody else installs, upgrades and removes: the wheel closure is pinned to CPython 3.12
/// on Windows x64, and a machine that quietly moved to 3.13 would fail at the first native import
/// with an error about ABI tags that means nothing to the person holding the meeting. Shipping the
/// interpreter makes the whole stack one pinned thing.
/// </para>
///
/// <para>
/// The archive is a <c>.tar.gz</c> rather than a zip because that is what the publisher signs and
/// hashes, and the digest is the point. It is extracted into a directory named for the pinned
/// revision, so a re-pin installs beside the old one rather than over it, and activation is a
/// directory rename after everything is on disk — an interrupted unpack leaves a
/// <c>.building</c> directory that the next attempt discards, never a half-populated runtime that
/// looks complete because <c>python.exe</c> happened to be written first.
/// </para>
/// </summary>
public sealed class PythonRuntimeInstaller
{
    /// <summary>The manifest entry that is the interpreter. Named once, so nothing has to guess.</summary>
    public const string ArtifactId = "python.cpython-3-12-13";

    private const string StampFileName = "installed.json";

    /// <summary>Where python-build-standalone puts everything inside its archive.</summary>
    private const string ArchiveRoot = "python/";

    private readonly ArtifactRegistry _registry;
    private readonly AppLayout _layout;
    private readonly Lock _sync = new();

    public PythonRuntimeInstaller(ArtifactRegistry registry, AppLayout? layout = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _layout = layout ?? AppLayout.Current;
    }

    public ArtifactEntry? Entry => _registry.Find(ArtifactId);

    /// <summary>Where a given revision is unpacked to.</summary>
    public string HomeFor(string revision) => Path.Combine(_layout.PythonRoot, revision);

    public static string ExecutableIn(string home) => Path.Combine(home, "python.exe");

    /// <summary>
    /// The interpreter, or null when it is not installed.
    ///
    /// <para>
    /// Cheap enough for a status refresh: it checks that the executable exists and that the stamp
    /// says it was unpacked from the digest currently pinned. It deliberately does not re-hash the
    /// 46 MB archive, which <see cref="RepairAsync"/> does when something looks wrong.
    /// </para>
    /// </summary>
    public AppLocalPython? TryResolve()
    {
        if (Entry is not { } entry)
        {
            return null;
        }

        string home = HomeFor(entry.Revision);
        string executable = ExecutableIn(home);

        if (!File.Exists(executable))
        {
            return null;
        }

        InstallStamp? stamp = ReadStamp(home);
        if (stamp is null || !string.Equals(stamp.Sha256, entry.Sha256, StringComparison.Ordinal))
        {
            return null;
        }

        return new AppLocalPython(executable, home, entry.Revision, stamp.Version);
    }

    /// <summary>What state the interpreter is in, without doing any work.</summary>
    public RuntimeComponentState Status()
    {
        if (Entry is not { } entry)
        {
            return RuntimeComponentState.Missing(
                RuntimeComponentId.PythonRuntime,
                "The pinned interpreter is not in this build's manifest.");
        }

        if (TryResolve() is { } python)
        {
            return RuntimeComponentState.Ready(
                RuntimeComponentId.PythonRuntime,
                string.Create(CultureInfo.InvariantCulture, $"CPython {python.Version}"),
                entry.SizeBytes);
        }

        ArtifactState artifact = _registry.Status(entry);

        return artifact.Status switch
        {
            ArtifactStatus.Installed => new RuntimeComponentState(
                RuntimeComponentId.PythonRuntime,
                RuntimeComponentStatus.Installing,
                "The interpreter has been downloaded and still has to be unpacked.",
                artifact.BytesOnDisk,
                entry.SizeBytes),

            ArtifactStatus.Downloading => new RuntimeComponentState(
                RuntimeComponentId.PythonRuntime,
                RuntimeComponentStatus.Downloading,
                "The interpreter is still downloading.",
                artifact.BytesOnDisk,
                entry.SizeBytes),

            ArtifactStatus.Invalid => new RuntimeComponentState(
                RuntimeComponentId.PythonRuntime,
                RuntimeComponentStatus.Corrupt,
                artifact.Detail ?? "The downloaded interpreter did not match what was pinned.",
                artifact.BytesOnDisk,
                entry.SizeBytes),

            _ => new RuntimeComponentState(
                RuntimeComponentId.PythonRuntime,
                RuntimeComponentStatus.NotInstalled,
                "EchoForge's own copy of Python has not been installed yet.",
                artifact.BytesOnDisk,
                entry.SizeBytes),
        };
    }

    /// <summary>Downloads the archive if needed, then unpacks it. Safe to call when already ready.</summary>
    public async Task<AppLocalPython?> EnsureAsync(
        IProgress<ArtifactProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Entry is not { } entry)
        {
            return null;
        }

        if (TryResolve() is { } existing)
        {
            return existing;
        }

        ArtifactState state = await _registry
            .EnsureAsync(ArtifactId, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!state.IsUsable)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        Extract(entry, cancellationToken);
        return TryResolve();
    }

    /// <summary>
    /// Re-verifies the archive and unpacks it again.
    ///
    /// <para>
    /// The repair for this component and nothing else: it never touches the worker environment,
    /// the models, or anything under the sessions directory.
    /// </para>
    /// </summary>
    public async Task<AppLocalPython?> RepairAsync(
        IProgress<ArtifactProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Entry is not { } entry)
        {
            return null;
        }

        ArtifactState verified = await _registry
            .VerifyInstalledAsync(ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (!verified.IsUsable)
        {
            // Whatever is there is not what was pinned. Download it again rather than unpacking it.
            TryDeleteDirectory(HomeFor(entry.Revision));
            return await EnsureAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        TryDeleteDirectory(HomeFor(entry.Revision));
        Extract(entry, cancellationToken);
        return TryResolve();
    }

    // -- unpacking --------------------------------------------------------------------------------

    private void Extract(ArtifactEntry entry, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            string home = HomeFor(entry.Revision);
            string building = home + ".building";

            if (File.Exists(ExecutableIn(home)) && ReadStamp(home) is { } stamp &&
                string.Equals(stamp.Sha256, entry.Sha256, StringComparison.Ordinal))
            {
                return;
            }

            TryDeleteDirectory(building);
            Directory.CreateDirectory(building);

            try
            {
                using (FileStream archive = File.OpenRead(_registry.InstallPath(entry)))
                using (GZipStream decompressed = new(archive, CompressionMode.Decompress))
                using (TarReader reader = new(decompressed))
                {
                    while (reader.GetNextEntry() is { } tarEntry)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ExtractEntry(tarEntry, building);
                    }
                }

                if (!File.Exists(ExecutableIn(building)))
                {
                    TryDeleteDirectory(building);
                    return;
                }

                WriteStamp(building, entry);

                TryDeleteDirectory(home);
                Directory.CreateDirectory(Path.GetDirectoryName(home)!);
                Directory.Move(building, home);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or OperationCanceledException)
            {
                // The previous runtime, if any, is untouched: everything happened in .building.
                TryDeleteDirectory(building);

                if (ex is OperationCanceledException)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Writes one archive entry, refusing anything that tries to leave the destination.
    ///
    /// <para>
    /// The archive is verified by digest before this runs, so a traversing path would mean the
    /// publisher shipped one — but a path check costs nothing and an extractor that trusts its
    /// input is the wrong thing to have written either way.
    /// </para>
    /// </summary>
    private static void ExtractEntry(TarEntry entry, string destination)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
        {
            return;
        }

        string relative = entry.Name;

        // Everything sits under a single "python/" directory in the archive; the runtime is that
        // directory's contents, not a directory containing it.
        if (relative.StartsWith(ArchiveRoot, StringComparison.Ordinal))
        {
            relative = relative[ArchiveRoot.Length..];
        }

        if (relative.Length == 0)
        {
            return;
        }

        string root = Path.GetFullPath(destination);
        string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("the interpreter archive contains a path outside its own directory");
        }

        if (entry.EntryType == TarEntryType.Directory)
        {
            Directory.CreateDirectory(target);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, overwrite: true);
    }

    private static string StampPath(string home) => Path.Combine(home, StampFileName);

    private static InstallStamp? ReadStamp(string home)
    {
        try
        {
            if (!File.Exists(StampPath(home)))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(StampPath(home));
            return JsonSerializer.Deserialize(stream, InstallStampContext.Default.InstallStamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteStamp(string home, ArtifactEntry entry)
    {
        InstallStamp stamp = new()
        {
            ArtifactId = entry.ArtifactId,
            Revision = entry.Revision,
            Sha256 = entry.Sha256,
            Version = DescribeVersion(entry),
            InstalledUtc = DateTimeOffset.UtcNow,
        };

        using FileStream stream = File.Create(StampPath(home));
        JsonSerializer.Serialize(stream, stamp, InstallStampContext.Default.InstallStamp);
    }

    /// <summary>
    /// The version, taken from the pinned entry rather than by running the interpreter.
    ///
    /// <para>
    /// Running it would be the more direct answer and is what
    /// <c>WorkerEnvironmentInstaller</c> does when it builds the environment; here the archive's
    /// own identity is enough, and a status refresh that started a process every time would be a
    /// poor trade.
    /// </para>
    /// </summary>
    private static string DescribeVersion(ArtifactEntry entry)
    {
        // "CPython 3.12.13 (python-build-standalone 20260805, ...)" -> "3.12.13"
        const string prefix = "CPython ";
        string text = entry.RuntimeVersion;

        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return text;
        }

        string rest = text[prefix.Length..];
        int space = rest.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? rest : rest[..space];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover directory with no stamp reads as not installed, which is the truth.
        }
    }
}

/// <summary>What an unpacked runtime was built from, so a re-pin invalidates it.</summary>
internal sealed record InstallStamp
{
    [JsonPropertyName("artifact_id")]
    public string ArtifactId { get; init; } = string.Empty;

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("installed_utc")]
    public DateTimeOffset InstalledUtc { get; init; }
}

[JsonSerializable(typeof(InstallStamp))]
internal sealed partial class InstallStampContext : JsonSerializerContext;
