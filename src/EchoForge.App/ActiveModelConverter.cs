using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EchoForge.App;

/// <summary>
/// Whether a row in the Models list is the model EchoForge is currently set to use.
///
/// <para>
/// The list holds speech models and summary models together, and a row is "in use" only against
/// the choice of its own kind: the summary model being Gemma says nothing about which speech model
/// is selected. So the comparison takes the row's id, its category, and both active ids, and picks
/// the one that applies.
/// </para>
///
/// <para>
/// Bound rather than computed on the row because the row is a snapshot rebuilt by every scan,
/// while the choice lives in settings and changes independently of it. A row that carried its own
/// "in use" flag would be stale the moment somebody used the picker instead.
/// </para>
/// </summary>
public sealed class ActiveModelConverter : IMultiValueConverter
{
    /// <summary>Set to return a <see cref="Visibility"/> instead of a boolean.</summary>
    public bool AsVisibility { get; set; }

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);

        bool active = values.Length >= 4
            && values[0] is string id
            && values[1] is string category
            && Matches(id, category, values[2] as string, values[3] as string);

        if (!AsVisibility)
        {
            return active;
        }

        return active ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Matches(string id, string category, string? activeAsr, string? activeSummary)
    {
        string? chosen = string.Equals(category, "SPEECH", StringComparison.OrdinalIgnoreCase)
            ? activeAsr
            : activeSummary;

        return chosen is not null && string.Equals(id, chosen, StringComparison.Ordinal);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
