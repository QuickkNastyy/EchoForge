using EchoForge.App.Recording;

namespace EchoForge.UnitTests;

/// <summary>
/// The speech-density ribbon's data: a bounded, presentation-only picture of the recording on the
/// canonical clock. The rules that matter are that silence is recorded rather than dropped, a lost
/// track flatlines, the two lanes never blur together, and the memory stays bounded however long the
/// meeting runs — a three-hour recording must not grow an unbounded pile of buckets.
/// </summary>
public sealed class SpeechActivityHistoryTests
{
    [Fact]
    public void SamplesAccumulateAsBucketsOnTheClock()
    {
        SpeechActivityHistory history = new(baseBucketSeconds: 1.0);
        history.Add(0, 0.4, true, 0.7, true);
        history.Add(1, 0.5, true, 0.2, true);
        history.Add(2, 0.6, true, 0.9, true);

        IReadOnlyList<RibbonBucket> buckets = history.Snapshot();
        Assert.Equal(3, buckets.Count);
        Assert.Equal(0.4f, buckets[0].You, 3);
        Assert.Equal(0.9f, buckets[2].Remote, 3);
    }

    [Fact]
    public void SilenceIsRecordedFlatRatherThanAbsent()
    {
        SpeechActivityHistory history = new();
        history.Add(0, 0.5, true, 0.5, true);
        history.Add(3, 0.0, true, 0.0, true); // a gap of silence, then a silent sample

        IReadOnlyList<RibbonBucket> buckets = history.Snapshot();
        Assert.Equal(4, buckets.Count); // seconds 0,1,2,3 all present
        Assert.Equal(0f, buckets[1].You, 3);
        Assert.Equal(0f, buckets[2].You, 3);
        Assert.True(buckets[1].YouActive);
    }

    [Fact]
    public void TheTwoLanesStaySeparate()
    {
        SpeechActivityHistory history = new();
        history.Add(0, 0.8, true, 0.1, true);

        RibbonBucket bucket = history.Snapshot()[0];
        Assert.Equal(0.8f, bucket.You, 3);
        Assert.Equal(0.1f, bucket.Remote, 3);
    }

    [Fact]
    public void ALostTrackIsMarkedInactiveForItsBuckets()
    {
        SpeechActivityHistory history = new();
        history.Add(0, 0.6, true, 0.6, true);
        history.Add(1, 0.0, false, 0.6, true); // microphone dropped

        RibbonBucket dropped = history.Snapshot()[1];
        Assert.False(dropped.YouActive);
        Assert.True(dropped.RemoteActive);
    }

    [Fact]
    public void SamplesInTheSameBucketFoldToTheLoudest()
    {
        SpeechActivityHistory history = new(baseBucketSeconds: 1.0);
        history.Add(0.2, 0.3, true, 0.1, true);
        history.Add(0.6, 0.7, true, 0.4, true);
        history.Add(0.9, 0.5, true, 0.2, true);

        IReadOnlyList<RibbonBucket> buckets = history.Snapshot();
        Assert.Single(buckets);
        Assert.Equal(0.7f, buckets[0].You, 3);
        Assert.Equal(0.4f, buckets[0].Remote, 3);
    }

    [Fact]
    public void AnEarlierSampleNeverRewritesHistory()
    {
        SpeechActivityHistory history = new();
        history.Add(5, 0.5, true, 0.5, true);
        int before = history.Count;

        history.Add(3, 0.9, true, 0.9, true); // out of order; folds into the newest bucket

        Assert.Equal(before, history.Count);
    }

    [Fact]
    public void LevelsAreClampedToTheUnitRange()
    {
        SpeechActivityHistory history = new();
        history.Add(0, 2.5, true, -1.0, true);

        RibbonBucket bucket = history.Snapshot()[0];
        Assert.Equal(1f, bucket.You, 3);
        Assert.Equal(0f, bucket.Remote, 3);
    }

    [Fact]
    public void MemoryStaysBoundedOverAThreeHourRecording()
    {
        SpeechActivityHistory history = new(baseBucketSeconds: 1.0);

        // Three hours at five samples a second, the real refresh cadence.
        for (int i = 0; i < 3 * 3600 * 5; i++)
        {
            double t = i / 5.0;
            history.Add(t, 0.5, true, 0.5, true);
        }

        Assert.True(history.Count <= SpeechActivityHistory.Capacity,
            $"bucket count {history.Count} exceeded the cap {SpeechActivityHistory.Capacity}");
        Assert.True(history.BucketSeconds > 1.0, "the bucket width should have grown for a long recording");
        // The span still covers the whole recording, give or take one bucket.
        Assert.True(history.TotalSeconds >= 3 * 3600 - history.BucketSeconds);
    }

    [Fact]
    public void DownsamplingKeepsTheLoudestActivity()
    {
        SpeechActivityHistory history = new(baseBucketSeconds: 1.0);

        // Fill past capacity so at least one downsample happens, with one loud spike.
        for (int i = 0; i <= SpeechActivityHistory.Capacity + 10; i++)
        {
            history.Add(i, i == 100 ? 1.0 : 0.1, true, 0.1, true);
        }

        Assert.True(history.Count <= SpeechActivityHistory.Capacity);
        Assert.Contains(history.Snapshot(), b => b.You >= 0.99f); // the spike survived the merge
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        SpeechActivityHistory history = new();
        history.Add(0, 0.5, true, 0.5, true);
        history.Add(1, 0.5, true, 0.5, true);

        history.Reset();

        Assert.Equal(0, history.Count);
        Assert.Empty(history.Snapshot());
        Assert.Equal(1.0, history.BucketSeconds);
    }
}
