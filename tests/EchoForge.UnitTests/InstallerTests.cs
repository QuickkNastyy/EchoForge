using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EchoForge.UnitTests;

/// <summary>
/// What the installer promises, held as tests rather than as good intentions.
///
/// <para>
/// The installer is the one part of EchoForge a user meets before anything else works, and its
/// mistakes are the expensive kind: an installer that quietly required administrator rights, or
/// duplicated itself on every upgrade, or took a user's meetings with it on uninstall, would be
/// discovered in the field rather than in a test. So the properties that matter are asserted here
/// against the actual <c>EchoForge.iss</c> and the release scripts.
/// </para>
///
/// <para>
/// These are ordinary unit tests: they read files and check text, and need neither Inno Setup nor a
/// signing certificate. The two that actually compile an installer are <see cref="PackagingFactAttribute"/>
/// and skip with a message when the compiler has not been staged.
/// </para>
/// </summary>
public sealed class InstallerTests
{
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EchoForge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string InstallerScript() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "packaging", "inno", "EchoForge.iss"));

    private static string Script(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", name));

    // -- version is single-sourced -----------------------------------------------------------------

    [Fact]
    public void TheInstallerVersionDefaultMatchesTheOneSourceOfTruth()
    {
        // Directory.Build.props VersionPrefix is the version. The .iss default must equal it so a
        // bare manual compile produces the same version the build scripts pass in; the scripts pass
        // /DAppVersion read from package.json, which is that same VersionPrefix.
        string props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        Match versionPrefix = Regex.Match(props, @"<VersionPrefix>([^<]+)</VersionPrefix>");
        Assert.True(versionPrefix.Success, "VersionPrefix not found in Directory.Build.props");

        Match issVersion = Regex.Match(InstallerScript(), @"#define\s+AppVersion\s+""([^""]+)""");
        Assert.True(issVersion.Success, "AppVersion default not found in EchoForge.iss");

        Assert.Equal(versionPrefix.Groups[1].Value.Trim(), issVersion.Groups[1].Value.Trim());
    }

    // -- upgrade identity ---------------------------------------------------------------------------

    [Fact]
    public void TheAppIdIsStableSoAnUpgradeReplacesRatherThanDuplicates()
    {
        // The AppId is the upgrade identity. If it ever changes, an "upgrade" installs a second,
        // unrelated EchoForge beside the first. This pins the exact value so a careless edit fails
        // here rather than in the field.
        Assert.Contains("AppId={{7F3C1B84-0C4E-4C1B-9A44-4A9E5F2D6C11}", InstallerScript(), StringComparison.Ordinal);
        Assert.Contains("UsePreviousAppDir=yes", InstallerScript(), StringComparison.Ordinal);
    }

    // -- x64 only -----------------------------------------------------------------------------------

    [Fact]
    public void TheInstallerIsX64Only()
    {
        // Every pinned inference artifact is win_amd64. An installer that ran on x86 or ARM64 would
        // install an application that cannot transcribe.
        string iss = InstallerScript();
        Assert.Contains("ArchitecturesAllowed=x64compatible", iss, StringComparison.Ordinal);
        Assert.Contains("ArchitecturesInstallIn64BitMode=x64compatible", iss, StringComparison.Ordinal);
    }

    // -- per-user, no elevation ---------------------------------------------------------------------

    [Fact]
    public void TheInstallerIsPerUserAndAsksForNoElevation()
    {
        Assert.Contains("PrivilegesRequired=lowest", InstallerScript(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerDoesNotHardcodeAMachineWideProgramFilesLocation()
    {
        // {autopf} under lowest privileges resolves to the per-user %LOCALAPPDATA%\Programs. A
        // literal Program Files path, or {commonpf}, would be a machine-wide install needing
        // administrator rights.
        string iss = InstallerScript();
        Assert.Contains("DefaultDirName={autopf}\\{#AppName}", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("{commonpf}", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("{commonpf64}", iss, StringComparison.Ordinal);
    }

    // -- data preservation on uninstall -------------------------------------------------------------

    [Fact]
    public void UninstallNeverTargetsTheUserDataRoot()
    {
        // The one rule that matters most: uninstalling must not be able to delete a meeting. No
        // UninstallDelete entry may point at the data root under the user's profile.
        string iss = InstallerScript();
        Match uninstallDelete = Regex.Match(iss, @"\[UninstallDelete\](.*?)(\[|\z)", RegexOptions.Singleline);
        Assert.True(uninstallDelete.Success, "[UninstallDelete] section not found");

        // Only the actual delete directives matter (lines with a Name:), not the comments that
        // explain why the data root is deliberately absent.
        IEnumerable<string> directives = uninstallDelete.Groups[1].Value
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith(';'))
            .Where(line => line.Contains("Name:", StringComparison.Ordinal));

        foreach (string directive in directives)
        {
            Assert.DoesNotContain("localappdata", directive, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("userappdata", directive, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sessions", directive, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void OptionalDataRemovalDefaultsToKeepAndIsTwiceConfirmed()
    {
        // If a destructive "also delete my recordings" path exists it must default to keeping the
        // data and must be confirmed twice, because it is irreversible. A silent uninstall must
        // never reach it.
        string iss = InstallerScript();
        Assert.Contains("UninstallSilent()", iss, StringComparison.Ordinal);
        Assert.Contains("DelTree", iss, StringComparison.Ordinal);

        // The first prompt keeps by default (MB_DEFBUTTON1 on a "keep = Yes" question), the second,
        // destructive prompt defaults to No (MB_DEFBUTTON2). Both must be present.
        Assert.Contains("MB_DEFBUTTON1", iss, StringComparison.Ordinal);
        Assert.Contains("MB_DEFBUTTON2", iss, StringComparison.Ordinal);

        // DelTree must be guarded by usPostUninstall (after the app is gone) and only reached
        // through the confirmations.
        Assert.Contains("usPostUninstall", iss, StringComparison.Ordinal);
    }

    // -- downgrade policy ---------------------------------------------------------------------------

    [Fact]
    public void TheInstallerRefusesAnUnsupportedDowngrade()
    {
        // A newer installed version must not be silently replaced by an older installer.
        string iss = InstallerScript();
        Assert.Contains("GetPreviousData", iss, StringComparison.Ordinal);
        Assert.Contains("CompareVersion", iss, StringComparison.Ordinal);
        Assert.Contains("Result := False", iss, StringComparison.Ordinal);
    }

    // -- compile-time input integrity ---------------------------------------------------------------

    [Fact]
    public void TheInstallerRefusesToBuildFromAnIncompleteOrNonSelfContainedPackage()
    {
        // Compile-time #error guards, so a half-staged directory fails at build rather than shipping
        // an installer that is missing a runtime.
        string iss = InstallerScript();
        foreach (string required in new[]
        {
            "package.json",
            "EchoForge.App.exe",
            "artifacts\\manifest.json",
            "echoforge_worker",
            "NOTICE.md",
            "System.Private.CoreLib.dll",   // the self-contained .NET runtime
            "PresentationFramework.dll",    // WPF
        })
        {
            Assert.Contains(required, iss, StringComparison.Ordinal);
        }

        Assert.Contains("#error", iss, StringComparison.Ordinal);
    }

    // -- signing configuration, and no secrets ------------------------------------------------------

    [Fact]
    public void TheInstallerHasASigningHookButEmbedsNoCertificate()
    {
        string iss = InstallerScript();
        Assert.Contains("SignInstaller", iss, StringComparison.Ordinal);
        Assert.Contains("SignTool=echoforge", iss, StringComparison.Ordinal);

        // No certificate material or password may be baked into the script.
        Assert.DoesNotContain(".pfx", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", iss, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SigningReadsItsSecretsFromTheEnvironmentAndNeverSelfSigns()
    {
        string sign = Script("sign.ps1");
        // Credentials come only from the environment.
        Assert.Contains("ECHOFORGE_SIGNING_THUMBPRINT", sign, StringComparison.Ordinal);
        Assert.Contains("ECHOFORGE_SIGNING_PFX", sign, StringComparison.Ordinal);
        // It never fabricates trust.
        Assert.DoesNotContain("New-SelfSignedCertificate", sign, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("makecert", sign, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseModeRefusesToClaimSuccessWithoutSignatures()
    {
        string release = Script("release.ps1");
        Assert.Contains("AUTHENTICODE RELEASE SIGNING BLOCKED", release, StringComparison.Ordinal);
        // The blocker is a non-zero exit, not a warning.
        Assert.Contains("exit 3", release, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSigningMaterialIsCheckedIntoTheRepository()
    {
        // A private key or certificate must never be in the tree. Search the source, excluding the
        // ignored build output and the git directory.
        string root = RepositoryRoot();
        string[] dangerous = { "*.pfx", "*.p12", "*.pvk", "*.snk" };
        List<string> found = new();
        foreach (string pattern in dangerous)
        {
            foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("build", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (relative.StartsWith(".git", StringComparison.OrdinalIgnoreCase)) { continue; }
                found.Add(relative);
            }
        }

        Assert.True(found.Count == 0, "signing material found in the repository: " + string.Join(", ", found));
    }

    // -- notices ship -------------------------------------------------------------------------------

    [Fact]
    public void TheProjectStagesTheThirdPartyNoticeAndLicenceTexts()
    {
        string csproj = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "EchoForge.App", "EchoForge.App.csproj"));
        Assert.Contains("third_party\\licenses", csproj, StringComparison.Ordinal);
        Assert.Contains("NOTICE.md", csproj, StringComparison.Ordinal);
    }

    // -- packaging qualification: the installer actually compiles ----------------------------------

    [PackagingFact]
    public void TheInstallerCompilesFromACompleteStubPackage()
    {
        using TempDirectory temp = new();
        string package = Path.Combine(temp.Path, "package");
        string output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(output);
        WriteStubPackage(package);

        int exit = CompileInstaller(package, output, out string log);
        Assert.True(exit == 0, "iscc did not compile a complete stub package:\n" + log);
        Assert.NotEmpty(Directory.GetFiles(output, "EchoForge-*-win-x64.exe"));
    }

    [PackagingFact]
    public void TheInstallerCompileFailsWhenTheSelfContainedRuntimeIsMissing()
    {
        using TempDirectory temp = new();
        string package = Path.Combine(temp.Path, "package");
        string output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(output);
        WriteStubPackage(package);

        // Remove the file that proves the package is self-contained. The guard must reject it.
        File.Delete(Path.Combine(package, "System.Private.CoreLib.dll"));

        int exit = CompileInstaller(package, output, out string log);
        Assert.True(exit != 0, "iscc compiled a package with no self-contained runtime:\n" + log);
        Assert.Empty(Directory.GetFiles(output, "EchoForge-*-win-x64.exe"));
    }

    /// <summary>Lays down the smallest directory that satisfies the installer's compile-time guards.</summary>
    private static void WriteStubPackage(string package)
    {
        Directory.CreateDirectory(package);
        void File_(string relative, string content = "stub")
        {
            string full = Path.Combine(package, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        File_("package.json", "{\"product\":\"EchoForge\",\"version\":\"0.6.0\"}");
        File_("EchoForge.App.exe");
        File_("artifacts\\manifest.json", "{\"artifacts\":[]}");
        File_("worker\\echoforge_worker\\__init__.py");
        File_("third_party\\NOTICE.md");
        File_("System.Private.CoreLib.dll");
        File_("PresentationFramework.dll");
    }

    private static int CompileInstaller(string package, string output, out string log)
    {
        string iss = Path.Combine(RepositoryRoot(), "packaging", "inno", "EchoForge.iss");
        ProcessStartInfo psi = new()
        {
            FileName = InstallerToolchain.IsccPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add($"/DSourceDir={package}");
        psi.ArgumentList.Add("/DAppVersion=0.6.0");
        psi.ArgumentList.Add($"/O{output}");
        psi.ArgumentList.Add(iss);

        using Process process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        log = stdout + stderr;
        return process.ExitCode;
    }
}

/// <summary>
/// Where the pinned Inno Setup compiler is, if it has been staged. Packaging tests need it; ordinary
/// developers and CI without it get a skip, not a failure.
/// </summary>
internal static class InstallerToolchain
{
    public static string? IsccPath { get; } = Resolve();

    private static string? Resolve()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EchoForge.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null) { return null; }

        string iscc = Path.Combine(directory.FullName, "build", "tools", "inno-7.0.2", "ISCC.exe");
        return File.Exists(iscc) ? iscc : null;
    }
}

/// <summary>A fact that compiles an installer, and skips when the Inno Setup compiler is not staged.</summary>
public sealed class PackagingFactAttribute : FactAttribute
{
    public PackagingFactAttribute()
    {
        if (InstallerToolchain.IsccPath is null)
        {
            Skip = "Inno Setup is not staged; run scripts\\stage-inno.ps1 to enable installer compile tests.";
        }
    }
}
