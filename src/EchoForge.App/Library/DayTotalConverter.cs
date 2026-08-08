using System.Collections;
using System.Globalization;
using System.Windows.Data;

namespace EchoForge.App.Library;

/// <summary>
/// Sums a day's recordings into the heading the design puts beside it: "2 recordings · 1h 13m".
///
/// <para>
/// Derived from the rows in that group rather than from a stored total, so it cannot drift from
/// what is listed underneath it, and a filtered or searched view says what it is actually showing.
/// </para>
/// </summary>
public sealed class DayTotalConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable rows)
        {
            return string.Empty;
        }

        int count = 0;
        TimeSpan total = TimeSpan.Zero;

        foreach (object row in rows)
        {
            if (row is not MeetingRow meeting)
            {
                continue;
            }

            count++;
            total += meeting.Entry.Duration;
        }

        if (count == 0)
        {
            return string.Empty;
        }

        string recordings = count == 1 ? "1 recording" : string.Create(CultureInfo.CurrentCulture, $"{count} recordings");

        string length = total >= TimeSpan.FromHours(1)
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)total.TotalHours}h {total.Minutes:00}m")
            : string.Create(CultureInfo.CurrentCulture, $"{(int)total.TotalMinutes}m");

        return recordings + " · " + length;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
