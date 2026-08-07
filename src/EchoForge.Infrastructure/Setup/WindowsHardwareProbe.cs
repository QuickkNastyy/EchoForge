using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Setup;

namespace EchoForge.Infrastructure.Setup;

/// <summary>
/// Reads this Windows machine.
///
/// <para>
/// <b>Nothing here guesses.</b> Every probe either produces a fact or records that it could not,
/// and the field stays null. A VRAM figure invented from a model name would flow straight into the
/// recommendation engine and produce a confident recommendation that fails halfway through
/// somebody's first meeting.
/// </para>
///
/// <para>
/// The adapters come from DXGI rather than WMI. DXGI is the API that actually enumerates what
/// Direct3D can see, it reports dedicated video memory directly, and it needs no extra package —
/// where WMI would add a dependency, be slow, and report the driver's idea of memory rather than
/// the adapter's. The NVIDIA driver version comes from <c>nvidia-smi</c>, which is the vendor's own
/// tool and is absent exactly when there is no NVIDIA driver to ask about.
/// </para>
///
/// <para>
/// <b>Every probe is individually guarded.</b> Hardware detection runs during startup, and a
/// machine with an unusual driver must produce a partial answer rather than stop somebody
/// recording a meeting.
/// </para>
/// </summary>
public sealed partial class WindowsHardwareProbe : IHardwareProbe
{
    private readonly AppLayout _layout;
    private readonly IAudioDeviceCatalog? _audio;
    private readonly Func<CancellationToken, Task<CudaAvailability>>? _cudaProbe;

    public WindowsHardwareProbe(
        AppLayout? layout = null,
        IAudioDeviceCatalog? audio = null,
        Func<CancellationToken, Task<CudaAvailability>>? cudaProbe = null)
    {
        _layout = layout ?? AppLayout.Current;
        _audio = audio;
        _cudaProbe = cudaProbe;
    }

    public async Task<HardwareSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        List<string> unavailable = [];

        (string? cpu, bool? avx2, bool? avx512) = ReadCpu(unavailable);
        (long? total, long? available) = ReadMemory(unavailable);
        (long? disk, string? volume) = ReadDisk(unavailable);
        IReadOnlyList<GpuInfo> gpus = ReadAdapters(unavailable);
        (IReadOnlyList<AudioEndpointSummary> inputs, IReadOnlyList<AudioEndpointSummary> outputs) = ReadAudio(unavailable);

        CudaAvailability cuda = await ProbeCudaAsync(gpus, cancellationToken).ConfigureAwait(false);

