using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EchoForge.Infrastructure.Workers;

/// <summary>
/// A Windows Job Object that owns a worker's whole process tree.
///
/// <para>
/// Killing a child process is not enough. A Python worker that later loads an inference
/// runtime may start helpers of its own, and those would survive a plain
/// <c>Process.Kill</c> — a stranded CUDA process holding VRAM is exactly the kind of thing
/// that makes the next recording fail for no visible reason.
/// </para>
///
/// <para>
/// The job is created with <c>KILL_ON_JOB_CLOSE</c>, so the tree dies when this handle is
/// released even if the host crashes without calling anything. Terminating explicitly is
/// the fast path; the handle is the guarantee.
/// </para>
/// </summary>
public sealed partial class WindowsJobObject : IDisposable
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly SafeJobObjectHandle _handle;
    private bool _disposed;

    private WindowsJobObject(SafeJobObjectHandle handle) => _handle = handle;

    /// <summary>
    /// Creates a job, or returns <c>null</c> if the platform or the OS refuses.
    ///
    /// <para>
    /// A null result is not fatal: the supervisor falls back to killing the process tree
    /// directly and records that it is running without the stronger guarantee. Failing the
    /// whole job because a containment mechanism was unavailable would be worse than
    /// running with the weaker one and saying so.
    /// </para>
    /// </summary>
    public static WindowsJobObject? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        SafeJobObjectHandle handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        JobObjectExtendedLimitInformation information = default;
        information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        if (!SetInformationJobObject(
                handle,
                ExtendedLimitInformationClass,
                ref information,
                Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            handle.Dispose();
            return null;
        }

        return new WindowsJobObject(handle);
    }

    /// <summary>
    /// Puts a process, and everything it goes on to start, inside the job.
    ///
    /// <para>
    /// Timing matters here. The worker's first act is to wait for the host's <c>hello</c> on
    /// stdin, so it does nothing at all between being started and being assigned. The gap
    /// exists, but nothing can escape through it.
    /// </para>
    /// </summary>
    public bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // The process exited before it could be assigned. Nothing to contain.
            return false;
        }
    }

    /// <summary>Kills every process in the job. Idempotent, and safe after the tree is gone.</summary>
    public bool TerminateAll(uint exitCode = 1)
    {
        if (_disposed || _handle.IsInvalid || _handle.IsClosed)
        {
            return false;
        }

        return TerminateJobObject(_handle, exitCode);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing the last handle is what triggers KILL_ON_JOB_CLOSE, so this is the
        // backstop for every path that forgot, threw, or crashed on the way out.
        _handle.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobObjectHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobObjectHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        int informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobObjectHandle job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeJobObjectHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }
}

/// <summary>The job handle itself. Releasing it is what kills the contained tree.</summary>
internal sealed partial class SafeJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    // The interop marshaller constructs this itself, so the constructor has to be reachable
    // even though nothing in EchoForge ever calls it.
    public SafeJobObjectHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
