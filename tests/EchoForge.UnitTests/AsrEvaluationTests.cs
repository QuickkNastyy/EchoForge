using EchoForge.Contracts.Evaluation;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Evaluation;

namespace EchoForge.UnitTests;

public sealed class AsrEvaluationTests
{
    [Fact]
    public void CountsWordErrorsAndAccuracyTerms()
    {
        TranscriptDocument reference = Transcript(
            Segment("r1", "microphone", 0, 2, "I sent it Tuesday"),
            Segment("r2", "system", 3, 6, "Ask Ada Lovelace about API 42"));
        TranscriptDocument hypothesis = Transcript(
            Segment("h1", "microphone", 0.1, 2.1, "I send it Tuesday"),
            Segment("h2", "system", 3.1, 6.2, "Ask Ada Lovelace about API 24")) with
        {
            TranscriptRevision = 7,
        };

        AsrEvaluationScore score = AsrScorer.Score(reference, hypothesis, new AsrReferenceTerms
        {
            ProperNames = ["Ada Lovelace"],
            Acronyms = ["API"],
            NumericExpressions = ["42"],
        });

        Assert.Equal(7, score.TranscriptRevision);
        Assert.Equal(2, score.NormalizedWordErrors.Substitutions);
        Assert.Equal(1, score.ProperNameAccuracy.Numerator);
        Assert.Equal(1, score.AcronymAccuracy.Numerator);
        Assert.Equal(0, score.NumericAccuracy.Numerator);
        Assert.Equal(1, score.NumericAccuracy.Denominator);
    }

    [Fact]
    public void MissingShortRepliesAreVisibleAsRecallFailures()
    {
        TranscriptDocument reference = Transcript(
            Segment("r1", "system", 1, 1.5, "Yeah"),
            Segment("r2", "system", 5, 5.8, "I sent it"),
            Segment("r3", "microphone", 10, 15, "This is a longer explanatory sentence"));
        TranscriptDocument hypothesis = Transcript(
            Segment("h3", "microphone", 10.1, 15.2, "This is a longer explanatory sentence"));

        AsrEvaluationScore score = AsrScorer.Score(reference, hypothesis);

        Assert.Equal(0, score.ShortUtteranceRecall.Numerator);
        Assert.Equal(2, score.ShortUtteranceRecall.Denominator);
        Assert.Equal(1, score.SpeechRegionRecall.Numerator);
        Assert.Equal(3, score.SpeechRegionRecall.Denominator);
    }

    [Fact]
    public void WrongTrackAttributionIsNotCountedAsCorrect()
    {
        TranscriptDocument reference = Transcript(Segment("r1", "system", 2, 4, "Tuesday works"));
        TranscriptDocument hypothesis = Transcript(Segment("h1", "microphone", 2.1, 4.1, "Tuesday works"));

        AsrEvaluationScore score = AsrScorer.Score(reference, hypothesis);

        Assert.Equal(1, score.SpeechRegionRecall.Numerator);
        Assert.Equal(0, score.SourceAttributionAccuracy.Numerator);
        Assert.Equal(1, score.SourceAttributionAccuracy.Denominator);
        Assert.NotNull(score.MeanTimestampErrorSeconds);
    }

    [Fact]
    public void RefusesToCompareDifferentSessions()
    {
        TranscriptDocument reference = Transcript(Segment("r1", "system", 0, 1, "hello"));
        TranscriptDocument other = Transcript(Segment("h1", "system", 0, 1, "hello")) with
        {
            SessionId = "other",
        };

        Assert.Throws<ArgumentException>(() => AsrScorer.Score(reference, other));
    }

    private static TranscriptDocument Transcript(params TranscriptSegment[] segments) => new()
    {
        SchemaVersion = 2,
        SessionId = "session",
        TranscriptRevision = 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        SourceManifestSha256 = new string('a', 64),
        DurationSeconds = segments.Length == 0 ? 0 : segments.Max(segment => segment.EndSeconds),
        Model = new TranscriptModel(
            "test", "test", "model", "revision", "cpu", true, "test"),
        Epochs = [new TranscriptEpoch(1, 0, 60)],
        Speakers = [],
        Languages = [],
        Segments = segments,
    };

    private static TranscriptSegment Segment(
        string id,
        string track,
        double start,
        double end,
        string text) => new()
    {
        Id = id,
        Epoch = 1,
        StartSeconds = start,
        EndSeconds = end,
        SpeakerId = track,
        SpeakerName = track == "system" ? "Remote" : "You",
        SourceTrack = track,
        Text = text,
        Confidence = null,
        Language = "en",
        Words = [],
        OverlapsSegmentIds = [],
    };
}
