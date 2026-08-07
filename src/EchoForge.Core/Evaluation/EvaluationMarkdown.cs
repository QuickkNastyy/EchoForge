using System.Globalization;
using System.Text;
using EchoForge.Contracts.Evaluation;

namespace EchoForge.Core.Evaluation;

/// <summary>
/// Renders a report a person reads.
///
/// <para>
/// The JSON is the record; this is the version somebody pastes into a decision. Which means the
/// one thing it must never do is let a number be read as more than it is, so what the data
/// <i>is</i> comes first, before any metric — a development number and an acceptance number look
/// identical on a page unless something says otherwise.
/// </para>
/// </summary>
public static class EvaluationMarkdown
{
    public static string Render(EvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        StringBuilder markdown = new();

        markdown.AppendLine("# Summary evaluation");
        markdown.AppendLine();

        // What the data is, before what it says. A reader who stops after the first paragraph
        // should still be unable to mistake a synthetic run for an acceptance result.
        markdown.AppendLine(report.CorpusKind switch
        {
            CorpusKind.Release => "**Held-out release corpus.** This is acceptance data.",
            CorpusKind.Development => "**Development corpus.** Working measurements. Never an acceptance result.",
            _ => "**SYNTHETIC DATA.** Written by hand to test the scorer. These numbers say the arithmetic works "
                 + "and say nothing whatever about any model's summary quality.",
        });

        markdown.AppendLine();
        markdown.AppendLine(Invariant($"- Corpus: `{report.CorpusId}` ({report.CorpusKind.ToString().ToLowerInvariant()})"));
        markdown.AppendLine(Invariant($"- Corpus fingerprint: `{Short(report.CorpusSha256)}`"));
        markdown.AppendLine(Invariant($"- Generated: {report.GeneratedUtc:u}"));

        if (report.PromptVersions.Count > 0)
        {
            markdown.AppendLine(Invariant($"- Prompts: {string.Join(", ", report.PromptVersions)}"));
        }

        markdown.AppendLine();

        if (report.Acceptance is { } acceptance)
        {
            markdown.AppendLine("## Acceptance");
            markdown.AppendLine();
            markdown.AppendLine(Invariant($"**{acceptance.Statement}**"));
            markdown.AppendLine();

            if (acceptance.Failures.Count > 0)
            {
                foreach (string failure in acceptance.Failures)
                {
                    markdown.AppendLine(Invariant($"- {failure}"));
                }

                markdown.AppendLine();
            }
        }

        markdown.AppendLine("## Quality");
        markdown.AppendLine();
        markdown.AppendLine("| Metric | " + string.Join(" | ", report.Models.Select(m => m.Backend)) + " |");
        markdown.AppendLine("|---|" + string.Concat(report.Models.Select(_ => "---|")));

        Row(markdown, report, "Action/decision precision", m => m.CombinedPrecision.ToString());
        Row(markdown, report, "Action/decision recall", m => m.CombinedRecall.ToString());
        Row(markdown, report, "Owner precision", m => m.OwnerPrecision.ToString());
        Row(markdown, report, "Date precision", m => m.DatePrecision.ToString());
        Row(markdown, report, "Evidence validity", m => m.EvidenceValidity.ToString());
        Row(markdown, report, "Evidence coverage", m => m.EvidenceCoverage.ToString());
        Row(markdown, report, "Key-point coverage", m => m.KeyPointCoverage.ToString());
        Row(markdown, report, "Contradictions kept", m => m.ContradictionHandling.ToString());
        Row(markdown, report, "Unsupported explicit owners", m => m.UnsupportedExplicitOwners.ToString(CultureInfo.InvariantCulture));
        Row(markdown, report, "Unsupported explicit dates", m => m.UnsupportedExplicitDates.ToString(CultureInfo.InvariantCulture));
        Row(markdown, report, "Failed runs", m => m.FailureRate.ToString());

        markdown.AppendLine();
        markdown.AppendLine("Readability is deliberately absent. It is a human judgement, and an automatic");
        markdown.AppendLine("stand-in for one would be a number pretending to be an opinion.");
        markdown.AppendLine();

        markdown.AppendLine("## Cost");
        markdown.AppendLine();
        markdown.AppendLine("| Measurement | " + string.Join(" | ", report.Models.Select(m => m.Backend)) + " |");
        markdown.AppendLine("|---|" + string.Concat(report.Models.Select(_ => "---|")));

        Row(markdown, report, "Median seconds/meeting", m => m.MedianSeconds.ToString("F1", CultureInfo.InvariantCulture));
        Row(markdown, report, "Total seconds", m => m.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture));
        Row(markdown, report, "Peak VRAM", m => Gigabytes(m.PeakVramBytes));

