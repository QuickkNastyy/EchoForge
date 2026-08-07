#:project ../src/EchoForge.App/EchoForge.App.csproj
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// What EchoForge thinks this machine is, and what it would recommend installing on it.
//
// Hardware detection reads the machine through DXGI, CPUID, GlobalMemoryStatusEx and WASAPI, and
// none of that can be proven correct by a unit test: a fake describes a machine the code has never
// seen. This runs the real probes on the real machine and prints what came back, so a wrong vendor
// ID or a mis-declared struct is visible rather than hiding behind a plausible-looking null.
//
// It reads and prints. It downloads nothing, installs nothing, and touches no session.
//
//   dotnet run scripts/smoke-setup.cs

using System.IO;
using EchoForge.Audio.Windows;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Core.Setup;
using EchoForge.Infrastructure.Setup;

List<string> failures = [];

void Check(bool condition, string what)
{
    Console.WriteLine((condition ? "  ok    " : "  FAIL  ") + what);
    if (!condition)
    {
        failures.Add(what);
    }
}

static void Note(string what) => Console.WriteLine("  note  " + what);

AppLayout layout = AppLayout.Current;

Console.WriteLine("EchoForge setup smoke test");
Console.WriteLine();
Console.WriteLine("Layout");
Console.WriteLine("  application  " + layout.ApplicationRoot);
Console.WriteLine("  data         " + layout.DataRoot);
Console.WriteLine("  manifest     " + layout.ManifestPath);
Console.WriteLine("  worker       " + layout.WorkerPackageRoot);
Console.WriteLine();

Check(File.Exists(layout.ManifestPath), "the pinned manifest travels with the application");
Check(Directory.Exists(layout.WorkerPackageRoot), "the worker package travels with the application");
Check(
    File.Exists(Path.Combine(layout.WorkerPackageRoot, "requirements-production.txt")),
    "the package list travels with the worker");

using SetupServices? setup = SetupServices.TryOpen(out IReadOnlyList<string> problems, layout);

if (setup is null)
{
    foreach (string problem in problems)
    {
        Console.WriteLine("  FAIL  manifest: " + problem);
    }

    Console.WriteLine();
    Console.WriteLine("  result  FAIL");
    return 1;
}

// -- hardware ------------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("Hardware");

using AudioDeviceCatalog catalog = new();

// The probe asks the installed worker environment about CUDA rather than inferring it from the
// adapter list: an NVIDIA card that enumerates and a CUDA stack that runs are different facts.
HardwareSnapshot hardware = await setup.HardwareProbe(catalog).ProbeAsync();

Console.WriteLine("  os           " + hardware.OperatingSystem + " (" + hardware.Architecture + ")");
Console.WriteLine("  cpu          " + (hardware.CpuName ?? "unknown") + ", " + hardware.LogicalCores + " logical cores");
Console.WriteLine("  avx2         " + Describe(hardware.HasAvx2) + "   avx512 " + Describe(hardware.HasAvx512));
Console.WriteLine("  memory       " + Bytes(hardware.TotalMemoryBytes) + " total, " + Bytes(hardware.AvailableMemoryBytes) + " available");
Console.WriteLine("  disk         " + Bytes(hardware.AvailableDiskBytes) + " free on " + (hardware.DataVolume ?? "unknown"));

foreach (GpuInfo gpu in hardware.Gpus)
{
    Console.WriteLine("  gpu          " + gpu.Vendor + " " + gpu.Model
        + "  vram=" + Bytes(gpu.DedicatedMemoryBytes)
        + "  driver=" + (gpu.DriverVersion ?? "unknown")
        + (gpu.IsSoftware ? "  (software)" : string.Empty));
}

Console.WriteLine("  cuda         " + hardware.Cuda);
Console.WriteLine("  microphones  " + hardware.InputDevices.Count);
Console.WriteLine("  playback     " + hardware.OutputDevices.Count);

if (hardware.Unavailable.Count > 0)
{
    Console.WriteLine("  unknown      " + string.Join(", ", hardware.Unavailable));
}

