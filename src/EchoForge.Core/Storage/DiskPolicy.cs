namespace EchoForge.Core.Storage;

/// <summary>Reads free space for a path. Injected so thresholds can be tested without a full disk.</summary>
public interface IDiskSpaceProbe
{
    long AvailableBytes(string path);
}

/// <summary>What the disk policy wants done right now.</summary>
public enum DiskAction
{
    /// <summary>Plenty of room.</summary>
    Continue,

    /// <summary>Getting low. Warn the user; keep recording.</summary>
    Warn,

    /// <summary>Too low to continue safely. Stop in a controlled way, preserving every chunk.</summary>
    ControlledStop,
}

/// <summary>The result of a disk check, with the numbers the UI shows.</summary>
public sealed record DiskStatus(DiskAction Action, long AvailableBytes, double BytesPerSecond)
{
    public TimeSpan EstimatedRemaining => BytesPerSecond <= 0
        ? TimeSpan.MaxValue
        : TimeSpan.FromSeconds(AvailableBytes / BytesPerSecond);

    public double AvailableGigabytes => AvailableBytes / 1_000_000_000.0;
}

/// <summary>
/// Disk thresholds for recording.
///
/// <para>
/// Warn at 5 GB, stop in a controlled way at 2 GB, and refuse to start without the larger of
/// 2 GB or ten minutes of worst-case recording plus a reserve. A controlled stop preserves every
/// completed chunk; running the volume to zero does not.
/// </para>
/// </summary>
public sealed record DiskPolicy
{
    public long WarnBytes { get; init; } = 5_000_000_000;

    public long ControlledStopBytes { get; init; } = 2_000_000_000;

    public long MinimumStartBytes { get; init; } = 2_000_000_000;

    public TimeSpan PreflightDuration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Small preallocated file released during recovery to patch headers and journals.</summary>
    public long RecoveryReserveBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Worst-case bytes per second: 48 kHz PCM16 stereo plus mono, about 288 KB/s.</summary>
    public double WorstCaseBytesPerSecond { get; init; } = (48_000 * 2 * 2) + (48_000 * 2);

    /// <summary>Bytes that must be free before a recording may start.</summary>
    public long RequiredToStartBytes => Math.Max(
        MinimumStartBytes,
        (long)(WorstCaseBytesPerSecond * PreflightDuration.TotalSeconds) + RecoveryReserveBytes);

    public DiskStatus Evaluate(long availableBytes, double bytesPerSecond)
    {
        DiskAction action = availableBytes switch
        {
            _ when availableBytes <= ControlledStopBytes => DiskAction.ControlledStop,
            _ when availableBytes <= WarnBytes => DiskAction.Warn,
            _ => DiskAction.Continue,
        };

        return new DiskStatus(action, availableBytes, bytesPerSecond);
    }

    /// <summary>Whether a recording may start, and why not when it may not.</summary>
    public (bool Allowed, string? Reason) CanStart(long availableBytes)
    {
        if (availableBytes >= RequiredToStartBytes)
        {
            return (true, null);
        }

        return (false,
            $"Only {availableBytes / 1_000_000_000.0:0.0} GB free. EchoForge needs " +
            $"{RequiredToStartBytes / 1_000_000_000.0:0.0} GB to start a recording safely.");
    }
}
