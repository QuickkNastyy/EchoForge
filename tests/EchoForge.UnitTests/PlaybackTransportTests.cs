using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Playback;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Playback;
using EchoForge.Infrastructure.Playback;

namespace EchoForge.UnitTests;

/// <summary>
/// Playing a meeting back, and landing where the caller asked to land.
///
/// <para>
/// The architecture's completion criterion for Phase 4 is that clicking a citation reaches the
/// right audio <b>within 250 ms</b>. That is measured here as a logical fact — requested session
/// time in, playback position out — because that is the part that can be wrong. Waiting for a
/// speaker would test the machine the suite happens to run on and would fail on a build agent that
/// has no sound card, while proving nothing about the mapping.
/// </para>
///
/// <para>
/// The interesting seeks are the ones where a naive implementation drifts: across a chunk
/// boundary, immediately after a pause, and inside a later epoch. Those are done against a real
/// derivative built from real chunks, and the test checks both the reported position <i>and</i>
/// the audio that comes out — so a seek that reported the right number while playing the wrong
/// moment would still fail.
/// </para>
/// </summary>
public sealed class PlaybackTransportTests : IDisposable
{
    /// <summary>The architecture's acceptance criterion.</summary>
    private const double CriterionSeconds = 0.250;

    private readonly PlaybackFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static PlaybackChunkSpec Mic(int epoch, double seconds, short level, double gapBefore = 0) =>
        new(SourceTrack.Microphone, epoch, seconds, level, 48000, 1, gapBefore);

    private static PlaybackChunkSpec Sys(int epoch, double seconds, short level) =>
        new(SourceTrack.System, epoch, seconds, level, 48000, 2);

    /// <summary>Builds the derivative and opens a transport over it, with a fake device.</summary>
    private async Task<(PlaybackEngine Engine, FakePlaybackDevice Device, PlaybackDerivativeRecord Record)>
        OpenAsync(params PlaybackChunkSpec[] specs)
    {
        TranscriptionRequest request = _fixture.Build(specs);
        PlaybackBuildResult built = await new PlaybackDerivativeBuilder(_fixture.Sessions).BuildAsync(request);
        Assert.True(built.Succeeded, built.Detail);

        string path = Path.Combine(
            _fixture.Paths.Root, built.Record!.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        FakePlaybackDevice device = new();
        PlaybackEngine engine = new(
            WavPlaybackAudioSource.Open(path),
            device,
            built.Record.For("microphone")!.HasAudio,
            built.Record.For("system")!.HasAudio);

        return (engine, device, built.Record);
    }

    private static PlaybackEngine Synthetic(out FakePlaybackDevice device, double seconds = 60, int rate = 24000)
    {
        device = new FakePlaybackDevice();
        return new PlaybackEngine(new CountingPlaybackSource(rate, (long)(seconds * rate)), device);
    }

    // -- transport ---------------------------------------------------------------------------------

    [Fact]
    public void PlayPauseResumeAndStopMoveThroughTheStatesAndDriveTheDevice()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice device);

        Assert.Equal(PlaybackState.Ready, engine.State);
        Assert.True(device.IsOpen);

        engine.Play();
        Assert.Equal(PlaybackState.Playing, engine.State);
        Assert.True(device.IsPlaying);

        engine.Pause();
        Assert.Equal(PlaybackState.Paused, engine.State);
        Assert.False(device.IsPlaying);

        engine.Play();
        Assert.Equal(PlaybackState.Playing, engine.State);

        engine.Stop();
        Assert.Equal(PlaybackState.Ready, engine.State);
        Assert.False(device.IsPlaying);

