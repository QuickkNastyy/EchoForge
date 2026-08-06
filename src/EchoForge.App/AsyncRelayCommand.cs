using System.IO;
using System.Windows.Input;

namespace EchoForge.App;

/// <summary>
/// A gate shared by every lifecycle command, so only one of Start, Pause, Resume, Stop, or a
/// recovery scan can run at a time and the others visibly disable while it does.
/// </summary>
public sealed class OperationGate
{
    private int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public event EventHandler? Changed;

    public bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Exit()
    {
        Volatile.Write(ref _busy, 0);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// A command whose work runs off the UI thread.
///
/// <para>
/// Start, Pause, and Stop join capture threads, finalize WAVs, hash them, and fsync the journal.
/// Doing that inline from a WPF command freezes the window — including the recording indicator —
/// at exactly the moment the user most needs to see it. The work moves to a background thread and
/// only the resulting state change comes back to the dispatcher.
/// </para>
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly OperationGate _gate;
    private readonly Action<string>? _onError;

    public AsyncRelayCommand(
        Func<Task> execute,
        OperationGate gate,
        Func<bool>? canExecute = null,
        Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(gate);

        _execute = execute;
        _gate = gate;
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_gate.IsBusy && (_canExecute?.Invoke() ?? true);

    /// <summary>
    /// ICommand.Execute is void, so this is the one permitted async void boundary: it is a
    /// framework event adapter. Every exception is caught and surfaced as a notice.
    /// </summary>
    public async void Execute(object? parameter)
    {
        if (!_gate.TryEnter())
        {
            return;
        }

        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _onError?.Invoke($"{Humanise(ex)}");
        }
        finally
        {
            _gate.Exit();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static string Humanise(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "EchoForge does not have permission to write to the session folder.",
        IOException io => $"A file could not be written: {io.Message}",
        InvalidOperationException invalid => invalid.Message,
        _ => $"Something went wrong: {ex.GetType().Name}",
    };
}
