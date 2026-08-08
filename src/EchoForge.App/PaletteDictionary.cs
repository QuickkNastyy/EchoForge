using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

using Binding = System.Windows.Data.Binding;
using BindingMode = System.Windows.Data.BindingMode;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace EchoForge.App;

/// <summary>
/// The colours behind the palette brushes, as one observable object.
///
/// <para>
/// Every brush binds its <c>Color</c> here. Repainting is then a single change notification rather
/// than a walk over the resource dictionary, and — the reason this indirection exists at all — a
/// data-bound <see cref="System.Windows.Freezable"/> reports that it cannot be frozen, which is what
/// keeps the brushes mutable. See <see cref="PaletteDictionary"/>.
/// </para>
/// </summary>
internal sealed class PaletteSource : INotifyPropertyChanged
{
    private readonly Dictionary<string, Color> _colors = new(StringComparer.Ordinal);

    private PaletteSource() => Load(AppTheme.Dark);

    public static PaletteSource Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Indexed so one binding path per key is enough, with no property per tone.</summary>
    public Color this[string key] =>
        _colors.TryGetValue(key, out Color color) ? color : Colors.Transparent;

    public void Load(AppTheme theme)
    {
        foreach ((string key, Color color) in Theme.Colors(theme))
        {
            _colors[key] = color;
        }

        // "Item[]" is WPF's signal that every indexer binding should re-read.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}

/// <summary>
/// The palette, built in code so that it can still be repainted.
///
/// <para>
/// This exists for one reason, and it is not obvious. A <c>ResourceDictionary</c> that belongs to
/// the <c>Application</c> <b>freezes every Freezable in it</b>, so that application resources are
/// safe to touch from more than one thread — and a frozen brush cannot have its colour changed. A
/// theme switch that writes new colours into those brushes therefore does nothing at all, which is
/// exactly what happened until the rendered pixels reported that two per cent of the page had gone
/// light.
/// </para>
///
/// <para>
/// A Freezable with a data binding on one of its properties reports that it cannot be frozen, so
/// binding each brush's colour to <see cref="PaletteSource"/> keeps the whole palette mutable while
/// it still lives in application resources. Which means every <c>StaticResource</c> in the
/// application — several hundred of them, in templates that are parsed once — keeps working
/// untouched, and no reference had to become a <c>DynamicResource</c>.
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1010:Generic interface should also be implemented",
    Justification = "A merged dictionary has to be a ResourceDictionary for WPF to accept it, and " +
                    "ResourceDictionary's own non-generic ICollection is not ours to change. " +
                    "Nothing enumerates this type; it is a container the XAML loader merges.")]
public sealed class PaletteDictionary : ResourceDictionary
{
    public PaletteDictionary()
    {
        foreach ((string key, Color color) in Theme.Colors(AppTheme.Dark))
        {
            SolidColorBrush brush = new(color);

            BindingOperations.SetBinding(
                brush,
                SolidColorBrush.ColorProperty,
                new Binding($"[{key}]") { Source = PaletteSource.Instance, Mode = BindingMode.OneWay });

            Add(key, brush);
        }
    }
}
