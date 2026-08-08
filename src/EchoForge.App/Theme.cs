using System.Windows;
using System.Windows.Media;

// The application enables WinForms for the tray icon, which brings System.Drawing into scope and
// collides with the WPF drawing types. Pin every ambiguous name to its WPF meaning.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace EchoForge.App;

/// <summary>Which palette the application is painting with.</summary>
public enum AppTheme
{
    Dark,

    Light,
}

/// <summary>
/// The two palettes, and the one operation that swaps between them.
///
/// <para>
/// Switching does not reload a dictionary or re-resolve a single <c>StaticResource</c>. Every brush
/// in the palette is one shared instance whose colour is bound to <c>PaletteSource</c>, so changing
/// the palette repaints everything drawn with it — chrome, text, meters, and the two ribbons, which
/// take the same brush objects rather than freezing private copies. That is the whole mechanism: no
/// restart, no rebuilt visual tree, and no second set of keys for anyone to forget to use. See
/// <c>PaletteDictionary</c> for why the brushes have to be built the way they are.
/// </para>
///
/// <para>
/// Both palettes come from <c>docs/design/ui-mockups.html</c> unchanged. Colour still carries
/// meaning in either one: amber is You, teal is Remote, red is capture and appears nowhere else.
/// The light values are darker, not lighter, because they have to hold the same contrast against a
/// white ground that the dark ones hold against graphite.
/// </para>
/// </summary>
public static class Theme
{
    private static readonly Dictionary<string, (Color Dark, Color Light)> Palette = new(StringComparer.Ordinal)
    {
        ["Ground"] = (Rgb("#0D1318"), Rgb("#EDF0F3")),
        ["Surface"] = (Rgb("#141C23"), Rgb("#FFFFFF")),
        ["Raised"] = (Rgb("#1D2831"), Rgb("#E7ECF1")),
        ["Sunken"] = (Rgb("#0A1015"), Rgb("#F5F7F9")),

        // rgba(255,255,255,.09) and rgba(10,20,30,.11) from the design, and their stronger pair.
        ["Line"] = (Rgb("#17FFFFFF"), Rgb("#1C0A141E")),
        ["LineStrong"] = (Rgb("#29FFFFFF"), Rgb("#330A141E")),

        ["Ink"] = (Rgb("#E6EDF3"), Rgb("#0E1720")),
        ["InkDim"] = (Rgb("#8496A6"), Rgb("#54687A")),
        ["InkFaint"] = (Rgb("#5B6C7B"), Rgb("#7B8B99")),
        ["Focus"] = (Rgb("#9CC4F0"), Rgb("#1D5FA8")),

        ["You"] = (Rgb("#F2A93B"), Rgb("#B36C10")),
        ["Remote"] = (Rgb("#3FBFC7"), Rgb("#0B767E")),
        ["Rec"] = (Rgb("#E2453D"), Rgb("#C22C25")),

        ["YouWash"] = (Rgb("#24F2A93B"), Rgb("#1FB36C10")),
        ["RemoteWash"] = (Rgb("#243FBFC7"), Rgb("#1F0B767E")),
        ["RecWash"] = (Rgb("#21E2453D"), Rgb("#1AC22C25")),

        ["Warn"] = (Rgb("#D8A657"), Rgb("#8A5B0F")),
        ["WarnWash"] = (Rgb("#1E1A12"), Rgb("#FBF2E0")),

        ["Hover"] = (Rgb("#2C3A46"), Rgb("#DAE1E8")),
        ["Disabled"] = (Rgb("#18222B"), Rgb("#DFE5EB")),
    };

    /// <summary>Every tone in one palette, for the dictionary that builds the brushes.</summary>
    public static IEnumerable<(string Key, Color Color)> Colors(AppTheme theme) =>
        Palette.Select(entry => (entry.Key, theme == AppTheme.Dark ? entry.Value.Dark : entry.Value.Light));

    /// <summary>The palette currently painted. Dark is the default, as the design's is.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>
    /// Raised after the brushes hold the new palette.
    ///
    /// <para>
    /// Almost nothing needs this: anything drawn with the shared brushes has already repainted by
    /// the time it fires. It exists for the few places that have to re-read a colour rather than a
    /// brush, and for saving the choice.
    /// </para>
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>The other one, for a toggle to ask for.</summary>
    public static AppTheme Other => Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;

    /// <summary>What a toggle should say it will do, rather than what is showing now.</summary>
    public static string ToggleDescription =>
        Current == AppTheme.Dark ? "Switch to the light theme" : "Switch to the dark theme";

    /// <summary>
    /// Repaints the application in the given palette.
    ///
    /// <para>
    /// Safe to call before any window exists and safe to call repeatedly. A brush key the
    /// application does not define is skipped rather than throwing: a missing tone should cost one
    /// wrong colour, never a crash on a theme toggle.
    /// </para>
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;

        // One notification. Every palette brush binds its colour here, so the whole application
        // repaints from this line, including the two ribbons that draw with the same brushes.
        PaletteSource.Instance.Load(theme);

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Switches to the palette that is not showing.</summary>
    public static void Toggle() => Apply(Other);

    /// <summary>
    /// The toggle, as a command every window can bind to.
    ///
    /// <para>
    /// Static on purpose. The palette is one global fact, and threading a command through the
    /// recorder's view model, the library's, and setup's — none of which otherwise know a theme
    /// exists — would be plumbing in exchange for nothing.
    /// </para>
    /// </summary>
    public static System.Windows.Input.ICommand ToggleCommand { get; } = new RelayCommand(Toggle);

    /// <summary>Reads a stored preference. Anything unrecognised is dark.</summary>
    public static AppTheme Parse(string? stored) =>
        string.Equals(stored, "light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;

    /// <summary>How the current palette is written to settings.</summary>
    public static string Name(AppTheme theme) => theme == AppTheme.Light ? "light" : "dark";

    /// <summary>
    /// A brush from the palette by key.
    ///
    /// <para>
    /// The shared instance, never a copy — a control that drew with a copy would keep the colour it
    /// started with and quietly stop following the theme. Returns transparent rather than throwing
    /// when there is no application, which is what a designer surface and a bare unit test see.
    /// </para>
    /// </summary>
    public static Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    /// <summary>A pen over a palette brush, unfrozen for the same reason the brushes are.</summary>
    public static Pen Pen(string key, double thickness) => new(Brush(key), thickness);

    private static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}
