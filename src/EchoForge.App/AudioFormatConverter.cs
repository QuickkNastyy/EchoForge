using System.Globalization;
using System.Windows.Data;
using EchoForge.Contracts.Audio;

namespace EchoForge.App;

/// <summary>
/// Renders a negotiated <see cref="CaptureFormat"/> the way the device picker shows it —
/// "48 kHz · stereo · 16-bit" — rather than the raw hertz of the record's own <c>ToString</c>.
///
/// <para>
/// Presentation only. The value it formats is exactly the format the endpoint reported; this never
/// invents, rounds away, or negotiates anything, and returning an empty string for anything that is
/// not a format keeps a device row from ever falling back to a DTO dump.
/// </para>
/// </summary>
public sealed class AudioFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CaptureFormat format)
        {
            return string.Empty;
        }

        string channels = format.Channels switch
        {
            1 => "mono",
            2 => "stereo",
            _ => string.Create(CultureInfo.InvariantCulture, $"{format.Channels} ch"),
        };

        double kilohertz = format.SampleRate / 1000.0;
        string rate = kilohertz == Math.Floor(kilohertz)
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)kilohertz} kHz")
            : string.Create(CultureInfo.InvariantCulture, $"{kilohertz:0.0} kHz");

        return string.Create(CultureInfo.InvariantCulture, $"{rate} · {channels} · {format.BitsPerSample}-bit");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
