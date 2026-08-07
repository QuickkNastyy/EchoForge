using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Playback;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Processing;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Processing;

namespace EchoForge.Infrastructure.Playback;

/// <summary>Progress while the playback derivative is built. Names a chunk, never content.</summary>
public sealed class PlaybackBuildProgressEventArgs(long completedFrames, long totalFrames) : EventArgs
{
    public long CompletedFrames { get; } = completedFrames;

    public long TotalFrames { get; } = totalFrames;

    public double Fraction => TotalFrames <= 0 ? 0 : Math.Clamp((double)CompletedFrames / TotalFrames, 0, 1);
}

/// <summary>The outcome of preparing a session's audio for listening.</summary>
public sealed record PlaybackBuildResult(PlaybackDerivativeRecord? Record, string? Code, string? Detail)
{
    public bool Succeeded => Record is not null;

    public static PlaybackBuildResult Fail(string code, string detail) => new(null, code, detail);
}

/// <summary>
/// Builds the aligned two-track file a meeting is played back from.
///
/// <para>
/// <b>This is not the chunks concatenated.</b> Concatenation answers the question "what was
/// recorded" and gets "when" wrong the moment anything interrupts a meeting: every pause would
/// close up, every later timestamp would be early by the total length of the pauses, and a citation
/// two hours in would point at the wrong sentence by minutes. So the derivative is laid out by
/// <i>absolute session time</i>. Each chunk's first and last output frame are computed from its own
/// session position, silence is written wherever the timeline says time passed with nothing
/// captured, and the two tracks are placed independently against the same clock. A rounding error
/// in one chunk therefore cannot push anything after it, and the file is the meeting rather than a
/// summary of its audio.
/// </para>
///
/// <para>
/// <b>Both tracks, side by side, still identifiable.</b> Microphone goes to channel 0 and system to
/// channel 1 — never mixed down here. A mixed file could not tell anyone afterwards which half was
/// You, and the listening balance would be frozen into bytes that cost a rebuild to change. The mix
/// happens on the way to the speakers instead, so muting a track is free and changes nothing a
/// citation points at.
/// </para>
///
/// <para>
/// <b>Sources are read and nothing else</b>, exactly as in the transcription derivative. Every file
/// here is opened read-only; the only writes are to a staging file that is renamed into place at
/// the end, so a cancelled or failed build cannot damage the audio or the derivative that was
/// already there.
/// </para>
/// </summary>
public sealed class PlaybackDerivativeBuilder(ISessionStore sessions)
{
    private const int WavHeaderBytes = 44;

    /// <summary>Frames per write. About a fifth of a second, so cancellation is prompt.</summary>
    private const int BlockFrames = 4096;

    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public event EventHandler<PlaybackBuildProgressEventArgs>? Progress;

    /// <summary>Where a session's playback audio lives, one directory per processing version.</summary>
    public static string PlaybackDirectory(SessionPaths paths, PlaybackOptions options) =>
        Path.Combine(paths.Root, "derived", "playback", options.ProcessingVersion);

