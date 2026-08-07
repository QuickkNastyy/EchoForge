using System.Security.Cryptography;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Processing;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// Building 16 kHz mono processing audio from immutable source chunks, and being able to get
/// back.
///
/// <para>
/// The properties under test are the ones a transcript's usefulness rests on: that a derivative
/// frame means one exact moment of one exact source file, that the meaning does not drift over
/// hours, and that a gap in the recording stays a gap.
/// </para>
/// </summary>
public sealed class DerivativePipelineTests : IDisposable
{
    private const string SessionId = "01JDERIVE";

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _sessions;

    public DerivativePipelineTests()
    {
        _sessions = new FileSessionStore(_temp.Path);
        _sessions.Create(SessionId);
    }

    public void Dispose() => _temp.Dispose();

    // -- fixtures ------------------------------------------------------------------------------

    private sealed record ChunkSpec(
        int Epoch,
        double Seconds,
        int SampleRate = 48000,
        int Channels = 2,
        bool Silent = false,
        int Seed = 1);

    /// <summary>Writes a canonical PCM16 WAV whose content is arithmetic, so runs agree.</summary>
    private static long WriteWav(string path, ChunkSpec spec)
    {
        long frames = (long)Math.Round(spec.Seconds * spec.SampleRate);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] data = new byte[frames * spec.Channels * 2];
        if (!spec.Silent)
        {
            for (long frame = 0; frame < frames; frame++)
            {
                for (int channel = 0; channel < spec.Channels; channel++)
                {
                    // A low tone, well under every Nyquist in play, so resampling should
                    // preserve it rather than filter it away.
                    double phase = 2 * Math.PI * (200 + (spec.Seed * 10) + (channel * 50)) * frame / spec.SampleRate;
                    short value = (short)(9000 * Math.Sin(phase));
                    BitConverter.TryWriteBytes(data.AsSpan((int)((frame * spec.Channels) + channel) * 2, 2), value);
                }
            }
        }

