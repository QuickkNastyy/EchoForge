using EchoForge.Contracts.Audio;

namespace EchoForge.Infrastructure.Processing;

/// <summary>Why a source chunk could not be decoded. Processing stops; nothing is skipped.</summary>
public sealed class SourceAudioException(string message) : Exception(message);

/// <summary>
/// Read-only streaming access to a finalized PCM16 chunk, for building derivatives.
///
/// <para>
/// Deliberately separate from the capture layer's reader and writer. Infrastructure does not
/// reference the audio project — recovery repairs chunks through an interface for the same
/// reason — and duplicating a forty-four byte header parse costs less than making the processing
/// pipeline depend on the code that produced the files it is checking.
/// </para>
///
/// <para>
/// Everything here opens files for reading and nothing writes. A source chunk plus its validated
/// metadata is canonical; a processing stage that repaired one in place would destroy the only
/// authoritative record of what was captured.
/// </para>
/// </summary>
public sealed class PcmSourceReader : IDisposable
{
    private const int HeaderBytes = 44;

    private readonly FileStream _stream;

    private PcmSourceReader(FileStream stream, CaptureFormat format, long frames)
    {
        _stream = stream;
        Format = format;
        Frames = frames;
    }

    public CaptureFormat Format { get; }

    /// <summary>Frames the data chunk actually holds, not what any metadata claims.</summary>
    public long Frames { get; }

    /// <summary>
    /// Opens a chunk and checks it against the metadata that describes it.
    ///
    /// <para>
    /// A disagreement is fatal to the job rather than something to work around. The alternative
    /// is a derivative silently shorter or longer than the timeline says, and every transcript
    /// timestamp after that point being wrong by the difference.
    /// </para>
    /// </summary>
    public static PcmSourceReader Open(string path, int expectedSampleRate, int expectedChannels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SourceAudioException($"a source chunk could not be opened ({ex.GetType().Name})");
        }

        try
        {
            if (stream.Length < HeaderBytes)
            {
                throw new SourceAudioException("a source chunk is shorter than a RIFF header");
            }

            Span<byte> header = stackalloc byte[HeaderBytes];
            stream.ReadExactly(header);

            if (!header[..4].SequenceEqual("RIFF"u8) ||
                !header[8..12].SequenceEqual("WAVE"u8) ||
                !header[12..16].SequenceEqual("fmt "u8) ||
                !header[36..40].SequenceEqual("data"u8))
            {
                throw new SourceAudioException("a source chunk is not a canonical PCM RIFF file");
            }

            if (BitConverter.ToUInt16(header[20..22]) != 1)
            {
                throw new SourceAudioException("a source chunk is not uncompressed PCM");
            }

            int channels = BitConverter.ToUInt16(header[22..24]);
            int sampleRate = (int)BitConverter.ToUInt32(header[24..28]);
            int bits = BitConverter.ToUInt16(header[34..36]);

            if (bits != 16)
            {
                throw new SourceAudioException($"a source chunk is {bits}-bit; EchoForge records PCM16");
            }

            if (channels < 1 || sampleRate < 1)
            {
                throw new SourceAudioException("a source chunk declares an unusable format");
            }

            if (sampleRate != expectedSampleRate || channels != expectedChannels)
            {
                throw new SourceAudioException(
                    $"a source chunk is {sampleRate} Hz / {channels} ch but its metadata says " +
                    $"{expectedSampleRate} Hz / {expectedChannels} ch");
            }

            long dataBytes = stream.Length - HeaderBytes;
            int bytesPerFrame = channels * 2;

            if (dataBytes % bytesPerFrame != 0)
            {
                throw new SourceAudioException("a source chunk does not hold a whole number of frames");
            }

            return new PcmSourceReader(stream, new CaptureFormat(sampleRate, channels, bits), dataBytes / bytesPerFrame);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the whole chunk as mono.
    ///
    /// <para>
    /// One chunk at a time, never a whole meeting: sixty seconds of 48 kHz stereo is under six
    /// megabytes as mono, and the working set stays flat however long the recording is.
    /// </para>
    /// </summary>
    public async Task<short[]> ReadMonoAsync(CancellationToken cancellationToken)
    {
        short[] mono = new short[Frames];
        if (Frames == 0)
        {
            return mono;
        }

        int channels = Format.Channels;
        int framesPerBlock = Math.Max(1, 64 * 1024 / channels);
        byte[] buffer = new byte[framesPerBlock * channels * 2];

        _stream.Position = HeaderBytes;
        long written = 0;

        while (written < Frames)
        {
            int wantFrames = (int)Math.Min(framesPerBlock, Frames - written);
            int wantBytes = wantFrames * channels * 2;

            await _stream.ReadExactlyAsync(buffer.AsMemory(0, wantBytes), cancellationToken).ConfigureAwait(false);

            ReadOnlySpan<short> interleaved = System.Runtime.InteropServices.MemoryMarshal
                .Cast<byte, short>(buffer.AsSpan(0, wantBytes));

            EchoForge.Core.Processing.AudioResampler.MixToMono(
                interleaved, channels, mono.AsSpan((int)written, wantFrames));

            written += wantFrames;
        }

        return mono;
    }

    public void Dispose() => _stream.Dispose();
}
