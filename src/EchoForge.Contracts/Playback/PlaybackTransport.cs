namespace EchoForge.Contracts.Playback;

/// <summary>The interleaved PCM16 format a device is asked to render.</summary>
public readonly record struct PlaybackFormat(int SampleRate, int Channels)
{
    public int BytesPerFrame => Channels * 2;
}

/// <summary>Where a transport is. One enum, because the UI shows exactly one of these at a time.</summary>
public enum PlaybackState
{
    /// <summary>Nothing opened yet.</summary>
    Idle,

    /// <summary>The aligned derivative is being built. Long the first time, instant afterwards.</summary>
    Preparing,

    /// <summary>Audio is loaded and positioned, and nothing is coming out of the speakers.</summary>
    Ready,

    Playing,

    Paused,

    /// <summary>Playback ran to the end of the meeting on its own.</summary>
    Ended,

    /// <summary>Preparation or the output device failed. <c>Message</c> says what a user can do.</summary>
    Failed,
}

/// <summary>Why a playback device stopped being usable. Never carries a path or a device serial.</summary>
public sealed record PlaybackFailure(string Code, string Message);

/// <summary>
/// Raised when a device fails while open. Playback surfaces it rather than falling silent, because
/// a transport that shows "playing" with no sound is worse than one that says the device went away.
/// </summary>
public sealed class PlaybackFailedEventArgs(PlaybackFailure failure) : EventArgs
{
    public PlaybackFailure Failure { get; } = failure;
}

/// <summary>Thrown when a device cannot be opened at all. The transport turns it into a state.</summary>
public sealed class PlaybackDeviceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public PlaybackFailure ToFailure() => new(Code, Message);
}

/// <summary>
/// Fills device buffers on demand.
///
/// <para>
/// Pull rather than push, so the transport — not the device — owns where in the meeting the next
/// frame comes from. A seek is then a change to one number, and no audio has to be re-queued for
/// the logical position to be correct immediately.
/// </para>
/// </summary>
public interface IPlaybackRenderer
{
    /// <summary>
    /// Writes up to <paramref name="frames"/> interleaved frames into <paramref name="destination"/>.
    /// Returns how many were written; fewer than asked means the end of the meeting.
    /// </summary>
    int Render(Span<short> destination, int frames);
}

/// <summary>
/// An audio output device, reduced to what a meeting transport needs.
///
/// <para>
/// Deliberately small and deliberately abstract. The routine tests must not need a sound card, and
/// nothing about which frame is logically playing may depend on a driver having been reached.
/// </para>
/// </summary>
public interface IPlaybackDevice : IDisposable
{
    /// <summary>Prepares the device. Throws <see cref="PlaybackDeviceException"/> when it cannot.</summary>
    void Open(PlaybackFormat format, IPlaybackRenderer renderer);

    void Play();

    void Pause();

    void Stop();

    /// <summary>
    /// Discards anything already handed to the device but not yet heard.
    ///
    /// <para>
    /// What makes a seek audible promptly instead of after the output buffer drains. Without it a
    /// jump would still be logically exact and would play a fraction of a second of the previous
    /// moment first.
    /// </para>
    /// </summary>
    void Flush();

    /// <summary>
    /// Frames handed to the device that have not reached the speakers yet, or 0 when the device
    /// cannot say. Subtracted from the read cursor so the reported position is what is audible.
    /// </summary>
    long PendingFrames { get; }

    bool IsPlaying { get; }

    event EventHandler<PlaybackFailedEventArgs>? Failed;
}
