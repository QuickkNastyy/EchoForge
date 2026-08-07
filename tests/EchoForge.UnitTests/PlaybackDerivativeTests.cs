using System.Text.Json;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Playback;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Workers;
using EchoForge.Infrastructure.Playback;

namespace EchoForge.UnitTests;

/// <summary>
/// Building the aligned two-track file a meeting is listened to from.
///
/// <para>
/// The property every one of these tests is really about is the same: <b>a frame of the derivative
/// means one exact moment of the meeting</b>. Pauses stay pauses, epochs land where they happened,
/// two tracks recorded at different rates line up with each other, and none of it drifts as the
/// meeting gets longer. That is what makes clicking a citation land on the sentence it cites rather
/// than near it.
/// </para>
/// </summary>
public sealed class PlaybackDerivativeTests : IDisposable
{
    private readonly PlaybackFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private PlaybackDerivativeBuilder Builder() => new(_fixture.Sessions);

    private static PlaybackChunkSpec Mic(int epoch, double seconds, short level, int rate = 48000, int channels = 1, double gapBefore = 0) =>
        new(SourceTrack.Microphone, epoch, seconds, level, rate, channels, gapBefore);

    private static PlaybackChunkSpec Sys(int epoch, double seconds, short level, int rate = 48000, int channels = 2, double gapBefore = 0) =>
        new(SourceTrack.System, epoch, seconds, level, rate, channels, gapBefore);

