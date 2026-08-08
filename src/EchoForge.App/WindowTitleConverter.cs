using System.Globalization;
using System.Windows.Data;

namespace EchoForge.App;

/// <summary>
/// Splits a window title at its em dash, so the bar can set the product name in ink and whatever
/// the window is currently doing in a quieter tone — "<b>EchoForge</b> — recording".
///
/// <para>
/// The window's own <c>Title</c> stays one string, because that is what the taskbar, Alt-Tab and
/// every screen reader read. This only decides how the two halves are painted.
/// </para>
/// </summary>
public sealed class WindowTitleConverter : IValueConverter
{
    /// <summary>Either <c>Name</c> for the part before the dash, or <c>State</c> for the rest.</summary>
    public string Part { get; set; } = "Name";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string title = value as string ?? string.Empty;
        int dash = title.IndexOf('—', StringComparison.Ordinal);

        if (dash < 0)
        {
            return string.Equals(Part, "Name", StringComparison.Ordinal) ? title : string.Empty;
        }

        return string.Equals(Part, "Name", StringComparison.Ordinal)
            ? title[..dash].TrimEnd()
            : " " + title[dash..];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
