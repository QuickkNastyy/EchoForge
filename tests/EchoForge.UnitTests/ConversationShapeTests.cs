using EchoForge.App.Library;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.UnitTests;

/// <summary>
/// The miniature conversation shape the library rows and the detail timeline draw. It is derived from
/// the transcript's segments, bucketed by the time each speaker covered, and it must stay bounded and
/// honest: You and Remote separate, silence quiet, and a meeting with no transcript simply empty.
/// </summary>
public sealed class ConversationShapeTests
{
    private static TranscriptDocument Transcript(double durationSeconds, params (string Track, double Start, double End)[] segments)
    {
        List<TranscriptSegment> list = [];
        for (int i = 0; i < segments.Length; i++)
        {
            (string track, double start, double end) = segments[i];
            (string speakerId, string speakerName) = TranscriptSpeakers.For(track);
            list.Add(new TranscriptSegment
            {
                Id = $"segment-{i + 1:D6}",
                Epoch = 1,
                StartSeconds = start,
                EndSeconds = end,
                SpeakerId = speakerId,
                SpeakerName = speakerName,
                SourceTrack = track,
                Text = "x",
                Confidence = null,
                Language = "en",
                Words = [],
            });
        }

        return new TranscriptDocument
        {
            SessionId = "01J",
            TranscriptRevision = 1,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            DurationSeconds = durationSeconds,
            Model = new TranscriptModel("e", "b", "m", "r", "c", true, "1.0.0"),
            Epochs = [new TranscriptEpoch(1, 0, durationSeconds)],
            Speakers = [],
            Languages = [],
            Segments = list,
        };
    }

    [Fact]
    public void ANullOrEmptyTranscriptHasNoData()
    {
        ConversationShape empty = ConversationShape.FromTranscript(Transcript(0));
        Assert.False(empty.HasData);
        Assert.Equal(ConversationShape.DefaultBuckets, empty.You.Length);
        Assert.Equal(ConversationShape.DefaultBuckets, empty.Remote.Length);
    }

    [Fact]
    public void ItBucketsByCoverageAndKeepsTheLanesSeparate()
    {
        // You talks through the first half, Remote through the second half.
        ConversationShape shape = ConversationShape.FromTranscript(Transcript(
            100,
            ("microphone", 0, 50),
            ("system", 50, 100)));

        Assert.True(shape.HasData);
        int n = shape.You.Length;

        // First half: You active, Remote silent.
        Assert.True(shape.You[n / 4] > 0.5f);
        Assert.Equal(0f, shape.Remote[n / 4], 3);

        // Second half: Remote active, You silent.
        Assert.True(shape.Remote[(3 * n) / 4] > 0.5f);
        Assert.Equal(0f, shape.You[(3 * n) / 4], 3);
    }

    [Fact]
    public void SilenceBetweenSegmentsReadsAsZero()
    {
        ConversationShape shape = ConversationShape.FromTranscript(Transcript(
            100,
            ("microphone", 0, 10),
            ("microphone", 90, 100)));

        int n = shape.You.Length;
        // The middle is silence on both lanes.
        Assert.Equal(0f, shape.You[n / 2], 3);
        Assert.Equal(0f, shape.Remote[n / 2], 3);
        // The ends carry the two utterances.
        Assert.True(shape.You[0] > 0f);
        Assert.True(shape.You[n - 1] > 0f);
    }

    [Fact]
    public void CoverageIsClampedToTheUnitRange()
    {
        ConversationShape shape = ConversationShape.FromTranscript(Transcript(
            10,
            ("microphone", 0, 10),
            ("microphone", 0, 10)));  // overlapping, would sum past 1 without clamping

        foreach (float v in shape.You)
        {
            Assert.True(v <= 1.0f);
        }
    }

    [Fact]
    public void ItStaysBoundedRegardlessOfDuration()
    {
        ConversationShape shape = ConversationShape.FromTranscript(Transcript(
            3 * 3600,
            ("microphone", 0, 3 * 3600)), buckets: 64);

        Assert.Equal(64, shape.You.Length);
        Assert.Equal(64, shape.Remote.Length);
    }
}
