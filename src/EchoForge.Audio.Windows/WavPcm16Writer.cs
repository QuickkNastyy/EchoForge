using EchoForge.Contracts.Audio;

namespace EchoForge.Audio.Windows;

/// <summary>
/// Streams PCM16 frames into a RIFF/WAVE file, patching the header on close.
///
/// <para>
/// The header is written up front with placeholder sizes so that a process kill leaves a
/// file whose data is still recoverable from its length. <see cref="WavPcm16Reader.Repair"/>
/// completes that recovery. Nothing here rewrites audio that has already been written.
/// </para>
/// </summary>
public sealed class WavPcm16Writer : IDisposable
{
    /// <summary>Canonical PCM16 RIFF header length.</summary>
    public const int HeaderBytes = 44;

    private readonly FileStream _stream;
    private readonly CaptureFormat _format;
    private bool _closed;

    public WavPcm16Writer(string path, CaptureFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(format);
        if (format.BitsPerSample != 16)
        {
            throw new ArgumentException("Source chunks are written as PCM16.", nameof(format));
        }

        _format = format;
        Path = path;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        WriteHeader(_stream, format, dataBytes: 0);
    }

    public string Path { get; }

    /// <summary>Frames written so far, including inserted silence.</summary>
    public long FramesWritten { get; private set; }

    /// <summary>Appends PCM16 frames exactly as supplied.</summary>
    public void WriteFrames(ReadOnlySpan<byte> pcm16, long frameCount)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _stream.Write(pcm16);
        FramesWritten += frameCount;
    }

    /// <summary>Appends digital silence. Used when the device position says frames are missing.</summary>
    public void WriteSilence(long frameCount)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (frameCount <= 0)
        {
            return;
        }

        int bytesPerFrame = _format.BytesPerFrame;
        Span<byte> zeros = stackalloc byte[4096];
        zeros.Clear();

        long remaining = frameCount * bytesPerFrame;
        while (remaining > 0)
        {
            int take = (int)Math.Min(zeros.Length, remaining);
            _stream.Write(zeros[..take]);
            remaining -= take;
        }

        FramesWritten += frameCount;
    }

    /// <summary>
    /// Flushes buffered data to durable storage. Called on the flush cadence and at every
    /// pause, stop, device, and power transition.
    /// </summary>
    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Patches the header with the real sizes, flushes, and closes. After this the file is
    /// a valid, immutable source chunk.
    /// </summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        long dataBytes = FramesWritten * _format.BytesPerFrame;
        _stream.Flush();
        _stream.Position = 0;
        WriteHeader(_stream, _format, dataBytes);
        _stream.Flush(flushToDisk: true);
        _stream.Dispose();
        _closed = true;
    }

    public void Dispose() => Close();

    internal static void WriteHeader(Stream stream, CaptureFormat format, long dataBytes)
    {
        int byteRate = format.SampleRate * format.BytesPerFrame;
        Span<byte> header = stackalloc byte[HeaderBytes];

        "RIFF"u8.CopyTo(header[..4]);
        BitConverter.TryWriteBytes(header[4..8], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..12]);
        "fmt "u8.CopyTo(header[12..16]);
        BitConverter.TryWriteBytes(header[16..20], 16u);
        BitConverter.TryWriteBytes(header[20..22], (ushort)1);
        BitConverter.TryWriteBytes(header[22..24], (ushort)format.Channels);
        BitConverter.TryWriteBytes(header[24..28], (uint)format.SampleRate);
        BitConverter.TryWriteBytes(header[28..32], (uint)byteRate);
        BitConverter.TryWriteBytes(header[32..34], (ushort)format.BytesPerFrame);
        BitConverter.TryWriteBytes(header[34..36], (ushort)format.BitsPerSample);
        "data"u8.CopyTo(header[36..40]);
        BitConverter.TryWriteBytes(header[40..44], (uint)dataBytes);

        stream.Write(header);
    }
}
