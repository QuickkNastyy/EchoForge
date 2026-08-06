using EchoForge.Contracts.Audio;

namespace EchoForge.Audio.Windows;

/// <summary>
/// The real chunk repairer, backed by the independent WAV reader.
///
/// <para>
/// Exists so recovery — which is storage logic — can repair and validate audio without the
/// storage layer depending on the audio layer.
/// </para>
/// </summary>
public sealed class WavChunkRepairer : IActiveChunkRepairer
{
    public ChunkRepairOutcome Repair(string partPath, CaptureFormat format)
    {
        WavRepairResult result = WavPcm16Reader.Repair(partPath, format);
        return new ChunkRepairOutcome(result.Repaired, result.FrameCount, result.TrimmedBytes, result.Problem);
    }

    public ChunkValidation Validate(string chunkPath)
    {
        WavValidation result = WavPcm16Reader.Validate(chunkPath);
        return new ChunkValidation(result.IsValid, result.FrameCount, result.Problem);
    }
}