    /// <summary>
    /// Builds the derivative, or hands back the one that is still exactly right.
    /// </summary>
    /// <param name="request">
    /// The same description of the session the transcription pipeline is given. Sharing it is what
    /// guarantees that what a listener hears and what a transcript claims sit on one timeline.
    /// </param>
    public async Task<PlaybackBuildResult> BuildAsync(
        TranscriptionRequest request,
        PlaybackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new PlaybackOptions();

        SessionPaths paths = _sessions.Resolve(request.SessionId);
        string directory = PlaybackDirectory(paths, options);
        string manifest = TranscriptionRequestBuilder.SourceManifestSha256(request);

        if (request.DurationSeconds <= 0)
        {
            return PlaybackBuildResult.Fail("no_audio", "That recording has no audio to play.");
        }

        try
        {
            if (TryReuse(directory, manifest, options) is { } existing)
            {
                return new PlaybackBuildResult(existing, null, null);
            }

            Directory.CreateDirectory(directory);
            return new PlaybackBuildResult(
                await WriteAsync(request, paths, directory, manifest, options, cancellationToken).ConfigureAwait(false),
                null,
                null);
        }
        catch (OperationCanceledException)
        {
            // Sources are untouched and any previously valid derivative is still in place: the
            // staged file is the only thing discarded.
            Discard(StagingPath(directory));
            return PlaybackBuildResult.Fail("cancelled", "Preparing the audio was cancelled.");
        }
        catch (SourceAudioException ex)
        {
            Discard(StagingPath(directory));
            return PlaybackBuildResult.Fail("source_audio_invalid", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Discard(StagingPath(directory));
            return PlaybackBuildResult.Fail(
                "playback_write_failed", $"The audio could not be prepared ({ex.GetType().Name}).");
        }
    }

    // -- writing ---------------------------------------------------------------------------------

    private async Task<PlaybackDerivativeRecord> WriteAsync(
        TranscriptionRequest request,
        SessionPaths paths,
        string directory,
        string sourceManifestSha256,
        PlaybackOptions options,
        CancellationToken cancellationToken)
    {
        int rate = options.SampleRate;
        long total = DerivativeBuilder.FrameAt(request.DurationSeconds, rate);

        string audioPath = Path.Combine(directory, "playback.wav");
        string staging = StagingPath(directory);

        // One renderer per track slot, so a session missing a track still produces a two-channel
        // file with a silent side rather than a differently shaped one nothing else expects.
        using TrackRenderer you = TrackRenderer.For(
            paths, request, TranscriptSpeakers.MicrophoneTrack, rate, total);
        using TrackRenderer remote = TrackRenderer.For(
            paths, request, TranscriptSpeakers.SystemTrack, rate, total);

        short[] left = new short[BlockFrames];
        short[] right = new short[BlockFrames];
        short[] interleaved = new short[BlockFrames * 2];
        byte[] bytes = new byte[BlockFrames * 2 * 2];

        await using (FileStream output = new(
            staging, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            byte[] header = new byte[WavHeaderBytes];
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

            long cursor = 0;
            while (cursor < total)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int frames = (int)Math.Min(BlockFrames, total - cursor);

                await you.FillAsync(cursor, left.AsMemory(0, frames), cancellationToken).ConfigureAwait(false);
                await remote.FillAsync(cursor, right.AsMemory(0, frames), cancellationToken).ConfigureAwait(false);

                for (int i = 0; i < frames; i++)
                {
                    interleaved[(i * 2) + PlaybackChannels.You] = left[i];
                    interleaved[(i * 2) + PlaybackChannels.Remote] = right[i];
                }

                Buffer.BlockCopy(interleaved, 0, bytes, 0, frames * 2 * 2);
                await output.WriteAsync(bytes.AsMemory(0, frames * 2 * 2), cancellationToken).ConfigureAwait(false);

                cursor += frames;
                Progress?.Invoke(this, new PlaybackBuildProgressEventArgs(cursor, total));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            DerivativeBuilder.WriteWavHeader(header, rate, 2, total);
            output.Position = 0;
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        List<PlaybackTrack> tracks = [];
        foreach ((TrackRenderer renderer, int channel) in ((TrackRenderer, int)[])
            [(you, PlaybackChannels.You), (remote, PlaybackChannels.Remote)])
        {
            string mapPath = Path.Combine(directory, $"playback.{renderer.SourceTrack}.timing.json");

            TimingMap map = new()
            {
                SessionId = request.SessionId,
                SourceTrack = renderer.SourceTrack,
                SampleRate = rate,
                Channels = 1,
                TotalFrames = total,
                ProcessingVersion = options.ProcessingVersion,
                SourceManifestSha256 = sourceManifestSha256,
                Spans = renderer.Spans,
            };

            byte[] mapBytes = JsonSerializer.SerializeToUtf8Bytes(map, TimingMap.Json);
            DerivativeBuilder.WriteAtomically(mapPath, mapBytes);

            tracks.Add(new PlaybackTrack
            {
                SourceTrack = renderer.SourceTrack,
                Channel = channel,
                TimingMapRelativePath = DerivativeBuilder.Relative(paths, mapPath),
                TimingMapSha256 = Convert.ToHexStringLower(SHA256.HashData(mapBytes)),
                HasAudio = renderer.HasAudio,
            });
        }

        string digest = await DerivativeBuilder.HashFileAsync(staging, cancellationToken).ConfigureAwait(false);
        long size = new FileInfo(staging).Length;

        File.Move(staging, audioPath, overwrite: true);

        PlaybackDerivativeRecord record = new()
        {
            SessionId = request.SessionId,
            RelativePath = DerivativeBuilder.Relative(paths, audioPath),
            Sha256 = digest,
            SizeBytes = size,
            SampleRate = rate,
            Channels = 2,
            TotalFrames = total,
            Tracks = tracks,
            SourceManifestSha256 = sourceManifestSha256,
            ProcessingVersion = options.ProcessingVersion,
            CreatedUtc = request.CreatedAtUtc,
        };

        DerivativeBuilder.WriteAtomically(
            RecordPath(directory), JsonSerializer.SerializeToUtf8Bytes(record, PlaybackDerivativeRecord.Json));

        return record;
    }

    // -- reuse -----------------------------------------------------------------------------------

    /// <summary>
    /// Reuses a derivative only when every part of its identity matches and the file it names is
    /// still exactly the file that was hashed.
    /// </summary>
    internal static PlaybackDerivativeRecord? TryReuse(
        string directory, string sourceManifestSha256, PlaybackOptions options)
    {
        string recordPath = RecordPath(directory);
        if (!File.Exists(recordPath))
        {
            return null;
        }

        PlaybackDerivativeRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<PlaybackDerivativeRecord>(
                File.ReadAllBytes(recordPath), PlaybackDerivativeRecord.Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (record is null || !record.Matches(sourceManifestSha256, options))
        {
            return null;
        }

        string audioPath = Path.Combine(directory, "playback.wav");
        if (!File.Exists(audioPath))
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
            if (!string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(stream)), record.Sha256, StringComparison.Ordinal))
            {
                return null;
            }

            foreach (PlaybackTrack track in record.Tracks)
            {
                string mapPath = Path.Combine(directory, $"playback.{track.SourceTrack}.timing.json");
                if (!File.Exists(mapPath) ||
                    !string.Equals(
                        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(mapPath))),
                        track.TimingMapSha256,
                        StringComparison.Ordinal))
                {
                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return record;
    }

    private static string RecordPath(string directory) => Path.Combine(directory, "playback.derivative.json");

    private static string StagingPath(string directory) => Path.Combine(directory, "playback.wav.partial");

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next build overwrites it. A leftover staging file is never mistaken for a
            // derivative: only the record vouches for one, and it is written last.
        }
    }

