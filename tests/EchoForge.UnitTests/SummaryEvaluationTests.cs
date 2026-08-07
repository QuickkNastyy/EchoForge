using EchoForge.Contracts.Evaluation;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Evaluation;
using EchoForge.Core.Summaries;

namespace EchoForge.UnitTests;

/// <summary>
/// The evaluation harness: the corpus rules, and the arithmetic that turns a summary into a score.
///
/// <para>
/// These matter more than most tests in this repository, because a scorer that is quietly wrong
/// does not fail — it produces a number, and the number gets quoted in a decision about which
/// model ships. Every metric here is tested at its edges, including the ones with no denominator.
/// </para>
/// </summary>
public sealed class SummaryEvaluationTests
{
    // -- fixtures ---------------------------------------------------------------------------------

    private static TranscriptDocument Transcript(int segments = 8)
    {
        (string speakerId, string speakerName) = TranscriptSpeakers.For(TranscriptSpeakers.MicrophoneTrack);

        return new TranscriptDocument
        {
            SessionId = "01JEVAL",
            TranscriptRevision = 1,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero),
            SourceManifestSha256 = new string('a', 64),
            DurationSeconds = 600,
            Model = new TranscriptModel("m", "m", "m", "m", "none", false, "0.1.0"),
            Epochs = [new TranscriptEpoch(1, 0, 600)],
            Speakers = [new TranscriptSpeaker(speakerId, speakerName, TranscriptSpeakers.MicrophoneTrack)],
            Languages = [new TranscriptLanguage(TranscriptSpeakers.MicrophoneTrack, "en", null)],
            Segments =
            [
                .. Enumerable.Range(1, segments).Select(i => new TranscriptSegment
                {
                    Id = $"segment-{i:D6}",
                    Epoch = 1,
                    StartSeconds = i * 10,
                    EndSeconds = (i * 10) + 5,
                    SpeakerId = speakerId,
                    SpeakerName = speakerName,
                    SourceTrack = TranscriptSpeakers.MicrophoneTrack,
                    Text = $"line {i}",
                    Confidence = null,
                    Language = "en",
                    Words = [],
                })
            ],
        };
    }

    private static SummaryEvidence Cite(string segmentId) =>
        SummaryValidator.Cite(Transcript(), segmentId)!;

    private static SummaryDocument Summary(
        IEnumerable<SummaryItem>? decisions = null,
        IEnumerable<SummaryAction>? actions = null,
        IEnumerable<SummaryItem>? keyPoints = null) => new()
        {
            SessionId = "01JEVAL",
            SummaryRevision = 1,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero),
            TranscriptRevision = 1,
            TranscriptSha256 = new string('b', 64),
            PromptVersion = "meeting-summary-v1",
            Model = new SummaryModel("llama.cpp", "gemma-4-12b", "g", "g", 32768, false, true, "0.1.0"),
            Decisions = decisions is null ? [] : [.. decisions],
            ActionItems = actions is null ? [] : [.. actions],
            KeyPoints = keyPoints is null ? [] : [.. keyPoints],
        };

    private static SummaryItem Decision(string id, string text, params string[] segments) => new()
    {
        Id = id,
        Text = text,
        Certainty = SupportStatuses.Explicit,
        Confidence = null,
        Evidence = [.. segments.Select(Cite)],
    };

    private static SummaryAction Action(
        string id,
        string task,
        string[] segments,
        string? owner = null,
        string ownerStatus = SupportStatuses.Unknown,
        string? dueDate = null,
        string dueStatus = SupportStatuses.Unknown) => new()
        {
            Id = id,
            Task = task,
            Certainty = SupportStatuses.Explicit,
            Confidence = null,
            Evidence = [.. segments.Select(Cite)],
            Owner = owner,
            OwnerStatus = ownerStatus,
            DueDate = dueDate,
            DueDateText = dueDate is null ? null : "by then",
            DueDateStatus = dueStatus,
        };

    private static CorpusMeeting Meeting(GoldSummary gold, string id = "m1") => new()
    {
        MeetingId = id,
        MeetingDate = "2026-08-07",
        TranscriptPath = "transcripts/m1.json",
        TranscriptSha256 = new string('c', 64),
        TranscriptFidelity = TranscriptFidelity.HumanCorrected,
        Gold = gold,
    };

    private static GoldItem Gold(string id, string text, params string[] segments) =>
        new() { Id = id, Text = text, Evidence = segments };

    private static GoldAction GoldAct(
        string id,
        string task,
        string[] segments,
        string? owner = null,
        string ownerStatus = "unknown",
        string? dueDate = null,
        string dueStatus = "unknown") => new()
        {
            Id = id,
            Task = task,
            Evidence = segments,
            Owner = owner,
            OwnerStatus = ownerStatus,
            DueDate = dueDate,
            DueDateStatus = dueStatus,
        };

    private static SummaryCorpus Corpus(CorpusKind kind, params CorpusMeeting[] meetings) => new()
    {
        CorpusId = kind.ToString().ToLowerInvariant(),
        Kind = kind,
        Meetings = meetings,
    };

    // -- scoring: the ordinary cases -----------------------------------------------------------------

    [Fact]
    public void APerfectSummaryScoresPerfectly()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            Decisions = [Gold("d1", "Ship the beta on Friday", "segment-000002")],
            ActionItems = [GoldAct("a1", "Prepare the release notes", ["segment-000004"], "Alex", "explicit", "2026-08-14", "explicit")],
        });

        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(
                decisions: [Decision("p1", "Ship the beta on Friday", "segment-000002")],
                actions: [Action("p2", "Prepare the release notes", ["segment-000004"], "Alex", SupportStatuses.Explicit, "2026-08-14", SupportStatuses.Explicit)]),
            Transcript());

        Assert.Equal(1.0, score.CombinedPrecision.Value);
        Assert.Equal(1.0, score.CombinedRecall.Value);
        Assert.Equal(1.0, score.OwnerPrecision.Value);
        Assert.Equal(1.0, score.DatePrecision.Value);
        Assert.Equal(1.0, score.EvidenceValidity.Value);
        Assert.Equal(0, score.UnsupportedExplicitOwners);
    }

    [Fact]
    public void EveryPredictionBeingInventedIsZeroPrecisionAndZeroRecall()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            Decisions = [Gold("d1", "Ship the beta on Friday", "segment-000002")],
        });

        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(decisions: [Decision("p1", "Cancel the project entirely", "segment-000007")]),
            Transcript());

        Assert.Equal(0.0, score.CombinedPrecision.Value);
        Assert.Equal(0.0, score.CombinedRecall.Value);
        Assert.Contains(score.Decisions, d => d.Outcome == "false_positive");
        Assert.Contains(score.Decisions, d => d.Outcome == "false_negative");
    }

    [Fact]
    public void FindingNothingIsFullPrecisionAndNoRecall()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            Decisions = [Gold("d1", "Ship the beta on Friday", "segment-000002")],
        });

        MeetingScore score = SummaryScorer.Score(meeting, Summary(), Transcript());

        // Emitting nothing is not a precision failure — it emitted no wrong claims. It is a
        // recall failure, and reporting it as both would double-count one mistake.
        Assert.False(score.CombinedPrecision.IsApplicable);
        Assert.Equal(0.0, score.CombinedRecall.Value);
    }

    [Fact]
    public void HalfRightIsReportedAsHalfRight()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            Decisions =
            [
                Gold("d1", "Ship the beta on Friday", "segment-000002"),
                Gold("d2", "Freeze the schema this week", "segment-000003"),
            ],
        });

        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(decisions:
            [
                Decision("p1", "Ship the beta on Friday", "segment-000002"),
                Decision("p2", "Hire two more engineers", "segment-000006"),
            ]),
            Transcript());

        Assert.Equal(0.5, score.CombinedPrecision.Value);
        Assert.Equal(0.5, score.CombinedRecall.Value);
    }

    // -- matching: conservative on purpose --------------------------------------------------------

    [Fact]
    public void TwoStatementsCitingNoCommonSegmentAreNeverTheSameFact()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            Decisions = [Gold("d1", "Ship the beta on Friday", "segment-000002")],
        });

        // Word-for-word identical, different moment of the meeting. Evidence anchors the match,
        // so this is a miss and an invention rather than a hit.
        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(decisions: [Decision("p1", "Ship the beta on Friday", "segment-000008")]),
            Transcript());

        Assert.Equal(0.0, score.CombinedPrecision.Value);
        Assert.Contains(score.Decisions, d => d.Outcome == "false_positive" && d.Reason!.Contains("no segment", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAnnotatorAcceptedWordingCounts()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            Decisions =
            [
                new GoldItem
                {
                    Id = "d1",
                    Text = "Ship the beta on Friday",
                    Evidence = ["segment-000002"],
                    Aliases = ["The beta will go out at the end of the week"],
                },
            ],
        });

        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(decisions: [Decision("p1", "The beta will go out at the end of the week!", "segment-000002")]),
            Transcript());

        Assert.Equal(1.0, score.CombinedPrecision.Value);
    }

    [Fact]
    public void PunctuationAndCasingAreNoiseButWordChoiceIsNot()
    {
        Assert.Equal(SummaryScorer.Normalise("Ship the BETA, on Friday!"), SummaryScorer.Normalise("ship the beta on friday"));
        Assert.NotEqual(SummaryScorer.Normalise("ship on friday"), SummaryScorer.Normalise("ship on monday"));
    }

    [Fact]
    public void OneGoldFactCanOnlyBeFoundOnce()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            Decisions = [Gold("d1", "Ship the beta on Friday", "segment-000002")],
        });

        // The same decision emitted twice is one hit and one invention, not two hits. A model
        // that repeats itself must not be able to inflate recall by doing so.
        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(decisions:
            [
                Decision("p1", "Ship the beta on Friday", "segment-000002"),
                Decision("p2", "Ship the beta on Friday", "segment-000002"),
            ]),
            Transcript());

        Assert.Equal(0.5, score.CombinedPrecision.Value);
        Assert.Equal(1.0, score.CombinedRecall.Value);
    }

    [Fact]
    public void TwoNearlyIdenticalGoldFactsAreNotBothClaimedByOnePrediction()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            Decisions =
            [
                Gold("d1", "Ship the beta on Friday", "segment-000002"),
                Gold("d2", "Ship the beta on Friday", "segment-000002"),
            ],
        });

        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(decisions: [Decision("p1", "Ship the beta on Friday", "segment-000002")]),
            Transcript());

        Assert.Equal(1.0, score.CombinedPrecision.Value);
        Assert.Equal(0.5, score.CombinedRecall.Value);
    }

    [Fact]
    public void MatchingIsDeterministic()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            Decisions =
            [
                Gold("d1", "Ship the beta on Friday", "segment-000002"),
                Gold("d2", "Ship the release on Friday", "segment-000002"),
            ],
        });

        SummaryDocument prediction = Summary(decisions:
        [
            Decision("p1", "Ship the beta on Friday", "segment-000002"),
            Decision("p2", "Ship the release on Friday", "segment-000002"),
        ]);

        string First() => string.Join(
            ";",
            SummaryScorer.Score(meeting, prediction, Transcript()).Decisions
                .Select(d => $"{d.PredictedId}->{d.GoldId}:{d.Outcome}"));

        Assert.Equal(First(), First());
    }

    // -- owners and dates ----------------------------------------------------------------------------

    [Fact]
    public void AWrongOwnerIsAPrecisionMissNotASilentPass()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            ActionItems = [GoldAct("a1", "Prepare the deck", ["segment-000004"], "Alex", "explicit")],
        });

        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(actions: [Action("p1", "Prepare the deck", ["segment-000004"], "Priya", SupportStatuses.Explicit)]),
            Transcript());

        Assert.Equal(1.0, score.CombinedPrecision.Value);
        Assert.Equal(0.0, score.OwnerPrecision.Value);
    }

    [Fact]
    public void AWrongDateIsAPrecisionMiss()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            ActionItems = [GoldAct("a1", "Prepare the deck", ["segment-000004"], dueDate: "2026-08-14", dueStatus: "explicit")],
        });

        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(actions: [Action("p1", "Prepare the deck", ["segment-000004"], dueDate: "2026-08-21", dueStatus: SupportStatuses.Explicit)]),
            Transcript());

        Assert.Equal(0.0, score.DatePrecision.Value);
    }

    [Fact]
    public void KeepingADeliberateUnknownIsMeasuredAsSuccess()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            ActionItems = [GoldAct("a1", "Write up the migration guide", ["segment-000006"])],
        });

        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(actions: [Action("p1", "Write up the migration guide", ["segment-000006"])]),
            Transcript());

        Assert.Equal(1.0, score.UnknownOwnerPreserved.Value);
        Assert.Equal(1.0, score.UnknownDatePreserved.Value);
        Assert.Equal(0, score.UnsupportedExplicitOwners);

        // The gold names nobody, so there is no owner to be precise about.
        Assert.False(score.OwnerPrecision.IsApplicable);
    }

    [Fact]
    public void InventingAnOwnerForAnUnassignedActionIsCountedSeparately()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary
        {
            ActionItems = [GoldAct("a1", "Write up the migration guide", ["segment-000006"])],
        });

        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(actions: [Action("p1", "Write up the migration guide", ["segment-000006"], "Someone", SupportStatuses.Explicit)]),
            Transcript());

        // The action itself was found. What was invented is the commitment, which the plan's gate
        // allows none of and which would otherwise hide inside a good-looking precision figure.
        Assert.Equal(1.0, score.CombinedPrecision.Value);
        Assert.Equal(1, score.UnsupportedExplicitOwners);
        Assert.Equal(0.0, score.UnknownOwnerPreserved.Value);
    }

    [Fact]
    public void AnInventedActionThatAlsoAssertsAnOwnerCountsBoth()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship on Friday", "segment-000002")] });

        MeetingScore score = SummaryScorer.Score(
            meeting,
            Summary(actions: [Action("p1", "Rewrite the billing system", ["segment-000007"], "Alex", SupportStatuses.Explicit, "2026-09-01", SupportStatuses.Explicit)]),
            Transcript());

        Assert.Equal(1, score.UnsupportedExplicitOwners);
        Assert.Equal(1, score.UnsupportedExplicitDates);
    }

    // -- evidence and contradictions ------------------------------------------------------------------

    [Fact]
    public void ACitationToASegmentThatDoesNotExistFailsEvidenceValidity()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship on Friday", "segment-000002")] });

        SummaryItem invented = new()
        {
            Id = "p1",
            Text = "Ship on Friday",
            Certainty = SupportStatuses.Explicit,
            Evidence = [new SummaryEvidence
            {
                TranscriptRevision = 1,
                SegmentId = "segment-999999",
                SourceTrack = "microphone",
                StartSeconds = 10,
                EndSeconds = 15,
                DisplayTimestamp = "00:00:10",
            }],
        };

        MeetingScore score = SummaryScorer.Score(meeting, Summary(decisions: [invented]), Transcript());

        Assert.Equal(0.0, score.EvidenceValidity.Value);
    }

    [Fact]
    public void AContradictionOnlyCountsWhenBothSidesSurvive()
    {
        GoldSummary gold = new()
        {
            Decisions =
            [
                Gold("d1", "Ship the beta on Friday", "segment-000002"),
                Gold("d2", "Ship the beta on Monday instead", "segment-000005"),
            ],
            Contradictions = [new GoldContradiction { Id = "c1", ItemIds = ["d1", "d2"] }],
        };

        MeetingScore both = SummaryScorer.Score(
            Meeting(gold),
            Summary(decisions:
            [
                Decision("p1", "Ship the beta on Friday", "segment-000002"),
                Decision("p2", "Ship the beta on Monday instead", "segment-000005"),
            ]),
            Transcript());

        Assert.Equal(1.0, both.ContradictionHandling.Value);

        MeetingScore tidied = SummaryScorer.Score(
            Meeting(gold),
            Summary(decisions: [Decision("p1", "Ship the beta on Monday instead", "segment-000005")]),
            Transcript());

        // Keeping the later half of a reversal is not partial credit. It is the specific failure
        // that makes a reader act on a decision the meeting undid.
        Assert.Equal(0.0, tidied.ContradictionHandling.Value);
    }

    [Fact]
    public void AMeetingWithNoContradictionsHasNoContradictionScore()
    {
        MeetingScore score = SummaryScorer.Score(
            Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship on Friday", "segment-000002")] }),
            Summary(decisions: [Decision("p1", "Ship on Friday", "segment-000002")]),
            Transcript());

        Assert.False(score.ContradictionHandling.IsApplicable);
    }

    // -- zero denominators ------------------------------------------------------------------------------

    [Fact]
    public void AnEmptyMeetingWithAnEmptySummaryIsNotApplicableRatherThanPerfect()
    {
        MeetingScore score = SummaryScorer.Score(Meeting(new GoldSummary()), Summary(), Transcript());

        // Reporting 100% here would let a corpus of empty meetings clear an acceptance gate.
        Assert.False(score.CombinedPrecision.IsApplicable);
        Assert.False(score.CombinedRecall.IsApplicable);
        Assert.Null(score.CombinedPrecision.Value);
    }

    [Fact]
    public void ARunThatProducedNothingIsAFailureNotAMeetingOfZeroes()
    {
        MeetingScore score = SummaryScorer.Score(
            Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship on Friday", "segment-000002")] }),
            prediction: null,
            Transcript(),
            failureReason: "the model would not load");

        Assert.False(score.ProducedSummary);
        Assert.Equal("the model would not load", score.FailureReason);
        Assert.False(score.CombinedPrecision.IsApplicable);

        ModelEvaluation aggregate = SummaryScorer.Aggregate("gemma-4-12b", [score]);
        Assert.Equal(1.0, aggregate.FailureRate.Value);
    }

    [Fact]
    public void AggregationAddsCountsRatherThanAveragingPercentages()
    {
        Ratio big = Ratio.Of(90, 100);
        Ratio small = Ratio.Of(0, 1);

        // Averaging would give 45%. The truth is 89 out of 101.
        Assert.Equal(90.0 / 101, (big + small).Value);
    }

    // -- acceptance --------------------------------------------------------------------------------------

    private static ModelEvaluation Evaluation(
        double precision = 1.0,
        double recall = 1.0,
        double evidence = 1.0,
        int badOwners = 0,
        double failureRate = 0.0,
        long vram = 8_000_000_000) => new()
        {
            Backend = "test",
            CombinedPrecision = Ratio.Of((int)(precision * 100), 100),
            CombinedRecall = Ratio.Of((int)(recall * 100), 100),
            EvidenceValidity = Ratio.Of((int)(evidence * 100), 100),
            OwnerPrecision = Ratio.Of(100, 100),
            DatePrecision = Ratio.Of(100, 100),
            UnsupportedExplicitOwners = badOwners,
            FailureRate = Ratio.Of((int)(failureRate * 100), 100),
            PeakVramBytes = vram,
        };

    [Fact]
    public void GoodNumbersOnDevelopmentDataAreStillNotAnAcceptanceResult()
    {
        AcceptanceVerdict verdict = SummaryScorer.Judge(Evaluation(), CorpusKind.Development);

        Assert.True(verdict.Passed);
        Assert.False(verdict.IsAcceptanceRun);
        Assert.Contains("NOT AN ACCEPTANCE RESULT", verdict.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void PerfectNumbersOnSyntheticDataSaySoLoudly()
    {
        AcceptanceVerdict verdict = SummaryScorer.Judge(Evaluation(), CorpusKind.Synthetic);

        Assert.False(verdict.IsAcceptanceRun);
        Assert.Contains("synthetic", verdict.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheGateRequiresEveryTargetTogether()
    {
        Assert.True(SummaryScorer.Judge(Evaluation(), CorpusKind.Release).Passed);

        Assert.False(SummaryScorer.Judge(Evaluation(precision: 0.94), CorpusKind.Release).Passed);
        Assert.False(SummaryScorer.Judge(Evaluation(recall: 0.84), CorpusKind.Release).Passed);
        Assert.False(SummaryScorer.Judge(Evaluation(evidence: 0.99), CorpusKind.Release).Passed);

        // One invented commitment fails the gate however good everything else is.
        Assert.False(SummaryScorer.Judge(Evaluation(badOwners: 1), CorpusKind.Release).Passed);
    }

    [Fact]
    public void AnUnmeasurableModelDoesNotPassByDefault()
    {
        ModelEvaluation nothing = new() { Backend = "test" };

        AcceptanceVerdict verdict = SummaryScorer.Judge(nothing, CorpusKind.Release);

        Assert.False(verdict.Passed);
        Assert.NotEmpty(verdict.Failures);
    }

    // -- the bake-off rule ---------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultStandsUnlessTheChallengerClearsFivePoints()
    {
        ModelEvaluation incumbent = Evaluation(precision: 0.90, recall: 0.80);

        Assert.False(BakeoffDecision.Decide(incumbent, Evaluation(precision: 0.91, recall: 0.81)).ShouldSwitchDefault);
        Assert.False(BakeoffDecision.Decide(incumbent, incumbent).ShouldSwitchDefault);
        Assert.True(BakeoffDecision.Decide(incumbent, Evaluation(precision: 1.0, recall: 1.0)).ShouldSwitchDefault);
    }

    [Fact]
    public void AChallengerThatFailsMoreOftenDoesNotTakeTheDefault()
    {
        BakeoffVerdict verdict = BakeoffDecision.Decide(
            Evaluation(precision: 0.85, recall: 0.75, failureRate: 0.0),
            Evaluation(precision: 1.0, recall: 1.0, failureRate: 0.20));

        // Better when it works and failing five times as often is not better.
        Assert.False(verdict.ShouldSwitchDefault);
        Assert.Contains(verdict.Reasons, r => r.Contains("failure rate regressed", StringComparison.Ordinal));
    }

    [Fact]
    public void AChallengerThatEatsTheVramHeadroomDoesNotTakeTheDefault()
    {
        BakeoffVerdict verdict = BakeoffDecision.Decide(
            Evaluation(precision: 0.85, recall: 0.75, vram: 8_000_000_000),
            Evaluation(precision: 1.0, recall: 1.0, vram: 14_000_000_000));

        Assert.False(verdict.ShouldSwitchDefault);
        Assert.Contains(verdict.Reasons, r => r.Contains("peak VRAM", StringComparison.Ordinal));
    }

    [Fact]
    public void AChallengerThatInventsMoreCommitmentsDoesNotTakeTheDefault()
    {
        BakeoffVerdict verdict = BakeoffDecision.Decide(
            Evaluation(precision: 0.85, recall: 0.75, badOwners: 0),
            Evaluation(precision: 1.0, recall: 1.0, badOwners: 3));

        Assert.False(verdict.ShouldSwitchDefault);
    }

    [Fact]
    public void AnIncomparablePairDecidesNothingAndTheDefaultStands()
    {
        BakeoffVerdict verdict = BakeoffDecision.Decide(Evaluation(), new ModelEvaluation { Backend = "unmeasured" });

        Assert.False(verdict.ShouldSwitchDefault);
        Assert.Null(verdict.ChallengerComposite);
        Assert.Contains(verdict.Reasons, r => r.Contains("decides nothing", StringComparison.Ordinal));
    }

    // -- corpus rules -------------------------------------------------------------------------------------

    [Fact]
    public void AWellFormedCorpusValidates()
    {
        CorpusVerdict verdict = CorpusValidator.Validate(Corpus(
            CorpusKind.Development,
            Meeting(new GoldSummary
            {
                Decisions = [Gold("d1", "Ship on Friday", "segment-000002")],
                ActionItems = [GoldAct("a1", "Prepare the deck", ["segment-000004"], "Alex", "explicit")],
            })));

        Assert.True(verdict.IsValid, string.Join("; ", verdict.Problems));
    }

    [Fact]
    public void SyntheticDataCannotMasqueradeAsRealData()
    {
        CorpusMeeting written = Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship", "segment-000002")] }) with
        {
            Synthetic = true,
        };

        foreach (CorpusKind kind in (CorpusKind[])[CorpusKind.Development, CorpusKind.Release])
        {
            CorpusVerdict verdict = CorpusValidator.Validate(Corpus(kind, written));

            Assert.False(verdict.IsValid);
            Assert.Contains(verdict.Problems, p => p.Contains("marked synthetic", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RealDataCannotHideInASyntheticCorpusEither()
    {
        CorpusVerdict verdict = CorpusValidator.Validate(Corpus(
            CorpusKind.Synthetic,
            Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship", "segment-000002")] })));

        Assert.False(verdict.IsValid);
    }

    [Fact]
    public void SummaryQualityIsNeverScoredAgainstRawRecogniserOutput()
    {
        CorpusMeeting uncorrected = Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship", "segment-000002")] }) with
        {
            TranscriptFidelity = TranscriptFidelity.Recognised,
        };

        CorpusVerdict verdict = CorpusValidator.Validate(Corpus(CorpusKind.Development, uncorrected));

        Assert.False(verdict.IsValid);
        Assert.Contains(verdict.Problems, p => p.Contains("nobody has corrected", StringComparison.Ordinal));
    }

    [Fact]
    public void AGoldFactWithNoEvidenceIsRejected()
    {
        CorpusVerdict verdict = CorpusValidator.Validate(Corpus(
            CorpusKind.Development,
            Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship on Friday")] })));

        Assert.False(verdict.IsValid);
        Assert.Contains(verdict.Problems, p => p.Contains("cites no evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void AGoldUnknownOwnerMayNotHaveAName()
    {
        CorpusVerdict verdict = CorpusValidator.Validate(Corpus(
            CorpusKind.Development,
            Meeting(new GoldSummary
            {
                ActionItems = [GoldAct("a1", "Prepare the deck", ["segment-000004"], "Alex")],
            })));

        Assert.False(verdict.IsValid);
        Assert.Contains(verdict.Problems, p => p.Contains("unknown owner but names one", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateMeetingIdsAndDuplicateGoldIdsAreRejected()
    {
        CorpusMeeting meeting = Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship", "segment-000002")] });

        Assert.False(CorpusValidator.Validate(Corpus(CorpusKind.Development, meeting, meeting)).IsValid);

        Assert.False(CorpusValidator.Validate(Corpus(
            CorpusKind.Development,
            Meeting(new GoldSummary
            {
                Decisions = [Gold("d1", "Ship", "segment-000002")],
                ActionItems = [GoldAct("d1", "Prepare", ["segment-000004"])],
            }))).IsValid);
    }

    [Fact]
    public void AContradictionNamingAFactThatDoesNotExistIsRejected()
    {
        CorpusVerdict verdict = CorpusValidator.Validate(Corpus(
            CorpusKind.Development,
            Meeting(new GoldSummary
            {
                Decisions = [Gold("d1", "Ship", "segment-000002")],
                Contradictions = [new GoldContradiction { Id = "c1", ItemIds = ["d1", "d9"] }],
            })));

        Assert.False(verdict.IsValid);
        Assert.Contains(verdict.Problems, p => p.Contains("not a gold fact", StringComparison.Ordinal));
    }

    [Fact]
    public void GoldEvidenceMustResolveInTheTranscriptItIsScoredAgainst()
    {
        CorpusVerdict verdict = CorpusValidator.ValidateAgainstTranscript(
            Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship", "segment-999999")] }),
            Transcript());

        Assert.False(verdict.IsValid);
        Assert.Contains(verdict.Problems, p => p.Contains("segment-999999", StringComparison.Ordinal));
    }

    [Fact]
    public void DevelopmentAndReleaseMayNotShareAMeeting()
    {
        CorpusMeeting shared = Meeting(new GoldSummary { Decisions = [Gold("d1", "Ship", "segment-000002")] });

        CorpusVerdict verdict = CorpusValidator.ValidateSeparation(
            Corpus(CorpusKind.Development, shared),
            Corpus(CorpusKind.Release, shared));

        Assert.False(verdict.IsValid);
    }

    [Fact]
    public void TheSameTranscriptUnderTwoNamesIsStillAnOverlap()
    {
        GoldSummary gold = new() { Decisions = [Gold("d1", "Ship", "segment-000002")] };

        CorpusVerdict verdict = CorpusValidator.ValidateSeparation(
            Corpus(CorpusKind.Development, Meeting(gold, "dev-1")),
            Corpus(CorpusKind.Release, Meeting(gold, "rel-1")));

        // Renaming a re-exported meeting is how this actually happens, and a meeting ID check
        // alone would miss every instance of it.
        Assert.False(verdict.IsValid);
        Assert.Contains(verdict.Problems, p => p.Contains("same transcript under two names", StringComparison.Ordinal));
    }

    // -- corpus identity and resume ---------------------------------------------------------------------------

    [Fact]
    public void TheCorpusFingerprintIsStableAcrossReadsAndChangesWithTheGold()
    {
        SummaryCorpus corpus = Corpus(CorpusKind.Development, Meeting(new GoldSummary
        {
            Decisions = [Gold("d1", "Ship on Friday", "segment-000002")],
        }));

        Assert.Equal(CorpusValidator.Fingerprint(corpus), CorpusValidator.Fingerprint(corpus));

        SummaryCorpus edited = Corpus(CorpusKind.Development, Meeting(new GoldSummary
        {
            Decisions = [Gold("d1", "Ship on Monday", "segment-000002")],
        }));

        Assert.NotEqual(CorpusValidator.Fingerprint(corpus), CorpusValidator.Fingerprint(edited));
    }

    [Fact]
    public void AnEditedNoteDoesNotInvalidateACompletedEvaluation()
    {
        SummaryCorpus before = Corpus(CorpusKind.Development, Meeting(new GoldSummary
        {
            Decisions = [Gold("d1", "Ship on Friday", "segment-000002")],
        }));

        SummaryCorpus after = Corpus(CorpusKind.Development, Meeting(new GoldSummary
        {
            Decisions = [new GoldItem { Id = "d1", Text = "Ship on Friday", Evidence = ["segment-000002"], Notes = "clarified" }],
        }));

        // An annotator explaining their reasoning should not throw away hours of completed runs.
        // A changed fact must.
        Assert.Equal(CorpusValidator.Fingerprint(before), CorpusValidator.Fingerprint(after));
    }

    [Fact]
    public void AChangedPromptOrModelInvalidatesACheckpoint()
    {
        string baseline = EvaluationCheckpoints.Fingerprint("corpus", "m1", "gemma-4-12b", "rev1", ["extract-v1"], "seed=7");

        Assert.Equal(baseline, EvaluationCheckpoints.Fingerprint("corpus", "m1", "gemma-4-12b", "rev1", ["extract-v1"], "seed=7"));
        Assert.NotEqual(baseline, EvaluationCheckpoints.Fingerprint("corpus", "m1", "gemma-4-12b", "rev1", ["extract-v2"], "seed=7"));
        Assert.NotEqual(baseline, EvaluationCheckpoints.Fingerprint("corpus", "m1", "gemma-4-12b", "rev2", ["extract-v1"], "seed=7"));
        Assert.NotEqual(baseline, EvaluationCheckpoints.Fingerprint("corpus", "m1", "ministral-3-14b", "rev1", ["extract-v1"], "seed=7"));
        Assert.NotEqual(baseline, EvaluationCheckpoints.Fingerprint("corpus2", "m1", "gemma-4-12b", "rev1", ["extract-v1"], "seed=7"));
        Assert.NotEqual(baseline, EvaluationCheckpoints.Fingerprint("corpus", "m1", "gemma-4-12b", "rev1", ["extract-v1"], "seed=8"));
    }

    [Fact]
    public void AStaleCheckpointIsNotReused()
    {
        MeetingScore score = SummaryScorer.Score(Meeting(new GoldSummary()), Summary(), Transcript());

        EvaluationJournal journal = new()
        {
            Entries = [new EvaluationCheckpoint
            {
                MeetingId = "m1",
                Backend = "gemma-4-12b",
                InputFingerprint = "old",
                Score = score,
            }],
        };

        Assert.NotNull(EvaluationCheckpoints.Reusable(journal, "m1", "gemma-4-12b", "old"));
        Assert.Null(EvaluationCheckpoints.Reusable(journal, "m1", "gemma-4-12b", "new"));
        Assert.Null(EvaluationCheckpoints.Reusable(journal, "m1", "ministral-3-14b", "old"));
    }

    // -- what the report says ------------------------------------------------------------------------------

    [Fact]
    public void ASyntheticReportSaysSoBeforeItSaysAnythingElse()
    {
        EvaluationReport report = new()
        {
            CorpusId = "synthetic-v1",
            CorpusKind = CorpusKind.Synthetic,
            GeneratedUtc = DateTimeOffset.UtcNow,
            Models = [Evaluation()],
            Acceptance = SummaryScorer.Judge(Evaluation(), CorpusKind.Synthetic),
        };

        string markdown = EvaluationMarkdown.Render(report);
        int banner = markdown.IndexOf("SYNTHETIC DATA", StringComparison.Ordinal);

        Assert.True(banner >= 0, "a synthetic report must say so");
        Assert.True(banner < markdown.IndexOf("## Quality", StringComparison.Ordinal), "it must say so before any metric");
    }

    [Fact]
    public void TheReportRefusesToInventAReadabilityScore()
    {
        string markdown = EvaluationMarkdown.Render(new EvaluationReport
        {
            CorpusId = "c",
            CorpusKind = CorpusKind.Development,
            GeneratedUtc = DateTimeOffset.UtcNow,
            Models = [Evaluation()],
        });

        Assert.Contains("Readability is deliberately absent", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TelemetryCarriesNoMeetingContent()
    {
        // Every field on the measurements record must be an identity, a count or a duration.
        // A string field that could hold a sentence is the one way transcript text reaches a
        // report file, and this is where that would be introduced without anyone noticing.
        string[] allowedText =
        [
            "Backend", "ModelId", "ModelRevision", "Quantization", "LlamaVersion", "PromptVersion",
            "KvCacheType", "RuntimeTier", "VramSource",
        ];

        foreach (System.Reflection.PropertyInfo property in typeof(RunMeasurements).GetProperties())
        {
            if (property.PropertyType == typeof(string))
            {
                Assert.Contains(property.Name, allowedText);
            }
            else if (property.PropertyType == typeof(IReadOnlyList<string>))
            {
                Assert.Equal("FallbackSteps", property.Name);
            }
        }
    }
}
