using EchoForge.Contracts.Transcripts;

namespace EchoForge.App.Library;

/// <summary>
/// A tiny, bounded picture of who spoke when across a recording: two lanes of activity, You and
/// Remote, over the recording's own time.
///
/// <para>
/// It is derived from the transcript's segments — not the audio — so it is cheap, deterministic, and
/// carries no second copy of the recording. Each bucket holds the fraction of that slice of time the
/// track was speaking (0 = silence, 1 = talking throughout), which is exactly the shape the library
/// row and the detail timeline draw. A recording with no transcript has <see cref="HasData"/> false,
/// and its ribbon well is simply quiet rather than faked.
/// </para>
/// </summary>
public sealed record ConversationShape(float[] You, float[] Remote, bool HasData)
{
    /// <summary>The number of buckets. Bounded, so a three-hour meeting draws the same as a short one.</summary>
    public const int DefaultBuckets = 64;

    public static readonly ConversationShape Empty =
        new(new float[DefaultBuckets], new float[DefaultBuckets], HasData: false);

    /// <summary>
    /// Buckets a transcript into two activity lanes by the time each segment covers.
    ///
    /// <para>
    /// A segment adds coverage to every bucket its [start, end] range overlaps, in proportion to how
    /// much of the bucket it fills, so a bucket a speaker talked through reads as one and a bucket
    /// they were silent in reads as zero. Times are the transcript's own session-relative seconds —
    /// the same canonical clock everything else uses.
    /// </para>
    /// </summary>
    public static ConversationShape FromTranscript(TranscriptDocument transcript, int buckets = DefaultBuckets)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (buckets < 1)
        {
            buckets = DefaultBuckets;
        }

        double total = transcript.DurationSeconds;
        if (total <= 0)
        {
            foreach (TranscriptSegment s in transcript.Segments)
            {
                total = Math.Max(total, s.EndSeconds);
            }
        }

        if (total <= 0 || transcript.Segments.Count == 0)
        {
            return new ConversationShape(new float[buckets], new float[buckets], HasData: false);
        }

        double bucketSeconds = total / buckets;
        float[] you = new float[buckets];
        float[] remote = new float[buckets];

        foreach (TranscriptSegment segment in transcript.Segments)
        {
            float[] lane = segment.SourceTrack == TranscriptSpeakers.MicrophoneTrack ? you : remote;

            double start = Math.Max(0, segment.StartSeconds);
            double end = Math.Max(start, segment.EndSeconds);

            int first = (int)(start / bucketSeconds);
            int last = (int)(end / bucketSeconds);
            first = Math.Clamp(first, 0, buckets - 1);
            last = Math.Clamp(last, 0, buckets - 1);

            for (int b = first; b <= last; b++)
            {
                double bucketStart = b * bucketSeconds;
                double bucketEnd = bucketStart + bucketSeconds;
                double covered = Math.Min(end, bucketEnd) - Math.Max(start, bucketStart);
                if (covered > 0)
                {
                    lane[b] = (float)Math.Min(1.0, lane[b] + (covered / bucketSeconds));
                }
            }
        }

        return new ConversationShape(you, remote, HasData: true);
    }
}
