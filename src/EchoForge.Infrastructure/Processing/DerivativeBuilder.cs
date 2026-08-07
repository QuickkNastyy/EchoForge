using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Processing;
using EchoForge.Core.Transcripts;

namespace EchoForge.Infrastructure.Processing;

/// <summary>Progress while derivatives are built. Names a track and a chunk, never content.</summary>
public sealed class DerivativeProgressEventArgs(string sourceTrack, int completedChunks, int totalChunks) : EventArgs
{
    public string SourceTrack { get; } = sourceTrack;

    public int CompletedChunks { get; } = completedChunks;

    public int TotalChunks { get; } = totalChunks;
}

/// <summary>The outcome of preparing a session's audio.</summary>
public sealed record DerivativeBuildResult(DerivativeSet? Set, string? Code, string? Detail)
{
    public bool Succeeded => Set is not null;

    public static DerivativeBuildResult Fail(string code, string detail) => new(null, code, detail);
}

/// <summary>
/// Turns immutable source chunks into the 16 kHz mono audio a recogniser wants, and records
/// exactly how to get back.
///
/// <para>
/// Three properties matter more than speed here.
/// </para>
///
/// <para>
/// <b>The derivative is laid out by session time, not by counting.</b> Each chunk's first and last
/// output frame come from its own absolute session position, so a rounding error in one chunk
/// cannot push everything after it. A derivative built by appending "however many frames this
/// chunk resampled to" would drift a little at every boundary, and three hours later a transcript
/// timestamp would point at the wrong sentence.
/// </para>
///
/// <para>
/// <b>Gaps are audio.</b> Time the session says passed with nothing recorded â€” a pause, a stall, a
/// device that died â€” becomes explicit silence with an explicit span. Closing the gap instead
/// would make everything after it early by the length of the gap.
/// </para>
///
/// <para>
/// <b>Sources are read and nothing else.</b> Every file here is opened read-only. A processing
/// stage that repaired a chunk in place would destroy the only authoritative copy of what was
/// captured.
/// </para>
/// </summary>
public sealed class DerivativeBuilder(ISessionStore sessions)
{
    private const int WavHeaderBytes = 44;

    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public event EventHandler<DerivativeProgressEventArgs>? Progress;

    /// <summary>Where a session's derivatives live, one directory per processing version.</summary>
    public static string DerivativeDirectory(SessionPaths paths, DerivativeOptions options) =>
        Path.Combine(paths.Root, "derived", "audio", options.ProcessingVersion);

