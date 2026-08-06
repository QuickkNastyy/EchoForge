using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;

namespace EchoForge.UnitTests;

public sealed class BoundedAudioQueueTests
{
    private static readonly CaptureFormat Mono48 = new(48_000, 1, 16);

    private static PacketHeader Header(long devicePosition, int frames) =>
        new(devicePosition, devicePosition * 10_000_000 / 48_000, frames, AudioPacketConditions.None);

    [Fact]
    public void PacketsRoundTripThroughTheQueueIntact()
    {
        using BoundedAudioQueue queue = new(Mono48, TimeSpan.FromSeconds(1));
        byte[] payload = [1, 2, 3, 4];

        Assert.True(queue.TryEnqueue(Header(0, 2), payload));

        CapturedPacket? packet = queue.TryDequeue(TimeSpan.FromSeconds(1));
        Assert.NotNull(packet);
        Assert.Equal(2, packet.Header.FrameCount);
        Assert.True(packet.Payload.SequenceEqual(payload));
        BoundedAudioQueue.Release(packet);
    }

    [Fact]
    public void OverflowDropsThePacketAndCountsItRatherThanBlocking()
    {
        // One second of capacity at 48 kHz.
        using BoundedAudioQueue queue = new(Mono48, TimeSpan.FromSeconds(1));

        // Fill to the bound without dequeuing, as if the writer had stalled.
        for (int i = 0; i < 100; i++)
        {
            queue.TryEnqueue(Header(i * 480, 480), new byte[960]);
        }

        Assert.Equal(48_000, queue.QueuedFrames);
        Assert.Equal(0, queue.DroppedFrames);

        // The next packet cannot fit and must be dropped, not awaited.
        bool accepted = queue.TryEnqueue(Header(48_000, 480), new byte[960]);

        Assert.False(accepted);
        Assert.Equal(1, queue.DroppedPackets);
        Assert.Equal(480, queue.DroppedFrames);
        Assert.Equal(48_000, queue.QueuedFrames);
    }

    [Fact]
    public void PeakDepthIsRetainedForTheBoundedQueueEvidence()
    {
        using BoundedAudioQueue queue = new(Mono48, TimeSpan.FromSeconds(5));

        for (int i = 0; i < 10; i++)
        {
            queue.TryEnqueue(Header(i * 480, 480), new byte[960]);
        }

        while (queue.TryDequeue(TimeSpan.Zero) is { } packet)
        {
            BoundedAudioQueue.Release(packet);
        }

        Assert.Equal(0, queue.QueuedFrames);
        Assert.Equal(4_800, queue.PeakQueuedFrames);
    }

    [Fact]
    public void DequeueReturnsNullWhenNothingIsWaiting()
    {
        using BoundedAudioQueue queue = new(Mono48, TimeSpan.FromSeconds(1));
        Assert.Null(queue.TryDequeue(TimeSpan.Zero));
    }
}

public sealed class DriftEstimatorTests
{
    private const int SampleRate = 48_000;

    /// <summary>
    /// Builds anchors for a device clock running fast by a known number of milliseconds
    /// per hour, then checks the estimator recovers that rate.
    /// </summary>
    private static DriftEstimator Simulate(double millisecondsPerHour, double durationSeconds)
    {
        DriftEstimator estimator = new(SampleRate);
        double slope = millisecondsPerHour / 3600.0 / 1000.0;
        const int FramesPerPacket = 480;

        long capturedFrames = 0;
        while (true)
        {
            // The endpoint has delivered this much audio; a clock running fast by `slope`
            // reaches that point in proportionally less wall time.
            double deliveredSeconds = (double)capturedFrames / SampleRate;
            double wallSeconds = deliveredSeconds / (1.0 + slope);
            if (wallSeconds > durationSeconds)
            {
                break;
            }

            long qpc = (long)Math.Round(wallSeconds * CaptureClock.UnitsPerSecond);
            estimator.Add(new PacketHeader(0, qpc, FramesPerPacket, AudioPacketConditions.None));
            capturedFrames += FramesPerPacket;
        }

        return estimator;
    }

    [Fact]
    public void RecoversAKnownDriftRate()
    {
        DriftEstimator estimator = Simulate(millisecondsPerHour: 50.0, durationSeconds: 600);

        double? rate = estimator.MillisecondsPerHour();

        Assert.NotNull(rate);
        Assert.InRange(rate.Value, 49.0, 51.0);
    }

    [Fact]
    public void RecoversANegativeDriftRate()
    {
        DriftEstimator estimator = Simulate(millisecondsPerHour: -120.0, durationSeconds: 600);

        double? rate = estimator.MillisecondsPerHour();

        Assert.NotNull(rate);
        Assert.InRange(rate.Value, -121.0, -119.0);
    }

    [Fact]
    public void APerfectClockReportsEssentiallyZeroDrift()
    {
        DriftEstimator estimator = Simulate(millisecondsPerHour: 0.0, durationSeconds: 600);

        double? rate = estimator.MillisecondsPerHour();

        Assert.NotNull(rate);
        Assert.InRange(rate.Value, -1.0, 1.0);
    }

    [Fact]
    public void ReportsNullRatherThanFabricatingARateFromTooFewAnchors()
    {
        DriftEstimator estimator = new(SampleRate);
        estimator.Add(new PacketHeader(0, 0, 480, AudioPacketConditions.None));

        Assert.Null(estimator.MillisecondsPerHour());
    }

    [Fact]
    public void PacketsFlaggedWithATimestampErrorAreExcluded()
    {
        DriftEstimator estimator = new(SampleRate);

        estimator.Add(new PacketHeader(0, 0, 48_000, AudioPacketConditions.None));
        estimator.Add(new PacketHeader(
            0, long.MaxValue / 2, 48_000, AudioPacketConditions.TimestampError));
        estimator.Add(new PacketHeader(0, CaptureClock.UnitsPerSecond, 48_000, AudioPacketConditions.None));

        double? rate = estimator.MillisecondsPerHour();

        Assert.Equal(2, estimator.AnchorCount);
        Assert.NotNull(rate);
        Assert.InRange(rate.Value, -1.0, 1.0);
    }

    [Fact]
    public void TenMinutesAtFiftyMillisecondsPerHourStaysInsideThePhaseZeroCeiling()
    {
        // The gate pair: <=50 ms/hour residual drift keeps the ten-minute absolute error
        // well under 100 ms, and a three-hour run near 150 ms.
        DriftEstimator estimator = Simulate(millisecondsPerHour: 50.0, durationSeconds: 600);

        Assert.InRange(Math.Abs(estimator.LastDriftMilliseconds), 0.0, 100.0);
        Assert.InRange(estimator.MillisecondsPerHour()!.Value * 3.0, 0.0, 250.0);
    }
}
