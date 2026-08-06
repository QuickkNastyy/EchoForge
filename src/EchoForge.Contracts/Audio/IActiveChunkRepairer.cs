namespace EchoForge.Contracts.Audio;

/// <summary>What happened to an abandoned active chunk during recovery.</summary>
/// <param name="Repaired">Whether a valid chunk was produced.</param>
/// <param name="FrameCount">Frames retained after trimming any incomplete trailing frame.</param>
/// <param name="TrimmedBytes">Bytes discarded because they did not form a whole frame.</param>
/// <param name="Problem">Why repair failed, or null.</param>
public sealed record ChunkRepairOutcome(bool Repaired, long FrameCount, int TrimmedBytes, string? Problem);

/// <summary>
/// Result of independently decoding a finalized chunk.
///
/// <para>
/// The format is read back <em>from the file</em>, not from any metadata, so a metadata record
/// can be checked against what the audio actually is rather than trusted about it.
/// </para>
/// </summary>
public sealed record ChunkValidation(
    bool IsValid,
    long FrameCount,
    string? Problem,
    int SampleRate = 0,
    int Channels = 0);

/// <summary>
/// Repairs and validates source chunks on behalf of recovery.
///
/// <para>
/// Abstracted so that recovery — which is storage logic — does not depend on the audio layer,
/// and so recovery can be driven by a fake that injects damaged files without needing real WAVs.
/// </para>
/// </summary>
public interface IActiveChunkRepairer
{
    /// <summary>
    /// Completes an active chunk whose header was never patched. Trailing bytes that do not form
    /// a whole frame are trimmed. Audio that was already durable is preserved; nothing is invented.
    /// </summary>
    ChunkRepairOutcome Repair(string partPath, CaptureFormat format);

    /// <summary>Decodes a finalized chunk and checks its declared sizes against its length.</summary>
    ChunkValidation Validate(string chunkPath);
}
