using System.Buffers;
using System.Collections.Concurrent;
using EchoForge.Contracts.Audio;

namespace EchoForge.Audio.Windows;

/// <summary>One captured packet copied off the capture thread, with its clock anchors.</summary>
public sealed class CapturedPacket
{
    internal CapturedPacket(PacketHeader header, byte[] buffer, int byteCount)
    {
        Header = header;
        Buffer = buffer;
        ByteCount = byteCount;
    }

    public PacketHeader Header { get; }

    internal byte[] Buffer { get; }

    public int ByteCount { get; }

    public ReadOnlySpan<byte> Payload => Buffer.AsSpan(0, ByteCount);
}

/// <summary>
/// A queue between the capture thread and the writer task, bounded by seconds of audio.
///
/// <para>
/// Enqueue never blocks and never waits on disk. If the writer falls far enough behind that
/// the bound is reached, the packet is dropped and counted — the plan requires overflow to
/// surface as a recorded discontinuity and a visible error, never a silent loss.
/// </para>
/// </summary>
public sealed class BoundedAudioQueue : IDisposable
{
    private readonly ConcurrentQueue<CapturedPacket> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly long _capacityFrames;

    private long _queuedFrames;
    private bool _disposed;

    public BoundedAudioQueue(CaptureFormat format, TimeSpan capacity)
    {
        ArgumentNullException.ThrowIfNull(format);
        _capacityFrames = format.FramesForDuration(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_capacityFrames);
    }

    /// <summary>Frames currently waiting to be written.</summary>
    public long QueuedFrames => Interlocked.Read(ref _queuedFrames);

    /// <summary>The bound, in frames.</summary>
    public long CapacityFrames => _capacityFrames;

    /// <summary>Packets dropped because the bound was reached.</summary>
    public long DroppedPackets { get; private set; }

    /// <summary>Frames dropped because the bound was reached.</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>Largest queue depth observed, for the Phase 0 bounded-queue evidence.</summary>
    public long PeakQueuedFrames { get; private set; }

    /// <summary>
    /// Copies a packet off the capture thread. Returns false when the packet was dropped.
    /// </summary>
    public bool TryEnqueue(in PacketHeader header, ReadOnlySpan<byte> payload)
    {
        long queued = Interlocked.Read(ref _queuedFrames);
        if (queued + header.FrameCount > _capacityFrames)
        {
            DroppedPackets++;
            DroppedFrames += header.FrameCount;
            return false;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(payload.Length, 1));
        payload.CopyTo(buffer);

        _queue.Enqueue(new CapturedPacket(header, buffer, payload.Length));
        long depth = Interlocked.Add(ref _queuedFrames, header.FrameCount);
        if (depth > PeakQueuedFrames)
        {
            PeakQueuedFrames = depth;
        }

        _available.Release();
        return true;
    }

    /// <summary>Takes the next packet, waiting up to <paramref name="timeout"/>.</summary>
    public CapturedPacket? TryDequeue(TimeSpan timeout)
    {
        if (!_available.Wait(timeout))
        {
            return null;
        }

        if (!_queue.TryDequeue(out CapturedPacket? packet))
        {
            return null;
        }

        Interlocked.Add(ref _queuedFrames, -packet.Header.FrameCount);
        return packet;
    }

    /// <summary>Returns a packet's pooled buffer once the writer is finished with it.</summary>
    public static void Release(CapturedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArrayPool<byte>.Shared.Return(packet.Buffer);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        while (_queue.TryDequeue(out CapturedPacket? packet))
        {
            Release(packet);
        }

        _available.Dispose();
        _disposed = true;
    }
}
