using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EchoForge.App;

/// <summary>
/// True collapses, false shows — the opposite of the built-in converter, so two views can be toggled
/// by the same boolean without a second flag.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
