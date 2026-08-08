using System.Globalization;
using System.Windows.Data;

using Brush = System.Windows.Media.Brush;

namespace EchoForge.App;

/// <summary>
/// Turns "a capture device may still be live" into the mark on the window bar.
///
/// <para>
/// Red while capture may be live and nothing at all otherwise, so the state is legible from the
/// title bar as well as the screen. It follows the same flag the on-screen indicator does rather
/// than a second one, which is why the two cannot disagree — and it clears only once the capture
/// threads have genuinely stopped, not when Stop was asked for.
/// </para>
/// </summary>
public sealed class RecordingAccentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Theme.Brush("Rec") : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
