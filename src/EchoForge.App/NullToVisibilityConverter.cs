using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EchoForge.App;

/// <summary>
/// Hides a section whose view model does not exist yet.
///
/// <para>
/// Transcription, summarisation and the runtime surface all arrive after startup, because finding
/// a Python runtime means starting processes and the recorder must never wait on that. A settings
/// page that drew empty pickers in the meantime would look broken rather than unfinished.
/// </para>
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // "invert" shows the element only when the value is missing: the empty-state message that
        // stands in for a section whose view model never attached.
        bool invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        bool present = value is not null;
        return present != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
