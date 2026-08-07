using System.Runtime.InteropServices;
using EchoForge.Contracts.Sessions;

namespace EchoForge.Infrastructure.Storage;

/// <summary>
/// The Recycle Bin, through the shell file operation that owns it.
///
/// <para>
/// <b>The dangerous part of this API is the part that looks harmless.</b> <c>SHFileOperation</c>
/// with <c>FOF_ALLOWUNDO</c> falls back to permanent deletion, silently, whenever the target volume
/// has no Recycle Bin — a network share, some removable drives, a volume where the bin is disabled
/// by policy. That is exactly the behaviour the architecture forbids, and it is invisible from the
/// return code. So the volume is asked first, and a volume that cannot recycle gets a refusal
/// rather than an irreversible delete.
/// </para>
///
/// <para>
/// The success of the call is not taken on trust either: the operation is asked whether it was
/// aborted, and the folder is then checked for having actually gone. The index entry is removed on
/// the strength of that, so a half-completed shell operation cannot leave search claiming a meeting
/// exists when it does not, or the reverse.
/// </para>
/// </summary>
public sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint FoDelete = 0x0003;

    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmMkDir = 0x0200;

    private const int SOk = 0;

    public bool IsAvailableFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string? root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            ShQueryRbInfo info = new() { CbSize = Marshal.SizeOf<ShQueryRbInfo>() };
            return SHQueryRecycleBinW(root, ref info) == SOk;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No shell to ask. Reporting "no Recycle Bin" is the safe answer, because the caller
            // refuses rather than falling back.
            return false;
        }
    }

    public RecycleOutcome Recycle(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        string full = Path.GetFullPath(directoryPath);

        if (!Directory.Exists(full))
        {
            return RecycleOutcome.Fail("not_found", "the folder is not there");
        }

        if (!IsAvailableFor(full))
        {
            return RecycleOutcome.Fail(
                "recycle_unavailable", "the drive this recording is on does not have a Recycle Bin");
        }

        // pFrom is a double-null-terminated list. One entry, so one extra terminator on the end;
        // the marshaller supplies the other.
        IntPtr from = Marshal.StringToHGlobalUni(full + "\0");

        try
        {
            ShFileOpStruct operation = new()
            {
                Hwnd = IntPtr.Zero,
                Func = FoDelete,
                From = from,
                To = IntPtr.Zero,
                Flags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent | FofNoConfirmMkDir,
                AnyOperationsAborted = 0,
                NameMappings = IntPtr.Zero,
                ProgressTitle = IntPtr.Zero,
            };

            int result = SHFileOperationW(ref operation);

            if (result != 0)
            {
                return RecycleOutcome.Fail("shell_failed", $"the shell reported error {result}");
            }

            if (operation.AnyOperationsAborted != 0)
            {
                return RecycleOutcome.Fail("aborted", "the operation was stopped part-way");
            }

            // Believe the filesystem rather than the return code.
            return Directory.Exists(full)
                ? RecycleOutcome.Fail("still_there", "the folder is still on disk afterwards")
                : RecycleOutcome.Ok;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return RecycleOutcome.Fail("shell_unavailable", "the Windows shell could not be reached");
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperationW(ref ShFileOpStruct operation);

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string rootPath, ref ShQueryRbInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int CbSize;
        public long Size;
        public long ItemCount;
    }
}
