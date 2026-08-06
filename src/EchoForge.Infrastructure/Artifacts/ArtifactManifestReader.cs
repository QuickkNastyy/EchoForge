using System.Globalization;
using System.Text.Json;
using EchoForge.Contracts.Artifacts;

namespace EchoForge.Infrastructure.Artifacts;

/// <summary>The outcome of loading a manifest. A rejected manifest permits nothing.</summary>
public sealed record ManifestLoadResult(ArtifactManifest? Manifest, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Manifest is not null && Problems.Count == 0;
}

/// <summary>
/// Reads and re-validates <c>artifacts/manifest.json</c>.
///
/// <para>
/// The PowerShell gate checks the same rules at build time. This checks them again at run time,
/// because the file on a user's disk is not the file that was reviewed, and a downloader that
/// trusted whatever it was handed would be a downloader with no pinning at all. A manifest that
/// fails any check is refused whole: half a manifest is not a smaller allow-list, it is an
/// unreviewed one.
/// </para>
/// </summary>
public static class ArtifactManifestReader
{
    /// <summary>Names that move. Pinning to one of them pins nothing.</summary>
    private static readonly string[] MovingReferences =
        ["main", "master", "latest", "head", "dev", "develop", "trunk", "stable", "newest"];

    private static readonly string[] KnownKinds =
        ["speech-model", "summary-model", "diarization-model", "runtime"];

    private static readonly JsonSerializerOptions Json = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    };

    public static ManifestLoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new ManifestLoadResult(null, [$"the artifact manifest was not found at {Path.GetFileName(path)}"]);
        }

        ArtifactManifest? manifest;
        try
        {
            using FileStream stream = File.OpenRead(path);
            manifest = JsonSerializer.Deserialize<ArtifactManifest>(stream, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ManifestLoadResult(null, [$"the artifact manifest could not be read ({ex.GetType().Name})"]);
        }

        return manifest is null
            ? new ManifestLoadResult(null, ["the artifact manifest is empty"])
            : Validate(manifest);
    }

    public static ManifestLoadResult Validate(ArtifactManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        List<string> problems = [];

        if (manifest.SchemaVersion != 1)
        {
            problems.Add(Invariant($"manifest schema_version {manifest.SchemaVersion} is not supported"));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (ArtifactEntry entry in manifest.Artifacts)
        {
            string id = string.IsNullOrWhiteSpace(entry.ArtifactId) ? "<no artifact_id>" : entry.ArtifactId;

            if (!seen.Add(id))
            {
                problems.Add(Invariant($"{id} appears more than once"));
            }

            if (!KnownKinds.Contains(entry.Kind, StringComparer.Ordinal))
            {
                problems.Add(Invariant($"{id} has unknown kind '{entry.Kind}'"));
            }

            if (MovingReferences.Contains(entry.Revision, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(Invariant($"{id} pins '{entry.Revision}', which moves"));
            }
            else if (entry.Revision.Length < 7)
            {
                problems.Add(Invariant($"{id} has a revision too short to be immutable"));
            }

            if (entry.FileName.Length == 0 ||
                entry.FileName.IndexOfAny(['*', '?']) >= 0 ||
                entry.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                problems.Add(Invariant($"{id} does not name one exact file"));
            }

            if (entry.SizeBytes <= 0)
            {
                problems.Add(Invariant($"{id} has a non-positive size"));
            }

            if (!IsSha256(entry.Sha256))
            {
                problems.Add(Invariant($"{id} has a sha256 that is not 64 lower-case hex characters"));
            }

            problems.AddRange(ValidateUrl(id, entry));

            if (entry.Profiles.Count == 0)
            {
                problems.Add(Invariant($"{id} belongs to no processing profile"));
            }

            if (string.IsNullOrWhiteSpace(entry.License) || string.IsNullOrWhiteSpace(entry.LicenseFile))
            {
                problems.Add(Invariant($"{id} does not record its licence"));
            }
        }

        return problems.Count > 0
            ? new ManifestLoadResult(null, problems)
            : new ManifestLoadResult(manifest, []);
    }

    private static IEnumerable<string> ValidateUrl(string id, ArtifactEntry entry)
    {
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out Uri? uri))
        {
            yield return Invariant($"{id} has no usable download URL");
            yield break;
        }

        // Plain HTTP is permitted only on the loopback interface. That exists so the
        // downloader's own tests can run against a local server without a public network;
        // anything reachable off this machine must be encrypted.
        bool https = uri.Scheme == Uri.UriSchemeHttps;
        bool loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;

        if (!https && !loopbackHttp)
        {
            yield return Invariant($"{id} would be fetched over an unencrypted connection");
        }

        if (entry.Url.IndexOfAny(['{', '}', '*', '?']) >= 0)
        {
            yield return Invariant($"{id} has a templated URL rather than an exact location");
        }
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