    /// <summary>
    /// One track's contribution, produced in absolute session order.
    ///
    /// <para>
    /// The span layout is computed up front and is the single authority for where audio sits; the
    /// filling then only has to answer "what is at frame n", which is a lookup rather than an
    /// accumulation. That is the same discipline the transcription derivative uses, and it is why
    /// the two files agree about time to the sample.
    /// </para>
    /// </summary>
    private sealed class TrackRenderer : IDisposable
    {
        private readonly SessionPaths _paths;
        private readonly IReadOnlyList<RequestChunk> _chunks;
        private readonly int _rate;

        private PcmSourceReader? _reader;
        private short[] _window = [];
        private long _windowOffset;
        private AudioResampler? _resampler;
        private int _loadedChunk = -1;
        private int _spanCursor;

        private TrackRenderer(
            SessionPaths paths,
            string sourceTrack,
            IReadOnlyList<RequestChunk> chunks,
            IReadOnlyList<TimingSpan> spans,
            int rate)
        {
            _paths = paths;
            _chunks = chunks;
            _rate = rate;
            SourceTrack = sourceTrack;
            Spans = spans;
        }

        public string SourceTrack { get; }

        public IReadOnlyList<TimingSpan> Spans { get; }

        public bool HasAudio => _chunks.Count > 0;

