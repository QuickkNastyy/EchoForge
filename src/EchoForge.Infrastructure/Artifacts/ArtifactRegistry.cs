using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EchoForge.Contracts.Artifacts;

namespace EchoForge.Infrastructure.Artifacts;

/// <summary>What happens to bytes that arrived but did not match what was pinned.</summary>
public enum ArtifactMismatchPolicy
{
    /// <summary>
    /// Keep them beside the destination under a <c>.rejected</c> name. A digest mismatch is
    /// either a corrupted transfer or a substituted file, and the second is worth being able
    /// to look at afterwards.
    /// </summary>
    Quarantine,

    /// <summary>Delete them. Chosen explicitly, never as a silent default.</summary>
    Discard,
}

/// <summary>
/// The only thing that downloads anything.
///
/// <para>
/// It reads the manifest and nothing else. An artifact that is not listed cannot be requested,
/// there is no URL anywhere in the codebase for it to fall back to, and a file is never presented
/// as installed until its length and SHA-256 match the values pinned when it was reviewed.
/// </para>
///
/// <para>
/// Installed artifacts live at
/// <c>&lt;model root&gt;\&lt;artifact id&gt;\&lt;revision&gt;\&lt;filename&gt;</c>, the same layout
/// <c>scripts/verify-models.ps1</c> checks, so the build gate and the application agree about
/// where a verified file is.
/// </para>
/// </summary>
public sealed class ArtifactRegistry : IDisposable
{
    /// <summary>How much of a large file to hash at a time. Bounded on purpose.</summary>
    private const int HashBufferBytes = 1024 * 1024;

    private const int TransferBufferBytes = 128 * 1024;

    private readonly ArtifactManifest _manifest;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, SemaphoreSlim> _perArtifact = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private bool _disposed;

    public ArtifactRegistry(
        ArtifactManifest manifest,
        string? modelRoot = null,
        HttpMessageHandler? handler = null,
        TimeProvider? clock = null,
        ArtifactMismatchPolicy mismatchPolicy = ArtifactMismatchPolicy.Quarantine)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ModelRoot = modelRoot ?? DefaultModelRoot();
        MismatchPolicy = mismatchPolicy;
        _clock = clock ?? TimeProvider.System;

        _ownsHttp = handler is null;
        _http = new HttpClient(handler ?? CreateDefaultHandler(), disposeHandler: _ownsHttp)
        {
            // Timeouts are enforced per call through a linked token instead, because
            // HttpClient.Timeout covers the whole body read and would abort a large,
            // healthy download purely for being large.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Opens the repository manifest, or explains why it cannot be trusted.</summary>
    public static ArtifactRegistry? TryOpen(
        string manifestPath,
        out IReadOnlyList<string> problems,
        string? modelRoot = null,
        HttpMessageHandler? handler = null)
    {
        ManifestLoadResult loaded = ArtifactManifestReader.Load(manifestPath);
        problems = loaded.Problems;

        return loaded.Succeeded ? new ArtifactRegistry(loaded.Manifest!, modelRoot, handler) : null;
    }

    /// <summary>Where verified artifacts live. Never inside the repository.</summary>
    public static string DefaultModelRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EchoForge",
        "models");

    public string ModelRoot { get; }

    public ArtifactMismatchPolicy MismatchPolicy { get; }

    /// <summary>How long one artifact's transfer may take before it is abandoned.</summary>
    public TimeSpan TransferTimeout { get; init; } = TimeSpan.FromMinutes(60);

    public IReadOnlyList<ArtifactEntry> Artifacts => _manifest.Artifacts;

    public ArtifactEntry? Find(string artifactId) =>
        _manifest.Artifacts.FirstOrDefault(a => string.Equals(a.ArtifactId, artifactId, StringComparison.Ordinal));

    /// <summary>
    /// The processing profiles this manifest can support, derived from the artifacts themselves
    /// so nothing can disagree about what a profile needs.
    /// </summary>
    public IReadOnlyList<ProcessingProfile> Profiles()
    {
        List<ProcessingProfile> profiles =
        [
            new(ProcessingProfile.Mock, "Deterministic placeholder. Recognises no speech and needs nothing downloaded.", []),
        ];

        foreach (string id in (string[])
        [
            ProcessingProfile.CpuInt8,
            ProcessingProfile.CudaFp16,
            ProcessingProfile.CudaInt8Float16,
            ProcessingProfile.SummaryCudaQ4,
            ProcessingProfile.SummaryCpuQ4,
            ProcessingProfile.SummaryBakeoff,
        ])
        {
            List<ArtifactEntry> required = [.. _manifest.Artifacts.Where(a => a.BelongsTo(id))];
            if (required.Count > 0)
            {
                profiles.Add(new ProcessingProfile(id, DescribeProfile(id), required));
            }
        }

        return profiles;
    }

