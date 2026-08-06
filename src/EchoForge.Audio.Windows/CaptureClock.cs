namespace EchoForge.Audio.Windows;

/// <summary>
/// The single monotonic timeline both tracks are measured against.
///
/// <para>
/// WASAPI reports a packet's performance-counter position in 100-nanosecond units, which
/// is a different scale from <see cref="System.Diagnostics.Stopwatch.Frequency"/>. Mixing
/// the two silently is an easy way to manufacture drift, so this type is the only place
/// the conversion happens.
/// </para>
/// </summary>
public sealed class CaptureClock
{
    /// <summary>QPC positions from WASAPI are expressed in 100-nanosecond units.</summary>
    public const long UnitsPerSecond = 10_000_000;

    private CaptureClock(long epochQpc) => EpochQpc = epochQpc;

    /// <summary>The QPC position both tracks treat as t=0, in 100-nanosecond units.</summary>
    public long EpochQpc { get; }

    /// <summary>Starts a timeline at the given QPC position.</summary>
    public static CaptureClock StartAt(long epochQpc) => new(epochQpc);

    /// <summary>Reads the current performance counter in the same 100-nanosecond units.</summary>
    public static long Now() =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
               (UnitsPerSecond / (double)System.Diagnostics.Stopwatch.Frequency));

    /// <summary>Seconds elapsed on the session timeline at the given QPC position.</summary>
    public double SecondsSinceEpoch(long qpcPosition) =>
        (double)(qpcPosition - EpochQpc) / UnitsPerSecond;

    /// <summary>Converts a duration in 100-nanosecond units to seconds.</summary>
    public static double UnitsToSeconds(long units) => (double)units / UnitsPerSecond;
}