        return new HardwareSnapshot
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            CpuName = cpu,
            LogicalCores = Environment.ProcessorCount,
            HasAvx2 = avx2,
            HasAvx512 = avx512,
            TotalMemoryBytes = total,
            AvailableMemoryBytes = available,
            AvailableDiskBytes = disk,
            DataVolume = volume,
            Gpus = gpus,
            Cuda = cuda,
            InputDevices = inputs,
            OutputDevices = outputs,
            Unavailable = unavailable,
        };
    }

    // -- processor ----------------------------------------------------------------------------------

    private static (string? Name, bool? Avx2, bool? Avx512) ReadCpu(List<string> unavailable)
    {
        try
        {
            bool avx2 = Avx2.IsSupported;
            bool avx512 = Avx512F.IsSupported;
            return (ReadCpuBrand() ?? Fallback(unavailable), avx2, avx512);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            unavailable.Add("processor features");
            return (null, null, null);
        }

        static string? Fallback(List<string> unavailable)
        {
            unavailable.Add("processor name");
            return null;
        }
    }

    /// <summary>
    /// The processor brand string, straight from CPUID leaves 0x80000002-4.
    ///
    /// <para>
    /// The documented way to ask a processor what it is called, and it needs neither a registry
    /// read nor a WMI query. A machine that does not implement the extended leaves reports nothing,
    /// which is recorded as unknown rather than filled in.
    /// </para>
    /// </summary>
    private static string? ReadCpuBrand()
    {
        if (!X86Base.IsSupported)
        {
            return null;
        }

        (int maxExtended, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
        if ((uint)maxExtended < 0x80000004u)
        {
            return null;
        }

        Span<int> words = stackalloc int[12];

        for (int leaf = 0; leaf < 3; leaf++)
        {
            (int eax, int ebx, int ecx, int edx) = X86Base.CpuId(unchecked((int)(0x80000002 + leaf)), 0);
            words[(leaf * 4) + 0] = eax;
            words[(leaf * 4) + 1] = ebx;
            words[(leaf * 4) + 2] = ecx;
            words[(leaf * 4) + 3] = edx;
        }

        Span<byte> bytes = MemoryMarshal.AsBytes(words);
        string brand = System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0').Trim();

        return brand.Length == 0 ? null : brand;
    }

    // -- memory and disk ----------------------------------------------------------------------------

    private static (long? Total, long? Available) ReadMemory(List<string> unavailable)
    {
        try
        {
            MemoryStatusEx status = new() { Length = (uint)Unsafe.SizeOf<MemoryStatusEx>() };

            if (GlobalMemoryStatusEx(ref status))
            {
                return ((long)status.TotalPhysical, (long)status.AvailablePhysical);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Falls through to the same answer.
        }

        unavailable.Add("system memory");
        return (null, null);
    }

    private (long? Free, string? Volume) ReadDisk(List<string> unavailable)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(_layout.DataRoot)) ?? string.Empty;
            if (root.Length == 0)
            {
                unavailable.Add("free disk space");
                return (null, null);
            }

            DriveInfo drive = new(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, root) : (null, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            unavailable.Add("free disk space");
            return (null, null);
        }
    }

    // -- adapters -----------------------------------------------------------------------------------

    private static IReadOnlyList<GpuInfo> ReadAdapters(List<string> unavailable)
    {
        List<GpuInfo> adapters;

        try
        {
            adapters = EnumerateAdapters();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            unavailable.Add("graphics adapters");
            return [];
        }

        if (adapters.Count == 0)
        {
            unavailable.Add("graphics adapters");
            return [];
        }

        // The driver version only for NVIDIA, because that is the only vendor whose stack any
        // pinned profile depends on, and its own tool is the authority on it.
        string? driver = adapters.Any(a => a.IsNvidia) ? ReadNvidiaDriverVersion() : null;

        return
        [
            .. adapters.Select(a => a.IsNvidia && driver is not null ? a with { DriverVersion = driver } : a)
        ];
    }

    /// <summary>
    /// Every adapter DXGI can see, with the memory each one declares.
    ///
    /// <para>
    /// Called through the COM vtable directly rather than through generated interop, because the
    /// alternative is a package dependency for three method calls. The offsets are the documented
    /// layout of IDXGIFactory1 and IDXGIAdapter1 and do not move.
    /// </para>
    /// </summary>
    private static unsafe List<GpuInfo> EnumerateAdapters()
    {
        List<GpuInfo> found = [];

        Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
        void* factory = null;

        if (CreateDXGIFactory1(&factoryId, &factory) < 0 || factory is null)
        {
            return found;
        }

        try
        {
            void** vtable = *(void***)factory;

            // IUnknown(3) + IDXGIObject(4) + IDXGIFactory(5) = 12: EnumAdapters1.
            delegate* unmanaged<void*, uint, void**, int> enumAdapters =
                (delegate* unmanaged<void*, uint, void**, int>)vtable[12];

            for (uint index = 0; index < 16; index++)
            {
                void* adapter = null;
                if (enumAdapters(factory, index, &adapter) < 0 || adapter is null)
                {
                    break;
                }

                try
                {
                    void** adapterVtable = *(void***)adapter;

                    // IUnknown(3) + IDXGIObject(4) + IDXGIAdapter(3) = 10: GetDesc1.
                    delegate* unmanaged<void*, AdapterDescription1*, int> getDesc =
                        (delegate* unmanaged<void*, AdapterDescription1*, int>)adapterVtable[10];

                    AdapterDescription1 description;
                    if (getDesc(adapter, &description) < 0)
                    {
                        continue;
                    }

                    string model = new string(description.Description).TrimEnd('\0').Trim();

                    found.Add(new GpuInfo
                    {
                        Vendor = VendorName(description.VendorId),
                        Model = model.Length == 0 ? "unknown adapter" : model,
                        DedicatedMemoryBytes = description.DedicatedVideoMemory > 0
                            ? (long)description.DedicatedVideoMemory
                            : null,
                        // DXGI_ADAPTER_FLAG_SOFTWARE. The Basic Render Driver is not a GPU.
                        IsSoftware = (description.Flags & 0x2u) != 0,
                    });
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return found;
    }

    private static unsafe void Release(void* unknown)
    {
        void** vtable = *(void***)unknown;
        delegate* unmanaged<void*, uint> release = (delegate* unmanaged<void*, uint>)vtable[2];
        release(unknown);
    }

    private static string VendorName(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        _ => string.Create(CultureInfo.InvariantCulture, $"PCI 0x{vendorId:X4}"),
    };

    /// <summary>
    /// The NVIDIA driver version, from the vendor's own tool.
    ///
    /// <para>
    /// Absent exactly when there is no NVIDIA driver, which is the right answer rather than an
    /// error. Bounded hard: this runs during startup.
    /// </para>
    /// </summary>
    private static string? ReadNvidiaDriverVersion()
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "nvidia-smi",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("--query-gpu=driver_version");
            startInfo.ArgumentList.Add("--format=csv,noheader");

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(5_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // It exited between the check and the kill.
                }

                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            string first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? string.Empty;

            return first.Length == 0 ? null : first;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether CUDA would actually work, rather than whether an NVIDIA card is present.
    ///
    /// <para>
    /// The two are different often enough to matter: a driver too old for the pinned CTranslate2,
    /// or a laptop whose discrete adapter is switched off, both present an adapter and fail to run
    /// anything. The real answer comes from asking CTranslate2 in the installed worker environment,
    /// which the caller supplies; without one this reports what the adapters imply and says so.
    /// </para>
    /// </summary>
    private async Task<CudaAvailability> ProbeCudaAsync(
        IReadOnlyList<GpuInfo> gpus, CancellationToken cancellationToken)
    {
        bool nvidia = gpus.Any(g => g.IsNvidia && !g.IsSoftware);

        if (!nvidia)
        {
            return gpus.Count == 0 ? CudaAvailability.Unknown : CudaAvailability.NoNvidiaAdapter;
        }

        if (_cudaProbe is null)
        {
            return CudaAvailability.AdapterWithoutRuntime;
        }

        try
        {
            return await _cudaProbe(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return CudaAvailability.Unknown;
        }
    }

    // -- audio --------------------------------------------------------------------------------------

    private (IReadOnlyList<AudioEndpointSummary> Inputs, IReadOnlyList<AudioEndpointSummary> Outputs) ReadAudio(
        List<string> unavailable)
    {
        if (_audio is null)
        {
            unavailable.Add("audio devices");
            return ([], []);
        }

        try
        {
            return (
                [.. _audio.GetCaptureEndpoints().Select(Summarise)],
                [.. _audio.GetRenderEndpoints().Select(Summarise)]);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            unavailable.Add("audio devices");
            return ([], []);
        }

        static AudioEndpointSummary Summarise(Contracts.Audio.AudioEndpointInfo endpoint) =>
            new(endpoint.Id, endpoint.FriendlyName, endpoint.IsDefault);
    }

    // -- interop ------------------------------------------------------------------------------------

    [LibraryImport("dxgi.dll")]
    private static unsafe partial int CreateDXGIFactory1(Guid* riid, void** factory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    /// <summary>DXGI_ADAPTER_DESC1, laid out exactly as the header declares it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDescription1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}
