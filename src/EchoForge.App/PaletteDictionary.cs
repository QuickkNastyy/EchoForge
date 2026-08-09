using System.Windows;
using System.Windows.Media;

using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace EchoForge.App;

/// <summary>
/// Owns the application palette as immutable brush resources.
///
/// <para>
/// WPF's render thread is allowed to consume application resources concurrently with the UI
/// thread. Mutating a brush that is already in that render graph is therefore the wrong primitive
/// for a theme switch: the previous implementation kept brushes mutable with data bindings and a
/// live switch could drive wpfgfx into a native fail-fast. Each palette entry here is instead a
/// frozen brush. Switching themes replaces the resource value, and DynamicResource consumers pick
/// up the new immutable object through WPF's normal resource invalidation path.
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1010:Generic interface should also be implemented",
    Justification = "A merged palette dictionary has to be a ResourceDictionary for WPF to consume it.")]
public sealed class PaletteDictionary : ResourceDictionary
{
    public PaletteDictionary() => Apply(AppTheme.Dark);

    /// <summary>Replaces every palette entry with a newly frozen brush for <paramref name="theme"/>.</summary>
    internal void Apply(AppTheme theme)
    {
        foreach ((string key, Color color) in Theme.Colors(theme))
        {
            SolidColorBrush brush = new(color);
            brush.Freeze();
            this[key] = brush;
        }
    }

    /// <summary>Updates the palette dictionary merged into the running application's resources.</summary>
    internal static bool TryApplyToApplication(AppTheme theme)
    {
        ResourceDictionary? resources = System.Windows.Application.Current?.Resources;
        if (resources is null)
        {
            return false;
        }

        foreach (ResourceDictionary dictionary in resources.MergedDictionaries)
        {
            if (dictionary is PaletteDictionary palette)
            {
                palette.Apply(theme);
                return true;
            }
        }

        return false;
    }
}
