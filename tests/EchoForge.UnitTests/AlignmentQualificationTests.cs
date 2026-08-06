using EchoForge.Audio.Windows;

namespace EchoForge.UnitTests;

/// <summary>
/// The gates must never report a pass they did not measure. Track duration is not an input
/// here at all: both tracks are padded to a shared stop instant, so equal durations prove
/// nothing about alignment.
/// </summary>
public sealed class AlignmentQualificationTests
{
    private static List<AlignmentSample> Ramp(double minutes, double startOffsetMs, double msPerHour)
    {
        List<AlignmentSample> samples = [];
        for (double second = 0; second <= minutes * 60; second += 30)
        {
            samples.Add(new AlignmentSample(second, startOffsetMs + (msPerHour * second / 3600.0)));
        }

        return samples;
    }

    [Fact]
    public void NoMeasurementsMeansNeitherGateIsEvaluatedOrPassed()
    {
        AlignmentGateResult result = AlignmentQualification.Evaluate([]);

        Assert.False(result.TenMinuteEvaluated);
        Assert.False(result.TenMinutePassed);
        Assert.False(result.DriftEvaluated);
        Assert.False(result.DriftPassed);
        Assert.False(result.Qualified);
        Assert.Contains("NOT QUALIFIED", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortRunCannotQualifyEitherGateEvenWithPerfectOffsets()
    {
        // Five minutes of flawless alignment is still not a ten-minute measurement.
        AlignmentGateResult result = AlignmentQualification.Evaluate(Ramp(5, 0, 0));

        Assert.False(result.TenMinuteEvaluated);
        Assert.False(result.TenMinutePassed);
        Assert.False(result.Qualified);
    }

    [Fact]
    public void TenMinutesQualifiesTheAbsoluteGateButNotTheDriftGate()
    {
        AlignmentGateResult result = AlignmentQualification.Evaluate(Ramp(12, 10, 20));

        Assert.True(result.TenMinuteEvaluated);
        Assert.True(result.TenMinutePassed);
        Assert.False(result.DriftEvaluated);
        Assert.False(result.Qualified);
    }

    [Fact]
    public void AnOffsetOverTheCeilingFailsTheAbsoluteGate()
    {
        AlignmentGateResult result = AlignmentQualification.Evaluate(Ramp(12, 150, 0));

        Assert.True(result.TenMinuteEvaluated);
        Assert.False(result.TenMinutePassed);
        Assert.Equal(150, result.WorstOffsetMilliseconds!.Value, 1);
    }

    [Fact]
    public void AnHourWithinBothCeilingsQualifies()
    {
        AlignmentGateResult result = AlignmentQualification.Evaluate(Ramp(65, 5, 30));

        Assert.True(result.TenMinuteEvaluated);
        Assert.True(result.TenMinutePassed);
        Assert.True(result.DriftEvaluated);
        Assert.True(result.DriftPassed);
        Assert.True(result.Qualified);
        Assert.InRange(result.DriftMillisecondsPerHour!.Value, 29.0, 31.0);
    }

    [Fact]
    public void AnHourExceedingTheDriftCeilingFails()
    {
        AlignmentGateResult result = AlignmentQualification.Evaluate(Ramp(65, 0, 120));

        Assert.True(result.DriftEvaluated);
        Assert.False(result.DriftPassed);
        Assert.False(result.Qualified);
        Assert.InRange(result.DriftMillisecondsPerHour!.Value, 119.0, 121.0);
    }

    [Fact]
    public void DriftIsFittedFromTheSlopeNotTheEndpoints()
    {
        double? rate = AlignmentQualification.FitDriftMillisecondsPerHour(Ramp(60, 40, -45));

        Assert.NotNull(rate);
        Assert.InRange(rate.Value, -46.0, -44.0);
    }

    [Fact]
    public void ASingleSampleCannotProduceADriftRate()
    {
        Assert.Null(AlignmentQualification.FitDriftMillisecondsPerHour(
            [new AlignmentSample(0, 0)]));
    }
}
