using System.Globalization;
using System.Security.Cryptography;
using EchoForge.Contracts.Audio;

namespace EchoForge.Audio.Windows;

/// <summary>
/// Turns a stream of captured packets into immutable fixed-duration PCM16 chunks.
///
/// <para>
/// The timeline is built from each packet's <see cref="PacketHeader.QpcPosition"/>, never from
/// when managed code observed the packet. When the QPC position runs ahead of the frames
/// written so far, the missing frames are written as explicit silence and recorded as a
/// discontinuity, so a chunk's frame count always equals its wall-clock duration. That
/// property is what lets the two tracks be aligned later without guessing.
/// </para>
///
/// <para>
/// <b>Why not device position.</b> Phase 0 measured a headset microphone whose device position
/// advanced 160 frames for every 480 frames delivered: the endpoint captures at 16 kHz and the
/// audio engine resamples up to the 48 kHz mix format. <see cref="PacketHeader.DevicePosition"/>
/// counts frames in the <em>device's</em> rate, not the mix format's, so it cannot be used as a
/// frame counter whenever the engine resamples. It is retained as a diagnostic and as a
/// corroborating discontinuity signal only.
/// </para>
/// </summary>
public sealed class PcmChunkWriter : IDisposable
{
    private readonly string _activeDirectory;
    private readonly string _chunksDirectory;
    private readonly CaptureFormat _format;
    private readonly SourceTrack _track;
    private readonly long _chunkFrames;
    private readonly long _flushIntervalFrames;
    private readonly List<AudioChunkMetadata> _completed = [];
    private readonly List<CaptureDiscontinuity> _pending = [];

    private readonly long _gapThresholdFrames;
    private readonly long _epochQpc;

    private WavPcm16Writer? _writer;
    private int _chunkIndex;
    private long _chunkStartSessionFrame;
    private long _sessionFrames;
    private long _framesSinceFlush;
    private long _lastDevicePosition;
    private bool _disposed;

    /// <param name="epochQpc">
    /// The shared session epoch. Both tracks must be given the same value: it is what makes
    /// t=0 mean the same instant on each, and therefore what makes them alignable at all.
    /// </param>
    public PcmChunkWriter(
        string trackDirectory,
        SourceTrack track,
        CaptureFormat format,
        long epochQpc,
        TimeSpan? chunkDuration = null,
        TimeSpan? flushInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackDirectory);
        ArgumentNullException.ThrowIfNull(format);

        _format = format;
        _track = track;
        _epochQpc = epochQpc;
        _activeDirectory = Path.Combine(trackDirectory, "active");
        _chunksDirectory = Path.Combine(trackDirectory, "chunks");
        Directory.CreateDirectory(_activeDirectory);
        Directory.CreateDirectory(_chunksDirectory);

        _chunkFrames = format.FramesForDuration(chunkDuration ?? TimeSpan.FromSeconds(60));
        _flushIntervalFrames = format.FramesForDuration(flushInterval ?? TimeSpan.FromSeconds(2));

