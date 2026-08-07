using System.Globalization;
using EchoForge.Contracts.Evaluation;

namespace EchoForge.Core.Evaluation;

/// <summary>
/// The preregistered rule for whether the comparison model takes the default.
///
/// <para>
/// Written down, in code, before any held-out run. That ordering is the whole point: a decision
/// rule chosen after seeing the numbers is not a decision rule, it is a description of the result
/// somebody preferred. The plan fixes the shape — "change the default to Ministral only if it
/// improves the preregistered composite by at least five percentage points with no material memory
/// or failure regression" — and this is that sentence made executable.
/// </para>
///
/// <para>
/// The incumbent wins ties, wins near-ties, and wins anything the rule cannot decide. Gemma is the
/// default because the architecture selected it; displacing it takes a clear, measured margin, not
/// a favourable rounding.
/// </para>
/// </summary>
public static class BakeoffDecision
{
    /// <summary>Five percentage points on the composite, as the plan preregisters.</summary>
    public const double RequiredMarginPoints = 5.0;

    /// <summary>
    /// A failure rate this much worse than the incumbent's is a material regression, whatever the
    /// quality numbers say. A summariser that is better when it works and fails twice as often is
    /// not better.
    /// </summary>
    public const double MaterialFailureRegression = 0.05;

    /// <summary>
    /// Peak VRAM this much higher is a material regression. The architecture's constraint is a
    /// 16 GB card at 32K context, and a candidate that wins on quality while leaving no headroom
    /// has not won the thing that was being asked.
    /// </summary>
    public const double MaterialVramRegressionFraction = 0.10;

    /// <summary>
    /// The composite, weighted and fixed in advance.
    ///
    /// <para>
    /// Precision is weighted above recall because the failure this product cannot afford is a
    /// confident claim nobody made; a missed action item is a worse summary, an invented one is a
    /// worse decision. Evidence validity carries real weight because a citation that does not
    /// resolve teaches the reader that the citations are decorative. Owner and date precision are
    /// included because they are where a plausible summary does its most specific damage.
    /// </para>
    ///
    /// <para>
    /// Returns null when any component has no denominator: a composite computed over metrics that
    /// were never measured would be a number with no meaning, and comparing two of them would be
    /// worse than not comparing at all.
    /// </para>
    /// </summary>
    public static double? Composite(ModelEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        (Ratio Metric, double Weight)[] components =
        [
            (evaluation.CombinedPrecision, 0.35),
            (evaluation.CombinedRecall, 0.25),
            (evaluation.EvidenceValidity, 0.20),
            (evaluation.OwnerPrecision, 0.10),
            (evaluation.DatePrecision, 0.10),
        ];

        double total = 0;
        double weightUsed = 0;

        foreach ((Ratio metric, double weight) in components)
        {
            if (metric.Value is not { } value)
            {
                return null;
            }

            total += value * weight;
            weightUsed += weight;
        }

        return weightUsed == 0 ? null : total / weightUsed;
    }

    /// <summary>Decides, and explains every reason it decided that way.</summary>
    public static BakeoffVerdict Decide(ModelEvaluation incumbent, ModelEvaluation challenger)
    {
        ArgumentNullException.ThrowIfNull(incumbent);
        ArgumentNullException.ThrowIfNull(challenger);

        double? incumbentComposite = Composite(incumbent);
        double? challengerComposite = Composite(challenger);

        List<string> reasons = [];

        if (incumbentComposite is not { } baseline || challengerComposite is not { } candidate)
        {
            reasons.Add(
                "the composite could not be computed for both models, so the comparison decides nothing and the default stands");

            return new BakeoffVerdict
            {
                Incumbent = incumbent.Backend,
                Challenger = challenger.Backend,
                IncumbentComposite = incumbentComposite,
                ChallengerComposite = challengerComposite,
                MarginPoints = null,
                ShouldSwitchDefault = false,
                Reasons = reasons,
            };
        }

        double margin = (candidate - baseline) * 100.0;
        bool clearsMargin = margin >= RequiredMarginPoints;

        reasons.Add(Invariant(
            $"composite {candidate:P1} against {baseline:P1}, a margin of {margin:F1} points; {RequiredMarginPoints:F0} are required"));

        if (!clearsMargin)
        {
            reasons.Add("the margin does not clear the preregistered threshold, so the default stands");
        }

        // Regressions are checked whatever the margin, so the report says everything that was
        // wrong with a candidate rather than stopping at the first thing.
        bool regressed = false;

        double incumbentFailure = incumbent.FailureRate.Value ?? 0;
        double challengerFailure = challenger.FailureRate.Value ?? 0;

        if (challengerFailure > incumbentFailure + MaterialFailureRegression)
        {
            regressed = true;
            reasons.Add(Invariant(
                $"failure rate regressed from {incumbentFailure:P1} to {challengerFailure:P1}, which is material"));
        }

        if (incumbent.PeakVramBytes > 0 && challenger.PeakVramBytes > 0)
        {
            double growth = ((double)challenger.PeakVramBytes - incumbent.PeakVramBytes) / incumbent.PeakVramBytes;
            if (growth > MaterialVramRegressionFraction)
            {
                regressed = true;
                reasons.Add(Invariant($"peak VRAM grew by {growth:P1}, which is material on a 16 GB card"));
            }
        }

        if (challenger.UnsupportedExplicitOwners > incumbent.UnsupportedExplicitOwners ||
            challenger.UnsupportedExplicitDates > incumbent.UnsupportedExplicitDates)
        {
            regressed = true;
            reasons.Add(Invariant(
                $"unsupported explicit owners/dates regressed from {incumbent.UnsupportedExplicitOwners}/{incumbent.UnsupportedExplicitDates} to {challenger.UnsupportedExplicitOwners}/{challenger.UnsupportedExplicitDates}"));
        }

        bool shouldSwitch = clearsMargin && !regressed;

        if (shouldSwitch)
        {
            reasons.Add("the challenger clears the margin with no material regression, so the default may change");
        }
        else if (clearsMargin && regressed)
        {
            reasons.Add("the challenger clears the margin but regressed materially, so the default stands");
        }

        return new BakeoffVerdict
        {
            Incumbent = incumbent.Backend,
            Challenger = challenger.Backend,
            IncumbentComposite = incumbentComposite,
            ChallengerComposite = challengerComposite,
            MarginPoints = margin,
            ShouldSwitchDefault = shouldSwitch,
            Reasons = reasons,
        };
    }

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