    public ProcessingProfile? Profile(string id) =>
        Profiles().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    private static string DescribeProfile(string id) => id switch
    {
        ProcessingProfile.CpuInt8 => "Whisper on the CPU, INT8. Slow but needs no GPU.",
        ProcessingProfile.CudaFp16 => "Whisper on an NVIDIA GPU, FP16.",
        ProcessingProfile.CudaInt8Float16 => "Whisper on an NVIDIA GPU, INT8/FP16. Lower memory.",
        ProcessingProfile.SummaryCudaQ4 => "Gemma 4 12B Q4_0 on an NVIDIA GPU, through llama.cpp.",
        ProcessingProfile.SummaryCpuQ4 => "Gemma 4 12B Q4_0 on the CPU. Works everywhere, and is slow enough to say so.",
        ProcessingProfile.SummaryBakeoff => "Ministral 3 14B Q4_K_M, for measuring against the default. Never the default itself.",
        _ => id,
    };

    // -- where things live -------------------------------------------------------------------

    public string InstallDirectory(ArtifactEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Path.Combine(ModelRoot, entry.ArtifactId, entry.Revision);
    }

    public string InstallPath(ArtifactEntry entry) =>
        Path.Combine(InstallDirectory(entry), entry.FileName);

    private static string PartialPath(string installPath) => installPath + ".partial";

    private static string MarkerPath(string installPath) => installPath + ".verified.json";

    private static string RejectedPath(string installPath) => installPath + ".rejected";

    private static string LockPath(string installPath) => installPath + ".lock";

    // -- status --------------------------------------------------------------------------------

    /// <summary>
    /// What is on disk, cheaply.
    ///
    /// <para>
    /// A file counts as installed only when a verification marker written by this code says its
    /// digest matched, and the file still has the length and modification time recorded then.
    /// Re-hashing gigabytes on every status query would make the UI unusable, so the marker
    /// carries the proof and <see cref="VerifyInstalledAsync"/> re-establishes it on demand. A
    /// file present without a marker is <see cref="ArtifactStatus.Invalid"/>, never installed:
    /// unverified bytes have no standing at all.
    /// </para>
    /// </summary>
    public ArtifactState Status(ArtifactEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string installPath = InstallPath(entry);
        FileInfo file = new(installPath);

        if (!file.Exists)
        {
            FileInfo partial = new(PartialPath(installPath));
            return partial.Exists
                ? new ArtifactState(entry, ArtifactStatus.Downloading, "partly downloaded", partial.Length)
                : new ArtifactState(entry, ArtifactStatus.NotInstalled);
        }

        VerificationMarker? marker = ReadMarker(MarkerPath(installPath));

        if (marker is null)
        {
            return new ArtifactState(entry, ArtifactStatus.Invalid, "present but never verified", file.Length);
        }

        if (!string.Equals(marker.Sha256, entry.Sha256, StringComparison.Ordinal))
        {
            return new ArtifactState(entry, ArtifactStatus.Invalid, "verified against a different pinned digest", file.Length);
        }

        if (file.Length != entry.SizeBytes || marker.Length != file.Length)
        {
            return new ArtifactState(entry, ArtifactStatus.Invalid, "the file has changed length since it was verified", file.Length);
        }

        if (marker.LastWriteUtc != file.LastWriteTimeUtc)
        {
            return new ArtifactState(entry, ArtifactStatus.Invalid, "the file has been modified since it was verified", file.Length);
        }

        return new ArtifactState(entry, ArtifactStatus.Installed, null, file.Length);
    }

    public IReadOnlyList<ArtifactState> Status(ProcessingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return [.. profile.Artifacts.Select(Status)];
    }

    public bool IsProfileReady(ProcessingProfile profile) =>
        profile.Artifacts.Count == 0 || Status(profile).All(s => s.IsUsable);

    /// <summary>Re-hashes an installed artifact and rewrites its marker. Explicit and slow.</summary>
    public async Task<ArtifactState> VerifyInstalledAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        ArtifactEntry entry = Require(artifactId);
        string installPath = InstallPath(entry);

        if (!File.Exists(installPath))
        {
            return new ArtifactState(entry, ArtifactStatus.NotInstalled);
        }

