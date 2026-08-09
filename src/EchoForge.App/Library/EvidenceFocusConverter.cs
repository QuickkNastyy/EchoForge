using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EchoForge.App.Library;

/// <summary>
/// Whether a transcript line is the one the selected claim cites, and how to draw it if not.
///
/// <para>
/// Takes the line's own segment ID and the meeting's currently cited one. Kept as a comparison at
/// render time rather than a flag on each line, because a three-hour meeting is tens of thousands
/// of lines and marking one of them should cost one property change, not a rebuilt list.
/// </para>
///
/// <para>
/// <c>ConverterParameter</c> picks what to answer with: <c>mark</c> for whether this is the cited
/// line, and <c>emphasis</c> for the opacity everything else drops to while a claim is being
/// followed. With no claim selected nothing is dimmed — the transcript is not a search result.
/// </para>
/// </summary>
public sealed class EvidenceFocusConverter : IMultiValueConverter
{
    /// <summary>How far the rest of the transcript recedes. Still readable, plainly secondary.</summary>
    private const double Receded = 0.45;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        IReadOnlyCollection<string>? segmentIds = values.Length > 0 ? values[0] as IReadOnlyCollection<string> : null;
        string? cited = values.Length > 1 ? values[1] as string : null;

        bool isCited = cited is not null && segmentIds?.Contains(cited, StringComparer.Ordinal) == true;

        return parameter as string == "mark"
            ? isCited ? Visibility.Visible : Visibility.Collapsed
            : cited is null || isCited ? 1.0 : Receded;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
