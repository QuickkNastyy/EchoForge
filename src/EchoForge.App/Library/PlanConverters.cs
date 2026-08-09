using System.Globalization;
using System.Windows.Data;
using EchoForge.Contracts.Summaries;

namespace EchoForge.App.Library;

/// <summary>
/// Turns a plan step's timing into the word a person reads on the chip.
///
/// <para>
/// The wire values are stable identifiers and the labels are not, so the mapping lives here rather
/// than in the persisted document. "Later" says something quite different from "backlog", and
/// which one is right is a presentation decision that should not require rewriting summaries.
/// </para>
/// </summary>
public sealed class PlanTimingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            PlanTimings.Immediate => "now",
            PlanTimings.Next => "next",
            PlanTimings.Later => "later",
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Says where a step's ordering came from, and says nothing when the meeting said it outright.
///
/// <para>
/// Silence is the point. Labelling every step would train a reader to ignore the label, and the
/// only case worth flagging is the one where EchoForge, not the meeting, decided what comes first.
/// </para>
/// </summary>
public sealed class PlanBasisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            PlanBases.GroundedInference => "follows from what was said",
            PlanBases.Recommendation => "suggested order",
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
