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
/// Palette brushes are immutable once WPF can render them. A switch replaces the values in the
/// merged <c>PaletteDictionary</c>; controls reference palette keys with <c>DynamicResource</c>, so
/// WPF invalidates those resource references without mutating any Freezable already owned by the
/// render thread. Custom-drawn ribbons listen for <see cref="Changed"/> and redraw from the same
/// frozen resources.
/// </para>
///
/// <para>
/// The dark palette is the approved "Graphite" direction: neutral charcoal surfaces with a slight
/// cool cast, quiet hairline borders, light neutral text, and one muted green accent. Semantic
/// colours keep their jobs: green is You and the primary action, teal is Remote, red is capture,
/// amber is a warning. The light palette remains a clean neutral counterpart with the same
/// contrast hierarchy.
/// </para>
/// </summary>
public static class Theme
{
    private static readonly Dictionary<string, (Color Dark, Color Light)> Palette = new(StringComparer.Ordinal)
    {
        ["Ground"] = (Rgb("#0E1012"), Rgb("#EEF1F3")),
        ["Surface"] = (Rgb("#14161A"), Rgb("#F7F9FA")),
        ["CardFill"] = (Rgb("#17191D"), Rgb("#FFFFFF")),
        ["Panel"] = (Rgb("#111316"), Rgb("#ECEFF2")),
        ["Raised"] = (Rgb("#1D2025"), Rgb("#E7ECF0")),
        ["Sunken"] = (Rgb("#101215"), Rgb("#F1F4F6")),

        // rgba(255,255,255,.06) and rgba(10,20,30,.11) hairlines, and their stronger pair.
        ["Line"] = (Rgb("#12FFFFFF"), Rgb("#1C0A141E")),
        ["LineStrong"] = (Rgb("#22FFFFFF"), Rgb("#330A141E")),

        ["Ink"] = (Rgb("#E7E9EB"), Rgb("#101820")),
        ["InkDim"] = (Rgb("#9AA0A6"), Rgb("#55636F")),
        ["InkFaint"] = (Rgb("#6E7478"), Rgb("#7D8994")),
        ["Focus"] = (Rgb("#63B981"), Rgb("#1E7A46")),
        ["FocusWash"] = (Rgb("#2463B981"), Rgb("#E3F1E8")),

        // Dark text for a solid green plate, white for the deep green the light theme uses.
        ["OnAccent"] = (Rgb("#0D1A11"), Rgb("#FFFFFF")),

        ["You"] = (Rgb("#5FBF7E"), Rgb("#1E7A46")),
        ["Remote"] = (Rgb("#58A8AD"), Rgb("#0B767E")),
        ["Rec"] = (Rgb("#D9534E"), Rgb("#C22C25")),

        ["YouWash"] = (Rgb("#245FBF7E"), Rgb("#1F1E7A46")),
        ["RemoteWash"] = (Rgb("#2458A8AD"), Rgb("#1F0B767E")),
        ["RecWash"] = (Rgb("#21D9534E"), Rgb("#1AC22C25")),

        ["Warn"] = (Rgb("#D6A054"), Rgb("#8A5B0F")),
        ["WarnWash"] = (Rgb("#1E1A12"), Rgb("#FBF2E0")),

        ["Hover"] = (Rgb("#23262B"), Rgb("#DEE4E9")),
        ["Disabled"] = (Rgb("#1B1E22"), Rgb("#E2E7EB")),
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
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Apply(theme));
            return;
        }

        // Never mutate a brush already handed to WPF's render thread. The crash dump from the live
        // theme-switch failure terminates inside wpfgfx_cor3.dll while those old bound brushes were
        // being repainted. Replacing frozen resources is WPF's supported cross-render boundary.
        PaletteDictionary.TryApplyToApplication(theme);
        Current = theme;

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
    /// Returns the frozen brush currently stored under the palette key. Custom-drawn controls ask
    /// again on each render after <see cref="Changed"/> invalidates them. Returns transparent
    /// when there is no application, which is what a designer surface and a bare unit test see.
    /// </para>
    /// </summary>
    public static Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    /// <summary>A short-lived drawing pen over the current immutable palette brush.</summary>
    public static Pen Pen(string key, double thickness) => new(Brush(key), thickness);

    private static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}
