namespace EchoForge.Contracts.Library;

/// <summary>
/// Which meetings to show, before any search is applied.
///
/// <para>
/// Bounds are absolute instants, inclusive at both ends. Turning "the 3rd of August" into instants
/// is done once, by <see cref="ForLocalDates"/>, because that is the conversion people get wrong:
/// meetings are stored in UTC and remembered in local time, and comparing a local date against a
/// UTC instant is how a meeting recorded at nine in the evening ends up filed under the next day.
/// </para>
/// </summary>
public sealed record LibraryFilter
{
    public static readonly LibraryFilter None = new();

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }

    public bool IsEmpty => Since is null && Until is null;

    /// <summary>
    /// True when the range cannot contain anything.
    ///
    /// <para>
    /// Reported rather than quietly swapped. Somebody who typed the dates the wrong way round
    /// should be told, not shown results for a range they did not ask for.
    /// </para>
    /// </summary>
    public bool IsReversed => Since is { } since && Until is { } until && since > until;

    /// <summary>
    /// Builds a filter from local calendar dates, inclusive of both days.
    ///
    /// <para>
    /// The start is local midnight on the first day and the end is the last instant before local
    /// midnight on the day after the second, so a meeting anywhere in either day is inside the
    /// range whatever the offset is and whether or not the clocks changed between them.
    /// </para>
    /// </summary>
    public static LibraryFilter ForLocalDates(DateOnly? start, DateOnly? end, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;

        return new LibraryFilter
        {
            Since = start is { } from ? LocalMidnight(from, zone) : null,
            Until = end is { } to ? LocalMidnight(to.AddDays(1), zone).AddTicks(-1) : null,
        };
    }

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        DateTime midnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // A day that does not start at midnight — some zones skip the hour for daylight saving —
        // still has a first instant, and that is the one the range should begin at.
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }
}
