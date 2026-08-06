using EchoForge.Contracts.Audio;

namespace EchoForge.Audio.Windows;

/// <summary>
/// Measures how fast one endpoint's sample clock diverges from the performance counter.
///
/// <para>
/// Phase 0 gates drift as a <em>rate</em> rather than a single offset, because an absolute
/// reading at ten minutes does not predict a three-hour session. The estimator compares the
/// audio actually delivered — the running total of frames, converted to seconds at the mix
/// rate — against elapsed QPC time, and fits a least-squares slope through the difference.
/// The slope is seconds of drift per second of wall time; the gate is milliseconds per hour.
/// </para>
///
/// <para>
/// It deliberately does not use <see cref="PacketHeader.DevicePosition"/>. Phase 0 measured a
/// 16 kHz headset microphone resampled to a 48 kHz mix format, where device position advanced
/// at a third of the delivered frame rate and would have reported enormous false drift.
/// </para>
/// </summary>
public sealed class DriftEstimator
{
    private readonly int _sampleRate;

    private long _anchorCount;
    private long? _firstQpc;
    private long _capturedFrames;

    // Running sums for a least-squares fit of drift (y) against wall seconds (x).
    private double _sumX;
    private double _sumY;
    private double _sumXy;
    private double _sumXx;

    public DriftEstimator(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _sampleRate = sampleRate;
    }

    /// <summary>Number of packets folded into the estimate.</summary>
    public long AnchorCount => _anchorCount;

    /// <summary>Wall-clock seconds spanned by the anchors so far.</summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>The most recent instantaneous drift, in milliseconds.</summary>
    public double LastDriftMilliseconds { get; private set; }

    /// <summary>
    /// Folds one packet's clock anchors into the estimate. Packets flagged with an
    /// unreliable timestamp are ignored rather than allowed to poison the fit.
    /// </summary>
    public void Add(in PacketHeader header)
    {
        if ((header.Conditions & AudioPacketConditions.TimestampError) != 0)
        {
            return;
        }

        _firstQpc ??= header.QpcPosition;

        // At this packet's timestamp the endpoint had delivered _capturedFrames frames.
        double deliveredSeconds = (double)_capturedFrames / _sampleRate;
        double wallSeconds = CaptureClock.UnitsToSeconds(header.QpcPosition - _firstQpc.Value);
        double drift = deliveredSeconds - wallSeconds;

        _sumX += wallSeconds;
        _sumY += drift;
        _sumXy += wallSeconds * drift;
        _sumXx += wallSeconds * wallSeconds;
        _anchorCount++;
        _capturedFrames += header.FrameCount;

        ElapsedSeconds = wallSeconds;
        LastDriftMilliseconds = drift * 1000.0;
    }

    /// <summary>
    /// Drift rate in milliseconds per hour, or null when there is not yet enough spread
    /// to fit a line. Reporting null is deliberate: a fabricated rate from two adjacent
    /// packets would read as a passing gate.
    /// </summary>
    public double? MillisecondsPerHour()
    {
        if (_anchorCount < 2)
        {
            return null;
        }

        double n = _anchorCount;
        double denominator = (n * _sumXx) - (_sumX * _sumX);
        if (Math.Abs(denominator) < 1e-9)
        {
            return null;
        }

        double slope = ((n * _sumXy) - (_sumX * _sumY)) / denominator;
        return slope * 3600.0 * 1000.0;
    }
}

