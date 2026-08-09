using System.Security.Cryptography;
using EchoForge.Contracts.Artifacts;
using EchoForge.Infrastructure.Artifacts;

namespace EchoForge.UnitTests;

/// <summary>
/// The downloader, judged entirely against a local server.
///
/// <para>
/// Every interesting case is a misbehaviour: a server that ignores a range request, one that
/// closes halfway through, one that offers a different length from the one that was pinned, one
/// that serves the wrong bytes entirely. None of those can be arranged reliably against a public
/// host, and a test suite that reached for one would be slow, flaky, and offline-hostile.
/// </para>
/// </summary>
public sealed class ArtifactRegistryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static byte[] Payload(int length = 64 * 1024, byte seed = 7)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31 + seed) & 0xFF);
        }

        return bytes;
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ArtifactEntry Entry(string url, byte[] content, string id = "runtime.test") => new()
    {
        ArtifactId = id,
        Kind = "runtime",
        Repository = "https://example.invalid/project",
        Url = url,
        Revision = "0d8bcd362ac75ef860ef161d6f0efad0ae439ff0",
        FileName = "artifact.bin",
        SizeBytes = content.Length,
        Sha256 = Digest(content),
        License = "MIT",
        LicenseFile = "third_party/licenses/ctranslate2-4.8.1-LICENSE.txt",
        RuntimeVersion = "test",
        Profiles = ["cpu-int8"],
        VerifiedUtc = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero),
    };

    private ArtifactRegistry Registry(params ArtifactEntry[] entries) =>
        new(new ArtifactManifest { Artifacts = entries }, Path.Combine(_temp.Path, "models"))
        {
            TransferTimeout = TimeSpan.FromSeconds(30),
        };

    // -- the manifest is the whole allow-list ------------------------------------------------

    [Fact]
    public async Task AnArtifactThatIsNotInTheManifestCannotBeRequested()
    {
        using ArtifactRegistry registry = Registry(Entry("https://example.invalid/a", Payload()));

        // There is no URL anywhere else in the codebase to fall back to, so this is not merely
        // refused - there is nothing it could possibly mean.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => registry.EnsureAsync("runtime.not-listed"));
    }

    [Fact]
    public void AManifestWithAMovingRevisionIsRefusedWhole()
    {
        ArtifactEntry moving = Entry("https://example.invalid/a", Payload()) with { Revision = "main" };

        ManifestLoadResult result = ArtifactManifestReader.Validate(new ArtifactManifest { Artifacts = [moving] });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Contains("moves", StringComparison.Ordinal));
    }

    [Fact]
    public void AManifestWithAnUnencryptedOffMachineUrlIsRefused()
    {
        ArtifactEntry insecure = Entry("http://example.invalid/a", Payload());

        ManifestLoadResult result = ArtifactManifestReader.Validate(new ArtifactManifest { Artifacts = [insecure] });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Contains("unencrypted", StringComparison.Ordinal));
    }

    [Fact]
    public void PlainHttpIsAllowedOnLoopbackSoTheseTestsCanRunOffline()
    {
        ArtifactEntry loopback = Entry("http://127.0.0.1:9/a", Payload());

        Assert.True(ArtifactManifestReader.Validate(new ArtifactManifest { Artifacts = [loopback] }).Succeeded);
    }

    [Fact]
    public void AStagedFilenameIsOptionalButMustRemainOneBasename()
    {
        ArtifactEntry historical = Entry("https://example.invalid/artifact.bin", Payload());
        ArtifactEntry invalid = historical with { StageFileName = "dependency?.json" };

        Assert.True(ArtifactManifestReader.Validate(
            new ArtifactManifest { Artifacts = [historical] }).Succeeded);
        ManifestLoadResult result = ArtifactManifestReader.Validate(
            new ArtifactManifest { Artifacts = [invalid] });
        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Contains("staged file", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRepositoryManifestLoadsAndYieldsProfiles()
    {
        string manifestPath = Path.Combine(WorkerTestEnvironment.RepositoryRoot, "artifacts", "manifest.json");

        using ArtifactRegistry? registry = ArtifactRegistry.TryOpen(
            manifestPath, out IReadOnlyList<string> problems, Path.Combine(_temp.Path, "models"));

        Assert.NotNull(registry);
        Assert.Empty(problems);

        IReadOnlyList<ProcessingProfile> profiles = registry!.Profiles();
        Assert.Contains(profiles, p => p.Id == ProcessingProfile.Mock);
        Assert.Contains(profiles, p => p.Id == ProcessingProfile.CpuInt8);
        ProcessingProfile nemo = Assert.Single(profiles, p => p.Id == ProcessingProfile.AsrNemoRuntime);
        Assert.Contains(nemo.Artifacts, artifact => artifact.ArtifactId == "runtime.uv-linux");

        ProcessingProfile mock = profiles.First(p => p.Id == ProcessingProfile.Mock);
        Assert.Empty(mock.Artifacts);
        Assert.True(registry.IsProfileReady(mock));

        // Nothing is downloaded, so a real profile is not ready and says so honestly.
        ProcessingProfile cpu = profiles.First(p => p.Id == ProcessingProfile.CpuInt8);
        Assert.NotEmpty(cpu.Artifacts);
        Assert.False(registry.IsProfileReady(cpu));
        Assert.All(registry.Status(cpu), s => Assert.Equal(ArtifactStatus.NotInstalled, s.Status));
    }

    // -- the happy path -------------------------------------------------------------------------

    [Fact]
    public async Task AVerifiedArtifactIsDownloadedAndActivated()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        Assert.Equal(ArtifactStatus.NotInstalled, registry.Status(entry).Status);

        List<ArtifactProgressEventArgs> progress = [];
        ArtifactState state = await registry.EnsureAsync(
            entry.ArtifactId, new Progress<ArtifactProgressEventArgs>(p => { lock (progress) { progress.Add(p); } }));

        Assert.Equal(ArtifactStatus.Installed, state.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(registry.InstallPath(entry)));

        // No partial file survives an activation.
        Assert.False(File.Exists(registry.InstallPath(entry) + ".partial"));

        lock (progress)
        {
            Assert.Contains(progress, p => p.Status == ArtifactStatus.Installed);
        }
    }

    [Fact]
    public async Task AnAlreadyInstalledArtifactIsNotDownloadedAgain()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        await registry.EnsureAsync(entry.ArtifactId);
        int after = server.Requests;

        ArtifactState again = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Installed, again.Status);
        Assert.Equal(after, server.Requests);
    }

    [Fact]
    public async Task AnInstalledArtifactStaysUsableWithNoServerAtAll()
    {
        byte[] content = Payload();
        ArtifactEntry entry;

        using (LoopbackHttpServer server = new(content))
        {
            entry = Entry(server.Url, content);
            using ArtifactRegistry online = Registry(entry);
            Assert.Equal(ArtifactStatus.Installed, (await online.EnsureAsync(entry.ArtifactId)).Status);
        }

        // The server is gone. Everything below has to work anyway.
        using ArtifactRegistry offline = Registry(entry);

        Assert.Equal(ArtifactStatus.Installed, offline.Status(entry).Status);
        Assert.Equal(ArtifactStatus.Installed, (await offline.EnsureAsync(entry.ArtifactId)).Status);
        Assert.Equal(ArtifactStatus.Installed, (await offline.VerifyInstalledAsync(entry.ArtifactId)).Status);
    }

    [Fact]
    public async Task PathsWithSpacesAndNonAsciiCharactersWork()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        ArtifactEntry entry = Entry(server.Url, content);

        string root = Path.Combine(_temp.Path, "мои модели (2026)", "model store");
        using ArtifactRegistry registry = new(new ArtifactManifest { Artifacts = [entry] }, root);

        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Installed, state.Status);
        Assert.StartsWith(root, registry.InstallPath(entry), StringComparison.Ordinal);
        Assert.True(File.Exists(registry.InstallPath(entry)));
    }

    // -- what must never be presented as installed -----------------------------------------------

    [Fact]
    public async Task BytesThatDoNotMatchThePinnedDigestAreNeverActivated()
    {
        byte[] pinned = Payload();
        using LoopbackHttpServer server = new(Payload(pinned.Length, seed: 99));
        ArtifactEntry entry = Entry(server.Url, pinned);
        using ArtifactRegistry registry = Registry(entry);

        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Invalid, state.Status);
        Assert.Contains("digest", state.Detail!, StringComparison.Ordinal);
        Assert.False(File.Exists(registry.InstallPath(entry)));

        // Quarantined for diagnosis, not deleted and not left wearing the real name.
        Assert.True(File.Exists(registry.InstallPath(entry) + ".rejected"));
    }

    [Fact]
    public async Task AServerOfferingADifferentLengthIsRefusedBeforeTheBodyIsRead()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content) { DeclaredLengthOverride = content.Length + 4096 };
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Failed, state.Status);
        Assert.False(File.Exists(registry.InstallPath(entry)));
    }

    [Fact]
    public async Task AnUndeclaredBodyThatOutgrowsThePinnedSizeIsStoppedMidTransfer()
    {
        byte[] content = Payload();

        // No Content-Length, so there is nothing to check up front and the connection close
        // delimits the body. The running size guard is the only thing left to stop this.
        using LoopbackHttpServer server = new(content)
        {
            OmitContentLength = true,
            ExtraTrailingBytes = 8192,
        };

        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        Assert.NotEqual(ArtifactStatus.Installed, state.Status);
        Assert.False(File.Exists(registry.InstallPath(entry)));
    }

    [Fact]
    public async Task AnUndeclaredBodyOfExactlyTheRightLengthStillInstalls()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content) { OmitContentLength = true };
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        Assert.Equal(ArtifactStatus.Installed, (await registry.EnsureAsync(entry.ArtifactId)).Status);
    }

    [Fact]
    public async Task AFilePresentWithoutAVerificationMarkerIsInvalidRatherThanInstalled()
    {
        byte[] content = Payload();
        ArtifactEntry entry = Entry("https://example.invalid/a", content);
        using ArtifactRegistry registry = Registry(entry);

        // Exactly the right bytes, put there by something other than this code.
        Directory.CreateDirectory(registry.InstallDirectory(entry));
        await File.WriteAllBytesAsync(registry.InstallPath(entry), content);

        ArtifactState state = registry.Status(entry);

        Assert.Equal(ArtifactStatus.Invalid, state.Status);
        Assert.Contains("never verified", state.Detail!, StringComparison.Ordinal);
        Assert.False(state.IsUsable);
    }

    [Fact]
    public async Task AnInstalledArtifactThatChangesOnDiskStopsCountingAsInstalled()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        await registry.EnsureAsync(entry.ArtifactId);
        Assert.Equal(ArtifactStatus.Installed, registry.Status(entry).Status);

        string path = registry.InstallPath(entry);
        DateTime verifiedAt = File.GetLastWriteTimeUtc(path);

        await File.WriteAllBytesAsync(path, Payload(content.Length, seed: 3));

        // Set the timestamp rather than trusting the rewrite to move it. Two writes inside one
        // filesystem timestamp tick leave it unchanged, which made this test pass or fail on
        // how fast the disk was - and the cheap check is precisely what is under test here.
        File.SetLastWriteTimeUtc(path, verifiedAt.AddSeconds(5));

        ArtifactState state = registry.Status(entry);
        Assert.Equal(ArtifactStatus.Invalid, state.Status);
        Assert.Contains("modified", state.Detail!, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Invalid, (await registry.VerifyInstalledAsync(entry.ArtifactId)).Status);
    }

    [Fact]
    public async Task ADeepVerifyCatchesAChangeThatKeptTheSameLengthAndTimestamp()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        await registry.EnsureAsync(entry.ArtifactId);

        string path = registry.InstallPath(entry);
        DateTime when = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, Payload(content.Length, seed: 5));
        File.SetLastWriteTimeUtc(path, when);

        // The cheap check is fooled; re-hashing is not, which is why the slow one exists.
        Assert.Equal(ArtifactStatus.Installed, registry.Status(entry).Status);
        Assert.Equal(ArtifactStatus.Invalid, (await registry.VerifyInstalledAsync(entry.ArtifactId)).Status);
    }

    // -- interrupted transfers -----------------------------------------------------------------------

    [Fact]
    public async Task AnInterruptedDownloadKeepsWhatItGotAndResumes()
    {
        byte[] content = Payload(200 * 1024);
        using LoopbackHttpServer server = new(content) { TruncateBodyAfter = 40 * 1024 };
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        ArtifactState first = await registry.EnsureAsync(entry.ArtifactId);

        Assert.NotEqual(ArtifactStatus.Installed, first.Status);
        string partial = registry.InstallPath(entry) + ".partial";
        Assert.True(File.Exists(partial), "the partial download was thrown away");
        long kept = new FileInfo(partial).Length;
        Assert.InRange(kept, 1, content.Length - 1);
        Assert.Equal(ArtifactStatus.Downloading, registry.Status(entry).Status);

        // The connection recovers.
        server.TruncateBodyAfter = null;
        ArtifactState second = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Installed, second.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(registry.InstallPath(entry)));
        Assert.True(server.RangeRequests >= 1, "the second attempt did not ask to resume");
    }

    [Fact]
    public async Task AServerThatIgnoresRangeRequestsIsHandledByStartingAgain()
    {
        byte[] content = Payload(200 * 1024);
        using LoopbackHttpServer server = new(content) { TruncateBodyAfter = 40 * 1024, SupportsRange = false };
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        Assert.NotEqual(ArtifactStatus.Installed, (await registry.EnsureAsync(entry.ArtifactId)).Status);

        server.TruncateBodyAfter = null;
        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        // Appending to a partial file after a 200 would splice the start of the file onto the
        // middle of itself: right length, entirely wrong content.
        Assert.Equal(ArtifactStatus.Installed, state.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(registry.InstallPath(entry)));
    }

    [Fact]
    public async Task APartialFileLongerThanThePinnedSizeIsDiscardedRatherThanResumed()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        Directory.CreateDirectory(registry.InstallDirectory(entry));
        await File.WriteAllBytesAsync(registry.InstallPath(entry) + ".partial", Payload(content.Length + 5000));

        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Installed, state.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(registry.InstallPath(entry)));
    }

    // -- refusals, cancellation, timeout ---------------------------------------------------------------

    [Fact]
    public async Task AMissingFileOnTheServerIsAnActionableFailure()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content) { ForceStatus = 404 };
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Failed, state.Status);
        Assert.Contains("no longer at that address", state.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableServerSaysSoRatherThanThrowing()
    {
        byte[] content = Payload();
        ArtifactEntry entry = Entry("http://127.0.0.1:9/artifact.bin", content);
        using ArtifactRegistry registry = Registry(entry);

        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Failed, state.Status);
        Assert.False(File.Exists(registry.InstallPath(entry)));
    }

    [Fact]
    public async Task CancellationStopsTheDownloadAndActivatesNothing()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content) { ResponseDelay = TimeSpan.FromSeconds(30) };
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        using CancellationTokenSource cancellation = new();
        Task<ArtifactState> run = registry.EnsureAsync(entry.ArtifactId, cancellationToken: cancellation.Token);

        await Task.Delay(300);
        await cancellation.CancelAsync();

        ArtifactState state = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ArtifactStatus.Failed, state.Status);
        Assert.Contains("cancelled", state.Detail!, StringComparison.Ordinal);
        Assert.False(File.Exists(registry.InstallPath(entry)));
    }

    [Fact]
    public async Task ATimeoutIsReportedAsATimeoutRatherThanACancellation()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content) { ResponseDelay = TimeSpan.FromSeconds(30) };
        ArtifactEntry entry = Entry(server.Url, content);

        using ArtifactRegistry registry = new(new ArtifactManifest { Artifacts = [entry] }, Path.Combine(_temp.Path, "models"))
        {
            TransferTimeout = TimeSpan.FromSeconds(2),
        };

        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Failed, state.Status);
        Assert.Contains("time limit", state.Detail!, StringComparison.Ordinal);
        Assert.False(File.Exists(registry.InstallPath(entry)));
    }

    // -- concurrency ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoCallersAskingForTheSameArtifactDownloadItOnce()
    {
        byte[] content = Payload(400 * 1024);
        using LoopbackHttpServer server = new(content);
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        ArtifactState[] states = await Task.WhenAll(
            registry.EnsureAsync(entry.ArtifactId),
            registry.EnsureAsync(entry.ArtifactId),
            registry.EnsureAsync(entry.ArtifactId));

        Assert.All(states, s => Assert.Equal(ArtifactStatus.Installed, s.Status));

        // The second and third callers waited, then found it already there.
        Assert.Equal(1, server.Requests);
        Assert.Equal(content, await File.ReadAllBytesAsync(registry.InstallPath(entry)));
    }

    [Fact]
    public async Task AnotherProcessHoldingTheArtifactIsReportedRatherThanRacedWith()
    {
        byte[] content = Payload();
        using LoopbackHttpServer server = new(content);
        ArtifactEntry entry = Entry(server.Url, content);
        using ArtifactRegistry registry = Registry(entry);

        Directory.CreateDirectory(registry.InstallDirectory(entry));

        // Exactly what a second EchoForge instance mid-download looks like from here.
        using FileStream held = new(
            registry.InstallPath(entry) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Failed, state.Status);
        Assert.Contains("another process", state.Detail!, StringComparison.Ordinal);
        Assert.Equal(0, server.Requests);
    }

    // -- profiles -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AProfileIsReadyOnlyWhenEveryArtifactItNeedsIsVerified()
    {
        byte[] first = Payload(1024, seed: 1);
        byte[] second = Payload(2048, seed: 2);

        using LoopbackHttpServer serverA = new(first);
        using LoopbackHttpServer serverB = new(second);

        ArtifactEntry a = Entry(serverA.Url, first, "runtime.a");
        ArtifactEntry b = Entry(serverB.Url, second, "runtime.b");
        using ArtifactRegistry registry = Registry(a, b);

        ProcessingProfile profile = registry.Profile(ProcessingProfile.CpuInt8)!;
        Assert.Equal(2, profile.Artifacts.Count);
        Assert.Equal(first.Length + second.Length, profile.TotalBytes);
        Assert.False(registry.IsProfileReady(profile));

        await registry.EnsureAsync("runtime.a");
        Assert.False(registry.IsProfileReady(profile));

        IReadOnlyList<ArtifactState> states = await registry.EnsureProfileAsync(ProcessingProfile.CpuInt8);

        Assert.All(states, s => Assert.Equal(ArtifactStatus.Installed, s.Status));
        Assert.True(registry.IsProfileReady(profile));
    }
}