        string actual;
        long length;
        try
        {
            (actual, length) = await HashFileAsync(installPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ArtifactState(entry, ArtifactStatus.Invalid, $"could not be read ({ex.GetType().Name})");
        }

        if (length != entry.SizeBytes || !string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
        {
            return new ArtifactState(entry, ArtifactStatus.Invalid, "does not match the pinned digest", length);
        }

        WriteMarker(installPath, entry, length);
        return new ArtifactState(entry, ArtifactStatus.Installed, null, length);
    }

    /// <summary>
    /// Assembles a profile's verified speech-model files into one directory a recogniser can
    /// load, and returns its path.
    ///
    /// <para>
    /// CTranslate2 wants <c>model.bin</c>, the tokenizer, and the configs side by side, while
    /// the artifact store keeps every file under its own artifact and revision so each can be
    /// verified independently. This bridges the two by copying verified bytes into one place —
    /// never by pointing the recogniser at an unverified directory, and never by downloading.
    /// </para>
    ///
    /// <para>
    /// Returns null when any required file is not installed, because a partially assembled
    /// model directory is worse than none: the library would load it and fail obscurely.
    /// </para>
    /// </summary>
    public string? TryStageModelDirectory(string profileId)
    {
        ProcessingProfile? profile = Profile(profileId);
        if (profile is null)
        {
            return null;
        }

        List<ArtifactEntry> model =
            [.. profile.Artifacts.Where(a => string.Equals(a.Kind, "speech-model", StringComparison.Ordinal))];

        if (model.Count == 0 || model.Any(a => !Status(a).IsUsable))
        {
            return null;
        }

        // One directory per model revision. Two revisions never share a directory, so a
        // re-pin cannot leave a mixed set of files that individually verify.
        string revision = model[0].Revision;
        string staged = Path.Combine(ModelRoot, "staged", revision);

        try
        {
            Directory.CreateDirectory(staged);

            foreach (ArtifactEntry entry in model)
            {
                string destination = Path.Combine(staged, entry.FileName);
                string source = InstallPath(entry);

                // Re-copy only when the staged copy is not already the right length. The model
                // weight file is 1.6 GB; copying it on every job would be an absurd tax.
                if (File.Exists(destination) && new FileInfo(destination).Length == entry.SizeBytes)
                {
                    continue;
                }

                File.Copy(source, destination, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return staged;
    }

    // -- installing -----------------------------------------------------------------------------

    /// <summary>
    /// Makes sure one artifact is present and proven, downloading it if it is not.
    ///
    /// <para>
    /// Safe to call when it is already installed and safe to call offline in that case: nothing
    /// touches the network once the marker holds. Concurrent calls for the same artifact are
    /// serialised, so a second caller waits and then finds it installed rather than fetching it
    /// twice into the same file.
    /// </para>
    /// </summary>
    public async Task<ArtifactState> EnsureAsync(
        string artifactId,
        IProgress<ArtifactProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArtifactEntry entry = Require(artifactId);
        ArtifactState current = Status(entry);
        if (current.IsUsable)
        {
            return current;
        }

        SemaphoreSlim gate = GateFor(entry.ArtifactId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Another caller may have finished while this one waited.
            current = Status(entry);
            if (current.IsUsable)
            {
                return current;
            }

            return await InstallAsync(entry, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Installs every artifact a profile needs, in manifest order.</summary>
    public async Task<IReadOnlyList<ArtifactState>> EnsureProfileAsync(
        string profileId,
        IProgress<ArtifactProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ProcessingProfile profile = Profile(profileId)
            ?? throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "unknown processing profile");

        List<ArtifactState> states = [];
        foreach (ArtifactEntry entry in profile.Artifacts)
        {
            states.Add(await EnsureAsync(entry.ArtifactId, progress, cancellationToken).ConfigureAwait(false));
        }

        return states;
    }

    private async Task<ArtifactState> InstallAsync(
        ArtifactEntry entry,
        IProgress<ArtifactProgressEventArgs>? progress,
        CancellationToken cancellationToken)
    {
        string installPath = InstallPath(entry);
        Directory.CreateDirectory(InstallDirectory(entry));

        // A lock file held with FileShare.None keeps a second process out of the same partial
        // file. The in-process gate above is not enough: two EchoForge instances would
        // otherwise interleave range requests into one file and produce plausible rubbish.
        FileStream? processLock = TryTakeProcessLock(installPath);
        if (processLock is null)
        {
            return new ArtifactState(entry, ArtifactStatus.Failed, "another process is already downloading this");
        }

        using CancellationTokenSource timeout = new(TransferTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            string partial = PartialPath(installPath);
            long received = await TransferAsync(entry, partial, progress, linked.Token).ConfigureAwait(false);

            progress?.Report(new ArtifactProgressEventArgs(entry.ArtifactId, ArtifactStatus.Verifying, received, entry.SizeBytes));

            if (received != entry.SizeBytes)
            {
                // Short, not wrong. The bytes that did arrive are a valid prefix, so they are
                // kept and the next attempt resumes from them. Quarantining here would make
                // every dropped connection restart a 1.6 GB download from zero.
                return new ArtifactState(
                    entry,
                    ArtifactStatus.Failed,
                    "the download was interrupted before it finished; it will continue from where it stopped",
                    received);
            }

            (string actual, long length) = await HashFileAsync(partial, linked.Token).ConfigureAwait(false);
            if (length != entry.SizeBytes || !string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
            {
                return Reject(entry, partial, "the downloaded bytes do not match the pinned digest");
            }

            // Verified. Only now may it take the name that means "installed".
            if (File.Exists(installPath))
            {
                File.Delete(installPath);
            }

            File.Move(partial, installPath);
            WriteMarker(installPath, entry, length);

            progress?.Report(new ArtifactProgressEventArgs(entry.ArtifactId, ArtifactStatus.Installed, length, entry.SizeBytes));
            return new ArtifactState(entry, ArtifactStatus.Installed, null, length);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new ArtifactState(entry, ArtifactStatus.Failed, "the download took longer than the time limit");
        }
        catch (OperationCanceledException)
        {
            // The partial file is deliberately kept: cancelling should cost the user the time
            // already spent, not the bytes already fetched.
            return new ArtifactState(entry, ArtifactStatus.Failed, "the download was cancelled", PartialLength(installPath));
        }
        catch (HttpRequestException ex)
        {
            return new ArtifactState(entry, ArtifactStatus.Failed, DescribeNetworkFailure(ex), PartialLength(installPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ArtifactState(entry, ArtifactStatus.Failed, $"could not be written ({ex.GetType().Name})", PartialLength(installPath));
        }
        finally
        {
            processLock.Dispose();
            TryDelete(LockPath(installPath));
        }
    }

    /// <summary>
    /// Moves the bytes, resuming where the server allows it.
    ///
    /// <para>
    /// A server that ignores the range header answers 200 with the whole file; that is not an
    /// error, it just means the partial file has to be thrown away and started again rather than
    /// appended to. Appending to it would splice the start of the file onto the middle of itself
    /// and produce something that is exactly the right length and completely wrong.
    /// </para>
    /// </summary>
    private async Task<long> TransferAsync(
        ArtifactEntry entry,
        string partial,
        IProgress<ArtifactProgressEventArgs>? progress,
        CancellationToken cancellationToken)
    {
        long resumeFrom = 0;
        FileInfo existing = new(partial);
        if (existing.Exists)
        {
            // More bytes than the pinned length means these are not the pinned bytes.
            resumeFrom = existing.Length > entry.SizeBytes ? 0 : existing.Length;
            if (resumeFrom == 0)
            {
                TryDelete(partial);
            }
        }

        using HttpRequestMessage request = new(HttpMethod.Get, entry.Url);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        }

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial is at least as long as the server thinks the file is. Start over.
            TryDelete(partial);
            return await TransferAsync(entry, partial, progress, cancellationToken).ConfigureAwait(false);
        }

        response.EnsureSuccessStatusCode();

        bool appending = response.StatusCode == HttpStatusCode.PartialContent && resumeFrom > 0;
        if (!appending)
        {
            resumeFrom = 0;
        }

        long? declared = DeclaredTotalLength(response, appending);
        if (declared is { } total && total != entry.SizeBytes)
        {
            throw new IOException(Invariant($"the server offers {total} bytes but {entry.SizeBytes} were pinned"));
        }

        FileMode mode = appending ? FileMode.Append : FileMode.Create;
        long written = resumeFrom;

        await using (FileStream file = new(partial, mode, FileAccess.Write, FileShare.None, TransferBufferBytes, useAsync: true))
        await using (Stream network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            byte[] buffer = new byte[TransferBufferBytes];
            long lastReported = 0;

            while (true)
            {
                int read = await network.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                // Stop the moment it grows past what was pinned, rather than filling a disk
                // with something that was going to be rejected anyway.
                if (written + read > entry.SizeBytes)
                {
                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                    throw new IOException(Invariant($"the server sent more than the pinned {entry.SizeBytes} bytes"));
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;

                if (written - lastReported >= TransferBufferBytes * 8)
                {
                    lastReported = written;
                    progress?.Report(new ArtifactProgressEventArgs(
                        entry.ArtifactId, ArtifactStatus.Downloading, written, entry.SizeBytes));
                }
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>The full length of the resource, when the response says it unambiguously.</summary>
    private static long? DeclaredTotalLength(HttpResponseMessage response, bool appending)
    {
        if (appending)
        {
            return response.Content.Headers.ContentRange?.Length;
        }

        return response.Content.Headers.ContentLength;
    }

    private ArtifactState Reject(ArtifactEntry entry, string partial, string reason)
    {
        string installPath = InstallPath(entry);

        if (MismatchPolicy == ArtifactMismatchPolicy.Quarantine)
        {
            try
            {
                string rejected = RejectedPath(installPath);
                TryDelete(rejected);
                File.Move(partial, rejected);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(partial);
            }
        }
        else
        {
            TryDelete(partial);
        }

        return new ArtifactState(entry, ArtifactStatus.Invalid, reason);
    }

    // -- plumbing ---------------------------------------------------------------------------------

    private ArtifactEntry Require(string artifactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);

        return Find(artifactId) ?? throw new ArgumentOutOfRangeException(
            nameof(artifactId),
            artifactId,
            "that artifact is not in the pinned manifest, so there is nowhere to fetch it from");
    }

    private SemaphoreSlim GateFor(string artifactId)
    {
        lock (_sync)
        {
            if (!_perArtifact.TryGetValue(artifactId, out SemaphoreSlim? gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _perArtifact[artifactId] = gate;
            }

            return gate;
        }
    }

    private static FileStream? TryTakeProcessLock(string installPath)
    {
        try
        {
            return new FileStream(LockPath(installPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long PartialLength(string installPath)
    {
        FileInfo partial = new(PartialPath(installPath));
        return partial.Exists ? partial.Length : 0;
    }

    private static async Task<(string Sha256, long Length)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[HashBufferBytes];
        long length = 0;

        await using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferBytes, useAsync: true))
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                length += read;
            }
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), length);
    }

    private void WriteMarker(string installPath, ArtifactEntry entry, long length)
    {
        FileInfo file = new(installPath);
        VerificationMarker marker = new()
        {
            ArtifactId = entry.ArtifactId,
            Revision = entry.Revision,
            Sha256 = entry.Sha256,
            Length = length,
            LastWriteUtc = file.LastWriteTimeUtc,
            VerifiedUtc = _clock.GetUtcNow(),
        };

        try
        {
            string path = MarkerPath(installPath);
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(marker, MarkerJson));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a marker the artifact reads as Invalid and gets re-verified. Failing to
            // write it costs time later; claiming it succeeded would cost correctness.
        }
    }

    private static VerificationMarker? ReadMarker(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<VerificationMarker>(stream, MarkerJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are untidy; none of them can be mistaken for a verified artifact.
        }
    }

    private static string DescribeNetworkFailure(HttpRequestException ex) => ex.StatusCode switch
    {
        HttpStatusCode.NotFound => "the pinned file is no longer at that address",
        HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "the download was refused by the server",
        null => "the download could not reach the server; check the network connection",
        _ => Invariant($"the server answered {(int)ex.StatusCode.Value}"),
    };

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        // The system proxy, and the credentials the user is already signed in with. A managed
        // machine that only reaches the internet through a proxy must still be able to install.
        UseProxy = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions MarkerJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsHttp)
        {
            _http.Dispose();
        }

        lock (_sync)
        {
            foreach (SemaphoreSlim gate in _perArtifact.Values)
            {
                gate.Dispose();
            }

            _perArtifact.Clear();
        }
    }

    /// <summary>
    /// Proof that a file's digest was checked, written only after it matched.
    ///
    /// <para>
    /// It records the length and modification time as well, so an artifact that was replaced or
    /// truncated after verification reads as invalid instead of quietly continuing to count as
    /// installed.
    /// </para>
    /// </summary>
    private sealed record VerificationMarker
    {
        [JsonPropertyName("artifact_id")]
        public required string ArtifactId { get; init; }

        [JsonPropertyName("revision")]
        public required string Revision { get; init; }

        [JsonPropertyName("sha256")]
        public required string Sha256 { get; init; }

        [JsonPropertyName("length")]
        public required long Length { get; init; }

        [JsonPropertyName("last_write_utc")]
        public required DateTime LastWriteUtc { get; init; }

        [JsonPropertyName("verified_utc")]
        public required DateTimeOffset VerifiedUtc { get; init; }
    }
}