Console.WriteLine();
Check(hardware.OperatingSystem.Length > 0, "the operating system was read");
Check(hardware.LogicalCores > 0, "the processor count was read");
Check(hardware.Gpus.Count > 0, "at least one graphics adapter was enumerated");

// Nothing is asserted about *which* hardware this is: the point is that a value is either a fact
// or an explicit unknown, never a guess.
Check(
    hardware.TotalMemoryBytes is null or > 0,
    "system memory is a real figure or an explicit unknown");

Check(
    hardware.Gpus.All(g => g.DedicatedMemoryBytes is null or > 0),
    "video memory is a real figure or an explicit unknown");

// -- recommendation ------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("Recommendation");

SetupRecommendation recommendation = ProfileRecommender.Recommend(hardware);

Console.WriteLine("  transcription  " + recommendation.Transcription.ProfileId
    + (recommendation.Transcription.IsFallback ? "  (fallback)" : string.Empty));
Console.WriteLine("                 " + recommendation.Transcription.Summary);
foreach (string reason in recommendation.Transcription.Reasons)
{
    Console.WriteLine("                 - " + reason);
}

Console.WriteLine("  summaries      " + recommendation.Summarization.ProfileId
    + (recommendation.Summarization.IsFallback ? "  (fallback)" : string.Empty));
Console.WriteLine("                 " + recommendation.Summarization.Summary);
foreach (string reason in recommendation.Summarization.Reasons)
{
    Console.WriteLine("                 - " + reason);
}

foreach (string warning in recommendation.Warnings)
{
    Note(warning);
}

Console.WriteLine();
Check(recommendation.Transcription.Reasons.Count > 0, "the transcription recommendation is explained");
Check(recommendation.Summarization.Reasons.Count > 0, "the summary recommendation is explained");

// -- installed components -------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("Components");

SetupSnapshot snapshot = setup.Runtimes.Snapshot(
    recommendation.Transcription.ProfileId, recommendation.Summarization.ProfileId);

foreach (RuntimeComponentState component in snapshot.Components)
{
    Console.WriteLine("  " + component.Id.ToString().PadRight(18)
        + component.Status.ToString().PadRight(14) + component.Detail);
}

Console.WriteLine();

foreach (CapabilityState capability in snapshot.Capabilities)
{
    Console.WriteLine("  " + capability.Level.ToString().PadRight(18)
        + (capability.Available ? "available" : "not yet").PadRight(14) + capability.Detail);
}

Console.WriteLine();
Check(
    snapshot.Capability(CapabilityLevel.Recording).Available,
    "recording is available whatever else is installed");

Check(
    snapshot.Component(RuntimeComponentId.BenchmarkModel).Status != RuntimeComponentStatus.NotInstalled,
    "the comparison model is never reported as something missing");

Check(
    setup.Artifacts.Find(PythonRuntimeInstaller.ArtifactId) is not null,
    "the app-local interpreter is pinned in the manifest");

if (setup.Python.TryResolve() is { } resolved)
{
    Console.WriteLine("  note  app-local python " + resolved.Version + " at " + resolved.ExecutablePath);

    Check(
        resolved.ExecutablePath.StartsWith(layout.RuntimeRoot, StringComparison.OrdinalIgnoreCase),
        "the interpreter is app-local rather than one found on the machine");
}
else
{
    Note("the app-local interpreter is not installed here yet");
}

if (setup.WorkerEnvironment.TryResolve() is { } environment)
{
    Console.WriteLine("  note  worker environment: " + environment.PackageSummary);

    Check(
        setup.TryResolveWorkerLaunch() is not null,
        "a worker can be launched from the app-local environment");
}
else
{
    Note("the worker environment is not built here yet");
}

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "  result  PASS" : "  result  FAIL");

foreach (string failure in failures)
{
    Console.WriteLine("    - " + failure);
}

return failures.Count == 0 ? 0 : 1;

static string Describe(bool? value) => value is null ? "unknown" : value.Value ? "yes" : "no";

static string Bytes(long? value) => value is null
    ? "unknown"
    : value.Value >= 1024L * 1024 * 1024
        ? (value.Value / (1024.0 * 1024 * 1024)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " GB"
        : (value.Value / (1024.0 * 1024)).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
