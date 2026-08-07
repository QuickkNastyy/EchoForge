using System.Security.Cryptography;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Setup;

namespace EchoForge.UnitTests;

/// <summary>
/// Installing EchoForge's own interpreter and its worker environment.
///
/// <para>
/// The point of shipping an interpreter is that the whole stack becomes one pinned thing, and the
/// point of these tests is the failure modes that would quietly undermine that: an archive that
/// does not match its digest, an unpack interrupted halfway, an environment built from a closure
/// that has since been re-pinned, and a repair that decides to re-download a gigabyte rather than
/// check what is already there.
/// </para>
/// </summary>
public sealed class RuntimeInstallationTests : IDisposable
{
    private readonly SetupFixture _fixture = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _fixture.Dispose();
    }

    private ArtifactRegistry Registry()
    {
        ArtifactRegistry registry = _fixture.Registry();
        _disposables.Add(registry);
        return registry;
    }

    // -- the interpreter -----------------------------------------------------------------------

    [Fact]
    public async Task TheInterpreterInstallsIntoADirectoryNamedForItsPinnedRevision()
    {
        ArtifactEntry entry = _fixture.AddPythonArchive();
        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller installer = _fixture.PythonInstaller(registry);

        Assert.Equal(RuntimeComponentStatus.NotInstalled, installer.Status().Status);

        AppLocalPython? python = await installer.EnsureAsync();

        Assert.NotNull(python);
        Assert.Equal(entry.Revision, python!.Revision);
        Assert.Equal("3.12.13", python.Version);
        Assert.True(File.Exists(python.ExecutablePath));

        // Under the data root, named for the revision, so a re-pin installs beside it rather
        // than over it.
        Assert.Equal(
            Path.Combine(_fixture.Layout.PythonRoot, entry.Revision, "python.exe"),
            python.ExecutablePath);

        Assert.Equal(RuntimeComponentStatus.Ready, installer.Status().Status);
    }

    [Fact]
    public async Task TheArchivePrefixIsStrippedRatherThanBecomingADirectory()
    {
        _fixture.AddPythonArchive();
        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller installer = _fixture.PythonInstaller(registry);

        AppLocalPython python = (await installer.EnsureAsync())!;

        // The archive holds python/python.exe. The runtime is that directory's contents.
        Assert.True(File.Exists(Path.Combine(python.HomeDirectory, "python.exe")));
        Assert.True(File.Exists(Path.Combine(python.HomeDirectory, "Lib", "os.py")));
        Assert.False(Directory.Exists(Path.Combine(python.HomeDirectory, "python")));
    }

    [Fact]
    public async Task AnArchiveThatDoesNotMatchItsDigestIsNeverUnpacked()
    {
        ArtifactEntry entry = _fixture.AddPythonArchive();
        _fixture.Substituted[entry.Url] = new byte[entry.SizeBytes];

        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller installer = _fixture.PythonInstaller(registry);

        AppLocalPython? python = await installer.EnsureAsync();

        Assert.Null(python);
        Assert.False(Directory.Exists(installer.HomeFor(entry.Revision)));
        Assert.NotEqual(RuntimeComponentStatus.Ready, installer.Status().Status);

        // Kept where somebody can look at it. A digest mismatch is either a corrupted transfer or
        // a substituted file, and the second is worth being able to examine afterwards.
        Assert.True(File.Exists(_fixture.InstalledPath(entry) + ".rejected"));
    }

    [Fact]
    public async Task AnArchiveWithoutAnInterpreterInItIsRefused()
    {
        _fixture.AddPythonArchive(includeExecutable: false);
        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller installer = _fixture.PythonInstaller(registry);

        Assert.Null(await installer.EnsureAsync());
        Assert.Null(installer.TryResolve());
    }

    [Fact]
    public async Task AnInterruptedUnpackLeavesNothingThatLooksInstalled()
    {
        ArtifactEntry entry = _fixture.AddPythonArchive();
        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller installer = _fixture.PythonInstaller(registry);

        await installer.EnsureAsync();
        string home = installer.HomeFor(entry.Revision);

        // What a process killed mid-unpack leaves: the executable present, and the stamp that
        // vouches for it never written.
        File.Delete(Path.Combine(home, "installed.json"));

        Assert.Null(installer.TryResolve());
        Assert.NotEqual(RuntimeComponentStatus.Ready, installer.Status().Status);

        // And it repairs without going near the network again.
        _fixture.Blocked.Add(entry.Url);
        Assert.NotNull(await installer.RepairAsync());
        Assert.Equal(RuntimeComponentStatus.Ready, installer.Status().Status);
    }

    [Fact]
    public async Task ARePinInvalidatesAnInterpreterUnpackedFromTheOldOne()
    {
        _fixture.AddPythonArchive();
        ArtifactRegistry first = Registry();
        PythonRuntimeInstaller installer = _fixture.PythonInstaller(first);

        await installer.EnsureAsync();
        Assert.NotNull(installer.TryResolve());

        // A different digest at the same revision: the archive was re-published.
        SetupFixture other = new();
        _disposables.Add(other);

        ArtifactManifest repinned = new()
        {
            SchemaVersion = 1,
            Artifacts =
            [
                first.Find(PythonRuntimeInstaller.ArtifactId)! with { Sha256 = new string('a', 64) },
            ],
        };

        using ArtifactRegistry second = new(repinned, _fixture.Layout.ModelsRoot);
        PythonRuntimeInstaller after = _fixture.PythonInstaller(second);

        Assert.Null(after.TryResolve());
    }

    [Fact]
    public async Task ADamagedArchiveIsDownloadedAgainRatherThanUnpacked()
    {
        ArtifactEntry entry = _fixture.AddPythonArchive();
        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller installer = _fixture.PythonInstaller(registry);

        await installer.EnsureAsync();

        // The archive on disk is corrupted after the fact, and the unpacked runtime is removed.
        await File.WriteAllBytesAsync(_fixture.InstalledPath(entry), new byte[entry.SizeBytes]);
        Directory.Delete(installer.HomeFor(entry.Revision), recursive: true);

        Assert.NotNull(await installer.RepairAsync());
        Assert.Equal(RuntimeComponentStatus.Ready, installer.Status().Status);

        // It had to fetch again, because what was on disk was not what was pinned.
        Assert.True(_fixture.Requests[entry.Url] > 1);
    }

    // -- downloads -----------------------------------------------------------------------------

    [Fact]
    public async Task ADownloadResumesFromTheBytesAlreadyOnDisk()
    {
        ArtifactEntry entry = _fixture.Add("runtime.big", "big.bin", RandomNumberGenerator.GetBytes(64 * 1024));
        ArtifactRegistry registry = Registry();

        // A partial file, as an interrupted download leaves.
        string partial = _fixture.InstalledPath(entry) + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);

        byte[] complete = await GetOriginBytesAsync(registry, entry);
        await File.WriteAllBytesAsync(partial, complete[..(32 * 1024)]);

        ArtifactState state = await registry.EnsureAsync(entry.ArtifactId);

        Assert.Equal(ArtifactStatus.Installed, state.Status);
        Assert.Equal(complete, await File.ReadAllBytesAsync(_fixture.InstalledPath(entry)));
    }

    [Fact]
    public async Task AFailedDownloadKeepsWhatArrivedSoTheNextAttemptContinues()
    {
        ArtifactEntry entry = _fixture.Add("runtime.blocked", "blocked.bin", RandomNumberGenerator.GetBytes(4096));
        _fixture.Blocked.Add(entry.Url);

        ArtifactRegistry registry = Registry();
        ArtifactState failed = await registry.EnsureAsync(entry.ArtifactId);

        Assert.NotEqual(ArtifactStatus.Installed, failed.Status);

        _fixture.Blocked.Remove(entry.Url);
        Assert.Equal(ArtifactStatus.Installed, (await registry.EnsureAsync(entry.ArtifactId)).Status);
    }

    [Fact]
    public async Task RestartingSetupDoesNotDiscardAnArtifactThatAlreadyFinished()
    {
        ArtifactEntry entry = _fixture.Add("runtime.done", "done.bin", RandomNumberGenerator.GetBytes(4096));
        ArtifactRegistry first = Registry();

        Assert.Equal(ArtifactStatus.Installed, (await first.EnsureAsync(entry.ArtifactId)).Status);
        int requests = _fixture.Requests[entry.Url];

        // A fresh process over the same data root: what was installed stays installed, and
        // nothing is fetched again.
        using ArtifactRegistry second = new(
            new ArtifactManifest { SchemaVersion = 1, Artifacts = [entry] }, _fixture.Layout.ModelsRoot);

        Assert.Equal(ArtifactStatus.Installed, second.Status(entry).Status);
        Assert.Equal(requests, _fixture.Requests[entry.Url]);
    }

    private static async Task<byte[]> GetOriginBytesAsync(ArtifactRegistry registry, ArtifactEntry entry)
    {
        // Fetch through the registry once into a scratch location, so the test does not have to
        // know how the fixture stores its origin content.
        await registry.EnsureAsync(entry.ArtifactId);
        return await File.ReadAllBytesAsync(registry.InstallPath(entry));
    }

    // -- the worker environment ------------------------------------------------------------------

    [Fact]
    public void TheWorkerEnvironmentWaitsForTheInterpreterRatherThanLookingForOne()
    {
        _fixture.AddPythonArchive();
        _fixture.AddWheel("example");
        _fixture.WriteRequirements();

        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller python = _fixture.PythonInstaller(registry);
        WorkerEnvironmentInstaller worker = _fixture.WorkerInstaller(registry, python);

        RuntimeComponentState state = worker.Status();

        Assert.Equal(RuntimeComponentStatus.NotInstalled, state.Status);
        Assert.Contains("Python", state.Detail, StringComparison.Ordinal);
        Assert.Null(worker.TryResolve());
    }

    [Fact]
    public async Task TheWorkerEnvironmentReportsWhatItIsStillWaitingToDownload()
    {
        _fixture.AddPythonArchive();
        _fixture.AddWheel("example");
        _fixture.AddWheel("another");
        _fixture.WriteRequirements();

        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller python = _fixture.PythonInstaller(registry);
        WorkerEnvironmentInstaller worker = _fixture.WorkerInstaller(registry, python);

        await python.EnsureAsync();

        RuntimeComponentState state = worker.Status();

        Assert.Equal(RuntimeComponentStatus.NotInstalled, state.Status);
        Assert.Equal(worker.BytesRequired, state.BytesRequired);
        Assert.True(state.BytesRequired > 0);
        Assert.Equal(2, worker.Wheels.Count);
    }

    [Fact]
    public async Task ACorruptWheelIsReportedAsSomethingToRepairRatherThanSomethingToDownload()
    {
        _fixture.AddPythonArchive();
        ArtifactEntry wheel = _fixture.AddWheel("example");
        _fixture.WriteRequirements();

        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller python = _fixture.PythonInstaller(registry);
        WorkerEnvironmentInstaller worker = _fixture.WorkerInstaller(registry, python);

        await python.EnsureAsync();
        await registry.EnsureAsync(wheel.ArtifactId);

        // Truncated after it was verified, which is what an interrupted copy or a failing disk
        // leaves behind. The cheap status check catches it on length alone.
        await File.WriteAllBytesAsync(_fixture.InstalledPath(wheel), new byte[wheel.SizeBytes - 1]);

        Assert.Equal(RuntimeComponentStatus.Corrupt, worker.Status().Status);

        // Substituted rather than truncated: the same length, different bytes. That one only a
        // re-hash can see, which is exactly what repair does before it downloads anything.
        await registry.EnsureAsync(wheel.ArtifactId);
        await File.WriteAllBytesAsync(_fixture.InstalledPath(wheel), new byte[wheel.SizeBytes]);

        Assert.Equal(
            ArtifactStatus.Invalid,
            (await registry.VerifyInstalledAsync(wheel.ArtifactId)).Status);
    }

    [Fact]
    public async Task AnEnvironmentBuiltFromADifferentClosureIsNotReused()
    {
        _fixture.AddPythonArchive();
        ArtifactEntry wheel = _fixture.AddWheel("example");
        _fixture.WriteRequirements();

        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller python = _fixture.PythonInstaller(registry);
        WorkerEnvironmentInstaller worker = _fixture.WorkerInstaller(registry, python);

        await python.EnsureAsync();
        await registry.EnsureAsync(wheel.ArtifactId);

        // What an installed environment looks like, stamped against a closure that has since
        // been re-pinned. It must not be trusted just because python.exe is where it should be.
        Directory.CreateDirectory(Path.Combine(worker.Root, "Scripts"));
        await File.WriteAllTextAsync(WorkerEnvironmentInstaller.ExecutableIn(worker.Root), "not python");
        await File.WriteAllTextAsync(
            Path.Combine(worker.Root, "echoforge-environment.json"),
            """{"python_revision":"rev-0000001","python_version":"3.12.13","package_summary":"stale","packages":["runtime.example:0000"],"installed_utc":"2026-08-07T00:00:00+00:00"}""");

        Assert.Null(worker.TryResolve());
        Assert.Equal(RuntimeComponentStatus.Corrupt, worker.Status().Status);
    }

    [Fact]
    public async Task RepairVerifiesWhatIsOnDiskBeforeDownloadingAnything()
    {
        _fixture.AddPythonArchive();
        ArtifactEntry wheel = _fixture.AddWheel("example");
        _fixture.WriteRequirements();

        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller python = _fixture.PythonInstaller(registry);
        WorkerEnvironmentInstaller worker = _fixture.WorkerInstaller(registry, python);

        await python.EnsureAsync();
        await registry.EnsureAsync(wheel.ArtifactId);

        int before = _fixture.Requests[wheel.Url];

        // The wheel is present and correct; only the proof is gone, which is what an artifact
        // installed by something other than this application looks like.
        File.Delete(_fixture.InstalledPath(wheel) + ".verified.json");
        Assert.Equal(ArtifactStatus.Invalid, registry.Status(wheel).Status);

        // The environment build itself needs a real interpreter, which this fixture's is not, so
        // the repair cannot finish. What it must not do is re-fetch a file that was already right.
        await worker.RepairAsync();

        Assert.Equal(ArtifactStatus.Installed, registry.Status(wheel).Status);
        Assert.Equal(before, _fixture.Requests[wheel.Url]);
    }

    [Fact]
    public async Task RepairNeverTouchesAnythingUnderTheSessionsDirectory()
    {
        _fixture.AddPythonArchive();
        _fixture.AddWheel("example");
        _fixture.WriteRequirements();

        // A meeting, in the place a real one would be.
        string session = Path.Combine(_fixture.Layout.SessionsRoot, "2026", "08", "01JSESSION");
        Directory.CreateDirectory(session);
        await File.WriteAllTextAsync(Path.Combine(session, "session.json"), "{\"session_id\":\"01JSESSION\"}");

        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller python = _fixture.PythonInstaller(registry);
        WorkerEnvironmentInstaller worker = _fixture.WorkerInstaller(registry, python);

        await python.EnsureAsync();
        await worker.RepairAsync();
        await python.RepairAsync();

        Assert.True(File.Exists(Path.Combine(session, "session.json")));
        Assert.Equal("{\"session_id\":\"01JSESSION\"}", await File.ReadAllTextAsync(Path.Combine(session, "session.json")));
    }

    [Fact]
    public async Task AMissingPackageListIsSaidPlainlyRatherThanFailingInPip()
    {
        _fixture.AddPythonArchive();
        _fixture.AddWheel("example");
        // Deliberately no requirements file: an installation missing a shipped file.

        ArtifactRegistry registry = Registry();
        PythonRuntimeInstaller python = _fixture.PythonInstaller(registry);
        WorkerEnvironmentInstaller worker = _fixture.WorkerInstaller(registry, python);

        WorkerEnvironmentResult result = await worker.EnsureAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("requirements_missing", result.Code);
    }
}