        markdown.AppendLine();

        if (report.Bakeoff is { } bakeoff)
        {
            markdown.AppendLine("## Bake-off");
            markdown.AppendLine();
            markdown.AppendLine(Invariant($"Incumbent `{bakeoff.Incumbent}` against challenger `{bakeoff.Challenger}`."));
            markdown.AppendLine();
            markdown.AppendLine(bakeoff.ShouldSwitchDefault
                ? "**The challenger has earned the default under the preregistered rule.**"
                : "**The default stands.**");
            markdown.AppendLine();

            foreach (string reason in bakeoff.Reasons)
            {
                markdown.AppendLine(Invariant($"- {reason}"));
            }

            markdown.AppendLine();
        }

        AppendMatchLog(markdown, report);

        return markdown.ToString();
    }

    /// <summary>
    /// Every match decision, so a disputed number can be argued with.
    ///
    /// <para>
    /// A precision figure nobody can take apart is a figure nobody should act on, and the argument
    /// about a bake-off will be about individual facts rather than about percentages.
    /// </para>
    /// </summary>
    private static void AppendMatchLog(StringBuilder markdown, EvaluationReport report)
    {
        markdown.AppendLine("## Match decisions");
        markdown.AppendLine();

        foreach (ModelEvaluation model in report.Models)
        {
            markdown.AppendLine(Invariant($"### {model.Backend}"));
            markdown.AppendLine();

            foreach (MeetingScore meeting in model.Meetings)
            {
                markdown.AppendLine(Invariant($"**{meeting.MeetingId}**"));
                markdown.AppendLine();

                if (!meeting.ProducedSummary)
                {
                    markdown.AppendLine(Invariant($"- produced no summary: {meeting.FailureReason}"));
                    markdown.AppendLine();
                    continue;
                }

                foreach (MatchDecision decision in meeting.Decisions)
                {
                    string symbol = decision.Outcome switch
                    {
                        "matched" => "OK  ",
                        "false_positive" => "FP  ",
                        _ => "FN  ",
                    };

                    markdown.AppendLine(Invariant(
                        $"- `{symbol}` {decision.Category} · {Describe(decision)}"));
                }

                markdown.AppendLine();
            }
        }
    }

    private static string Describe(MatchDecision decision) => decision.Outcome switch
    {
        "matched" => Invariant(
            $"`{decision.GoldId}` ← \"{Trim(decision.PredictedText)}\" (similarity {decision.Similarity:F2}, shared {string.Join(",", decision.SharedEvidence)})"),
        "false_positive" => Invariant($"\"{Trim(decision.PredictedText)}\" — {decision.Reason}"),
        _ => Invariant($"`{decision.GoldId}` \"{Trim(decision.GoldText)}\" — {decision.Reason}"),
    };

    private static void Row(StringBuilder markdown, EvaluationReport report, string label, Func<ModelEvaluation, string> value) =>
        markdown.AppendLine("| " + label + " | " + string.Join(" | ", report.Models.Select(value)) + " |");

    private static string Gigabytes(long bytes) =>
        bytes <= 0 ? "not measured" : (bytes / 1_000_000_000.0).ToString("F2", CultureInfo.InvariantCulture) + " GB";

    private static string Short(string digest) => digest.Length <= 12 ? digest : digest[..12];

    private static string Trim(string? text) =>
        text is null ? string.Empty : text.Length <= 90 ? text : text[..87] + "...";

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
