using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace EchoForge.UnitTests;

/// <summary>
/// The button label, actually rendered, against the exact hazard that made it disappear.
///
/// <para>
/// The shipped defect was invisible-until-hovered text: a button's string content generates a
/// TextBlock, and the application-wide implicit TextBlock style set an <b>explicit</b> near-white
/// foreground that beat the button template's inherited one — so the light primary button painted
/// near-white on near-white and only showed its label when a hover darkened the chrome.
/// </para>
///
/// <para>
/// This reproduces that hazard in a self-contained resource dictionary — the same near-white
/// implicit TextBlock style, and a primary button template built the way <c>App.xaml</c> now builds
/// it (a template-scoped empty TextBlock style so the generated text inherits the button's own dark
/// foreground). It then builds the real visual tree and reads the colour that reached the text.
/// Loading the compiled application resources into a test host is more trouble than it is worth;
/// rendering the technique the fix relies on is what actually proves the text is visible.
/// </para>
/// </summary>
public sealed class ButtonRenderingTests
{
    // The near-white ink and dark ground from App.xaml. The primary button is dark-on-light; the
    // default button is light-on-dark. Both must be readable without a hover.
    private const string Ink = "#E6EDF3";
    private const string Ground = "#0D1318";
    private const string Raised = "#1D2831";

    private const string ResourcesXaml = $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <!-- The hazard: an application-wide implicit TextBlock foreground that is near-white. -->
          <Style TargetType="TextBlock">
            <Setter Property="Foreground" Value="{{Ink}}" />
          </Style>

          <Style x:Key="ButtonBase" TargetType="Button">
            <Setter Property="Foreground" Value="{{Ink}}" />
            <Setter Property="Background" Value="{{Raised}}" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Chrome" Background="{TemplateBinding Background}">
                    <Border.Resources>
                      <!-- The fix: shadow the app-wide TextBlock style so generated string content
                           has no explicit foreground and inherits the button's own, below. -->
                      <Style TargetType="TextBlock" />
                    </Border.Resources>
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"
                                      TextElement.Foreground="{TemplateBinding Foreground}" />
                  </Border>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="PrimaryButton" TargetType="Button" BasedOn="{StaticResource ButtonBase}">
            <Setter Property="Background" Value="{{Ink}}" />
            <Setter Property="Foreground" Value="{{Ground}}" />
          </Style>
        </ResourceDictionary>
        """;

    private static void OnSta(Action action)
    {
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "STA render did not finish");

        if (error is not null)
        {
            throw error;
        }
    }

    private static (Color Foreground, Color Background) RenderContent(string styleKey)
    {
        ResourceDictionary resources = (ResourceDictionary)XamlReader.Parse(ResourcesXaml);

        Button button = new()
        {
            Content = "Start recording",
            Style = (Style)resources[styleKey],
        };

        Border host = new() { Child = button };
        host.Resources.MergedDictionaries.Add(resources);

        host.Measure(new Size(500, 120));
        host.Arrange(new Rect(0, 0, 500, 120));
        host.UpdateLayout();

        TextBlock text = FindText(button) ?? throw new InvalidOperationException("no text was generated for the button");

        Color foreground = ((SolidColorBrush)text.Foreground).Color;
        Color background = button.Background is SolidColorBrush brush ? brush.Color : Colors.Transparent;
        return (foreground, background);
    }

    private static TextBlock? FindText(DependencyObject root)
    {
        if (root is TextBlock block)
        {
            return block;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindText(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>WCAG relative-luminance contrast ratio, the standard readability measure.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Channel(double c)
        {
            c /= 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color c) =>
            (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

        double la = Luminance(a) + 0.05;
        double lb = Luminance(b) + 0.05;
        return la > lb ? la / lb : lb / la;
    }

    [Fact]
    public void ThePrimaryButtonLabelIsReadableWithoutAHover()
    {
        OnSta(() =>
        {
            (Color foreground, Color background) = RenderContent("PrimaryButton");

            // The bug rendered near-white on near-white: a contrast near 1.0. Readable dark-on-light
            // text clears the WCAG AA threshold comfortably.
            Assert.True(
                Contrast(foreground, background) >= 4.5,
                $"primary button text {foreground} on {background} is not readable");

            // And specifically: the text did not stay the app-wide near-white ink.
            Assert.NotEqual((Color)ColorConverter.ConvertFromString(Ink)!, foreground);
        });
    }

    [Fact]
    public void TheDefaultButtonLabelIsReadable()
    {
        OnSta(() =>
        {
            (Color foreground, Color background) = RenderContent("ButtonBase");

            Assert.True(
                Contrast(foreground, background) >= 4.5,
                $"default button text {foreground} on {background} is not readable");
        });
    }
}