    /// <summary>
    /// Builds both tracks' derivatives, reusing any that are still exactly right.
    /// </summary>
    /// <param name="request">
    /// The same request the worker would be given. Sharing it is what guarantees the derivative
    /// and the transcript agree about where every chunk sits on the session timeline.
    /// </param>
    public async Task<DerivativeBuildResult> BuildAsync(
        TranscriptionRequest request,
        DerivativeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new DerivativeOptions();

        SessionPaths paths = _sessions.Resolve(request.SessionId);
        string directory = DerivativeDirectory(paths, options);
        string manifest = TranscriptionRequestBuilder.SourceManifestSha256(request);

        List<DerivativeRecord> records = [];

        try
        {
            Directory.CreateDirectory(directory);

            foreach (RequestTrack track in request.Tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DerivativeRecord? existing = TryReuse(directory, track.SourceTrack, manifest, options);
                if (existing is not null)
                {
                    records.Add(existing);
                    continue;
                }

                records.Add(await BuildTrackAsync(
                    request, track, directory, manifest, options, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException)
        {
            // Sources are untouched and any previously valid derivative is still in place: the
            // staged files are the only thing discarded.
            return DerivativeBuildResult.Fail("cancelled", "audio preparation was cancelled");
        }
        catch (SourceAudioException ex)
        {
            return DerivativeBuildResult.Fail("source_audio_invalid", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DerivativeBuildResult.Fail("derivative_write_failed", $"could not be written ({ex.GetType().Name})");
        }

        return new DerivativeBuildResult(new DerivativeSet(records), null, null);
    }

    // -- one track -----------------------------------------------------------------------------

    private async Task<DerivativeRecord> BuildTrackAsync(
        TranscriptionRequest request,
        RequestTrack track,
        string directory,
        string sourceManifestSha256,
        DerivativeOptions options,
        CancellationToken cancellationToken)
    {
        SessionPaths paths = _sessions.Resolve(request.SessionId);
        string audioPath = Path.Combine(directory, $"{track.SourceTrack}.wav");
        string mapPath = Path.Combine(directory, $"{track.SourceTrack}.timing.json");
        string staging = audioPath + ".partial";

        int outRate = options.SampleRate;
        List<TimingSpan> spans = [];
        long cursor = 0;
        int completed = 0;

        // The digest is taken from the finished file rather than accumulated while writing: the
        // header is only correct once the frame count is known, so hashing as we go would have to
        // hash everything again anyway.
        await using (FileStream output = new(staging, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            // The header is rewritten once the frame count is known. Written now so the data
            // stream starts at a fixed offset and the file is a plausible WAV throughout.
            byte[] header = new byte[WavHeaderBytes];
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<RequestChunk> chunks = [.. track.Chunks.OrderBy(c => c.Epoch).ThenBy(c => c.Index)];

            for (int i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                RequestChunk chunk = chunks[i];
                long start = FrameAt(chunk.StartSeconds, outRate);
                long end = FrameAt(chunk.EndSeconds, outRate);

                if (start > cursor)
                {
                    // Time the session says passed with nothing captured.
                    long gap = start - cursor;
                    await WriteSilenceAsync(output, gap, cancellationToken).ConfigureAwait(false);
                    spans.Add(GapSpan(cursor, gap, chunk.Epoch, outRate));
                    cursor = start;
                }
                else if (start < cursor)
                {
                    // The builder makes epochs monotonic, so this means the snapshot itself is
                    // inconsistent. Overlapping audio cannot be laid down twice.
                    throw new SourceAudioException(
                        Invariant($"chunk {chunk.Index} on the {track.SourceTrack} track starts before the previous one ended"));
                }

                long frames = Math.Max(0, end - cursor);

                if (frames > 0)
                {
                    await WriteChunkAsync(
                        paths, chunk, chunks, i, output, frames, outRate, cancellationToken)
                        .ConfigureAwait(false);

                    spans.Add(SourceSpan(chunk, cursor, frames, outRate));
                    cursor += frames;
                }

                completed++;
                Progress?.Invoke(this, new DerivativeProgressEventArgs(track.SourceTrack, completed, chunks.Count));
            }

            // The session may run past the last chunk on this track: the other track kept going,
            // or the epoch was padded to a shared stop instant.
            long total = FrameAt(request.DurationSeconds, outRate);
            if (total > cursor)
            {
                long trailing = total - cursor;
                int epoch = request.Epochs.Count > 0 ? request.Epochs[^1].Index : 1;
                await WriteSilenceAsync(output, trailing, cancellationToken).ConfigureAwait(false);
                spans.Add(GapSpan(cursor, trailing, epoch, outRate));
                cursor = total;
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Patch the header, and fold exactly those bytes into the digest in the same place
            // a reader will find them.
            WriteWavHeader(header, outRate, options.Channels, cursor);
            output.Position = 0;
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        string digest = await HashFileAsync(staging, cancellationToken).ConfigureAwait(false);
        long size = new FileInfo(staging).Length;

        TimingMap map = new()
        {
            SessionId = request.SessionId,
            SourceTrack = track.SourceTrack,
            SampleRate = outRate,
            Channels = options.Channels,
            TotalFrames = cursor,
            ProcessingVersion = options.ProcessingVersion,
            SourceManifestSha256 = sourceManifestSha256,
            Spans = spans,
        };

        byte[] mapBytes = JsonSerializer.SerializeToUtf8Bytes(map, TimingMap.Json);
        string mapDigest = Convert.ToHexStringLower(SHA256.HashData(mapBytes));

        WriteAtomically(mapPath, mapBytes);
        File.Move(staging, audioPath, overwrite: true);

        DerivativeRecord record = new()
        {
            SourceTrack = track.SourceTrack,
            RelativePath = Relative(paths, audioPath),
            TimingMapRelativePath = Relative(paths, mapPath),
            Sha256 = digest,
            TimingMapSha256 = mapDigest,
            SizeBytes = size,
            SampleRate = outRate,
            Channels = options.Channels,
            TotalFrames = cursor,
            SourceManifestSha256 = sourceManifestSha256,
            ProcessingVersion = options.ProcessingVersion,
            CreatedUtc = request.CreatedAtUtc,
        };

        WriteAtomically(RecordPath(directory, track.SourceTrack), JsonSerializer.SerializeToUtf8Bytes(record, DerivativeRecord.Json));
        return record;
    }

    /// <summary>
    /// Resamples one chunk into exactly the frames session time allots it.
    ///
    /// <para>
    /// The filter needs samples either side of each position, so the neighbouring chunks' edges
    /// are read in as history and lookahead. Without them every sixty-second boundary would carry
    /// a small step discontinuity â€” inaudible alone, but a periodic click through an hour of
    /// audio is exactly the kind of artefact a recogniser notices.
    /// </para>
    /// </summary>
    private static async Task WriteChunkAsync(
        SessionPaths paths,
        RequestChunk chunk,
        IReadOnlyList<RequestChunk> chunks,
        int position,
        Stream output,
        long frames,
        int outRate,
        CancellationToken cancellationToken)
    {
        string path = Resolve(paths, chunk.RelativePath);

        using PcmSourceReader reader = PcmSourceReader.Open(path, chunk.SampleRate, chunk.Channels);
        short[] mono = await reader.ReadMonoAsync(cancellationToken).ConfigureAwait(false);

        int reach = AudioResampler.Reach;
        short[] before = await EdgeAsync(paths, Neighbour(chunks, position, -1), reach, fromEnd: true, cancellationToken).ConfigureAwait(false);
        short[] after = await EdgeAsync(paths, Neighbour(chunks, position, +1), reach, fromEnd: false, cancellationToken).ConfigureAwait(false);

        short[] window = new short[before.Length + mono.Length + after.Length];
        before.CopyTo(window, 0);
        mono.CopyTo(window, before.Length);
        after.CopyTo(window, before.Length + mono.Length);

        AudioResampler resampler = new(chunk.SampleRate, outRate);

        // The window starts this many input frames before the chunk's own frame zero.
        long inputOffset = -before.Length;

        const int block = 16 * 1024;
        short[] outBuffer = new short[block];
        byte[] bytes = new byte[block * 2];

        long produced = 0;
        while (produced < frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int count = (int)Math.Min(block, frames - produced);
            for (int j = 0; j < count; j++)
            {
                outBuffer[j] = resampler.Sample(window, inputOffset, produced + j);
            }

            Buffer.BlockCopy(outBuffer, 0, bytes, 0, count * 2);
            await output.WriteAsync(bytes.AsMemory(0, count * 2), cancellationToken).ConfigureAwait(false);
            produced += count;
        }
    }

    /// <summary>
    /// The neighbouring chunk, when it is genuinely contiguous with this one.
    ///
    /// <para>
    /// Across an epoch boundary or a format change there is no continuity to preserve, so the
    /// filter sees silence there instead. Borrowing samples across a gap would smear audio from
    /// one side of a pause into the other.
    /// </para>
    /// </summary>
    private static RequestChunk? Neighbour(IReadOnlyList<RequestChunk> chunks, int position, int direction)
    {
        int index = position + direction;
        if (index < 0 || index >= chunks.Count)
        {
            return null;
        }

        RequestChunk current = chunks[position];
        RequestChunk candidate = chunks[index];

        if (candidate.Epoch != current.Epoch ||
            candidate.SampleRate != current.SampleRate ||
            candidate.Channels != current.Channels)
        {
            return null;
        }

        // Contiguous in time, to within a sample.
        double seam = direction > 0
            ? Math.Abs(candidate.StartSeconds - current.EndSeconds)
            : Math.Abs(current.StartSeconds - candidate.EndSeconds);

        return seam <= 1.0 / current.SampleRate ? candidate : null;
    }

    private static async Task<short[]> EdgeAsync(
        SessionPaths paths,
        RequestChunk? neighbour,
        int frames,
        bool fromEnd,
        CancellationToken cancellationToken)
    {
        if (neighbour is null || frames <= 0)
        {
            return [];
        }

        using PcmSourceReader reader = PcmSourceReader.Open(
            Resolve(paths, neighbour.RelativePath), neighbour.SampleRate, neighbour.Channels);

        if (reader.Frames == 0)
        {
            return [];
        }

        short[] mono = await reader.ReadMonoAsync(cancellationToken).ConfigureAwait(false);
        int take = (int)Math.Min(frames, mono.Length);
        return fromEnd ? mono[^take..] : mono[..take];
    }

    // -- layout helpers -------------------------------------------------------------------------

    /// <summary>
    /// The derivative frame a session time lands on.
    ///
    /// <para>
    /// Absolute, and rounded once from the session-relative time itself. This is the single
    /// definition of where audio sits, shared by the writer and the timing map, so the two cannot
    /// disagree and neither can drift.
    /// </para>
    /// </summary>
    internal static long FrameAt(double sessionSeconds, int sampleRate) =>
        (long)Math.Round(Math.Max(0, sessionSeconds) * sampleRate, MidpointRounding.AwayFromZero);

    private static TimingSpan GapSpan(long firstFrame, long frames, int epoch, int rate) => new()
    {
        Kind = TimingSpanKind.Gap,
        DerivativeFrame = firstFrame,
        Frames = frames,
        Epoch = epoch,
        SessionStartSeconds = (double)firstFrame / rate,
        SessionEndSeconds = (double)(firstFrame + frames) / rate,
    };

    private static TimingSpan SourceSpan(RequestChunk chunk, long firstFrame, long frames, int rate) => new()
    {
        Kind = TimingSpanKind.Source,
        DerivativeFrame = firstFrame,
        Frames = frames,
        Epoch = chunk.Epoch,
        SessionStartSeconds = (double)firstFrame / rate,
        SessionEndSeconds = (double)(firstFrame + frames) / rate,
        ChunkIndex = chunk.Index,
        ChunkRelativePath = chunk.RelativePath,
        SourceFrame = 0,
        SourceFrames = chunk.Frames,
        SourceSampleRate = chunk.SampleRate,
        SourceChannels = chunk.Channels,
    };

    private static async Task WriteSilenceAsync(
        Stream output, long frames, CancellationToken cancellationToken)
    {
        const int block = 16 * 1024;
        byte[] zeros = new byte[block * 2];
        long remaining = frames;

        while (remaining > 0)
        {
            int count = (int)Math.Min(block, remaining);
            await output.WriteAsync(zeros.AsMemory(0, count * 2), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }
    }

    internal static void WriteWavHeader(byte[] header, int sampleRate, int channels, long frames)
    {
        long dataBytes = frames * channels * 2;

        "RIFF"u8.CopyTo(header.AsSpan(0));
        BitConverter.TryWriteBytes(header.AsSpan(4), (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BitConverter.TryWriteBytes(header.AsSpan(16), 16u);
        BitConverter.TryWriteBytes(header.AsSpan(20), (ushort)1);
        BitConverter.TryWriteBytes(header.AsSpan(22), (ushort)channels);
        BitConverter.TryWriteBytes(header.AsSpan(24), (uint)sampleRate);
        BitConverter.TryWriteBytes(header.AsSpan(28), (uint)(sampleRate * channels * 2));
        BitConverter.TryWriteBytes(header.AsSpan(32), (ushort)(channels * 2));
        BitConverter.TryWriteBytes(header.AsSpan(34), (ushort)16);
        "data"u8.CopyTo(header.AsSpan(36));
        BitConverter.TryWriteBytes(header.AsSpan(40), (uint)dataBytes);
    }

    private static string RecordPath(string directory, string track) =>
        Path.Combine(directory, $"{track}.derivative.json");

    /// <summary>
    /// Reuses a derivative only when every part of its identity matches, and the file it names is
    /// still exactly the file that was hashed.
    /// </summary>
    private static DerivativeRecord? TryReuse(
        string directory, string track, string sourceManifestSha256, DerivativeOptions options)
    {
        string recordPath = RecordPath(directory, track);
        if (!File.Exists(recordPath))
        {
            return null;
        }

        DerivativeRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<DerivativeRecord>(File.ReadAllBytes(recordPath), DerivativeRecord.Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (record is null || !record.Matches(sourceManifestSha256, options))
        {
            return null;
        }

        string audioPath = Path.Combine(directory, $"{track}.wav");
        string mapPath = Path.Combine(directory, $"{track}.timing.json");

        if (!File.Exists(audioPath) || !File.Exists(mapPath))
        {
            return null;
        }

        try
        {
            if (new FileInfo(audioPath).Length != record.SizeBytes)
            {
                return null;
            }

            using FileStream stream = File.OpenRead(audioPath);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), record.Sha256, StringComparison.Ordinal))
            {
                return null;
            }

            if (!string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(mapPath))),
                    record.TimingMapSha256,
                    StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return record;
    }

    internal static string Resolve(SessionPaths paths, string relativePath)
    {
        string root = Path.GetFullPath(paths.Root);
        string resolved = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceAudioException("a chunk path points outside its session");
        }

        if (!File.Exists(resolved))
        {
            throw new SourceAudioException("a source chunk is missing");
        }

        return resolved;
    }

    internal static string Relative(SessionPaths paths, string path) =>
        Path.GetRelativePath(paths.Root, path).Replace('\\', '/');

    internal static void WriteAtomically(string path, byte[] payload)
    {
        string temporary = path + ".partial";

        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);

        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
