using System.Runtime.InteropServices;
using EchoForge.Contracts.Playback;

namespace EchoForge.Infrastructure.Playback;

/// <summary>
/// Random-access reading of the aligned playback derivative.
///
/// <para>
/// Opened read-shared and never written. The derivative is a rebuildable projection of the source
/// chunks, but while a meeting is open it is also the thing being played, and a reader that took
/// the file exclusively would stop the rebuild a reprocess needs.
/// </para>
///
/// <para>
/// Reads are by absolute frame, so a seek is a change to an argument rather than a stream state.
/// Positions past the end return nothing instead of throwing: the transport asks for a block at a
/// time and the last one is short by definition.
/// </para>
/// </summary>
public sealed class WavPlaybackAudioSource : IPlaybackAudioSource
{
    private const int HeaderBytes = 44;

    private readonly FileStream _stream;
    private readonly Lock _sync = new();
    private byte[] _buffer = [];
    private bool _disposed;

    private WavPlaybackAudioSource(FileStream stream, int sampleRate, int channels, long frames)
    {
        _stream = stream;
        SampleRate = sampleRate;
        Channels = channels;
        TotalFrames = frames;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public long TotalFrames { get; }

    public double DurationSeconds => SampleRate <= 0 ? 0 : (double)TotalFrames / SampleRate;

    /// <summary>Opens a derivative. Throws <see cref="PlaybackDeviceException"/> if it is not one.</summary>
    public static WavPlaybackAudioSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlaybackDeviceException(
                "playback_audio_unreadable",
                "The prepared audio could not be opened. Preparing it again will rebuild it.");
        }

        try
        {
            if (stream.Length < HeaderBytes)
            {
                throw new PlaybackDeviceException("playback_audio_invalid", "The prepared audio is not usable.");
            }

            Span<byte> header = stackalloc byte[HeaderBytes];
            stream.ReadExactly(header);

            if (!header[..4].SequenceEqual("RIFF"u8) ||
                !header[8..12].SequenceEqual("WAVE"u8) ||
                BitConverter.ToUInt16(header[20..22]) != 1 ||
                BitConverter.ToUInt16(header[34..36]) != 16)
            {
                throw new PlaybackDeviceException("playback_audio_invalid", "The prepared audio is not usable.");
            }

            int channels = BitConverter.ToUInt16(header[22..24]);
            int sampleRate = (int)BitConverter.ToUInt32(header[24..28]);

            if (channels < 1 || sampleRate < 1)
            {
                throw new PlaybackDeviceException("playback_audio_invalid", "The prepared audio is not usable.");
            }

            long frames = (stream.Length - HeaderBytes) / (channels * 2);
            return new WavPlaybackAudioSource(stream, sampleRate, channels, frames);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public int Read(long startFrame, Span<short> destination, int frames)
    {
        if (frames <= 0 || startFrame < 0 || startFrame >= TotalFrames)
        {
            return 0;
        }

        int want = (int)Math.Min(frames, TotalFrames - startFrame);
        int samples = want * Channels;

        if (destination.Length < samples)
        {
            want = destination.Length / Channels;
            samples = want * Channels;
        }

        if (want <= 0)
        {
            return 0;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return 0;
            }

            int bytes = samples * 2;
            if (_buffer.Length < bytes)
            {
                _buffer = new byte[bytes];
            }

            try
            {
                _stream.Position = HeaderBytes + (startFrame * Channels * 2);
                _stream.ReadExactly(_buffer.AsSpan(0, bytes));
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                // A derivative that vanished under us stops playback rather than emitting noise.
                return 0;
            }

            MemoryMarshal.Cast<byte, short>(_buffer.AsSpan(0, bytes)).CopyTo(destination[..samples]);
        }

        return want;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _stream.Dispose();
    }
}
