using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace EchoForge.App;

/// <summary>
/// The tray icon.
///
/// <para>
/// It renders its own icon from the view model's state rather than holding any state of its own,
/// so it cannot show "recording" while capture has stopped, or the reverse. Both the window and
/// the tray read the same authoritative state from the recorder.
/// </para>
/// </summary>
public sealed class TrayIndicator : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainViewModel _viewModel;
    private readonly Action _showWindow;
    private bool? _lastRecording;
    private bool _disposed;

    public TrayIndicator(MainViewModel viewModel, Action showWindow)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(showWindow);

        _viewModel = viewModel;
        _showWindow = showWindow;

        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "EchoForge",
            ContextMenuStrip = BuildMenu(),
        };

        _icon.DoubleClick += (_, _) => _showWindow();
        _viewModel.PropertyChanged += OnViewModelChanged;
        Redraw(force: true);
    }

    /// <summary>Supplied by the composition root so tray Exit uses the same shutdown path.</summary>
    public ShutdownCoordinator? Shutdown { get; set; }

    private ContextMenuStrip BuildMenu()
    {
        ContextMenuStrip menu = new();
        menu.Items.Add("Open EchoForge", null, (_, _) => _showWindow());
        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem stop = new("Stop recording");
        stop.Click += (_, _) => _viewModel.StopCommand.Execute(null);
        menu.Items.Add(stop);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit EchoForge", null, (_, _) => ExitThroughCoordinator());

        menu.Opening += (_, _) => stop.Enabled = _viewModel.IsRecording || _viewModel.IsPaused;
        return menu;
    }

    /// <summary>
    /// Tray Exit takes the same route as the close button, so it cannot bypass the save.
    /// This is a framework event adapter, hence async void.
    /// </summary>
    private async void ExitThroughCoordinator()
    {
        if (Shutdown is null)
        {
            System.Windows.Application.Current?.Shutdown();
            return;
        }

        if (await Shutdown.TryShutdownAsync().ConfigureAwait(true))
        {
            System.Windows.Application.Current?.Shutdown();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TrayText)
            or nameof(MainViewModel.IndicatorVisible)
            or nameof(MainViewModel.IsRecording)
            or nameof(MainViewModel.IsPaused))
        {
            Redraw(force: false);
        }
    }

    private void Redraw(bool force)
    {
        // NotifyIcon.Text is capped at 63 characters by the shell.
        string text = _viewModel.TrayText;
        _icon.Text = text.Length > 63 ? text[..63] : text;

        // Matches the window indicator: lit while any capture source may still be live.
        bool recording = _viewModel.IndicatorVisible;
        if (!force && _lastRecording == recording)
        {
            return;
        }

        _lastRecording = recording;

        Icon? previous = _icon.Icon;
        _icon.Icon = RenderIcon(recording);
        previous?.Dispose();
    }

    // DllImport rather than LibraryImport: the source generator requires unsafe code, and one
    // handle-releasing call does not justify enabling it across the UI project.
#pragma warning disable SYSLIB1054
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
#pragma warning restore SYSLIB1054

    /// <summary>
    /// Draws a filled red dot while capturing, a hollow outline otherwise.
    ///
    /// <para>
    /// <c>Icon.FromHandle</c> does not own the HICON that <c>GetHicon</c> creates, so the native
    /// handle has to be destroyed explicitly. Redrawing on every state change would otherwise leak
    /// a GDI handle each time.
    /// </para>
    /// </summary>
    private static Icon RenderIcon(bool recording)
    {
        using Bitmap bitmap = new(16, 16);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            Rectangle bounds = new(2, 2, 11, 11);
            if (recording)
            {
                using SolidBrush brush = new(Color.FromArgb(226, 69, 61));
                graphics.FillEllipse(brush, bounds);
            }
            else
            {
                using Pen pen = new(Color.FromArgb(132, 150, 166), 2f);
                graphics.DrawEllipse(pen, bounds);
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // Clone so the managed Icon owns its own copy and the native handle can go now.
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
