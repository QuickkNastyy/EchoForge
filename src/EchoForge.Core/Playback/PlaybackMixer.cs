using EchoForge.Contracts.Playback;

namespace EchoForge.Core.Playback;

/// <summary>
/// Turns the two aligned tracks into what a listener hears.
///
/// <para>
/// <b>The mix cannot clip, by arithmetic rather than by hoping.</b> When both tracks carry audio
/// each contributes at most half of full scale, so their sum is at most full scale however loudly
/// both people talk at once. Halving one channel of a two-track meeting is the price of never
/// producing the crackle that a naive sum produces exactly when two people interrupt each other —
/// which is the moment somebody is most likely to be replaying.
/// </para>
///
/// <para>
/// A meeting with only one track is not halved. There is nothing to overlap with, so the single
/// track plays at full scale and a microphone-only recording does not come back at half volume for
/// no reason.
/// </para>
///
/// <para>
/// Everything here is a pure function of the samples and the levels. Two runs of the same meeting
/// with the same levels produce identical output, which is what lets a test assert on it.
/// </para>
/// </summary>
public static class PlaybackMixer
{
    /// <summary>
    /// The headroom each track gets before the user's own level is applied, which depends only on
    /// how many tracks the meeting actually has.
    /// </summary>
    public static (double You, double Remote) BaseGains(bool hasYou, bool hasRemote) =>
        hasYou && hasRemote ? (0.5, 0.5)
        : hasYou ? (1.0, 0.0)
        : hasRemote ? (0.0, 1.0)
        : (0.0, 0.0);

    /// <summary>The gains actually applied, after mutes and levels.</summary>
    public static (double You, double Remote) EffectiveGains(PlaybackMix mix, bool hasYou, bool hasRemote)
    {
        ArgumentNullException.ThrowIfNull(mix);

        (double you, double remote) = BaseGains(hasYou, hasRemote);

        return (
            mix.MuteYou ? 0 : you * Math.Clamp(mix.YouLevel, 0, 1),
            mix.MuteRemote ? 0 : remote * Math.Clamp(mix.RemoteLevel, 0, 1));
    }

    /// <summary>
    /// Mixes interleaved two-track source frames into interleaved device frames.
    ///
    /// <para>
    /// Both tracks land in the centre of the stereo image rather than one per ear. Hard-panning
    /// the two sides of a conversation is disorienting to listen to and makes a single-track
    /// meeting come out of one speaker.
    /// </para>
    /// </summary>
    public static void Mix(
        ReadOnlySpan<short> source,
        int sourceChannels,
        Span<short> destination,
        int destinationChannels,
        int frames,
        double youGain,
        double remoteGain)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceChannels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationChannels, 1);

        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * sourceChannels;

            double you = source[offset + PlaybackChannels.You] * youGain;
            double remote = sourceChannels > PlaybackChannels.Remote
                ? source[offset + PlaybackChannels.Remote] * remoteGain
                : 0;

            short sample = Clamp(you + remote);

            int target = frame * destinationChannels;
            for (int channel = 0; channel < destinationChannels; channel++)
            {
                destination[target + channel] = sample;
            }
        }
    }

    private static short Clamp(double value)
    {
        // Rounding away from zero rather than to even, so the result depends only on the inputs
        // and never on a platform's default rounding mode.
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded switch
        {
            >= short.MaxValue => short.MaxValue,
            <= short.MinValue => short.MinValue,
            _ => (short)rounded,
        };
    }
}
