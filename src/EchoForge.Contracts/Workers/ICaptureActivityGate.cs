namespace EchoForge.Contracts.Workers;

/// <summary>
/// Asks whether capture is live. Recording always has priority: no GPU-heavy processing job may
/// start, or keep running, while audio is being captured.
///
/// <para>
/// This is the seam that keeps that rule enforceable before the UI exists. The supervisor refuses
/// to launch while the gate says capture is live and answers <see cref="WorkerOutcome.Busy"/>, so
/// a later queue can be built on top without relaxing the rule to add it.
/// </para>
/// </summary>
public interface ICaptureActivityGate
{
    /// <summary>
    /// True while a recording may be writing audio. Erring towards true is correct: a delayed
    /// transcript costs time, a starved capture thread costs a meeting.
    /// </summary>
    bool IsCaptureActive { get; }
}

/// <summary>
/// The gate for a host with no recorder attached — tests, tools, and the worker smoke tests.
/// Named for what it asserts, so nobody uses it in the app by accident.
/// </summary>
public sealed class NoRecordingInProgressGate : ICaptureActivityGate
{
    public static readonly NoRecordingInProgressGate Instance = new();

    public bool IsCaptureActive => false;
}
