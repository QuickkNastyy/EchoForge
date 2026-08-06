namespace EchoForge.Audio.Windows;

/// <summary>
/// One end-to-end alignment measurement: at <paramref name="SessionSeconds"/> into the
/// recording, a known signal appeared on the two tracks <paramref name="OffsetMilliseconds"/>
/// apart after derivative correction.
///
/// <para>
/// These come from a signal-based harness — timed chirps played through the render endpoint
/// and picked up acoustically by the microphone. They cannot be derived from file lengths.
/// </para>
/// </summary>
public sealed record AlignmentSample(double SessionSeconds, double OffsetMilliseconds);

/// <summary>Outcome of the Phase 0 timing gates. An unevaluated gate is never a pass.</summary>
public sealed record AlignmentGateResult(
    bool TenMinuteEvaluated,
    bool TenMinutePassed,
    double? WorstOffsetMilliseconds,
    bool DriftEvaluated,
    bool DriftPassed,
    double? DriftMillisecondsPerHour,
    string Explanation)
{
    /// <summary>True only when both gates were actually measured and both passed.</summary>
    public bool Qualified => TenMinuteEvaluated && TenMinutePassed && DriftEvaluated && DriftPassed;
}

/// <summary>
/// Evaluates the Phase 0 timing gates from signal-based measurements.
///
/// <para>
/// Deliberately separate from the capture system so the measurements can be supplied later,
/// from a chirp harness or from an offline analysis, without touching the recorder. Track
/// duration is <em>not</em> an input: both tracks are padded to a shared stop instant, so equal
/// durations say nothing about whether their audio lines up.
/// </para>
/// </summary>
public static class AlignmentQualification
{
    /// <summary>Absolute post-correction alignment ceiling at ten minutes, in milliseconds.</summary>
    public const double TenMinuteCeilingMilliseconds = 100.0;

    /// <summary>Residual corrected drift ceiling, in milliseconds per hour.</summary>
    public const double DriftCeilingMillisecondsPerHour = 50.0;

    /// <summary>The ten-minute gate needs measurements spanning at least this long.</summary>
    public const double TenMinuteQualifyingMinutes = 10.0;

    /// <summary>The drift gate needs a continuous run of at least this long.</summary>
    public const double DriftQualifyingMinutes = 60.0;

    public static AlignmentGateResult Evaluate(IReadOnlyList<AlignmentSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return new AlignmentGateResult(
                false, false, null, false, false, null,
                "No signal-based alignment measurements supplied. Both timing gates are NOT QUALIFIED.");
        }

        double spanMinutes = (samples.Max(s => s.SessionSeconds) - samples.Min(s => s.SessionSeconds)) / 60.0;
        double worst = samples.Max(s => Math.Abs(s.OffsetMilliseconds));

        bool tenMinuteEvaluated = spanMinutes >= TenMinuteQualifyingMinutes;
        bool tenMinutePassed = tenMinuteEvaluated && worst <= TenMinuteCeilingMilliseconds;

        double? driftRate = FitDriftMillisecondsPerHour(samples);
        bool driftEvaluated = spanMinutes >= DriftQualifyingMinutes && driftRate is not null;
        bool driftPassed = driftEvaluated && Math.Abs(driftRate!.Value) <= DriftCeilingMillisecondsPerHour;

        string explanation = (tenMinuteEvaluated, driftEvaluated) switch
        {
            (false, _) => $"Measurements span {spanMinutes:0.0} min; the ten-minute gate needs at least {TenMinuteQualifyingMinutes:0} min.",
            (true, false) => $"Measurements span {spanMinutes:0.0} min; the drift gate needs a continuous run of at least {DriftQualifyingMinutes:0} min.",
            _ => "Both gates evaluated from signal-based measurements.",
        };

        return new AlignmentGateResult(
            tenMinuteEvaluated, tenMinutePassed, worst,
            driftEvaluated, driftPassed, driftRate,
            explanation);
    }

    /// <summary>
    /// Least-squares slope of offset against session time, converted to milliseconds per hour.
    /// Null when the samples do not span enough time to fit a line.
    /// </summary>
    public static double? FitDriftMillisecondsPerHour(IReadOnlyList<AlignmentSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2)
        {
            return null;
        }

        double n = samples.Count;
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;

        foreach (AlignmentSample sample in samples)
        {
            sumX += sample.SessionSeconds;
            sumY += sample.OffsetMilliseconds;
            sumXy += sample.SessionSeconds * sample.OffsetMilliseconds;
            sumXx += sample.SessionSeconds * sample.SessionSeconds;
        }

        double denominator = (n * sumXx) - (sumX * sumX);
        if (Math.Abs(denominator) < 1e-9)
        {
            return null;
        }

        // Slope is milliseconds of offset per second of session time.
        double slope = ((n * sumXy) - (sumX * sumY)) / denominator;
        return slope * 3600.0;
    }
}
