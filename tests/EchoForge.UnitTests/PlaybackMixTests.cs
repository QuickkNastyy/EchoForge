using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Playback;
using EchoForge.Core.Playback;
using EchoForge.Infrastructure.Playback;

namespace EchoForge.UnitTests;

/// <summary>
/// What a listener actually hears, and the guarantee that it never crackles.
///
/// <para>
/// The mix is applied on the way to the device rather than written into the derivative, so these
/// are pure functions of the samples and the levels. That is also why muting a track is free:
/// nothing is rebuilt, and nothing a citation points at can change.
/// </para>
/// </summary>
public sealed class PlaybackMixTests : IDisposable
{
    private readonly PlaybackFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void BothTracksAreAudibleAndNeitherDrownsTheOther()
    {
        short[] source = [10000, 10000];
        short[] destination = new short[2];

        (double you, double remote) = PlaybackMixer.EffectiveGains(PlaybackMix.Default, true, true);
        PlaybackMixer.Mix(source, 2, destination, 2, 1, you, remote);

        Assert.Equal(10000, destination[0]);
        Assert.Equal(destination[0], destination[1]);
    }

    [Fact]
    public void TwoLoudTracksAtOnceCannotClip()
    {
        // Both tracks at full scale simultaneously is exactly the moment a naive sum crackles,
        // and exactly the moment somebody is most likely to be replaying: two people interrupting
        // each other.
        short[] source = [short.MaxValue, short.MaxValue];
        short[] destination = new short[2];

        (double you, double remote) = PlaybackMixer.EffectiveGains(PlaybackMix.Default, true, true);
        PlaybackMixer.Mix(source, 2, destination, 2, 1, you, remote);

        Assert.Equal(short.MaxValue, destination[0]);

        short[] negative = [short.MinValue, short.MinValue];
        PlaybackMixer.Mix(negative, 2, destination, 2, 1, you, remote);

        Assert.Equal(short.MinValue, destination[0]);
    }

    [Fact]
    public void AMeetingWithOneTrackIsNotHalvedForNoReason()
    {
        (double you, double remote) = PlaybackMixer.EffectiveGains(PlaybackMix.Default, hasYou: true, hasRemote: false);

        Assert.Equal(1.0, you);
        Assert.Equal(0.0, remote);

        (double _, double onlyRemote) = PlaybackMixer.EffectiveGains(PlaybackMix.Default, hasYou: false, hasRemote: true);
        Assert.Equal(1.0, onlyRemote);
    }

    [Fact]
    public void MutingATrackSilencesItAndLeavesTheOther()
    {
        short[] source = [8000, 8000];
        short[] destination = new short[2];

        (double you, double remote) = PlaybackMixer.EffectiveGains(
            PlaybackMix.Default with { MuteYou = true }, true, true);

        PlaybackMixer.Mix(source, 2, destination, 2, 1, you, remote);

        Assert.Equal(4000, destination[0]);

        (you, remote) = PlaybackMixer.EffectiveGains(
            PlaybackMix.Default with { MuteYou = true, MuteRemote = true }, true, true);

        PlaybackMixer.Mix(source, 2, destination, 2, 1, you, remote);
        Assert.Equal(0, destination[0]);
    }

    [Fact]
    public void LevelsScaleDeterministicallyAndAreClampedRatherThanTrusted()
    {
        (double you, double _) = PlaybackMixer.EffectiveGains(
            PlaybackMix.Default with { YouLevel = 0.5 }, true, true);

        Assert.Equal(0.25, you, 6);

        (double loud, double _) = PlaybackMixer.EffectiveGains(
            PlaybackMix.Default with { YouLevel = 9 }, true, true);

        Assert.Equal(0.5, loud, 6);

        (double quiet, double _) = PlaybackMixer.EffectiveGains(
            PlaybackMix.Default with { YouLevel = -3 }, true, true);

        Assert.Equal(0, quiet, 6);
    }

    [Fact]
    public async Task ChangingTheMixNeverTouchesThePreparedAudio()
    {
        Contracts.Workers.TranscriptionRequest request = _fixture.Build(
            new PlaybackChunkSpec(SourceTrack.Microphone, 1, 2.0, 3000),
            new PlaybackChunkSpec(SourceTrack.System, 1, 2.0, 6000));

        PlaybackBuildResult built = await new PlaybackDerivativeBuilder(_fixture.Sessions).BuildAsync(request);
        PlaybackDerivativeRecord record = built.Record!;

        string path = Path.Combine(
            _fixture.Paths.Root, record.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        FakePlaybackDevice device = new();
        using PlaybackEngine engine = new(WavPlaybackAudioSource.Open(path), device, true, true);

        engine.Play();
        device.Pump(2400);

        engine.Mix = PlaybackMix.Default with { MuteRemote = true };
        device.Rendered.Clear();
        int frames = device.Pump(2400);

        // Only You is left, at half scale because the meeting has two tracks: muting is a
        // listening choice, not a re-mix of the file.
        short heard = device.Rendered[frames];
        Assert.True(Math.Abs(heard - 1500) <= 60, $"heard {heard}");

        // The bytes on disk are still exactly the bytes that were built.
        using FileStream stream = File.OpenRead(path);
        Assert.Equal(
            record.Sha256,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)));
    }
}
