using EchoForge.Contracts.Workers;
using EchoForge.Core.Recording;

namespace EchoForge.Core.Transcripts;

/// <summary>
/// Reports capture activity from the recorder itself, so processing cannot start behind the
/// recorder's back.
///
/// <para>
/// It asks <see cref="RecordingController.CaptureMayBeLive"/> rather than
/// <see cref="RecordingController.IsCapturing"/> on purpose. The first is true while capture is
/// stopping as well as while it runs, and the whole point is to be conservative: a transcript
/// that waits a few seconds costs nothing, and a capture thread that loses the machine to an
/// inference job costs the meeting.
/// </para>
/// </summary>
public sealed class RecordingCaptureGate(RecordingController controller) : ICaptureActivityGate
{
    private readonly RecordingController _controller =
        controller ?? throw new ArgumentNullException(nameof(controller));

    public bool IsCaptureActive => _controller.CaptureMayBeLive;
}
