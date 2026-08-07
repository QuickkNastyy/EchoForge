using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EchoForge.App.Setup;

/// <summary>
/// A tick or a dash for a boolean.
///
/// <para>
/// Text rather than a coloured dot alone, so the state survives a screenshot, a colour-blind
/// reader, and a high-contrast theme. Colour is the second signal here, never the only one.
/// </para>
/// </summary>
public sealed class TickConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "✓" : "•";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("one way only");
}

/// <summary>Green when something is ready, muted when it is not.</summary>
public sealed class ReadyBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ready = Frozen(System.Windows.Media.Color.FromRgb(0x63, 0xC1, 0x82));

    private static readonly SolidColorBrush Waiting = Frozen(System.Windows.Media.Color.FromRgb(0x84, 0x96, 0xA6));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Ready : Waiting;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("one way only");

    private static SolidColorBrush Frozen(System.Windows.Media.Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
