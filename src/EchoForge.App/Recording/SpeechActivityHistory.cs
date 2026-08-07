namespace EchoForge.App.Recording;

/// <summary>One time bucket of the two-lane activity ribbon.</summary>
/// <param name="You">Aggregated microphone activity in the bucket, 0..1.</param>
/// <param name="Remote">Aggregated system activity in the bucket, 0..1.</param>
/// <param name="YouActive">False when the microphone track was not capturing (device lost).</param>
/// <param name="RemoteActive">False when the system track was not capturing.</param>
public readonly record struct RibbonBucket(float You, float Remote, bool YouActive, bool RemoteActive);

/// <summary>
/// A bounded, presentation-only history of how loud each track was over the recording, indexed by the
/// canonical session-relative active time.
///
/// <para>
/// <b>This is a picture, never a record.</b> It is fed from the same UI refresh that reads the live
/// meters, it stores nothing to disk, and nothing here can affect what is captured or persisted — the
/// recording is authoritative and this only draws it. It is deliberately not a second timing source:
/// callers hand it the canonical active-time seconds and it buckets against that, so the ribbon and
/// the transcript agree about when things happened.
/// </para>
///
/// <para>
/// Memory is bounded for a meeting of any length. The history keeps at most <see cref="Capacity"/>
/// buckets; when the timeline outgrows them the bucket width doubles and adjacent buckets merge, so a
/// three-hour recording costs the same handful of buckets as a three-minute one and the control never
/// accumulates unbounded state.
/// </para>
/// </summary>
public sealed class SpeechActivityHistory
{
    /// <summary>The most buckets ever held. Wider buckets, never more of them.</summary>
    public const int Capacity = 600;

    private readonly RibbonBucket[] _buckets = new RibbonBucket[Capacity];
    private int _count;
    private double _bucketSeconds;
    private readonly double _baseBucketSeconds;

    /// <param name="baseBucketSeconds">
    /// The finest bucket width, in seconds. One second gives ten minutes of history before the first
    /// downsample, which is more than the eye resolves on a ribbon a few hundred pixels wide.
    /// </param>
    public SpeechActivityHistory(double baseBucketSeconds = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseBucketSeconds);
        _baseBucketSeconds = baseBucketSeconds;
        _bucketSeconds = baseBucketSeconds;
    }

    /// <summary>How many buckets are populated.</summary>
    public int Count => _count;

    /// <summary>The current bucket width in seconds, which grows as the recording gets longer.</summary>
    public double BucketSeconds => _bucketSeconds;

    /// <summary>The span the history covers, in seconds.</summary>
    public double TotalSeconds => _count * _bucketSeconds;

    /// <summary>A snapshot of the buckets, oldest first. A copy, safe to read on the render thread.</summary>
    public IReadOnlyList<RibbonBucket> Snapshot()
    {
        RibbonBucket[] copy = new RibbonBucket[_count];
        Array.Copy(_buckets, copy, _count);
        return copy;
    }

    /// <summary>Forgets everything. Called when a new recording starts, so lanes never carry over.</summary>
    public void Reset()
    {
        Array.Clear(_buckets, 0, _count);
        _count = 0;
        _bucketSeconds = _baseBucketSeconds;
    }

    /// <summary>
    /// Records the activity read at <paramref name="activeSeconds"/> of canonical active time.
    ///
    /// <para>
    /// Levels are clamped to 0..1. A lane that is not capturing is marked inactive for its bucket
    /// regardless of the level reported, so a lost device flatlines truthfully rather than holding its
    /// last value. Time only moves forward; a sample for an earlier second than the newest bucket is
    /// folded into the newest bucket rather than rewriting history.
    /// </para>
    /// </summary>
    public void Add(double activeSeconds, double youLevel, bool youActive, double remoteLevel, bool remoteActive)
    {
        if (double.IsNaN(activeSeconds) || activeSeconds < 0)
        {
            return;
        }

        float you = Clamp(youLevel);
        float remote = Clamp(remoteLevel);

        int index = (int)(activeSeconds / _bucketSeconds);

        // Grow the bucket width until the newest sample fits within Capacity. Doubling halves the
        // bucket count each pass, so this settles in a few iterations however long the meeting runs.
        while (index >= Capacity)
        {
            Downsample();
            index = (int)(activeSeconds / _bucketSeconds);
        }

        if (index < _count)
        {
            // A sample for a bucket we already have (time did not advance a whole bucket): fold it in.
            index = _count - 1;
            Merge(index, you, youActive, remote, remoteActive);
            return;
        }

        // Any buckets skipped between the last one and this one are silence that still happened.
        for (int i = _count; i < index; i++)
        {
            _buckets[i] = new RibbonBucket(0, 0, youActive, remoteActive);
        }

        _buckets[index] = new RibbonBucket(you, remote, youActive, remoteActive);
        _count = index + 1;
    }

    private void Merge(int index, float you, bool youActive, float remote, bool remoteActive)
    {
        RibbonBucket existing = _buckets[index];
        _buckets[index] = new RibbonBucket(
            Math.Max(existing.You, you),
            Math.Max(existing.Remote, remote),
            youActive,       // latest sample decides whether the lane is still live
            remoteActive);
    }

    /// <summary>Halves the resolution: merge each pair of buckets, double the width.</summary>
    private void Downsample()
    {
        int merged = (_count + 1) / 2;
        for (int i = 0; i < merged; i++)
        {
            RibbonBucket a = _buckets[i * 2];
            RibbonBucket b = (i * 2 + 1) < _count ? _buckets[i * 2 + 1] : a;
            _buckets[i] = new RibbonBucket(
                Math.Max(a.You, b.You),
                Math.Max(a.Remote, b.Remote),
                b.YouActive,     // the later of the two decides liveness
                b.RemoteActive);
        }

        Array.Clear(_buckets, merged, _count - merged);
        _count = merged;
        _bucketSeconds *= 2;
    }

    private static float Clamp(double value)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return 0;
        }

        return value >= 1 ? 1f : (float)value;
    }
}