        public static TrackRenderer For(
            SessionPaths paths,
            TranscriptionRequest request,
            string sourceTrack,
            int rate,
            long totalFrames)
        {
            RequestTrack? track = request.Tracks.FirstOrDefault(
                t => string.Equals(t.SourceTrack, sourceTrack, StringComparison.Ordinal));

            IReadOnlyList<RequestChunk> chunks = track is null
                ? []
                : [.. track.Chunks.OrderBy(c => c.Epoch).ThenBy(c => c.Index)];

            return new TrackRenderer(paths, sourceTrack, chunks, Layout(chunks, request, rate, totalFrames), rate);
        }

        /// <summary>
        /// Where every frame of this track comes from: source runs at their absolute session
        /// positions, explicit gaps in between, and explicit gaps at either end.
        /// </summary>
        private static List<TimingSpan> Layout(
            IReadOnlyList<RequestChunk> chunks,
            TranscriptionRequest request,
            int rate,
            long totalFrames)
        {
            List<TimingSpan> spans = [];
            long cursor = 0;
            int lastEpoch = request.Epochs.Count > 0 ? request.Epochs[^1].Index : 1;

            foreach (RequestChunk chunk in chunks)
            {
                long start = DerivativeBuilder.FrameAt(chunk.StartSeconds, rate);
                long end = Math.Min(totalFrames, DerivativeBuilder.FrameAt(chunk.EndSeconds, rate));

                if (start > cursor)
                {
                    spans.Add(Gap(cursor, start - cursor, chunk.Epoch, rate));
                    cursor = start;
                }
                else if (start < cursor)
                {
                    // Epochs are made monotonic upstream, so this means the snapshot itself is
                    // inconsistent. Overlapping audio cannot be laid down twice.
                    throw new SourceAudioException(FormattableString.Invariant(
                        $"chunk {chunk.Index} on the {chunk.Epoch} epoch starts before the previous one ended"));
                }

                long frames = Math.Max(0, end - cursor);
                if (frames > 0)
                {
                    spans.Add(Source(chunk, cursor, frames, rate));
                    cursor += frames;
                }
            }

            if (totalFrames > cursor)
            {
                // The session ran past this track: the other track kept going, or the epoch was
                // padded to a shared stop instant.
                spans.Add(Gap(cursor, totalFrames - cursor, lastEpoch, rate));
            }

            return spans;
        }

        /// <summary>Fills a run of frames starting at an absolute session frame.</summary>
        public async Task FillAsync(long startFrame, Memory<short> destination, CancellationToken cancellationToken)
        {
            destination.Span.Clear();

            int written = 0;
            while (written < destination.Length)
            {
                long frame = startFrame + written;
                TimingSpan? span = Advance(frame);

                if (span is null)
                {
                    return;
                }

                int take = (int)Math.Min(destination.Length - written, span.DerivativeEndFrame - frame);

                if (span.Kind == TimingSpanKind.Source)
                {
                    await LoadAsync(span, cancellationToken).ConfigureAwait(false);

                    Span<short> slice = destination.Span.Slice(written, take);
                    for (int i = 0; i < take; i++)
                    {
                        // Output frame index within the chunk, which is exactly what the
                        // transcription derivative feeds the resampler.
                        slice[i] = _resampler!.Sample(_window, _windowOffset, frame + i - span.DerivativeFrame);
                    }
                }

                written += take;
            }
        }

        /// <summary>The span holding a frame, walked forward only: filling is strictly in order.</summary>
        private TimingSpan? Advance(long frame)
        {
            while (_spanCursor < Spans.Count && !Spans[_spanCursor].Contains(frame))
            {
                if (Spans[_spanCursor].DerivativeEndFrame > frame)
                {
                    return null;
                }

                _spanCursor++;
            }

            return _spanCursor < Spans.Count ? Spans[_spanCursor] : null;
        }