    /// <summary>Reads the whole derivative as interleaved samples.</summary>
    private short[] ReadSamples(PlaybackDerivativeRecord record)
    {
        string path = Path.Combine(
            _fixture.Paths.Root, record.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        byte[] bytes = File.ReadAllBytes(path);
        short[] samples = new short[(bytes.Length - 44) / 2];
        Buffer.BlockCopy(bytes, 44, samples, 0, samples.Length * 2);
        return samples;
    }

    /// <summary>The sample on one channel at a session-relative moment.</summary>
    private static short At(short[] samples, PlaybackDerivativeRecord record, double seconds, int channel)
    {
        long frame = (long)Math.Round(seconds * record.SampleRate);
        return samples[(frame * record.Channels) + channel];
    }

    /// <summary>
    /// Asserts the level at a session moment.
    ///
    /// <para>
    /// With a tolerance, because a band-limited resampler is not an exact copier: the filter
    /// ripples by a few counts either side of a constant. Anything wider than a handful of counts
    /// would be a different chunk, not a filter artefact.
    /// </para>
    /// </summary>
    private static void Level(
        short[] samples, PlaybackDerivativeRecord record, double seconds, int channel, int expected, int tolerance)
    {
        short actual = At(samples, record, seconds, channel);

        Assert.True(
            Math.Abs(actual - expected) <= tolerance,
            $"at {seconds}s channel {channel}: expected about {expected}, found {actual}");
    }

    private static TimingMap ReadMap(PlaybackFixture fixture, PlaybackDerivativeRecord record, string track)
    {
        PlaybackTrack entry = record.For(track)!;
        string path = Path.Combine(
            fixture.Paths.Root, entry.TimingMapRelativePath.Replace('/', Path.DirectorySeparatorChar));

        return JsonSerializer.Deserialize<TimingMap>(File.ReadAllBytes(path), TimingMap.Json)!;
    }

    // -- shape -------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoTrackMeetingKeepsEachTrackOnItsOwnChannel()
    {
        TranscriptionRequest request = _fixture.Build(
            Mic(1, 4.0, 3000),
            Sys(1, 4.0, 6000));

        PlaybackBuildResult result = await Builder().BuildAsync(request);

        Assert.True(result.Succeeded, result.Detail);
        PlaybackDerivativeRecord record = result.Record!;

        Assert.Equal(2, record.Channels);
        Assert.Equal(24000, record.SampleRate);
        Assert.Equal(4.0, record.DurationSeconds, 3);

        short[] samples = ReadSamples(record);

        // Mixing them here would have thrown away which half was You.
        Level(samples, record, 2.0, PlaybackChannels.You, 3000, 30);
        Level(samples, record, 2.0, PlaybackChannels.Remote, 6000, 30);

        Assert.True(record.For("microphone")!.HasAudio);
        Assert.True(record.For("system")!.HasAudio);
    }

    [Fact]
    public async Task MicrophoneOnlyMeetingProducesASilentRemoteChannelRatherThanADifferentShape()
    {
        TranscriptionRequest request = _fixture.Build(Mic(1, 3.0, 4000));

        PlaybackBuildResult result = await Builder().BuildAsync(request);
        PlaybackDerivativeRecord record = result.Record!;
        short[] samples = ReadSamples(record);

        Assert.Equal(2, record.Channels);
        Level(samples, record, 1.5, PlaybackChannels.You, 4000, 30);
        Assert.Equal(0, At(samples, record, 1.5, PlaybackChannels.Remote));

        Assert.True(record.For("microphone")!.HasAudio);
        Assert.False(record.For("system")!.HasAudio);
    }

    [Fact]
    public async Task SystemOnlyMeetingProducesASilentYouChannel()
    {
        TranscriptionRequest request = _fixture.Build(Sys(1, 3.0, 5000));

        PlaybackBuildResult result = await Builder().BuildAsync(request);
        PlaybackDerivativeRecord record = result.Record!;
        short[] samples = ReadSamples(record);

        Assert.Equal(0, At(samples, record, 1.5, PlaybackChannels.You));
        Level(samples, record, 1.5, PlaybackChannels.Remote, 5000, 30);

        Assert.False(record.For("microphone")!.HasAudio);
        Assert.True(record.For("system")!.HasAudio);
    }

    [Theory]
    [InlineData(48000, 44100)]
    [InlineData(16000, 48000)]
    [InlineData(44100, 8000)]
    public async Task TracksRecordedAtDifferentRatesStillLineUpWithEachOther(int micRate, int systemRate)
    {
        TranscriptionRequest request = _fixture.Build(
            Mic(1, 3.0, 2500, micRate),
            Sys(1, 3.0, 7500, systemRate, channels: 1));

        PlaybackBuildResult result = await Builder().BuildAsync(request);
        PlaybackDerivativeRecord record = result.Record!;
        short[] samples = ReadSamples(record);

        // The same instant on both channels, whatever each was recorded at.
        Level(samples, record, 1.5, PlaybackChannels.You, 2500, 40);
        Level(samples, record, 1.5, PlaybackChannels.Remote, 7500, 40);
    }

    [Fact]
    public async Task StereoSourcesAreFoldedToOneChannelWithoutChangingLevel()
    {
        TranscriptionRequest request = _fixture.Build(Sys(1, 2.0, 6000, channels: 2));

        PlaybackBuildResult result = await Builder().BuildAsync(request);
        PlaybackDerivativeRecord record = result.Record!;

        // Averaged rather than summed: two identical channels stay at their own level rather
        // than clipping the moment they become one.
        Level(ReadSamples(record), record, 1.0, PlaybackChannels.Remote, 6000, 40);
    }

    // -- time --------------------------------------------------------------------------------------

    [Fact]
    public async Task ChunkBoundariesAreContinuousRatherThanAStepOrARepeat()
    {
        TranscriptionRequest request = _fixture.Build(
            Mic(1, 2.0, 4000),
            Mic(1, 2.0, 4000));

        PlaybackBuildResult result = await Builder().BuildAsync(request);
        PlaybackDerivativeRecord record = result.Record!;
        short[] samples = ReadSamples(record);

        Assert.Equal(4.0, record.DurationSeconds, 3);

        // Either side of the seam at two seconds, and on it.
        foreach (double when in (double[])[1.95, 1.999, 2.0, 2.001, 2.05])
        {
            Level(samples, record, when, PlaybackChannels.You, 4000, 60);
        }
    }

    [Fact]
    public async Task APauseBetweenEpochsBecomesRealSilenceAndDoesNotShiftWhatFollows()
    {
        TranscriptionRequest request = _fixture.Build(
            Mic(1, 3.0, 3000),
            Mic(2, 3.0, 9000));

        PlaybackBuildResult result = await Builder().BuildAsync(request);
        PlaybackDerivativeRecord record = result.Record!;
        short[] samples = ReadSamples(record);

        double secondEpoch = PlaybackFixture.EpochStart(request, 2);
        Assert.Equal(3.0 + PlaybackFixture.EpochGapSeconds, secondEpoch, 3);
        Assert.Equal(secondEpoch + 3.0, record.DurationSeconds, 3);

        // Inside the pause nothing was recorded, so nothing is there.
        Assert.Equal(0, At(samples, record, 5.0, PlaybackChannels.You));

        // And the second epoch is still where the session says it is. Closing the gap instead
        // would put this moment five seconds early for the rest of the meeting.
        Level(samples, record, secondEpoch + 1.5, PlaybackChannels.You, 9000, 40);
    }

    [Fact]
    public async Task AGapInsideOneEpochIsPreservedToo()
    {
        TranscriptionRequest request = _fixture.Build(
            Mic(1, 2.0, 3000),
            Mic(1, 2.0, 8000, gapBefore: 4.0));

        PlaybackBuildResult result = await Builder().BuildAsync(request);
        PlaybackDerivativeRecord record = result.Record!;
        short[] samples = ReadSamples(record);

        Assert.Equal(8.0, record.DurationSeconds, 3);
        Level(samples, record, 1.0, PlaybackChannels.You, 3000, 40);
        Assert.Equal(0, At(samples, record, 4.0, PlaybackChannels.You));
        Level(samples, record, 7.0, PlaybackChannels.You, 8000, 40);
    }

    [Fact]
    public async Task OneTrackEndingEarlyLeavesSilenceRatherThanShorteningTheMeeting()
    {
        TranscriptionRequest request = _fixture.Build(
            Mic(1, 6.0, 3000),
            Sys(1, 2.0, 7000, channels: 1));

        PlaybackBuildResult result = await Builder().BuildAsync(request);
        PlaybackDerivativeRecord record = result.Record!;
        short[] samples = ReadSamples(record);

        Assert.Equal(6.0, record.DurationSeconds, 3);
        Level(samples, record, 5.0, PlaybackChannels.You, 3000, 40);
        Assert.Equal(0, At(samples, record, 5.0, PlaybackChannels.Remote));
    }

    [Fact]
    public async Task TheTimingMapNamesTheChunkBehindEveryMomentAndTheGapsBetweenThem()
    {
        TranscriptionRequest request = _fixture.Build(
            Mic(1, 3.0, 3000),
            Mic(2, 3.0, 9000));

        PlaybackBuildResult result = await Builder().BuildAsync(request);
        PlaybackDerivativeRecord record = result.Record!;

        TimingMap map = ReadMap(_fixture, record, "microphone");
        Assert.Equal(record.SampleRate, map.SampleRate);
        Assert.Equal(record.TotalFrames, map.TotalFrames);

        SourcePosition inFirst = map.Resolve(map.FrameAt(1.0))!;
        Assert.False(inFirst.IsGap);
        Assert.Equal(1, inFirst.ChunkIndex);
        Assert.Equal(1, inFirst.Epoch);

        SourcePosition inPause = map.Resolve(map.FrameAt(5.0))!;
        Assert.True(inPause.IsGap);
        Assert.Null(inPause.ChunkIndex);

        SourcePosition inSecond = map.Resolve(map.FrameAt(PlaybackFixture.EpochStart(request, 2) + 1.0))!;
        Assert.False(inSecond.IsGap);
        Assert.Equal(2, inSecond.Epoch);
    }

    [Fact]
    public async Task TimeDoesNotDriftAcrossManyChunks()
    {
        // Sixty one-second chunks: enough that a per-chunk rounding error would be visible, and
        // 44.1 kHz so the ratio to 24 kHz is not a whole number.
        PlaybackChunkSpec[] specs = [.. Enumerable.Range(0, 60).Select(i => Mic(1, 1.0, (short)(1000 + (i * 100)), 44100))];

        TranscriptionRequest request = _fixture.Build(specs);
        PlaybackBuildResult result = await Builder().BuildAsync(request);
        PlaybackDerivativeRecord record = result.Record!;
        short[] samples = ReadSamples(record);

        Assert.Equal(60.0, record.DurationSeconds, 3);

        // The last chunk is still exactly where its own second says it is.
        Level(samples, record, 59.5, PlaybackChannels.You, 1000 + (59 * 100), 60);
        Level(samples, record, 30.5, PlaybackChannels.You, 1000 + (30 * 100), 60);
    }

    // -- identity ----------------------------------------------------------------------------------

    [Fact]
    public async Task TheSameMeetingProducesTheSameBytesTwice()
    {
        TranscriptionRequest request = _fixture.Build(
            Mic(1, 2.0, 3000, 44100),
            Sys(1, 2.0, 6000, 48000, channels: 2),
            Mic(2, 2.0, 5000, 44100));

        PlaybackDerivativeRecord first = (await Builder().BuildAsync(request)).Record!;

        // A different directory, so nothing can be reused: this has to be recomputed.
        PlaybackOptions elsewhere = new() { ProcessingVersion = "playback-v1-copy" };
        PlaybackDerivativeRecord second = (await Builder().BuildAsync(request, elsewhere)).Record!;

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.TotalFrames, second.TotalFrames);
    }