        byte[] header = new byte[44];
        long dataBytes = data.Length;
        "RIFF"u8.CopyTo(header.AsSpan(0));
        BitConverter.TryWriteBytes(header.AsSpan(4), (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BitConverter.TryWriteBytes(header.AsSpan(16), 16u);
        BitConverter.TryWriteBytes(header.AsSpan(20), (ushort)1);
        BitConverter.TryWriteBytes(header.AsSpan(22), (ushort)spec.Channels);
        BitConverter.TryWriteBytes(header.AsSpan(24), (uint)spec.SampleRate);
        BitConverter.TryWriteBytes(header.AsSpan(28), (uint)(spec.SampleRate * spec.Channels * 2));
        BitConverter.TryWriteBytes(header.AsSpan(32), (ushort)(spec.Channels * 2));
        BitConverter.TryWriteBytes(header.AsSpan(34), (ushort)16);
        "data"u8.CopyTo(header.AsSpan(36));
        BitConverter.TryWriteBytes(header.AsSpan(40), (uint)dataBytes);

        using FileStream stream = File.Create(path);
        stream.Write(header);
        stream.Write(data);
        return frames;
    }

    /// <summary>
    /// Builds a session with the given chunks on the microphone track and returns the request
    /// the worker (and the derivative builder) would be given.
    /// </summary>
    private TranscriptionRequest GivenSession(params ChunkSpec[] specs)
    {
        SessionPaths paths = _sessions.Resolve(SessionId);
        DateTimeOffset origin = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

        Dictionary<int, double> epochLength = [];
        List<AudioChunkMetadata> chunks = [];
        Dictionary<int, double> cursor = [];

        for (int i = 0; i < specs.Length; i++)
        {
            ChunkSpec spec = specs[i];
            string relative = $"tracks/microphone/chunks/{i + 1:D6}.wav";
            string path = Path.Combine(paths.Root, relative.Replace('/', Path.DirectorySeparatorChar));

            long frames = WriteWav(path, spec);
            double start = cursor.GetValueOrDefault(spec.Epoch);
            double length = (double)frames / spec.SampleRate;

            using FileStream stream = File.OpenRead(path);
            string digest = Convert.ToHexStringLower(SHA256.HashData(stream));

            chunks.Add(new AudioChunkMetadata(
                i + 1, relative, SourceTrack.Microphone, start, start + length,
                spec.SampleRate, spec.Channels, frames, digest, [], spec.Epoch));

            cursor[spec.Epoch] = start + length;
            epochLength[spec.Epoch] = cursor[spec.Epoch];
        }

        // Epochs are separated by a real gap: five seconds where the recorder was not running.
        List<SessionEpoch> epochs = [];
        double wall = 0;
        foreach (int index in epochLength.Keys.Order())
        {
            epochs.Add(new SessionEpoch(
                index, origin.AddSeconds(wall), origin.AddSeconds(wall + epochLength[index]), 0, 1, EpochEndReason.Paused));
            wall += epochLength[index] + 5;
        }

        SessionSnapshot snapshot = new(
            SessionId, SessionState.Recorded, origin, origin, origin.AddSeconds(wall), epochs,
            [
                new SessionTrack(
                    SourceTrack.Microphone, "mic", "mic",
                    new CaptureFormat(specs[0].SampleRate, specs[0].Channels, 16), chunks),
            ]);

        _sessions.WriteSnapshot(snapshot);

        RequestBuildResult built = TranscriptionRequestBuilder.Build(
            snapshot, paths.Root, Path.Combine(paths.Root, "out.json"), 1, origin,
            new RequestOptions { Backend = "mock" });

        Assert.True(built.Succeeded, built.Failure?.Detail);
        return built.Request!;
    }

    private DerivativeBuilder Builder() => new(_sessions);

    private static async Task<(TimingMap Map, short[] Samples)> ReadDerivativeAsync(
        FileSessionStore sessions, DerivativeRecord record)
    {
        SessionPaths paths = sessions.Resolve(SessionId);
        string audio = Path.Combine(paths.Root, record.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        string map = Path.Combine(paths.Root, record.TimingMapRelativePath.Replace('/', Path.DirectorySeparatorChar));

        byte[] bytes = await File.ReadAllBytesAsync(audio);
        short[] samples = new short[(bytes.Length - 44) / 2];
        Buffer.BlockCopy(bytes, 44, samples, 0, samples.Length * 2);

        TimingMap timing = System.Text.Json.JsonSerializer.Deserialize<TimingMap>(
            await File.ReadAllBytesAsync(map), TimingMap.Json)!;

        return (timing, samples);
    }

    // -- format conversion -------------------------------------------------------------------------

    [Theory]
    [InlineData(48000, 2)]
    [InlineData(48000, 1)]
    [InlineData(44100, 2)]
    [InlineData(44100, 1)]
    [InlineData(16000, 1)]
    [InlineData(8000, 1)]
    public async Task EverySupportedSourceFormatBecomesSixteenKilohertzMono(int rate, int channels)
    {
        TranscriptionRequest request = GivenSession(new ChunkSpec(1, 2.0, rate, channels));

        DerivativeBuildResult result = await Builder().BuildAsync(request);

        Assert.True(result.Succeeded, result.Detail);
        DerivativeRecord record = result.Set!.For("microphone")!;

        Assert.Equal(16000, record.SampleRate);
        Assert.Equal(1, record.Channels);

        (TimingMap map, short[] samples) = await ReadDerivativeAsync(_sessions, record);

        Assert.Equal(record.TotalFrames, samples.Length);
        Assert.Equal(16000, map.SampleRate);
        Assert.Equal(2.0, map.DurationSeconds, 3);

        // The tone survives: this is a resampler, not a mute button.
        Assert.True(samples.Max(Math.Abs) > 3000, "the derivative is far quieter than its source");
    }

    [Fact]
    public async Task ChannelsAreMixedDeterministicallyRatherThanDropped()
    {
        // Two channels whose content differs; averaging must consider both.
        short[] interleaved = [1000, 2000, -400, 400, 32767, 32767, -32768, -32768];
        short[] mono = new short[4];

        AudioResampler.MixToMono(interleaved, 2, mono);

        Assert.Equal(1500, mono[0]);
        Assert.Equal(0, mono[1]);
        Assert.Equal(32767, mono[2]);
        Assert.Equal(-32768, mono[3]);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task MixedSampleRatesAcrossEpochsAreEachConvertedCorrectly()
    {
        TranscriptionRequest request = GivenSession(
            new ChunkSpec(1, 1.5, 48000, 2),
            new ChunkSpec(2, 1.5, 16000, 1));

        DerivativeBuildResult result = await Builder().BuildAsync(request);
        Assert.True(result.Succeeded, result.Detail);

        (TimingMap map, short[] samples) = await ReadDerivativeAsync(_sessions, result.Set!.For("microphone")!);

        List<TimingSpan> sources = [.. map.Spans.Where(s => s.Kind == TimingSpanKind.Source)];
        Assert.Equal(2, sources.Count);
        Assert.Equal(48000, sources[0].SourceSampleRate);
        Assert.Equal(16000, sources[1].SourceSampleRate);

        // Each span is 1.5 seconds of output regardless of what it came from.
        foreach (TimingSpan span in sources)
        {
            Assert.Equal(1.5, (double)span.Frames / map.SampleRate, 3);
        }

        Assert.True(samples.Max(Math.Abs) > 3000);
    }

    // -- the timeline -------------------------------------------------------------------------------

    [Fact]
    public async Task AnEpochGapBecomesExplicitSilenceAndNoSourceSpanCrossesIt()
    {
        TranscriptionRequest request = GivenSession(
            new ChunkSpec(1, 1.0, 16000, 1),
            new ChunkSpec(2, 1.0, 16000, 1, Seed: 2));

        DerivativeBuildResult result = await Builder().BuildAsync(request);
        (TimingMap map, short[] samples) = await ReadDerivativeAsync(_sessions, result.Set!.For("microphone")!);

        TimingSpan gap = Assert.Single(map.Spans, s => s.Kind == TimingSpanKind.Gap);
        Assert.Equal(5.0, (double)gap.Frames / map.SampleRate, 3);

        // The gap really is silence, not the next epoch pulled forward.
        for (long frame = gap.DerivativeFrame; frame < gap.DerivativeEndFrame; frame++)
        {
            Assert.Equal(0, samples[frame]);
        }

        // No source span straddles the gap, so no transcript time can be attributed across it.
        foreach (TimingSpan span in map.Spans.Where(s => s.Kind == TimingSpanKind.Source))
        {
            Assert.True(
                span.DerivativeEndFrame <= gap.DerivativeFrame || span.DerivativeFrame >= gap.DerivativeEndFrame,
                $"{span.ChunkIndex} crosses the gap");
        }

        SourcePosition inside = map.Resolve(gap.DerivativeFrame + 100)!;
        Assert.True(inside.IsGap);
        Assert.Null(inside.ChunkIndex);
    }

    [Fact]
    public async Task EveryDerivativeFrameResolvesBackToExactlyOneSourcePosition()
    {
        TranscriptionRequest request = GivenSession(
            new ChunkSpec(1, 1.0, 48000, 2),
            new ChunkSpec(1, 1.0, 48000, 2, Seed: 2));

        DerivativeBuildResult result = await Builder().BuildAsync(request);
        (TimingMap map, _) = await ReadDerivativeAsync(_sessions, result.Set!.For("microphone")!);

        foreach (long frame in (long[])[0, 1, 8000, 15999, 16000, 24000, map.TotalFrames - 1])
        {
            SourcePosition? position = map.Resolve(frame);
            Assert.NotNull(position);

            if (!position!.IsGap)
            {
                Assert.NotNull(position.ChunkIndex);
                Assert.NotNull(position.SourceFrame);
                Assert.Equal(48000, position.SourceSampleRate);

                // A derivative frame maps into its own chunk, never past the end of it.
                Assert.InRange(position.SourceFrame!.Value, 0, 48000);
            }
        }

        // Exactly one span owns each frame: the spans tile the derivative without overlapping.
        long expected = 0;
        foreach (TimingSpan span in map.Spans)
        {
            Assert.Equal(expected, span.DerivativeFrame);
            expected += span.Frames;
        }

        Assert.Equal(map.TotalFrames, expected);
    }

    [Fact]
    public async Task RoundingDoesNotAccumulateAcrossManyChunks()
    {
        // Sixty chunks of an awkward length at an awkward rate: if each chunk's position were
        // derived from a running total of rounded lengths, the error would be visible by the end.
        ChunkSpec[] specs = [.. Enumerable.Range(0, 60).Select(i => new ChunkSpec(1, 0.7, 44100, 1, Seed: i))];
        TranscriptionRequest request = GivenSession(specs);

        DerivativeBuildResult result = await Builder().BuildAsync(request);
        (TimingMap map, _) = await ReadDerivativeAsync(_sessions, result.Set!.For("microphone")!);

        List<TimingSpan> sources = [.. map.Spans.Where(s => s.Kind == TimingSpanKind.Source)];
        Assert.Equal(60, sources.Count);

        // Every chunk's start is where absolute session time says, to within half a frame -
        // not within half a frame times sixty.
        for (int i = 0; i < sources.Count; i++)
        {
            double expected = request.Tracks[0].Chunks[i].StartSeconds;
            Assert.Equal(expected, sources[i].SessionStartSeconds, 4);
        }

        double total = request.Tracks[0].Chunks[^1].EndSeconds;
        Assert.Equal(total, map.DurationSeconds, 4);
    }

    [Fact]
    public async Task ContiguousChunksProduceContinuousAudioAcrossTheirBoundary()
    {
        TranscriptionRequest request = GivenSession(
            new ChunkSpec(1, 0.5, 16000, 1),
            new ChunkSpec(1, 0.5, 16000, 1));

        DerivativeBuildResult result = await Builder().BuildAsync(request);
        (TimingMap map, short[] samples) = await ReadDerivativeAsync(_sessions, result.Set!.For("microphone")!);

        long seam = map.Spans.First(s => s.Kind == TimingSpanKind.Source).Frames;

        // The two chunks hold the same tone at the same phase offset, so a seam that lost or
        // duplicated samples would show up as a step. Compare the jump at the boundary with the
        // jumps just before it.
        int atSeam = Math.Abs(samples[seam] - samples[seam - 1]);
        int typical = Enumerable.Range(2, 40).Max(i => Math.Abs(samples[seam - i] - samples[seam - i - 1]));

        Assert.True(atSeam <= typical * 4, $"a step of {atSeam} appeared at the chunk boundary (typical {typical})");
    }

    // -- safety ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSourceAudioIsNeverTouched()
    {
        TranscriptionRequest request = GivenSession(new ChunkSpec(1, 1.0), new ChunkSpec(1, 1.0));
        string tracks = _sessions.Resolve(SessionId).TracksRoot;

        DirectorySnapshot before = DirectorySnapshot.Capture(tracks);
        Assert.True((await Builder().BuildAsync(request)).Succeeded);
        DirectorySnapshot after = DirectorySnapshot.Capture(tracks);

        Assert.True(after.Matches(before), after.Describe());
    }

    [Fact]
    public async Task AnUnreadableSourceChunkFailsTheJobRatherThanBeingSkipped()
    {
        TranscriptionRequest request = GivenSession(new ChunkSpec(1, 1.0), new ChunkSpec(1, 1.0));

        string path = Path.Combine(
            _sessions.Resolve(SessionId).Root, "tracks", "microphone", "chunks", "000002.wav");
        await File.WriteAllTextAsync(path, "this is not a RIFF file at all");

        DerivativeBuildResult result = await Builder().BuildAsync(request);

        // Skipping it would produce a derivative that is silently missing a minute of audio,
        // and every timestamp after that point would be wrong.
        Assert.False(result.Succeeded);
        Assert.Equal("source_audio_invalid", result.Code);
    }

    [Fact]
    public async Task AChunkThatDisagreesWithItsMetadataFailsTheJob()
    {
        TranscriptionRequest request = GivenSession(new ChunkSpec(1, 1.0, 16000, 1));

        // Rewrite the file at a different rate from the one the metadata records.
        string path = Path.Combine(
            _sessions.Resolve(SessionId).Root, "tracks", "microphone", "chunks", "000001.wav");
        WriteWav(path, new ChunkSpec(1, 1.0, 44100, 1));

        DerivativeBuildResult result = await Builder().BuildAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("metadata says", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationLeavesNoDerivativeAndNoStagedFile()
    {
        TranscriptionRequest request = GivenSession(
            [.. Enumerable.Range(0, 40).Select(i => new ChunkSpec(1, 1.0, 48000, 2, Seed: i))]);

        using CancellationTokenSource cancellation = new();
        DerivativeBuilder builder = Builder();
        builder.Progress += (_, e) =>
        {
            if (e.CompletedChunks >= 2)
            {
                cancellation.Cancel();
            }
        };

        DerivativeBuildResult result = await builder.BuildAsync(request, cancellationToken: cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("cancelled", result.Code);

        string directory = DerivativeBuilder.DerivativeDirectory(_sessions.Resolve(SessionId), new DerivativeOptions());
        Assert.False(File.Exists(Path.Combine(directory, "microphone.wav")));
    }

    // -- determinism and reuse ------------------------------------------------------------------------------

    [Fact]
    public async Task TheSameSourceAlwaysProducesTheSameBytes()
    {
        TranscriptionRequest request = GivenSession(new ChunkSpec(1, 1.0, 44100, 2), new ChunkSpec(1, 1.0, 44100, 2));

        DerivativeRecord first = (await Builder().BuildAsync(request)).Set!.For("microphone")!;
        string directory = DerivativeBuilder.DerivativeDirectory(_sessions.Resolve(SessionId), new DerivativeOptions());
        byte[] firstBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "microphone.wav"));

        // Force a rebuild by removing the record that would otherwise allow reuse.
        File.Delete(Path.Combine(directory, "microphone.derivative.json"));

        DerivativeRecord second = (await Builder().BuildAsync(request)).Set!.For("microphone")!;
        byte[] secondBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "microphone.wav"));

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(firstBytes, secondBytes);
    }

    [Fact]
    public async Task AnExistingDerivativeIsReusedRatherThanRebuilt()
    {
        TranscriptionRequest request = GivenSession(new ChunkSpec(1, 1.0));

        DerivativeRecord first = (await Builder().BuildAsync(request)).Set!.For("microphone")!;
        string audio = Path.Combine(
            DerivativeBuilder.DerivativeDirectory(_sessions.Resolve(SessionId), new DerivativeOptions()), "microphone.wav");
        DateTime written = File.GetLastWriteTimeUtc(audio);

        await Task.Delay(50);
        DerivativeRecord second = (await Builder().BuildAsync(request)).Set!.For("microphone")!;

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(written, File.GetLastWriteTimeUtc(audio));
    }

    [Fact]
    public async Task ADifferentProcessingVersionBuildsSeparatelyRatherThanReusing()
    {
        TranscriptionRequest request = GivenSession(new ChunkSpec(1, 1.0));

        await Builder().BuildAsync(request);
        DerivativeOptions changed = new() { ProcessingVersion = "derivative-v2-test" };

        DerivativeBuildResult result = await Builder().BuildAsync(request, changed);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("derivative-v2-test", result.Set!.For("microphone")!.ProcessingVersion);

        // Two versions, two directories: a changed resampler must never reuse the old output.
        SessionPaths paths = _sessions.Resolve(SessionId);
        Assert.True(File.Exists(Path.Combine(DerivativeBuilder.DerivativeDirectory(paths, new DerivativeOptions()), "microphone.wav")));
        Assert.True(File.Exists(Path.Combine(DerivativeBuilder.DerivativeDirectory(paths, changed), "microphone.wav")));
    }

    [Fact]
    public async Task ADerivativeWhoseBytesChangedIsNotReused()
    {
        TranscriptionRequest request = GivenSession(new ChunkSpec(1, 1.0));
        DerivativeRecord first = (await Builder().BuildAsync(request)).Set!.For("microphone")!;

        string audio = Path.Combine(
            DerivativeBuilder.DerivativeDirectory(_sessions.Resolve(SessionId), new DerivativeOptions()), "microphone.wav");
        byte[] tampered = await File.ReadAllBytesAsync(audio);
        tampered[100] ^= 0xFF;
        await File.WriteAllBytesAsync(audio, tampered);

        DerivativeRecord second = (await Builder().BuildAsync(request)).Set!.For("microphone")!;

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(audio))));
    }

    // -- memory ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MemoryStaysBoundedHoweverMuchAudioThereIs()
    {
        // Twenty chunks of five seconds at 48 kHz stereo: about 19 MB of source, processed under
        // a ceiling that does not move with the total. A builder holding the meeting rather than
        // one chunk at a time would sail past it.
        ChunkSpec[] specs = [.. Enumerable.Range(0, 20).Select(i => new ChunkSpec(1, 5.0, 48000, 2, Seed: i))];
        TranscriptionRequest request = GivenSession(specs);

        long sourceBytes = request.Tracks[0].Chunks.Sum(c => c.Frames * c.Channels * 2);

        // A fixed ceiling, comfortably above one chunk's working set - a five-second 48 kHz
        // stereo chunk is under a megabyte as mono - and far below the source total.
        const long ceiling = 6 * 1024 * 1024;
        Assert.True(sourceBytes > ceiling * 2, "the fixture is too small to prove anything");

        // GC.GetTotalMemory is process-wide, and the rest of the suite is running in the same
        // process at the same time. Their allocations land in this reading and can only push it
        // up, so a single sample is an upper bound on what the builder holds rather than a
        // measurement of it - which is why this test passed alone and failed intermittently in
        // the full run. Taking the lowest of several readings removes the noise without weakening
        // what is asserted: a builder that really held the whole meeting could not produce a low
        // reading, however quiet the rest of the process happened to be.
        long best = long.MaxValue;

        for (int attempt = 0; attempt < 3 && best >= ceiling; attempt++)
        {
            // A built derivative is reused, and a reused one processes nothing. Each attempt
            // starts from nothing so it measures the same work as the first.
            string built = DerivativeBuilder.DerivativeDirectory(_sessions.Resolve(SessionId), new DerivativeOptions());
            if (Directory.Exists(built))
            {
                Directory.Delete(built, recursive: true);
            }

            long baseline = GC.GetTotalMemory(forceFullCollection: true);
            long peak = baseline;
            int samples = 0;

            DerivativeBuilder builder = Builder();

            // forceFullCollection, so what is measured is what the builder is still holding
            // rather than garbage that simply has not been collected yet.
            builder.Progress += (_, _) =>
            {
                samples++;
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
            };

            Assert.True((await builder.BuildAsync(request)).Succeeded);

            // An attempt that reused an earlier build would report almost no growth and pass
            // for the wrong reason.
            Assert.True(samples > 0, $"attempt {attempt} did no work");

            best = Math.Min(best, peak - baseline);
        }

        Assert.True(
            best < ceiling,
            $"live heap grew by {best} bytes while processing {sourceBytes} bytes of source");
    }
}