        /// <summary>
        /// Decodes the chunk a span names, with the neighbouring chunks' edges as filter history.
        ///
        /// <para>
        /// Without them every chunk boundary would carry a small step discontinuity — inaudible
        /// once, and a periodic click through an hour of playback.
        /// </para>
        /// </summary>
        private async Task LoadAsync(TimingSpan span, CancellationToken cancellationToken)
        {
            if (_loadedChunk == span.ChunkIndex)
            {
                return;
            }

            int position = IndexOf(span.ChunkIndex!.Value);
            RequestChunk chunk = _chunks[position];

            _reader?.Dispose();
            _reader = PcmSourceReader.Open(
                DerivativeBuilder.Resolve(_paths, chunk.RelativePath), chunk.SampleRate, chunk.Channels);

            short[] mono = await _reader.ReadMonoAsync(cancellationToken).ConfigureAwait(false);

            int reach = AudioResampler.Reach;
            short[] before = await EdgeAsync(Neighbour(position, -1), reach, fromEnd: true, cancellationToken)
                .ConfigureAwait(false);
            short[] after = await EdgeAsync(Neighbour(position, +1), reach, fromEnd: false, cancellationToken)
                .ConfigureAwait(false);

            _window = new short[before.Length + mono.Length + after.Length];
            before.CopyTo(_window, 0);
            mono.CopyTo(_window, before.Length);
            after.CopyTo(_window, before.Length + mono.Length);

            _windowOffset = -before.Length;
            _resampler = new AudioResampler(chunk.SampleRate, _rate);
            _loadedChunk = chunk.Index;
        }

        private int IndexOf(int chunkIndex)
        {
            for (int i = 0; i < _chunks.Count; i++)
            {
                if (_chunks[i].Index == chunkIndex)
                {
                    return i;
                }
            }

            throw new SourceAudioException("a span names a chunk the request does not contain");
        }

        /// <summary>
        /// The neighbouring chunk, when it is genuinely contiguous with this one.
        ///
        /// <para>
        /// Across an epoch boundary or a format change there is no continuity to preserve, so the
        /// filter sees silence there instead. Borrowing samples across a pause would smear audio
        /// from one side of it into the other.
        /// </para>
        /// </summary>
        private RequestChunk? Neighbour(int position, int direction)
        {
            int index = position + direction;
            if (index < 0 || index >= _chunks.Count)
            {
                return null;
            }

            RequestChunk current = _chunks[position];
            RequestChunk candidate = _chunks[index];

            if (candidate.Epoch != current.Epoch ||
                candidate.SampleRate != current.SampleRate ||
                candidate.Channels != current.Channels)
            {
                return null;
            }

            double seam = direction > 0
                ? Math.Abs(candidate.StartSeconds - current.EndSeconds)
                : Math.Abs(current.StartSeconds - candidate.EndSeconds);

            return seam <= 1.0 / current.SampleRate ? candidate : null;
        }

        private async Task<short[]> EdgeAsync(
            RequestChunk? neighbour, int frames, bool fromEnd, CancellationToken cancellationToken)
        {
            if (neighbour is null || frames <= 0)
            {
                return [];
            }

            using PcmSourceReader reader = PcmSourceReader.Open(
                DerivativeBuilder.Resolve(_paths, neighbour.RelativePath), neighbour.SampleRate, neighbour.Channels);

            if (reader.Frames == 0)
            {
                return [];
            }

            short[] mono = await reader.ReadMonoAsync(cancellationToken).ConfigureAwait(false);
            int take = (int)Math.Min(frames, mono.Length);
            return fromEnd ? mono[^take..] : mono[..take];
        }

        private static TimingSpan Gap(long firstFrame, long frames, int epoch, int rate) => new()
        {
            Kind = TimingSpanKind.Gap,
            DerivativeFrame = firstFrame,
            Frames = frames,
            Epoch = epoch,
            SessionStartSeconds = (double)firstFrame / rate,
            SessionEndSeconds = (double)(firstFrame + frames) / rate,
        };

        private static TimingSpan Source(RequestChunk chunk, long firstFrame, long frames, int rate) => new()
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

        public void Dispose() => _reader?.Dispose();
    }
}
