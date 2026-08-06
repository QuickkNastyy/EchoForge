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

    private ContextMenuStrip BuildMenu()
    {
        ContextMenuStrip menu = new();
        menu.Items.Add("Open EchoForge", null, (_, _) => _showWindow());
        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem stop = new("Stop recording");
        stop.Click += (_, _) => _viewModel.StopCommand.Execute(null);
        menu.Items.Add(stop);

        menu.Opening += (_, _) => stop.Enabled = _viewModel.IsRecording || _viewModel.IsPaused;
        return menu;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TrayText)
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

        bool recording = _viewModel.IsRecording;
        if (!force && _lastRecording == recording)
        {
            return;
        }

        _lastRecording = recording;

        Icon? previous = _icon.Icon;
        _icon.Icon = RenderIcon(recording);
        previous?.Dispose();
    }

    /// <summary>Draws a filled red dot while capturing, a hollow outline otherwise.</summary>
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

        return Icon.FromHandle(bitmap.GetHicon());
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
