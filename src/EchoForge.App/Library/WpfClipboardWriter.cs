using System.Runtime.InteropServices;
using System.Threading;

namespace EchoForge.App.Library;

/// <summary>
/// The real clipboard, via WPF.
///
/// <para>
/// The Windows clipboard is a single shared resource any process can hold for a moment, so a write
/// can fail transiently even when nothing is wrong. This retries a small, fixed number of times and
/// then gives up with a message — it never blocks forever and never crashes the application over a
/// clipboard that happened to be busy. It must be called on the UI (STA) thread, which is where the
/// menu action that uses it already runs.
/// </para>
///
/// <para>
/// The text is handed to the OS and nothing else: it is never written to a log, a diagnostics bundle,
/// or anywhere on disk by this class.
/// </para>
/// </summary>
public sealed class WpfClipboardWriter : IClipboardWriter
{
    private const int Attempts = 5;
    private const int PauseMilliseconds = 60;

    public ClipboardResult TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                // copy: true flushes the data so it survives this process exiting, which is what a
                // user pasting into another app a moment later expects.
                System.Windows.Clipboard.SetDataObject(text, copy: true);
                return ClipboardResult.Ok();
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                if (attempt == Attempts)
                {
                    return ClipboardResult.Fail(
                        "The clipboard is in use by another application. Nothing was copied — try again, or save to a file instead.");
                }

                Thread.Sleep(PauseMilliseconds);
            }
        }

        return ClipboardResult.Fail("The clipboard could not be written.");
    }
}
