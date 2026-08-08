using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace EchoForge.App;

/// <summary>
/// The behaviour behind the application's own title bar.
///
/// <para>
/// The design draws the window bar itself — a mark, the name, what the window is currently doing,
/// and the three caption buttons — so the windows run with <see cref="System.Windows.Shell.WindowChrome"/>
/// and no system caption. The look is the <c>AppWindow</c> style in <c>App.xaml</c>; this is the part
/// a style cannot express.
/// </para>
///
/// <para>
/// A chromeless window maximises over the whole monitor, taskbar included, because the default
/// maximum tracking size is the screen rather than the work area. <c>WM_GETMINMAXINFO</c> is
/// answered with the work area of the monitor the window is actually on, so maximising covers the
/// desktop and not the taskbar — on any monitor, at any scale factor, and after the window is
/// dragged between two monitors that differ in both.
/// </para>
///
/// <para>
/// <b>What this costs.</b> The Windows 11 snap flyout appears when the pointer rests on a caption
/// button the shell recognises, and it can only recognise the real system one. Offering it back
/// means answering <c>WM_NCHITTEST</c> with <c>HTMAXBUTTON</c>, which hands that button's input to
/// the non-client path and takes its hover and pressed states with it. A live hover state on a
/// button people click every day is worth more than a flyout, so the buttons stay ordinary WPF
/// buttons. Dragging to a screen edge and the Win+Arrow shortcuts are unaffected.
/// </para>
/// </summary>
public static class AppWindow
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    /// <summary>Set on a window to give it the application's chrome behaviour.</summary>
    public static readonly DependencyProperty ManagedProperty = DependencyProperty.RegisterAttached(
        "Managed", typeof(bool), typeof(AppWindow), new PropertyMetadata(false, OnManagedChanged));

    public static void SetManaged(DependencyObject element, bool value) => element.SetValue(ManagedProperty, value);

    public static bool GetManaged(DependencyObject element) => (bool)element.GetValue(ManagedProperty);

    /// <summary>
    /// The colour of the mark on the window bar. Null leaves it quiet.
    ///
    /// <para>
    /// The recorder binds this to whether a capture device may still be live, so the red indicator
    /// is on the title bar as well, and is gone the moment capture is.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.RegisterAttached(
        "Accent", typeof(System.Windows.Media.Brush), typeof(AppWindow), new PropertyMetadata(null));

    public static void SetAccent(DependencyObject element, System.Windows.Media.Brush? value) =>
        element.SetValue(AccentProperty, value);

    public static System.Windows.Media.Brush? GetAccent(DependencyObject element) =>
        (System.Windows.Media.Brush?)element.GetValue(AccentProperty);

    private static void OnManagedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || !Equals(e.NewValue, true))
        {
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.AddHook(Hook);
            }
        };

        // The caption buttons are ordinary buttons in the template, so they need the commands the
        // system caption would have given them.
        Bind(window, SystemCommands.MinimizeWindowCommand, () => SystemCommands.MinimizeWindow(window));
        Bind(window, SystemCommands.MaximizeWindowCommand, () => SystemCommands.MaximizeWindow(window));
        Bind(window, SystemCommands.RestoreWindowCommand, () => SystemCommands.RestoreWindow(window));
        Bind(window, SystemCommands.CloseWindowCommand, window.Close);
    }

    private static void Bind(Window window, ICommand command, Action execute) =>
        window.CommandBindings.Add(new CommandBinding(command, (_, _) => execute()));

    private static IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo)
        {
            ConstrainToWorkArea(hwnd, lParam);
        }

        return IntPtr.Zero;
    }

    /// <summary>Maximise to the monitor's work area, so the taskbar stays visible.</summary>
    private static void ConstrainToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        MinMaxInfo minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // Relative to the monitor, not the desktop: a secondary screen left of the primary has
        // negative coordinates, and using them directly puts the window off the edge of the world.
        minMax.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        minMax.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        minMax.MaxSize.X = info.Work.Right - info.Work.Left;
        minMax.MaxSize.Y = info.Work.Bottom - info.Work.Top;

        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }
}
