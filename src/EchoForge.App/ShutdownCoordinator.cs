using System.Windows;

namespace EchoForge.App;

/// <summary>What the user chose when asked about a live or paused session.</summary>
public enum ShutdownDecision
{
    /// <summary>Nothing was running; close immediately.</summary>
    NothingToSave,

    /// <summary>Stop the recording and save it, then close.</summary>
    SaveAndClose,

    /// <summary>Stay open.</summary>
    Cancel,
}

/// <summary>Asks the user what to do about a session that is still open. Abstracted for tests.</summary>
public interface IShutdownPrompt
{
    ShutdownDecision Ask(bool isRecording);
}

/// <summary>
/// The single path out of the application.
///
/// <para>
/// Closing used to fire Stop and continue immediately, so the window could disappear — and the
/// process exit — while chunks were still being finalized and the snapshot written. Everything
/// that ends the app now goes through here: the window's close button, the tray's Exit, and
/// Windows shutting down. The first close is cancelled, the user is asked, finalization is
/// awaited, and only a durable save closes the app. A failure keeps it open and says why.
/// </para>
/// </summary>
public sealed class ShutdownCoordinator
{
    private readonly MainViewModel _viewModel;
    private readonly IShutdownPrompt _prompt;
    private readonly Action<string> _onError;
    private int _running;

    public ShutdownCoordinator(MainViewModel viewModel, IShutdownPrompt prompt, Action<string> onError)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(onError);

        _viewModel = viewModel;
        _prompt = prompt;
        _onError = onError;
    }

    /// <summary>True once a shutdown has been approved, so the second close is allowed through.</summary>
    public bool IsShuttingDown { get; private set; }

    /// <summary>
    /// Runs the shutdown sequence. Returns true when the app may now close.
    ///
    /// <para>
    /// Re-entrant calls return false rather than starting a second sequence, which is what stops
    /// the close handler recursing when finalization completes and closes the window again.
    /// </para>
    /// </summary>
    public async Task<bool> TryShutdownAsync()
    {
        if (IsShuttingDown)
        {
            return true;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            bool hasOpenSession = _viewModel.IsRecording || _viewModel.IsPaused;
            if (hasOpenSession)
            {
                ShutdownDecision decision = _prompt.Ask(_viewModel.IsRecording);
                if (decision == ShutdownDecision.Cancel)
                {
                    return false;
                }
            }

            // Shows "Stopping…" then "Saving…" from the controller's own capture phase.
            bool saved = await _viewModel.FinalizeForShutdownAsync().ConfigureAwait(true);
            if (!saved)
            {
                _onError("EchoForge could not finish saving this recording, so it has stayed open. " +
                         "Your audio is on disk; try stopping again, or check free space.");
                return false;
            }

            IsShuttingDown = true;
            return true;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}

/// <summary>The real dialog. Wording differs for a live recording and a paused one.</summary>
public sealed class DialogShutdownPrompt(Window owner) : IShutdownPrompt
{
    public ShutdownDecision Ask(bool isRecording)
    {
        string message = isRecording
            ? "A recording is still running. Stop it and save before closing?"
            : "This recording is paused and has not been saved. Stop it and save before closing?";

        MessageBoxResult answer = System.Windows.MessageBox.Show(
            owner,
            message,
            isRecording ? "EchoForge is recording" : "EchoForge is paused",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        return answer == MessageBoxResult.OK ? ShutdownDecision.SaveAndClose : ShutdownDecision.Cancel;
    }
}
