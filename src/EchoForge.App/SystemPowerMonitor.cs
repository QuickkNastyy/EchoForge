using EchoForge.Contracts.Recording;
using Microsoft.Win32;

namespace EchoForge.App;

/// <summary>
/// Bridges Windows power notifications to <see cref="IPowerMonitor"/>.
///
/// <para>
/// Suspend handlers run on a system thread and Windows gives them very little time, so this does
/// nothing but forward the event; finalizing the epoch is the recorder's job.
/// </para>
/// </summary>
public sealed class SystemPowerMonitor : IPowerMonitor
{
    private bool _started;
    private bool _disposed;

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        _started = true;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Suspending?.Invoke(this, EventArgs.Empty);
                break;

            case PowerModes.Resume:
                Resumed?.Invoke(this, EventArgs.Empty);
                break;

            default:
                break;
        }
    }

    /// <summary>Logging off or shutting down deserves the same treatment as a suspend.</summary>
    private void OnSessionEnding(object sender, SessionEndingEventArgs e) =>
        Suspending?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_started)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
        }
    }
}
