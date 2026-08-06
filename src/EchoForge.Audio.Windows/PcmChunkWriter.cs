using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Audio;

namespace EchoForge.Audio.Windows;

/// <summary>
/// Turns a stream of captured packets into immutable fixed-duration PCM16 chunks.
///
/// <para>
/// The timeline is built from each packet's <see cref="PacketHeader.QpcPosition"/>, never from
/// when managed code observed the packet. When the QPC position runs ahead of the frames written
/// so far, the missing frames are written as explicit silence and recorded as a discontinuity, so
/// a chunk's frame count always equals its wall-clock duration.
/// </para>
///
/// <para>
/// <b>Why not device position.</b> Phase 0 measured a headset microphone whose device position
/// advanced 160 frames for every 480 frames delivered: the endpoint captures at 16 kHz and the
/// audio engine resamples up to the 48 kHz mix format. <see cref="PacketHeader.DevicePosition"/>
/// counts frames in the <em>device's</em> rate, not the mix format's, so it cannot be used as a
/// frame counter whenever the engine resamples. It is retained as a diagnostic only.
/// </para>
///
/// <para>
/// <b>Durability.</b> A finalized chunk is discoverable without the journal: the writer emits a
/// <c>.meta.json</c> record beside the audio immediately after the atomic rename, then notifies
/// its owner. That closes the window where a crash would leave a complete WAV that nothing knew
/// about. All of this happens on the writer thread; the capture thread never touches the disk.
/// </para>
/// </summary>
public sealed class PcmChunkWriter : IDisposable
{
    private readonly string _activeDirectory;
    private readonly string _chunksDirectory;
    private readonly string _sessionRoot;
    private readonly CaptureFormat _format;
    private readonly SourceTrack _track;
    private readonly long _chunkFrames;
    private readonly long _flushIntervalFrames;
    private readonly long _gapThresholdFrames;
    private readonly long _epochQpc;
    private readonly int _epochIndex;
    private readonly Action<AudioChunkMetadata>? _chunkFinalized;
    private readonly List<AudioChunkMetadata> _completed = [];
    private readonly List<CaptureDiscontinuity> _pending = [];

    private WavPcm16Writer? _writer;
    private int _chunkIndex;
    private long _chunkStartSessionFrame;
    private long _sessionFrames;
    private long _framesSinceFlush;
    private long _lastDevicePosition;
    private bool _sealed;
    private bool _disposed;