    [Fact]
    public async Task AValidDerivativeIsReusedRatherThanRebuilt()
    {
        TranscriptionRequest request = _fixture.Build(Mic(1, 2.0, 3000));

        PlaybackDerivativeRecord first = (await Builder().BuildAsync(request)).Record!;
        string path = Path.Combine(
            _fixture.Paths.Root, first.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        DateTime written = File.GetLastWriteTimeUtc(path);

        PlaybackDerivativeRecord second = (await Builder().BuildAsync(request)).Record!;

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ChangedSourceAudioIsNotReused()
    {
        TranscriptionRequest request = _fixture.Build(Mic(1, 2.0, 3000));
        PlaybackDerivativeRecord first = (await Builder().BuildAsync(request)).Record!;

        // A different manifest is a different meeting as far as the derivative is concerned.
        TranscriptionRequest changed = request with
        {
            Tracks =
            [
                .. request.Tracks.Select(track => track with
                {
                    Chunks = [.. track.Chunks.Select(chunk => chunk with { Sha256 = new string('f', 64) })],
                })
            ],
        };

        PlaybackDerivativeRecord second = (await Builder().BuildAsync(changed)).Record!;

        Assert.NotEqual(first.SourceManifestSha256, second.SourceManifestSha256);
    }

    [Fact]
    public async Task ADifferentProcessingVersionIsNotReused()
    {
        TranscriptionRequest request = _fixture.Build(Mic(1, 2.0, 3000));
        PlaybackDerivativeRecord first = (await Builder().BuildAsync(request)).Record!;

        PlaybackOptions next = new() { ProcessingVersion = "playback-v2" };
        PlaybackDerivativeRecord second = (await Builder().BuildAsync(request, next)).Record!;

        Assert.Equal("playback-v2", second.ProcessingVersion);
        Assert.NotEqual(first.RelativePath, second.RelativePath);

        // And the old one is still there: a version bump is a new file, not a rewritten one.
        Assert.True(File.Exists(Path.Combine(
            _fixture.Paths.Root, first.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task ADamagedDerivativeIsRebuiltRatherThanTrusted()
    {
        TranscriptionRequest request = _fixture.Build(Mic(1, 2.0, 3000));
        PlaybackDerivativeRecord first = (await Builder().BuildAsync(request)).Record!;

        string path = Path.Combine(
            _fixture.Paths.Root, first.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[2000] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        PlaybackDerivativeRecord second = (await Builder().BuildAsync(request)).Record!;

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Sha256, Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))));
    }

    // -- failure -----------------------------------------------------------------------------------

    [Fact]
    public async Task CancellationLeavesNoDerivativeAndNoDamage()
    {
        TranscriptionRequest request = _fixture.Build(Mic(1, 8.0, 3000));
        IReadOnlyDictionary<string, string> before = _fixture.SourceDigests();

        using CancellationTokenSource cancellation = new();
        PlaybackDerivativeBuilder builder = Builder();
        builder.Progress += (_, _) => cancellation.Cancel();

        PlaybackBuildResult result = await builder.BuildAsync(request, cancellationToken: cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("cancelled", result.Code);

        string directory = PlaybackDerivativeBuilder.PlaybackDirectory(_fixture.Paths, new PlaybackOptions());
        Assert.False(File.Exists(Path.Combine(directory, "playback.wav")));
        Assert.False(File.Exists(Path.Combine(directory, "playback.wav.partial")));

        Assert.Equal(before, _fixture.SourceDigests());
    }

    [Fact]
    public async Task CancellationCannotDamageADerivativeThatAlreadyWorked()
    {
        TranscriptionRequest request = _fixture.Build(Mic(1, 8.0, 3000));
        PlaybackDerivativeRecord good = (await Builder().BuildAsync(request)).Record!;

        // A version bump would normally rebuild; cancelling it must leave the first one alone.
        using CancellationTokenSource cancellation = new();
        PlaybackDerivativeBuilder builder = Builder();
        builder.Progress += (_, _) => cancellation.Cancel();

        await builder.BuildAsync(request, new PlaybackOptions(), cancellation.Token);

        string path = Path.Combine(
            _fixture.Paths.Root, good.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(good.Sha256, Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))));
    }

    [Fact]
    public async Task ACorruptSourceChunkIsRefusedRatherThanPlayedAround()
    {
        TranscriptionRequest request = _fixture.Build(Mic(1, 2.0, 3000));

        string chunk = Path.Combine(
            _fixture.Paths.Root,
            request.Tracks[0].Chunks[0].RelativePath.Replace('/', Path.DirectorySeparatorChar));

        await File.WriteAllTextAsync(chunk, "this is not a wave file at all");

        PlaybackBuildResult result = await Builder().BuildAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("source_audio_invalid", result.Code);
    }

    [Fact]
    public async Task BuildingNeverRewritesASourceChunk()
    {
        TranscriptionRequest request = _fixture.Build(
            Mic(1, 3.0, 3000, 44100),
            Sys(1, 3.0, 6000, 48000, channels: 2));

        IReadOnlyDictionary<string, string> before = _fixture.SourceDigests();

        Assert.True((await Builder().BuildAsync(request)).Succeeded);

        Assert.Equal(before, _fixture.SourceDigests());
    }

    [Fact]
    public async Task AMeetingWithNoAudioIsRefusedWithSomethingAUserCanRead()
    {
        TranscriptionRequest request = _fixture.Build(Mic(1, 2.0, 3000)) with { DurationSeconds = 0 };

        PlaybackBuildResult result = await Builder().BuildAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("no_audio", result.Code);
    }
}