        // QPC timestamps carry sub-millisecond jitter and packets are not perfectly evenly
        // spaced, so only a gap larger than this counts as missing audio. Below it, the frame
        // count is left to catch up on its own rather than churning tiny silences and drops.
        _gapThresholdFrames = format.FramesForDuration(TimeSpan.FromMilliseconds(30));
        if (_chunkFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkDuration), "Chunk duration must cover at least one frame.");
        }
    }

    /// <summary>Chunks finalized so far, in order. Finalized chunks are never rewritten.</summary>
    public IReadOnlyList<AudioChunkMetadata> CompletedChunks => _completed;

    /// <summary>Total frames placed on the session timeline, including inserted silence.</summary>
    public long SessionFrames => _sessionFrames;

    /// <summary>Frames of silence inserted to cover gaps the device position reported.</summary>
    public long SilenceFramesInserted { get; private set; }

    /// <summary>Discontinuities recorded on the chunk currently being written.</summary>
    public IReadOnlyList<CaptureDiscontinuity> PendingDiscontinuities => _pending;

    /// <summary>
    /// Places one captured packet on the timeline, inserting silence for any frames the
    /// device position says were skipped.
    /// </summary>
    public void Write(in PacketHeader header, ReadOnlySpan<byte> pcm16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long gap = TimelineFramesAt(header.QpcPosition) - _sessionFrames;
        int frames = header.FrameCount;
        int skipFrames = 0;

        if (gap > _gapThresholdFrames)
        {
            InsertSilence(gap);
        }
        else if (-gap > _gapThresholdFrames)
        {
            // The packet claims a moment already covered by frames on the timeline. Drop the
            // overlap rather than rewriting audio that is already written.
            skipFrames = (int)Math.Min(-gap, frames);
            RecordDiscontinuity(new CaptureDiscontinuity(
                DiscontinuityKind.TimestampError,
                _sessionFrames,
                skipFrames,
                $"packet timestamp is {-gap} frames behind the timeline; {skipFrames} overlapping frames dropped"));
        }

        if ((header.Flags & AudioPacketConditions.DataDiscontinuity) != 0)
        {
            RecordDiscontinuity(new CaptureDiscontinuity(
                DiscontinuityKind.EngineDiscontinuity,
                header.DevicePosition,
                0,
                "engine reported dropped data before this packet"));
        }

        int remaining = frames - skipFrames;
        if (remaining > 0)
        {
            bool silent = (header.Flags & AudioPacketConditions.Silent) != 0 || pcm16.IsEmpty;
            if (silent)
            {
                Append(default, remaining, silence: true);
            }
            else
            {
                int offset = skipFrames * _format.BytesPerFrame;
                int length = remaining * _format.BytesPerFrame;
                Append(pcm16.Slice(offset, length), remaining, silence: false);
            }
        }

        _lastDevicePosition = header.EndDevicePosition;
    }

    /// <summary>
    /// Moves the timeline forward to the given moment, filling anything still missing with
    /// explicit silence.
    ///
    /// <para>
    /// This is what keeps a stalled endpoint honest. When a device stops delivering — a silent
    /// loopback endpoint, or a headset microphone that powers down — no packet ever arrives to
    /// reveal that time passed, and a writer driven only by packets would simply end the track
    /// early. Phase 0 measured exactly that: a headset microphone that stopped after ten
    /// seconds of a thirty-second run. The writer thread calls this while idle, and the
    /// recorder calls it once more at stop.
    /// </para>
    /// </summary>
    /// <param name="exact">
    /// When true the jitter threshold is bypassed and the timeline is filled to the exact
    /// moment. Used for the final pad at stop, where leaving up to a threshold's worth of
    /// slack would show up directly as a track-length difference between the two tracks.
    /// </param>
    public void AdvanceTo(long qpcPosition, bool exact = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long gap = TimelineFramesAt(qpcPosition) - _sessionFrames;
        if (gap > (exact ? 0 : _gapThresholdFrames))
        {
            InsertSilence(gap);
        }
    }

    /// <summary>
    /// Records that the bounded queue dropped audio. The gap is not silently backfilled;
    /// it is written as silence and reported.
    /// </summary>
    public void RecordOverflow(long frameCount, string detail)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RecordDiscontinuity(new CaptureDiscontinuity(
            DiscontinuityKind.QueueOverflow, _lastDevicePosition, frameCount, detail));
    }

    /// <summary>Finalizes the active chunk, if any. Idempotent.</summary>
    public void Complete()
    {
        if (_disposed || _writer is null)
        {
            return;
        }

        FinalizeChunk();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Complete();
        _disposed = true;
    }

    /// <summary>Where a moment sits on the session timeline, in mix-format frames.</summary>
    private long TimelineFramesAt(long qpcPosition) => (long)Math.Round(
        CaptureClock.UnitsToSeconds(qpcPosition - _epochQpc) * _format.SampleRate,
        MidpointRounding.AwayFromZero);

    private void InsertSilence(long frameCount)
    {
        RecordDiscontinuity(CaptureDiscontinuity.Silence(_sessionFrames, frameCount));
        Append(default, frameCount, silence: true);
        SilenceFramesInserted += frameCount;
    }

    private void Append(ReadOnlySpan<byte> data, long frameCount, bool silence)
    {
        int consumedBytes = 0;

        while (frameCount > 0)
        {
            WavPcm16Writer writer = EnsureChunk();
            long room = _chunkFrames - (_sessionFrames - _chunkStartSessionFrame);
            long take = Math.Min(room, frameCount);

            if (silence)
            {
                writer.WriteSilence(take);
            }
            else
            {
                int byteCount = (int)take * _format.BytesPerFrame;
                writer.WriteFrames(data.Slice(consumedBytes, byteCount), take);
                consumedBytes += byteCount;
            }

            _sessionFrames += take;
            _framesSinceFlush += take;
            frameCount -= take;

            if (_sessionFrames - _chunkStartSessionFrame >= _chunkFrames)
            {
                FinalizeChunk();
            }
            else if (_framesSinceFlush >= _flushIntervalFrames)
            {
                FlushActive(writer);
            }
        }
    }

    private WavPcm16Writer EnsureChunk()
    {
        if (_writer is not null)
        {
            return _writer;
        }

        _chunkIndex++;
        _chunkStartSessionFrame = _sessionFrames;
        _writer = new WavPcm16Writer(ActivePath(_chunkIndex), _format);
        return _writer;
    }

    private void FlushActive(WavPcm16Writer writer)
    {
        writer.FlushToDisk();
        WriteSidecar(writer.FramesWritten);
        _framesSinceFlush = 0;
    }

    private void FinalizeChunk()
    {
        if (_writer is null)
        {
            return;
        }

        long frames = _writer.FramesWritten;
        string activePath = _writer.Path;
        _writer.Close();
        _writer = null;

        string finalPath = Path.Combine(_chunksDirectory, ChunkFileName(_chunkIndex));
        File.Move(activePath, finalPath, overwrite: false);

        string sidecar = SidecarPath(_chunkIndex);
        if (File.Exists(sidecar))
        {
            File.Delete(sidecar);
        }

        double startSeconds = (double)_chunkStartSessionFrame / _format.SampleRate;

        _completed.Add(new AudioChunkMetadata(
            _chunkIndex,
            Path.GetRelativePath(Path.GetDirectoryName(_chunksDirectory)!, finalPath).Replace('\\', '/'),
            _track,
            startSeconds,
            startSeconds + ((double)frames / _format.SampleRate),
            _format.SampleRate,
            _format.Channels,
            frames,
            ComputeSha256(finalPath),
            [.. _pending]));

        _pending.Clear();
        _framesSinceFlush = 0;
    }

    private void RecordDiscontinuity(CaptureDiscontinuity discontinuity) => _pending.Add(discontinuity);

    private void WriteSidecar(long framesWritten)
    {
        string json = string.Create(CultureInfo.InvariantCulture,
            $$"""
            {"chunk_index":{{_chunkIndex}},"frames_written":{{framesWritten}},"last_device_position":{{_lastDevicePosition}},"sample_rate":{{_format.SampleRate}},"channels":{{_format.Channels}}}
            """);

        File.WriteAllText(SidecarPath(_chunkIndex), json);
    }

    private string ActivePath(int index) =>
        Path.Combine(_activeDirectory, $"{index:D6}.part.wav");

    private string SidecarPath(int index) =>
        Path.Combine(_activeDirectory, $"{index:D6}.part.state.json");

    private static string ChunkFileName(int index) => $"{index:D6}.wav";

    private static string ComputeSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