    /// <param name="epochQpc">
    /// The shared epoch origin. Both tracks must be given the same value: it is what makes t=0
    /// mean the same instant on each, and therefore what makes them alignable at all.
    /// </param>
    /// <param name="firstChunkIndex">
    /// The index this epoch's first chunk takes. Resuming after a pause continues the session's
    /// numbering, so chunk indices stay unique across epochs and no finalized file is overwritten.
    /// </param>
    /// <param name="chunkFinalized">
    /// Raised on the writer thread once a chunk is complete, hashed, and its metadata record is
    /// durable. The handler must not block for long.
    /// </param>
    public PcmChunkWriter(
        string trackDirectory,
        SourceTrack track,
        CaptureFormat format,
        long epochQpc,
        TimeSpan? chunkDuration = null,
        TimeSpan? flushInterval = null,
        int firstChunkIndex = 1,
        int epochIndex = 1,
        string? sessionRoot = null,
        Action<AudioChunkMetadata>? chunkFinalized = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackDirectory);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstChunkIndex, 1);

        _format = format;
        _track = track;
        _epochQpc = epochQpc;
        _epochIndex = epochIndex;
        _chunkIndex = firstChunkIndex - 1;
        _chunkFinalized = chunkFinalized;

        _activeDirectory = Path.Combine(trackDirectory, "active");
        _chunksDirectory = Path.Combine(trackDirectory, "chunks");
        Directory.CreateDirectory(_activeDirectory);
        Directory.CreateDirectory(_chunksDirectory);

        // Relative paths are recorded against the session root so a moved session still resolves.
        _sessionRoot = sessionRoot ?? Path.GetDirectoryName(Path.GetDirectoryName(trackDirectory)!)!;

        _chunkFrames = format.FramesForDuration(chunkDuration ?? TimeSpan.FromSeconds(60));
        _flushIntervalFrames = format.FramesForDuration(flushInterval ?? TimeSpan.FromSeconds(2));

        // QPC timestamps carry sub-millisecond jitter and packets are not perfectly evenly
        // spaced, so only a gap larger than this counts as missing audio.
        _gapThresholdFrames = format.FramesForDuration(TimeSpan.FromMilliseconds(30));

        if (_chunkFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkDuration), "Chunk duration must cover at least one frame.");
        }
    }

    /// <summary>Chunks finalized so far, in order. Finalized chunks are never rewritten.</summary>
    public IReadOnlyList<AudioChunkMetadata> CompletedChunks => _completed;

    /// <summary>Total frames placed on the timeline for this epoch, including inserted silence.</summary>
    public long SessionFrames => _sessionFrames;

    /// <summary>The index the next chunk will take.</summary>
    public int NextChunkIndex => _chunkIndex + 1;

    /// <summary>Frames of silence inserted to cover gaps the session clock reported.</summary>
    public long SilenceFramesInserted { get; private set; }

    /// <summary>Discontinuities recorded on the chunk currently being written.</summary>
    public IReadOnlyList<CaptureDiscontinuity> PendingDiscontinuities => _pending;

    /// <summary>
    /// True once <see cref="Complete"/> has run. A sealed writer ignores further audio, silence,
    /// and overflow reports, so nothing can be appended to a finalized session.
    /// </summary>
    public bool IsSealed => _sealed;

    public void Write(in PacketHeader header, ReadOnlySpan<byte> pcm16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed)
        {
            return;
        }

        long gap = TimelineFramesAt(header.QpcPosition) - _sessionFrames;
        int frames = header.FrameCount;
        int skipFrames = 0;

        if (gap > _gapThresholdFrames)
        {
            InsertSilence(gap);
        }
        else if (-gap > _gapThresholdFrames)
        {
            skipFrames = (int)Math.Min(-gap, frames);
            RecordDiscontinuity(new CaptureDiscontinuity(
                DiscontinuityKind.TimestampError,
                _sessionFrames,
                skipFrames,
                $"packet timestamp is {-gap} frames behind the timeline; {skipFrames} overlapping frames dropped"));
        }

        if ((header.Conditions & AudioPacketConditions.DataDiscontinuity) != 0)
        {
            RecordDiscontinuity(new CaptureDiscontinuity(
                DiscontinuityKind.EngineDiscontinuity,
                _sessionFrames,
                0,
                "engine reported dropped data before this packet"));
        }

        int remaining = frames - skipFrames;
        if (remaining > 0)
        {
            bool silent = (header.Conditions & AudioPacketConditions.Silent) != 0 || pcm16.IsEmpty;
            if (silent)
            {
                Append(default, remaining, silence: true);
            }
            else
            {
                Append(pcm16.Slice(skipFrames * _format.BytesPerFrame, remaining * _format.BytesPerFrame), remaining, silence: false);
            }
        }

        _lastDevicePosition = header.EndDevicePosition;
    }

    /// <summary>
    /// Moves the timeline forward to the given moment, filling anything still missing with
    /// explicit silence.
    ///
    /// <para>
    /// This is what keeps a stalled endpoint honest. When a device stops delivering, no packet
    /// arrives to reveal that time passed, and a writer driven only by packets would end the
    /// track early. Phase 0 measured exactly that.
    /// </para>
    /// </summary>
    /// <param name="exact">
    /// Bypasses the jitter threshold. Used for the final pad at stop, where leaving slack would
    /// show up directly as a track-length difference between the two tracks.
    /// </param>
    public void AdvanceTo(long qpcPosition, bool exact = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed)
        {
            return;
        }

        long gap = TimelineFramesAt(qpcPosition) - _sessionFrames;
        if (gap > (exact ? 0 : _gapThresholdFrames))
        {
            InsertSilence(gap);
        }
    }

    /// <summary>Records that the bounded queue dropped audio. The gap is reported, never backfilled.</summary>
    public void RecordOverflow(long frameCount, string detail)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed)
        {
            return;
        }

        RecordDiscontinuity(new CaptureDiscontinuity(
            DiscontinuityKind.QueueOverflow, _sessionFrames, frameCount, detail));
    }

    /// <summary>Finalizes the active chunk, if any, and seals the writer. Idempotent.</summary>
    public void Complete()
    {
        if (_disposed || _sealed)
        {
            return;
        }

        if (_writer is not null)
        {
            FinalizeChunk();
        }

        _sealed = true;
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

    /// <summary>Where a moment sits on this epoch's timeline, in mix-format frames.</summary>
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
        WriteActiveRecord(0);
        return _writer;
    }

    private void FlushActive(WavPcm16Writer writer)
    {
        writer.FlushToDisk();
        WriteActiveRecord(writer.FramesWritten);
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
        int index = _chunkIndex;
        double startSeconds = (double)_chunkStartSessionFrame / _format.SampleRate;
        List<CaptureDiscontinuity> discontinuities = [.. _pending];

        _writer.Close();
        _writer = null;

        string finalPath = Path.Combine(_chunksDirectory, ChunkFileName(index));
        File.Move(activePath, finalPath, overwrite: false);

        string sha = ComputeSha256(finalPath);
        string relative = Relative(finalPath);

        AudioChunkMetadata metadata = new(
            index,
            relative,
            _track,
            startSeconds,
            startSeconds + ((double)frames / _format.SampleRate),
            _format.SampleRate,
            _format.Channels,
            frames,
            sha,
            discontinuities,
            _epochIndex);

        // Durable before anything else learns the chunk exists. A crash after this point is
        // recoverable from the record alone, with no journal line and no snapshot.
        WriteRecord(FinalizedRecordPath(index), BuildRecord(index, frames, startSeconds, relative, sha, true, discontinuities));

        // The active sidecar has been superseded by the finalized record.
        DeleteIfPresent(ActiveRecordPath(index));

        _completed.Add(metadata);
        _pending.Clear();
        _framesSinceFlush = 0;

        _chunkFinalized?.Invoke(metadata);
    }

    private void RecordDiscontinuity(CaptureDiscontinuity discontinuity) => _pending.Add(discontinuity);

    /// <summary>
    /// Writes the active chunk's record. Carries everything recovery needs to place a repaired
    /// chunk on the timeline: track, index, epoch, format, frames, start offset, and the epoch's
    /// QPC origin. Recovery must never have to invent a start time.
    /// </summary>
    private void WriteActiveRecord(long framesWritten)
    {
        double startSeconds = (double)_chunkStartSessionFrame / _format.SampleRate;
        WriteRecord(
            ActiveRecordPath(_chunkIndex),
            BuildRecord(_chunkIndex, framesWritten, startSeconds, Relative(ActivePath(_chunkIndex)), null, false, _pending));
    }

    private ChunkRecord BuildRecord(
        int index,
        long frames,
        double startSeconds,
        string relativePath,
        string? sha,
        bool finalized,
        IReadOnlyList<CaptureDiscontinuity> discontinuities) => new()
        {
            Track = _track.ToString(),
            Index = index,
            Epoch = _epochIndex,
            SampleRate = _format.SampleRate,
            Channels = _format.Channels,
            BitsPerSample = _format.BitsPerSample,
            Frames = frames,
            StartSeconds = startSeconds,
            EpochQpc = _epochQpc,
            RelativePath = relativePath,
            Sha256 = sha,
            Finalized = finalized,
            Discontinuities = [.. discontinuities.Select(DiscontinuityRecord.From)],
        };

    private static void WriteRecord(string path, ChunkRecord record)
    {
        string temporary = path + ".tmp";
        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record, ChunkRecord.Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string Relative(string path) =>
        Path.GetRelativePath(_sessionRoot, path).Replace('\\', '/');

    private string ActivePath(int index) => Path.Combine(_activeDirectory, $"{index:D6}.part.wav");

    private string ActiveRecordPath(int index) => Path.Combine(_activeDirectory, $"{index:D6}.part.state.json");

    private string FinalizedRecordPath(int index) => Path.Combine(_chunksDirectory, $"{index:D6}.meta.json");

    private static string ChunkFileName(int index) => $"{index:D6}.wav";

    private static string ComputeSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
