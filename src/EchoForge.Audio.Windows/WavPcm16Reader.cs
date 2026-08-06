using EchoForge.Contracts.Audio;

namespace EchoForge.Audio.Windows;

/// <summary>Result of independently validating a finalized WAV file.</summary>
/// <param name="IsValid">Whether the file parses and its sizes agree.</param>
/// <param name="Format">The format read back from the file.</param>
/// <param name="FrameCount">Frames the data chunk actually contains.</param>
/// <param name="Problem">Why validation failed, or null.</param>
public sealed record WavValidation(bool IsValid, CaptureFormat? Format, long FrameCount, string? Problem)
{
    public TimeSpan Duration => Format is null
        ? TimeSpan.Zero
        : Format.DurationForFrames(FrameCount);
}

/// <summary>Outcome of repairing an active <c>.part.wav</c> after an interrupted run.</summary>
/// <param name="Repaired">Whether a valid file was produced.</param>
/// <param name="FrameCount">Frames retained after trimming any incomplete trailing frame.</param>
/// <param name="TrimmedBytes">Bytes discarded because they did not form a whole frame.</param>
/// <param name="Problem">Why repair failed, or null.</param>
public sealed record WavRepairResult(bool Repaired, long FrameCount, int TrimmedBytes, string? Problem);

/// <summary>
/// Reads and repairs PCM16 RIFF files without going through the capture stack, so that
/// Phase 0 validation is genuinely independent of the code that wrote them.
/// </summary>
public static class WavPcm16Reader
{
    /// <summary>Parses a WAV file and checks that its declared sizes match its length.</summary>
    public static WavValidation Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < WavPcm16Writer.HeaderBytes)
        {
            return new WavValidation(false, null, 0, "file is shorter than a RIFF header");
        }

        Span<byte> header = stackalloc byte[WavPcm16Writer.HeaderBytes];
        stream.ReadExactly(header);

        if (!header[..4].SequenceEqual("RIFF"u8) ||
            !header[8..12].SequenceEqual("WAVE"u8) ||
            !header[12..16].SequenceEqual("fmt "u8) ||
            !header[36..40].SequenceEqual("data"u8))
        {
            return new WavValidation(false, null, 0, "not a canonical 44-byte PCM RIFF file");
        }

        ushort audioFormat = BitConverter.ToUInt16(header[20..22]);
        if (audioFormat != 1)
        {
            return new WavValidation(false, null, 0, $"audio format {audioFormat} is not PCM");
        }

        int channels = BitConverter.ToUInt16(header[22..24]);
        int sampleRate = (int)BitConverter.ToUInt32(header[24..28]);
        int bits = BitConverter.ToUInt16(header[34..36]);
        uint declaredData = BitConverter.ToUInt32(header[40..44]);
        uint declaredRiff = BitConverter.ToUInt32(header[4..8]);

        CaptureFormat format = new(sampleRate, channels, bits);
        long actualData = stream.Length - WavPcm16Writer.HeaderBytes;

        if (declaredData != actualData)
        {
            return new WavValidation(false, format, 0,
                $"data chunk declares {declaredData} bytes but the file holds {actualData}");
        }

        if (declaredRiff != 36 + actualData)
        {
            return new WavValidation(false, format, 0, "RIFF size does not match the data chunk");
        }

        if (format.BytesPerFrame == 0 || actualData % format.BytesPerFrame != 0)
        {
            return new WavValidation(false, format, 0, "data length is not a whole number of frames");
        }

        return new WavValidation(true, format, actualData / format.BytesPerFrame, null);
    }

    /// <summary>
    /// Completes an active chunk whose header was never patched because the process died.
    ///
    /// <para>
    /// Any trailing bytes that do not form a whole frame are trimmed, then the header is
    /// patched to the real length. Audio that was already durable is preserved; nothing is
    /// invented to fill the tail.
    /// </para>
    /// </summary>
    public static WavRepairResult Repair(string path, CaptureFormat expectedFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectedFormat);

        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length < WavPcm16Writer.HeaderBytes)
        {
            return new WavRepairResult(false, 0, 0, "file does not contain a complete header");
        }

        long dataBytes = stream.Length - WavPcm16Writer.HeaderBytes;
        int bytesPerFrame = expectedFormat.BytesPerFrame;
        int trimmed = (int)(dataBytes % bytesPerFrame);
        long keptBytes = dataBytes - trimmed;

        if (trimmed > 0)
        {
            stream.SetLength(WavPcm16Writer.HeaderBytes + keptBytes);
        }

        stream.Position = 0;
        WavPcm16Writer.WriteHeader(stream, expectedFormat, keptBytes);
        stream.Flush(flushToDisk: true);

        return new WavRepairResult(true, keptBytes / bytesPerFrame, trimmed, null);
    }
}
