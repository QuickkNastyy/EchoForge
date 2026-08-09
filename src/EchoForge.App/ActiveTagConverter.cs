using System.Globalization;
using System.Windows.Data;

namespace EchoForge.App;

/// <summary>
/// Marks the navigation button for the page that is showing.
///
/// <para>
/// The rail button style already reads <c>Tag="active"</c>; this turns "is this the current page"
/// into that tag, so the current destination is decided by the view model rather than by which
/// button happens to have been hard-coded as selected in each window's copy of the rail.
/// </para>
/// </summary>
public sealed class ActiveTagConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "active" : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
