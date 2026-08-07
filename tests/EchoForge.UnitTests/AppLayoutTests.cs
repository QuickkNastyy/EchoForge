using EchoForge.Infrastructure.Setup;

namespace EchoForge.UnitTests;

/// <summary>
/// Where an installed EchoForge reads and writes.
///
/// <para>
/// The defect this replaced was not subtle in hindsight: the worker package and the artifact
/// manifest were found by walking upwards from the executable looking for <c>EchoForge.slnx</c>.
/// That resolves on exactly one kind of machine. These tests are about the two properties that
/// replace it — everything shipped comes from beside the executable, and everything accumulated
/// goes under the user's profile — and about the paths that break naive implementations.
/// </para>
/// </summary>
public sealed class AppLayoutTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AppLayout Layout(string application, string data) =>
        AppLayout.For(_temp.Combine(application), _temp.Combine(data));

    [Fact]
    public void EverythingShippedIsResolvedFromBesideTheExecutable()
    {
        AppLayout layout = Layout("app", "data");

        Assert.StartsWith(layout.ApplicationRoot, layout.WorkerPackageRoot, StringComparison.Ordinal);
        Assert.StartsWith(layout.ApplicationRoot, layout.ManifestPath, StringComparison.Ordinal);
        Assert.StartsWith(layout.ApplicationRoot, layout.LicensesRoot, StringComparison.Ordinal);
        Assert.StartsWith(layout.ApplicationRoot, layout.NoticePath, StringComparison.Ordinal);
    }

    [Fact]
    public void EverythingTheUserAccumulatesIsOutsideTheInstallationDirectory()
    {
        AppLayout layout = Layout("Program Files\\EchoForge", "AppData\\EchoForge");

        // The one that matters most: uninstalling must not be able to take a meeting with it, and
        // a standard user must be able to record without write access to Program Files.
        foreach (string path in (string[])
        [
            layout.SessionsRoot, layout.ModelsRoot, layout.RuntimeRoot, layout.ConfigRoot,
            layout.LogsRoot, layout.DiagnosticsRoot, layout.TempRoot, layout.IndexPath,
        ])
        {
            Assert.StartsWith(layout.DataRoot, path, StringComparison.Ordinal);
            Assert.DoesNotContain(layout.ApplicationRoot, path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheIndexLivesBesideTheSessionsRatherThanInsideOneOfThem()
    {
        AppLayout layout = Layout("app", "data");

        Assert.Equal(Path.Combine(layout.SessionsRoot, "library.db"), layout.IndexPath);
    }

    [Fact]
    public void StagingIsOnTheSameVolumeAsTheThingItWillBecome()
    {
        AppLayout layout = Layout("app", "data");

        // Activation is a rename, and a rename across volumes is a copy. A 7 GB model unpacked
        // into %TEMP% and then "moved" would be copied twice and could run a drive out of space.
        Assert.Equal(Path.GetPathRoot(layout.ModelsRoot), Path.GetPathRoot(layout.TempRoot));
        Assert.StartsWith(layout.DataRoot, layout.TempRoot, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Program Files\\EchoForge", "AppData\\Local\\EchoForge")]
    [InlineData("apps\\Echo Forge (x64)", "data\\Echo Forge")]
    [InlineData("приложения\\EchoForge", "данные\\EchoForge")]
    [InlineData("アプリ\\EchoForge", "データ\\EchoForge")]
    public void PathsWithSpacesAndNonAsciiCharactersResolveAndAreUsable(string application, string data)
    {
        AppLayout layout = Layout(application, data);

        layout.EnsureDataDirectories();

        Assert.True(Directory.Exists(layout.SessionsRoot));
        Assert.True(Directory.Exists(layout.ModelsRoot));
        Assert.True(Directory.Exists(layout.ConfigRoot));

        // Round-trips through the filesystem, which is what a path that merely concatenates would
        // pass and a path that is actually written would not.
        string probe = Path.Combine(layout.SessionsRoot, "probe.txt");
        File.WriteAllText(probe, "ok");
        Assert.Equal("ok", File.ReadAllText(probe));
    }

    [Fact]
    public void NothingIsCreatedInsideTheInstallationDirectory()
    {
        AppLayout layout = Layout("app", "data");

        layout.EnsureDataDirectories();

        // EnsureDataDirectories creates what the application writes to. The installation
        // directory is read-only to a standard user, and nothing may assume otherwise.
        Assert.False(Directory.Exists(layout.ApplicationRoot));
    }

    [Fact]
    public void AnUnpublishedLayoutSaysSoRatherThanPretending()
    {
        AppLayout layout = Layout("app", "data");
        Directory.CreateDirectory(layout.ApplicationRoot);

        Assert.False(layout.LooksPublished);

        // A published layout is the executable, the worker package and the manifest together.
        // Any one of them alone is a build output, not something somebody installed.
        Directory.CreateDirectory(layout.WorkerPackageRoot);
        File.WriteAllText(Path.Combine(layout.ApplicationRoot, "EchoForge.App.exe"), string.Empty);
        Assert.False(layout.LooksPublished);

        Directory.CreateDirectory(Path.GetDirectoryName(layout.ManifestPath)!);
        File.WriteAllText(layout.ManifestPath, "{}");
        Assert.True(layout.LooksPublished);
    }

    [Fact]
    public void TheProcessLayoutFindsWhatWasShippedWithIt()
    {
        // The running test process is a build output with the same content the publish carries,
        // so this is the closest a unit test gets to checking a real installation.
        AppLayout layout = AppLayout.Current;

        Assert.True(File.Exists(layout.ManifestPath), "the pinned manifest travels with the build output");
        Assert.True(Directory.Exists(layout.WorkerPackageRoot), "the worker package travels with the build output");

        Assert.True(
            File.Exists(Path.Combine(layout.WorkerPackageRoot, "requirements-production.txt")),
            "the package list travels with the worker");

        Assert.True(
            File.Exists(Path.Combine(layout.WorkerPackageRoot, "echoforge_worker", "__init__.py")),
            "the worker module travels with the worker package");
    }
}
