using EchoForge.Core.Storage;

namespace EchoForge.Infrastructure.Storage;

/// <summary>Reads real free space from the volume a path sits on.</summary>
public sealed class VolumeDiskSpaceProbe : IDiskSpaceProbe
{
    public long AvailableBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return 0;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // An unreadable volume must not be reported as roomy.
            return 0;
        }
    }
}
