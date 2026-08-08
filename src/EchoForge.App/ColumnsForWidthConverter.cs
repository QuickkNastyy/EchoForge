using System.Globalization;
using System.Windows.Data;

namespace EchoForge.App;

/// <summary>
/// How many columns a panel pair should use at the width it has been given.
///
/// <para>
/// The design puts transcription and summary side by side, which only works while there is room
/// for two readable columns. Below that they stack, rather than being squeezed into a pair of
/// gutters. Expressed as a converter because a width threshold is the whole rule, and a trigger on
/// a size that has no equality to test against would be worse.
/// </para>
/// </summary>
public sealed class ColumnsForWidthConverter : IValueConverter
{
    /// <summary>Two columns need this much between them before either is worth reading.</summary>
    public double Threshold { get; set; } = 900;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width && width >= Threshold ? 2 : 1;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
