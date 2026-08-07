namespace EchoForge.App.Library;

/// <summary>The outcome of a clipboard write. A failure is reported, never thrown at the user.</summary>
public sealed record ClipboardResult(bool Succeeded, string Message)
{
    public static ClipboardResult Ok() => new(true, "Copied to the clipboard.");

    public static ClipboardResult Fail(string message) => new(false, message);
}

/// <summary>
/// Writing text to the clipboard, behind an interface so the handoff view model can be tested without
/// a real clipboard — and so the one place that touches the clipboard is the one place that has to
/// worry about it being busy.
/// </summary>
public interface IClipboardWriter
{
    /// <summary>
    /// Places <paramref name="text"/> on the clipboard. Never throws for an ordinary failure (the
    /// clipboard being held by another process); returns a failed result instead. The text is never
    /// logged.
    /// </summary>
    ClipboardResult TrySetText(string text);
}
