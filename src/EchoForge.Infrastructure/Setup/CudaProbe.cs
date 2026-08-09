using System.Diagnostics;
using System.Text;
using EchoForge.Contracts.Setup;

namespace EchoForge.Infrastructure.Setup;

/// <summary>
/// Asks the installed stack whether CUDA actually works.
///
/// <para>
/// <b>An NVIDIA adapter is not a CUDA device.</b> A driver older than the pinned CTranslate2, a
/// laptop whose discrete adapter is switched off, a card the process is not allowed to see — all
/// of them enumerate perfectly through DXGI and then fail to run anything. The only answer worth
/// having is the one from the library that will do the work. This runs the worker's effective
/// CUDA probe in the environment EchoForge built. That probe both asks CTranslate2 for devices and
/// loads the exact pinned cuBLAS DLLs that CTranslate2 otherwise defers until first inference.
/// </para>
///
/// <para>
/// It reports <see cref="CudaAvailability.Unknown"/> rather than "no" when it cannot ask. Treating
/// "we could not check" as "there is no GPU" would quietly move somebody onto the CPU profile and
/// leave them wondering why transcription takes an hour.
/// </para>
/// </summary>
public sealed class CudaProbe
{
    /// <summary>How long the probe may take. It imports a large library; it should not hang startup.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private readonly WorkerEnvironmentInstaller _environment;

    public CudaProbe(WorkerEnvironmentInstaller environment) =>
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>The delegate shape <see cref="WindowsHardwareProbe"/> takes.</summary>
    public Task<CudaAvailability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        WorkerEnvironment? environment = _environment.TryResolve();

        return environment is null
            ? Task.FromResult(CudaAvailability.AdapterWithoutRuntime)
            : RunAsync(
                environment.PythonExecutable,
                _environment.WorkerPackageRoot,
                cancellationToken);
    }

    private static async Task<CudaAvailability> RunAsync(
        string python,
        string workerPackageRoot,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = python,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "from echoforge_worker.compute import cuda_device_count; print(cuda_device_count())");

        // Offline, like every other child EchoForge starts. Importing CTranslate2 should not be
        // able to reach anything, and this runs on a machine that may have no network at all.
        OfflineEnvironment.Apply(startInfo.Environment);
        startInfo.Environment["PYTHONPATH"] = Path.GetFullPath(workerPackageRoot);

        using CancellationTokenSource timeout = new(Timeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using Process process = new() { StartInfo = startInfo };

            process.Start();

            string output = await process.StandardOutput.ReadToEndAsync(linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return CudaAvailability.AdapterWithoutRuntime;
            }

            return int.TryParse(output.Trim(), out int devices) && devices > 0
                ? CudaAvailability.Available
                : CudaAvailability.AdapterWithoutRuntime;
        }
        catch (OperationCanceledException)
        {
            return CudaAvailability.Unknown;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return CudaAvailability.Unknown;
        }
    }
}