        // Stop means back to the beginning, which is what it means everywhere else.
        Assert.Equal(0, engine.PositionSeconds, 6);
    }

    [Fact]
    public void PausingDoesNotMoveThePositionAndResumingContinuesFromIt()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice device);

        engine.Play();
        device.Pump(24000);
        double afterOneSecond = engine.PositionSeconds;

        engine.Pause();
        Assert.Equal(afterOneSecond, engine.PositionSeconds, 6);

        // A paused transport renders nothing even if the device asks.
        Assert.Equal(0, device.Pump(1000));
        Assert.Equal(afterOneSecond, engine.PositionSeconds, 6);

        engine.Play();
        device.Pump(24000);
        Assert.Equal(afterOneSecond + 1.0, engine.PositionSeconds, 3);
    }

    [Fact]
    public void RenderingAdvancesThePositionByExactlyWhatWasRendered()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice device, seconds: 2, rate: 1000);

        engine.Play();

        Assert.Equal(500, device.Pump(500));
        Assert.Equal(500, engine.PositionFrame);

        Assert.Equal(500, device.Pump(500));
        Assert.Equal(1000, engine.PositionFrame);
    }

    [Fact]
    public void PlayingToTheEndEndsRatherThanLoopingOrStalling()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice device, seconds: 1, rate: 1000);

        engine.Play();
        Assert.Equal(1000, device.Pump(1000));

        Assert.Equal(PlaybackState.Ended, engine.State);
        Assert.Equal(0, device.Pump(100));
    }

    [Fact]
    public void PlayingAgainAfterTheEndStartsTheMeetingOver()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice device, seconds: 1, rate: 1000);

        engine.Play();
        device.Pump(1000);
        Assert.Equal(PlaybackState.Ended, engine.State);

        engine.Play();

        Assert.Equal(PlaybackState.Playing, engine.State);
        Assert.Equal(0, engine.PositionFrame);
    }

    [Fact]
    public void TheReportedPositionIsWhatIsAudibleRatherThanWhatHasBeenQueued()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice device, seconds: 10, rate: 1000);

        engine.Play();
        device.Pump(5000);

        // Half of what was handed over is still sitting in the device.
        device.PendingFrames = 2500;

        Assert.Equal(2.5, engine.PositionSeconds, 3);
        Assert.Equal(5000, engine.PositionFrame);
    }

    // -- seeking -----------------------------------------------------------------------------------

    [Fact]
    public async Task EverySeekLandsWithinTheTwoHundredAndFiftyMillisecondCriterion()
    {
        // Two chunks, then a pause, then a second epoch: every awkward moment in one meeting.
        (PlaybackEngine engine, FakePlaybackDevice device, PlaybackDerivativeRecord record) =
            await OpenAsync(
                Mic(1, 30.0, 2000),
                Mic(1, 30.0, 4000),
                Mic(2, 30.0, 8000));

        using PlaybackEngine open = engine;
        double secondEpoch = 60.0 + PlaybackFixture.EpochGapSeconds;

        double[] moments =
        [
            0,                       // the very start
            15.0,                    // the middle of a chunk
            29.999, 30.0, 30.001,    // either side of a chunk boundary and on it
            59.999,                  // the end of the first epoch's audio
            62.0,                    // inside the pause
            secondEpoch,             // the instant a later epoch begins
            secondEpoch + 0.001,     // immediately after the gap
            secondEpoch + 29.0,      // deep inside a later epoch
        ];

        foreach (double moment in moments)
        {
            engine.Seek(moment);

            Assert.True(
                Math.Abs(engine.PositionSeconds - moment) <= CriterionSeconds,
                $"seeking to {moment}s landed at {engine.PositionSeconds}s");

            // The derivative allows exact sample positioning, so hold it to a far tighter bound
            // than the acceptance criterion. A regression that stayed inside 250 ms would still
            // be a regression.
            Assert.True(
                Math.Abs(engine.PositionSeconds - moment) <= 1.0 / record.SampleRate,
                $"seeking to {moment}s was only accurate to {Math.Abs(engine.PositionSeconds - moment)}s");
        }

        Assert.True(device.IsOpen);
        Assert.Same(engine, open);
    }

    [Fact]
    public async Task ASeekReachesTheAudioItNamedAndNotMerelyTheRightNumber()
    {
        (PlaybackEngine engine, FakePlaybackDevice device, PlaybackDerivativeRecord record) =
            await OpenAsync(
                Mic(1, 10.0, 2000),
                Mic(1, 10.0, 4000),
                Mic(2, 10.0, 8000));

        using PlaybackEngine open = engine;
        Assert.Same(engine, open);

        double secondEpoch = 20.0 + PlaybackFixture.EpochGapSeconds;

        AssertPlaysAbout(engine, device, record, 5.0, 2000);
        AssertPlaysAbout(engine, device, record, 15.0, 4000);
        AssertPlaysAbout(engine, device, record, 22.0, 0);              // inside the pause
        AssertPlaysAbout(engine, device, record, secondEpoch + 5.0, 8000);
    }

    /// <summary>Seeks, renders a moment of audio, and checks the level that comes out.</summary>
    private static void AssertPlaysAbout(
        PlaybackEngine engine,
        FakePlaybackDevice device,
        PlaybackDerivativeRecord record,
        double moment,
        int expectedLevel)
    {
        engine.Seek(moment);
        engine.Play();

        device.Rendered.Clear();
        int frames = device.Pump(record.SampleRate / 10);
        Assert.True(frames > 0);

        // Only one track carries audio here, so it plays at full scale rather than half.
        short sample = device.Rendered[frames * PlaybackEngine.DeviceChannels / 2];

        Assert.True(
            Math.Abs(sample - expectedLevel) <= 60,
            $"at {moment}s expected about {expectedLevel}, heard {sample}");

        engine.Pause();
    }

    [Fact]
    public void RepeatedSeeksDoNotAccumulateError()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice device, seconds: 3600, rate: 24000);

        for (int i = 0; i < 200; i++)
        {
            engine.Seek(1234.5678);
            engine.Seek(0);
            engine.Seek(3599.25);
        }

        engine.Seek(1234.5678);

        // Every seek is computed from the time itself, so the two-hundredth is the first.
        Assert.Equal(1234.5678, engine.PositionSeconds, 4);
        Assert.Equal(200 * 3, device.Flushes - 1);
    }

    [Fact]
    public void SeekingOutsideTheMeetingIsClampedRatherThanRefused()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice _, seconds: 10, rate: 1000);

        engine.Seek(-5);
        Assert.Equal(0, engine.PositionSeconds, 6);

        // A citation a moment past the last frame should land at the end, not report an error at
        // somebody who clicked a timestamp.
        engine.Seek(10.5);
        Assert.Equal(10.0, engine.PositionSeconds, 6);
    }

    [Fact]
    public void SeekingDropsWhatTheDeviceHadAlreadyQueued()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice device, seconds: 60, rate: 1000);

        engine.Play();
        device.Pump(10000);
        device.PendingFrames = 3000;

        engine.Seek(42);

        Assert.Equal(0, device.PendingFrames);
        Assert.Equal(42.0, engine.PositionSeconds, 6);
        Assert.True(device.IsPlaying);
    }

    [Fact]
    public void SeekingOutOfAFinishedMeetingMakesItPlayableAgain()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice device, seconds: 1, rate: 1000);

        engine.Play();
        device.Pump(1000);
        Assert.Equal(PlaybackState.Ended, engine.State);

        engine.Seek(0.5);

        Assert.Equal(PlaybackState.Ready, engine.State);
        Assert.Equal(0.5, engine.PositionSeconds, 6);
    }

    // -- evidence ----------------------------------------------------------------------------------

    [Fact]
    public void AResolvedCitationSeeksToTheMomentItCites()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice _, seconds: 600, rate: 24000);

        EvidenceLocation location = new()
        {
            SessionId = "s",
            TranscriptRevision = 3,
            SegmentId = "segment-000042",
            Resolution = EvidenceResolution.Resolved,
            StartSeconds = 421.75,
            EndSeconds = 425.0,
            Epoch = 2,
        };

        PlaybackRequest request = location.ToPlaybackRequest();
        Assert.False(request.IsApproximate);

        engine.Seek(request.StartSeconds);

        Assert.True(Math.Abs(engine.PositionSeconds - 421.75) <= CriterionSeconds);
        Assert.Equal(421.75, engine.PositionSeconds, 4);
    }

    [Fact]
    public void AnUnresolvedCitationStillSeeksButSaysItIsApproximate()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice _, seconds: 600, rate: 24000);

        EvidenceLocation location = new()
        {
            SessionId = "s",
            TranscriptRevision = 1,
            SegmentId = "segment-000042",
            Resolution = EvidenceResolution.Degraded,
            // The time stored with the citation, not a time looked up in a newer transcript.
            StartSeconds = 88.5,
            Explanation = "That transcript version is no longer on disk.",
        };

        PlaybackRequest request = location.ToPlaybackRequest();

        Assert.True(request.IsApproximate);

        engine.Seek(request.StartSeconds);

        // It still goes somewhere useful. What must never happen is the seek being presented as
        // exact, or the time being re-derived from whichever transcript is currently selected.
        Assert.Equal(88.5, engine.PositionSeconds, 4);
    }

    // -- failure and lifetime ----------------------------------------------------------------------

    [Fact]
    public void AMachineWithNoOutputDeviceFailsVisiblyRatherThanThrowing()
    {
        FakePlaybackDevice device = new()
        {
            OpenFailure = new PlaybackDeviceException(
                "playback_device_unavailable", "No audio output device is available."),
        };

        using PlaybackEngine engine = new(new CountingPlaybackSource(24000, 24000), device);

        Assert.Equal(PlaybackState.Failed, engine.State);
        Assert.Contains("No audio output device", engine.Message, StringComparison.Ordinal);

        // And every control is inert rather than throwing at whoever clicks it.
        engine.Play();
        engine.Seek(5);
        engine.Pause();
        engine.Stop();

        Assert.Equal(PlaybackState.Failed, engine.State);
    }

    [Fact]
    public void ADeviceThatDiesMidPlaybackSurfacesRatherThanPlayingSilently()
    {
        using PlaybackEngine engine = Synthetic(out FakePlaybackDevice device);
        List<PlaybackState> seen = [];
        engine.StateChanged += (_, e) => seen.Add(e.State);

        engine.Play();
        device.FailNow();

        Assert.Equal(PlaybackState.Failed, engine.State);
        Assert.Contains(PlaybackState.Failed, seen);
    }

    [Fact]
    public void DisposingReleasesBothTheDeviceAndTheFile()
    {
        FakePlaybackDevice device = new();
        CountingPlaybackSource source = new(24000, 24000);
        PlaybackEngine engine = new(source, device);

        engine.Play();
        engine.Dispose();

        Assert.True(device.IsDisposed);
        Assert.True(source.IsDisposed);
        Assert.False(device.IsPlaying);

        // Disposing twice is what a window closing after a meeting was already closed does.
        engine.Dispose();
    }

    [Fact]
    public async Task ClosingAMeetingLeavesNoHandleOnItsPreparedAudio()
    {
        (PlaybackEngine engine, FakePlaybackDevice _, PlaybackDerivativeRecord record) =
            await OpenAsync(Mic(1, 2.0, 3000));

        string path = Path.Combine(
            _fixture.Paths.Root, record.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        engine.Play();
        engine.Dispose();

        // A lingering read handle would block the rebuild a reprocess needs. Deleting the file is
        // the bluntest possible proof that nothing still holds it.
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OpeningASecondMeetingDoesNotKeepTheFirstOneAlive()
    {
        (PlaybackEngine first, FakePlaybackDevice firstDevice, PlaybackDerivativeRecord _) =
            await OpenAsync(Mic(1, 2.0, 3000));

        first.Play();

        // What switching meetings in the library does: the previous transport is disposed before
        // the next one is opened, so only one playback session exists at a time.
        first.Dispose();

        using PlaybackEngine second = Synthetic(out FakePlaybackDevice secondDevice);
        second.Play();

        Assert.True(firstDevice.IsDisposed);
        Assert.False(firstDevice.IsPlaying);
        Assert.True(secondDevice.IsPlaying);
    }
}
