using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EchoForge.App;

/// <summary>
/// Lets the wheel through a list that is not scrolling itself.
///
/// <para>
/// A <see cref="ListBox"/> owns an internal <see cref="ScrollViewer"/>, and that viewer marks every
/// wheel event handled — even when its scrollbars are disabled and it has nowhere to scroll to. The
/// settings sections size their lists to their content and let the page scroll, so the wheel died
/// the moment the pointer crossed the models list or the components list: the page simply stopped
/// responding over the largest part of itself.
/// </para>
///
/// <para>
/// Setting <c>app:WheelRelay.Enabled="True"</c> forwards the wheel to the nearest ancestor that can
/// actually use it. It is deliberately an attached property rather than a handler in one page's
/// code-behind, because this is a property of the control, not of the screen it happens to be on.
/// </para>
/// </summary>
public static class WheelRelay
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(WheelRelay),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(EnabledProperty, value);
    }

    public static bool GetEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(EnabledProperty);
    }

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.PreviewMouseWheel -= OnWheel;
        if (e.NewValue is true)
        {
            target.PreviewMouseWheel += OnWheel;
        }
    }

    /// <summary>
    /// Re-raises the wheel on the parent, so the scrolling ancestor sees an event the list never
    /// consumed. Handling it here first is what stops the list's own viewer from swallowing it.
    /// </summary>
    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement target)
        {
            return;
        }

        e.Handled = true;

        MouseWheelEventArgs relayed = new(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = target,
        };

        // The parent, not this element: raising it here again would come straight back through the
        // same preview handler.
        (System.Windows.Media.VisualTreeHelper.GetParent(target) as UIElement)?.RaiseEvent(relayed);
    }
}
