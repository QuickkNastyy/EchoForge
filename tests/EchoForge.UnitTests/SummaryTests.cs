using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.App;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Summaries;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Summaries;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.UnitTests;

/// <summary>
/// Summarisation: what may be shown, what must cite something, and what survives a failure.
///
/// <para>
/// The placeholder writes no prose worth testing. What is tested is the part that will still be
/// doing the work when a real model arrives — the validator, the chunker, and the storage that
/// decides when a summary becomes something a user acts on.
/// </para>
/// </summary>
public sealed class SummaryTests : IDisposable
{
    private const string SessionId = "01JSUMMARY";

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _sessions;
    private readonly FileTranscriptionStore _transcripts;
    private readonly FileSummaryStore _summaries;
    private readonly SwitchableGate _gate = new();
    private readonly List<IDisposable> _disposables = [];
    private DateTimeOffset _now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    public SummaryTests()
    {
        _sessions = new FileSessionStore(_temp.Path);
        _transcripts = new FileTranscriptionStore(_sessions);
        _summaries = new FileSummaryStore(_sessions);
        _sessions.Create(SessionId);
    }

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _temp.Dispose();
    }

    private DateTimeOffset Tick() => _now = _now.AddSeconds(1);

    // -- fixtures -----------------------------------------------------------------------------

    private static TranscriptSegment Segment(int index, string text, double start, string track = "microphone")
    {
        (string speakerId, string speakerName) = TranscriptSpeakers.For(track);
        return new TranscriptSegment
        {
            Id = $"segment-{index:D6}",
            Epoch = 1,
            StartSeconds = start,
            EndSeconds = start + 3,
            SpeakerId = speakerId,
            SpeakerName = speakerName,
            SourceTrack = track,
            Text = text,
            Confidence = null,
            Language = "en",
            Words = [],
        };
    }

    private static TranscriptDocument Transcript(params TranscriptSegment[] segments) => new()
    {
        SessionId = SessionId,
        TranscriptRevision = 1,
        CreatedAtUtc = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
        SourceManifestSha256 = new string('a', 64),
        DurationSeconds = 3600,
        Model = new TranscriptModel("echoforge-mock", "mock", "mock-v1", "mock-v1", "none", false, "0.1.0"),
        Epochs = [new TranscriptEpoch(1, 0, 3600)],
        Speakers =
        [
            new TranscriptSpeaker(TranscriptSpeakers.YouId, TranscriptSpeakers.YouName, TranscriptSpeakers.MicrophoneTrack),
            new TranscriptSpeaker(TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName, TranscriptSpeakers.SystemTrack),
        ],
        Languages = [new TranscriptLanguage(TranscriptSpeakers.MicrophoneTrack, "en", null)],
        Segments = segments,
    };

    private static SummaryDocument Summary(
        IEnumerable<SummaryItem>? decisions = null,
        IEnumerable<SummaryAction>? actions = null,
        string? meetingDate = null) => new()
    {
        SessionId = SessionId,
        SummaryRevision = 1,
        CreatedAtUtc = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
        TranscriptRevision = 1,
        TranscriptSha256 = new string('b', 64),
        MeetingDate = meetingDate,
        PromptVersion = "meeting-summary-v1",
        Model = new SummaryModel("echoforge-mock", "mock-summary", "mock-summary-v1", "mock-summary-v1", 0, false, false, "0.1.0"),
        Decisions = decisions is null ? [] : [.. decisions],
        ActionItems = actions is null ? [] : [.. actions],
    };

    private static SummaryItem Decision(string id, string text, string certainty, params SummaryEvidence[] evidence) => new()
    {
        Id = id,
        Text = text,
        Certainty = certainty,
        Confidence = null,
        Evidence = evidence,
    };

    private static SummaryAction Action(
        string id,
        string task,
        SummaryEvidence[] evidence,
        string? owner = null,
        string ownerStatus = SupportStatuses.Unknown,
        string? dueDate = null,
        string dueStatus = SupportStatuses.Unknown) => new()
    {
        Id = id,
        Task = task,
        Certainty = SupportStatuses.Explicit,
        Confidence = null,
        Evidence = evidence,
        Owner = owner,
        OwnerStatus = ownerStatus,
        DueDate = dueDate,
        DueDateText = dueDate is null ? null : "by then",
        DueDateStatus = dueStatus,
    };

    // -- evidence -------------------------------------------------------------------------------

    [Fact]
    public void AWellSupportedSummaryIsAccepted()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship on Friday", 10));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "segment-000001")!;

        SummaryVerdict verdict = SummaryValidator.Validate(
            Summary(decisions: [Decision("decision-001", "Ship on Friday", SupportStatuses.Explicit, citation)]),
            transcript);

        Assert.True(verdict.IsValid, string.Join("; ", verdict.Problems));
    }

    [Fact]
    public void ACitationToASegmentThatDoesNotExistIsRefused()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship on Friday", 10));

        SummaryEvidence invented = new()
        {
            TranscriptRevision = 1,
            SegmentId = "segment-999999",
            SourceTrack = "microphone",
            StartSeconds = 10,
            EndSeconds = 13,
            DisplayTimestamp = "00:00:10",
        };

        SummaryVerdict verdict = SummaryValidator.Validate(
            Summary(decisions: [Decision("decision-001", "Something nobody said", SupportStatuses.Explicit, invented)]),
            transcript);

        Assert.Contains(verdict.Problems, p => p.Contains("not in that transcript revision", StringComparison.Ordinal));
    }

    [Fact]
    public void ACitationIntoADifferentRevisionIsRefused()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship", 10));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "segment-000001")! with { TranscriptRevision = 2 };

        SummaryVerdict verdict = SummaryValidator.Validate(
            Summary(decisions: [Decision("decision-001", "Ship", SupportStatuses.Explicit, citation)]),
            transcript);

        Assert.Contains(verdict.Problems, p => p.Contains("was written from", StringComparison.Ordinal));
    }

    [Fact]
    public void TimestampsThatAreNotTheSegmentsOwnAreRefused()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship", 10));

        // A model that mis-states a timestamp would send the reader to the wrong audio and look
        // authoritative doing it.
        SummaryEvidence tampered = SummaryValidator.Cite(transcript, "segment-000001")! with { StartSeconds = 999 };

        SummaryVerdict verdict = SummaryValidator.Validate(
            Summary(decisions: [Decision("decision-001", "Ship", SupportStatuses.Explicit, tampered)]),
            transcript);

        Assert.Contains(verdict.Problems, p => p.Contains("not the segment's own", StringComparison.Ordinal));
    }

    [Fact]
    public void ADisplayTimestampThatWasNotDerivedIsRefused()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship", 3725));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "segment-000001")!;

        Assert.Equal("01:02:05", citation.DisplayTimestamp);

        SummaryVerdict verdict = SummaryValidator.Validate(
            Summary(decisions: [Decision("d1", "Ship", SupportStatuses.Explicit, citation with { DisplayTimestamp = "00:00:00" })]),
            transcript);

        Assert.Contains(verdict.Problems, p => p.Contains("not derived from it", StringComparison.Ordinal));
    }

    [Fact]
    public void ADecisionThatCitesNothingIsRefusedHoweverCertainItClaimsToBe()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship", 10));

        foreach (string certainty in (string[])[SupportStatuses.Explicit, SupportStatuses.Inferred, SupportStatuses.Unknown])
        {
            SummaryVerdict verdict = SummaryValidator.Validate(
                Summary(decisions: [Decision("decision-001", "Ship", certainty)]),
                transcript);

            Assert.Contains(verdict.Problems, p => p.Contains("cites nothing", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AnExplicitItemMustCiteSomething()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship", 10));

        SummaryDocument summary = Summary() with
        {
            KeyPoints = [Decision("point-001", "Something", SupportStatuses.Explicit)],
        };

        Assert.Contains(
            SummaryValidator.Validate(summary, transcript).Problems,
            p => p.Contains("explicit but cites nothing", StringComparison.Ordinal));
    }

    // -- owners and dates ---------------------------------------------------------------------------

    [Fact]
    public void AnUnknownOwnerMayNotHaveAName()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "Someone will write it up", 10));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "segment-000001")!;

        SummaryVerdict verdict = SummaryValidator.Validate(
            Summary(actions: [Action("action-001", "Write it up", [citation], owner: "Alex", ownerStatus: SupportStatuses.Unknown)]),
            transcript);

        Assert.Contains(verdict.Problems, p => p.Contains("unknown owner but names one", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExplicitOwnerMustActuallyBeNamed()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "Alex will write it up", 10));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "segment-000001")!;

        SummaryVerdict verdict = SummaryValidator.Validate(
            Summary(actions: [Action("action-001", "Write it up", [citation], owner: null, ownerStatus: SupportStatuses.Explicit)]),
            transcript);

        Assert.Contains(verdict.Problems, p => p.Contains("names nobody", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownDueDateMayNotHaveADate()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship soon", 10));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "segment-000001")!;

        SummaryVerdict verdict = SummaryValidator.Validate(
            Summary(actions: [Action("a1", "Ship", [citation], dueDate: "2026-08-07", dueStatus: SupportStatuses.Unknown)]),
            transcript);

        Assert.Contains(verdict.Problems, p => p.Contains("unknown due date but names one", StringComparison.Ordinal));
    }

    [Fact]
    public void ADateCannotBeResolvedWithoutAKnownMeetingDate()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "Ship by Friday", 10));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "segment-000001")!;

        // No meeting date on the summary: 'Friday' names no particular day.
        SummaryVerdict verdict = SummaryValidator.Validate(
            Summary(actions: [Action("a1", "Ship", [citation], dueDate: "2026-08-07", dueStatus: SupportStatuses.Explicit)]),
            transcript);

        Assert.Contains(verdict.Problems, p => p.Contains("meeting date is unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void ADateResolvedAgainstAKnownMeetingDateIsAccepted()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "Ship by Friday", 10));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "segment-000001")!;

        SummaryDocument summary = Summary(
            actions: [Action("a1", "Ship", [citation], dueDate: "2026-08-07", dueStatus: SupportStatuses.Explicit)],
            meetingDate: "2026-08-05");

        Assert.True(SummaryValidator.Validate(summary, transcript).IsValid);
    }

    [Fact]
    public void ADueDateThatIsNotAnIsoDateIsRefused()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "Ship by Friday", 10));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "segment-000001")!;

        SummaryDocument summary = Summary(
            actions: [Action("a1", "Ship", [citation], dueDate: "next Friday", dueStatus: SupportStatuses.Explicit)],
            meetingDate: "2026-08-05");

        Assert.Contains(
            SummaryValidator.Validate(summary, transcript).Problems,
            p => p.Contains("not an ISO calendar date", StringComparison.Ordinal));
    }

    // -- chunking ------------------------------------------------------------------------------------

    [Fact]
    public void ChunkingIsDeterministicForIdenticalInput()
    {
        TranscriptDocument transcript = Transcript(
            [.. Enumerable.Range(1, 200).Select(i => Segment(i, $"Sentence number {i} of the meeting", i * 4))]);

        SummaryOptions options = new() { ChunkCharacters = 600, OverlapSegments = 2 };

        IReadOnlyList<SummaryChunk> first = TranscriptChunker.Plan(transcript, options);
        IReadOnlyList<SummaryChunk> second = TranscriptChunker.Plan(transcript, options);

        Assert.NotEmpty(first);
        Assert.Equal(
            first.Select(c => (c.Index, c.FirstSegmentId, c.LastSegmentId, c.InputFingerprint)),
            second.Select(c => (c.Index, c.FirstSegmentId, c.LastSegmentId, c.InputFingerprint)));
    }

    [Fact]
    public void EverySegmentAppearsInAtLeastOneChunk()
    {
        TranscriptDocument transcript = Transcript(
            [.. Enumerable.Range(1, 500).Select(i => Segment(i, $"Point {i}", i * 4))]);

        IReadOnlyList<SummaryChunk> chunks = TranscriptChunker.Plan(
            transcript, new SummaryOptions { ChunkCharacters = 400, OverlapSegments = 2 });

        HashSet<string> covered = [];
        foreach (SummaryChunk chunk in chunks)
        {
            foreach (TranscriptSegment segment in TranscriptChunker.Resolve(transcript, chunk))
            {
                covered.Add(segment.Id);
            }
        }

        // Nothing is silently truncated away, however long the meeting.
        Assert.Equal(transcript.Segments.Count, covered.Count);
    }

    [Fact]
    public void AdjacentChunksShareSegmentsSoNothingIsCutInHalf()
    {
        TranscriptDocument transcript = Transcript(
            [.. Enumerable.Range(1, 60).Select(i => Segment(i, $"Point number {i} in the discussion", i * 4))]);

        IReadOnlyList<SummaryChunk> chunks = TranscriptChunker.Plan(
            transcript, new SummaryOptions { ChunkCharacters = 300, OverlapSegments = 3 });

        Assert.True(chunks.Count > 2);

        for (int i = 1; i < chunks.Count; i++)
        {
            IReadOnlyList<TranscriptSegment> previous = TranscriptChunker.Resolve(transcript, chunks[i - 1]);
            IReadOnlyList<TranscriptSegment> current = TranscriptChunker.Resolve(transcript, chunks[i]);

            Assert.True(
                previous.Select(s => s.Id).Intersect(current.Select(s => s.Id)).Any(),
                $"chunks {i - 1} and {i} share nothing");
        }
    }

    [Fact]
    public void AnOversizedSegmentBecomesItsOwnChunkRatherThanBeingDropped()
    {
        TranscriptDocument transcript = Transcript(
            Segment(1, new string('x', 5000), 0),
            Segment(2, "short", 10));

        IReadOnlyList<SummaryChunk> chunks = TranscriptChunker.Plan(
            transcript, new SummaryOptions { ChunkCharacters = 100, OverlapSegments = 0 });

        // Refusing to emit it would silently drop it from the summary.
        Assert.Contains(chunks, c => c.FirstSegmentId == "segment-000001");
        Assert.Contains(chunks, c => TranscriptChunker.Resolve(transcript, c).Any(s => s.Id == "segment-000002"));
    }

    [Fact]
    public void AChangedPromptVersionChangesEveryFingerprint()
    {
        TranscriptDocument transcript = Transcript(
            [.. Enumerable.Range(1, 30).Select(i => Segment(i, $"Point {i}", i * 4))]);

        IReadOnlyList<SummaryChunk> first = TranscriptChunker.Plan(transcript, new SummaryOptions());
        IReadOnlyList<SummaryChunk> second = TranscriptChunker.Plan(
            transcript, new SummaryOptions { PromptVersion = "meeting-summary-v3-test" });

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.NotEqual(first[i].InputFingerprint, second[i].InputFingerprint);
        }
    }

    [Fact]
    public void ADifferentTranscriptRevisionChangesEveryFingerprint()
    {
        TranscriptDocument first = Transcript([.. Enumerable.Range(1, 20).Select(i => Segment(i, $"Point {i}", i * 4))]);
        TranscriptDocument second = first with { TranscriptRevision = 2 };

        Assert.NotEqual(
            TranscriptChunker.Plan(first, new SummaryOptions())[0].InputFingerprint,
            TranscriptChunker.Plan(second, new SummaryOptions())[0].InputFingerprint);
    }

    [Fact]
    public void AnEmptyTranscriptProducesNoChunks()
    {
        Assert.Empty(TranscriptChunker.Plan(Transcript(), new SummaryOptions()));
    }

    // -- storage --------------------------------------------------------------------------------------

    private SummaryAttempt Stage(int revision, out string digest, SummaryDocument? document = null)
    {
        SummaryAttempt attempt = _summaries.BeginAttempt(
            SessionId, $"job-{revision}", 1, new string('b', 64), new SummaryOptions(), Tick());

        SummaryDocument summary = document ?? Summary() with { SummaryRevision = attempt.Revision };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(summary, SummaryDocument.Json);

        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
        File.WriteAllBytes(attempt.StagingPath, payload);

        digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        return attempt;
    }

    private SummaryRevisionRecord Activate()
    {
        SummaryAttempt attempt = Stage(0, out string digest);
        SummaryDocument summary = Summary() with { SummaryRevision = attempt.Revision };

        SummaryActivation activation = _summaries.Activate(attempt, digest, summary, Tick());
        Assert.True(activation.Activated, activation.Refusal);
        return activation.Revision!;
    }

    [Fact]
    public void TheFirstSuccessfulRunBecomesRevisionOneAndIsSelected()
    {
        SummaryRevisionRecord record = Activate();

        Assert.Equal(1, record.Revision);

        SummaryState state = _summaries.Read(SessionId);
        Assert.Equal(ProcessingStageState.Succeeded, state.Stage);
        Assert.Equal(1, state.SelectedRevision);
        Assert.False(state.Selected!.ProducesSummaries);
    }

    [Fact]
    public void ReprocessingAddsARevisionAndLeavesTheOlderOneUntouched()
    {
        Activate();
        byte[] first = File.ReadAllBytes(_summaries.PathFor(SessionId, 1));

        SummaryRevisionRecord second = Activate();

        Assert.Equal(2, second.Revision);
        Assert.Equal(2, _summaries.Read(SessionId).SelectedRevision);
        Assert.Equal(first, File.ReadAllBytes(_summaries.PathFor(SessionId, 1)));
    }

    [Fact]
    public void AnOlderRevisionCanBeSelected()
    {
        Activate();
        Activate();

        Assert.True(_summaries.SelectRevision(SessionId, 1, Tick()));
        Assert.Equal(1, _summaries.Read(SessionId).SelectedRevision);
    }

    [Fact]
    public void AStagedSummaryWhoseDigestDoesNotMatchIsRefused()
    {
        SummaryRevisionRecord good = Activate();

        SummaryAttempt attempt = Stage(0, out _);
        SummaryActivation outcome = _summaries.Activate(
            attempt, new string('c', 64), Summary() with { SummaryRevision = attempt.Revision }, Tick());

        Assert.False(outcome.Activated);
        Assert.Equal(good.Revision, _summaries.Read(SessionId).SelectedRevision);
        Assert.False(File.Exists(_summaries.PathFor(SessionId, attempt.Revision)));
    }

    [Fact]
    public void AFailedAttemptLeavesThePreviousRevisionSelected()
    {
        SummaryRevisionRecord good = Activate();

        SummaryAttempt attempt = Stage(0, out _);
        _summaries.MarkFailed(attempt, "worker_crashed", "It failed.", Tick());

        SummaryState state = _summaries.Read(SessionId);

        Assert.Equal(ProcessingStageState.Failed, state.Stage);
        Assert.Equal(good.Revision, state.SelectedRevision);
        Assert.False(File.Exists(attempt.StagingPath));
    }

    [Fact]
    public void ACancelledAttemptLeavesThePreviousRevisionSelected()
    {
        SummaryRevisionRecord good = Activate();

        SummaryAttempt attempt = Stage(0, out _);
        _summaries.MarkCancelled(attempt, Tick());

        SummaryState state = _summaries.Read(SessionId);

        Assert.Equal(ProcessingStageState.Cancelled, state.Stage);
        Assert.Equal(good.Revision, state.SelectedRevision);
    }

    [Fact]
    public void AStagedSummaryFromACrashedAttemptIsDiscarded()
    {
        SummaryRevisionRecord good = Activate();
        SummaryAttempt attempt = Stage(0, out _);

        Assert.True(File.Exists(attempt.StagingPath));
        Assert.Equal(1, _summaries.DiscardOrphanStaging(SessionId));

        Assert.False(File.Exists(attempt.StagingPath));
        Assert.Equal(good.Revision, _summaries.Read(SessionId).SelectedRevision);
    }

    [Fact]
    public void ARestartReconstructsEveryRevisionFromTheJournal()
    {
        Activate();
        Activate();
        Assert.True(_summaries.SelectRevision(SessionId, 1, Tick()));

        FileSummaryStore reopened = new(new FileSessionStore(_temp.Path));
        SummaryState state = reopened.Read(SessionId);

        Assert.Equal(2, state.Revisions.Count);
        Assert.Equal(1, state.SelectedRevision);
    }

    [Fact]
    public void NoJournalEventCarriesSummaryProse()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship on Friday", 10));
        SummaryEvidence citation = SummaryValidator.Cite(transcript, "segment-000001")!;

        SummaryAttempt attempt = _summaries.BeginAttempt(
            SessionId, "job-prose", 1, new string('b', 64), new SummaryOptions(), Tick());

        SummaryDocument summary = Summary(
            decisions: [Decision("decision-001", "Ship the release on Friday afternoon", SupportStatuses.Explicit, citation)])
            with { SummaryRevision = attempt.Revision, Overview = "A long overview nobody should find in a log." };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(summary, SummaryDocument.Json);
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
        File.WriteAllBytes(attempt.StagingPath, payload);

        _summaries.Activate(attempt, Convert.ToHexStringLower(SHA256.HashData(payload)), summary, Tick());

        foreach (JournalEvent entry in _sessions.ReadJournal(SessionId).Events)
        {
            foreach (string value in entry.Fields.Values)
            {
                Assert.DoesNotContain("Ship the release", value, StringComparison.Ordinal);
                Assert.DoesNotContain("nobody should find", value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ASummaryIsStaleWhenTheSelectedTranscriptMovesOn()
    {
        SummaryRevisionRecord record = Activate();

        Assert.False(record.IsStaleAgainst(1));
        Assert.True(record.IsStaleAgainst(2));

        // Stale is visible, never silent, and the summary stays readable against its own revision.
        Assert.Equal(1, record.TranscriptRevision);
    }

    // -- the coordinator ------------------------------------------------------------------------------

    private SummaryCoordinator Coordinator(Func<bool>? otherJobRunning = null)
    {
        SummaryCoordinator coordinator = new(
            _sessions,
            _summaries,
            _transcripts,
            new WorkerSupervisor(WorkerTestEnvironment.Options()),
            _gate,
            otherJobRunning);

        _disposables.Add(coordinator);
        return coordinator;
    }

    /// <summary>
    /// Puts a real activated transcript revision in place, so a summarisation run has something
    /// to be judged against rather than a fixture that only looks like one.
    /// </summary>
    private void PlantTranscript(params TranscriptSegment[] segments)
    {
        TranscriptionAttempt attempt = _transcripts.BeginAttempt(
            SessionId, "job-transcript", new string('a', 64), new TranscriptionOptions(), Tick());

        TranscriptDocument transcript = Transcript(segments) with { TranscriptRevision = attempt.Revision };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(transcript, TranscriptDocument.Json);

        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
        File.WriteAllBytes(attempt.StagingPath, payload);

        ActivationOutcome outcome = _transcripts.Activate(
            new ActivationRequest(attempt, Convert.ToHexStringLower(SHA256.HashData(payload)), transcript, 1, null),
            Tick());

        Assert.True(outcome.Activated, outcome.Refusal);
    }

    /// <summary>A meeting the placeholder can find decisions and actions in.</summary>
    private static TranscriptSegment[] MeetingSegments() =>
    [
        Segment(1, "Good morning everyone", 0),
        Segment(2, "We will ship on Friday", 10),
        Segment(3, "Alex will prepare the deck", 20),
        Segment(4, "I am worried about the vendor risk", 30),
    ];

    private static async Task<(SummaryRunResult Result, IReadOnlyList<string> Stages)> Summarise(
        SummaryCoordinator coordinator,
        string? testMode = null)
    {
        List<string> stages = [];
        coordinator.ProgressChanged += (_, e) => stages.Add(e.Stage);

        SummaryRunResult result = await coordinator.SummarizeAsync(SessionId, new SummaryOptions { TestMode = testMode });
        return (result, stages);
    }

    [Fact]
    public async Task ASessionWithNoTranscriptIsRefused()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);

        SummaryRunResult result = await Coordinator().SummarizeAsync(SessionId);

        Assert.Equal("no_transcript", result.FailureCode);
        Assert.Contains("not been transcribed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummarisingIsRefusedWhileCaptureIsLive()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        _gate.IsCaptureActive = true;

        SummaryRunResult result = await Coordinator().SummarizeAsync(SessionId);

        Assert.Equal("recording_active", result.FailureCode);
    }

    [Fact]
    public async Task SummarisingIsRefusedWhileAnotherHeavyJobRuns()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);

        SummaryRunResult result = await Coordinator(otherJobRunning: () => true).SummarizeAsync(SessionId);

        Assert.Equal("busy", result.FailureCode);
        Assert.Contains("Only one runs at a time", result.Message, StringComparison.Ordinal);
    }

    // -- the one bounded repair -------------------------------------------------------------------------

    [Fact]
    public async Task AGoodSummaryIsActivatedWithoutAnyRepair()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(MeetingSegments());

        (SummaryRunResult result, IReadOnlyList<string> stages) = await Summarise(Coordinator());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Revision);
        Assert.DoesNotContain(SummaryCoordinator.RepairingStage, stages);

        SummaryDocument summary = _summaries.ReadSummary(SessionId, 1)!;
        Assert.Equal(0, summary.RepairAttempt);
    }

    [Fact]
    public async Task AnUnsupportedSummaryIsGeneratedOnceMoreAndTheSecondAnswerIsActivated()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(MeetingSegments());

        // The first generation cites a segment the transcript does not contain; the re-ask does not.
        (SummaryRunResult result, IReadOnlyList<string> stages) = await Summarise(Coordinator(), "malformed_summary_once");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Revision);
        Assert.Contains("refused", result.Message, StringComparison.Ordinal);
        Assert.Single(stages, stage => stage == SummaryCoordinator.RepairingStage);

        SummaryRevisionRecord record = _summaries.Read(SessionId).Selected!;
        Assert.Equal(1, record.RepairAttempt);
        Assert.True(record.WasRepaired);
    }

    [Fact]
    public async Task ATruncatedSummaryIsAlsoWorthOneReAsk()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(MeetingSegments());

        (SummaryRunResult result, _) = await Summarise(Coordinator(), "truncated_summary_once");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, _summaries.ReadSummary(SessionId, 1)!.RepairAttempt);
    }

    [Fact]
    public async Task ARepairThatIsStillUnsupportedFailsRatherThanBeingAccepted()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(MeetingSegments());

        // Malformed every time, so the re-ask cannot succeed.
        (SummaryRunResult result, IReadOnlyList<string> stages) = await Summarise(Coordinator(), "malformed_summary");

        Assert.False(result.Succeeded);
        Assert.Equal("summary_invalid_after_repair", result.FailureCode);

        // Exactly one re-ask. Retrying until something passes is how a generator eventually
        // stumbles onto output that satisfies the checks without satisfying the transcript.
        Assert.Single(stages, stage => stage == SummaryCoordinator.RepairingStage);

        // Nothing was activated, and nothing was left behind.
        Assert.Empty(_summaries.Read(SessionId).Revisions);
        Assert.False(File.Exists(_summaries.PathFor(SessionId, 1)));
    }

    [Fact]
    public async Task ASummaryThatStaysUnreadableFailsAfterItsOneReAsk()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(MeetingSegments());

        (SummaryRunResult result, IReadOnlyList<string> stages) = await Summarise(Coordinator(), "truncated_summary");

        Assert.False(result.Succeeded);
        Assert.Equal("summary_unreadable_after_repair", result.FailureCode);
        Assert.Single(stages, stage => stage == SummaryCoordinator.RepairingStage);
        Assert.Empty(_summaries.Read(SessionId).Revisions);
    }

    [Fact]
    public async Task AFailedRepairLeavesTheEarlierSummaryExactlyWhereItWas()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(MeetingSegments());

        SummaryCoordinator coordinator = Coordinator();
        Assert.True((await Summarise(coordinator)).Result.Succeeded);
        byte[] first = File.ReadAllBytes(_summaries.PathFor(SessionId, 1));

        (SummaryRunResult result, _) = await Summarise(coordinator, "malformed_summary");

        Assert.False(result.Succeeded);
        Assert.Contains("untouched", result.Message, StringComparison.Ordinal);

        // The failure cost nothing: same selection, same bytes.
        SummaryState state = _summaries.Read(SessionId);
        Assert.Equal(1, state.SelectedRevision);
        Assert.Single(state.Revisions);
        Assert.Equal(first, File.ReadAllBytes(_summaries.PathFor(SessionId, 1)));
    }

    [Fact]
    public void ARepairIsNotAllowedToLoosenAnything()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship on Friday", 10));

        SummaryDocument repaired = Summary(decisions:
        [
            Decision("decision-001", "ship on Friday", SupportStatuses.Explicit, new SummaryEvidence
            {
                TranscriptRevision = 1,
                SegmentId = "segment-999999",
                SourceTrack = "microphone",
                StartSeconds = 10,
                EndSeconds = 13,
                DisplayTimestamp = "00:00:10",
            }),
        ]) with { RepairAttempt = 1 };

        // The same citation that was refused on the first attempt is refused on the second.
        Assert.False(SummaryValidator.Validate(repaired, transcript).IsValid);
    }

    [Fact]
    public void ASummaryClaimingMoreRepairsThanAreAllowedIsRefused()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship on Friday", 10));

        SummaryVerdict verdict = SummaryValidator.Validate(Summary() with { RepairAttempt = 2 }, transcript);

        Assert.False(verdict.IsValid);
        Assert.Contains(verdict.Problems, problem => problem.Contains("repair attempt 2", StringComparison.Ordinal));
    }

    // -- recursive synthesis -----------------------------------------------------------------------------

    [Fact]
    public async Task AGeneratedSummaryRecordsHowItsFactsWereFolded()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(MeetingSegments());

        Assert.True((await Summarise(Coordinator())).Result.Succeeded);

        SummarySynthesis fold = _summaries.ReadSummary(SessionId, 1)!.Synthesis!;

        Assert.True(fold.Levels >= 1);
        Assert.True(fold.Groups >= fold.Levels);
        Assert.False(fold.ReachedLevelCap);
    }

    /// <summary>Sixty distinct marked decisions, in session order.</summary>
    private static TranscriptSegment[] LongMeeting() =>
    [
        .. Enumerable.Range(1, 60).Select(i => Segment(
            i,
            $"We will ship item {i.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            i * 5))
    ];

    [Fact]
    public async Task ALongMeetingIsFoldedOverMoreThanOnePassAndKeepsEveryDecision()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(LongMeeting());

        // Small chunks with overlap, so most decisions are extracted twice and there is real
        // merging to do; and a fold too narrow to consider them all at once, which is the case
        // the recursion exists for.
        SummaryRunResult result = await Coordinator().SummarizeAsync(
            SessionId,
            new SummaryOptions { ChunkCharacters = 120, OverlapSegments = 1, SynthesisGroupSize = 4 });

        Assert.True(result.Succeeded, result.Message);

        SummaryDocument summary = _summaries.ReadSummary(SessionId, 1)!;

        Assert.True(summary.Synthesis!.Levels > 1, $"folded in {summary.Synthesis.Levels} pass(es)");
        Assert.True(summary.Synthesis.MergedItems > 0);

        // Every decision survives the fold exactly once: none lost to make the result fit, and
        // none duplicated by having been seen in two chunks.
        Assert.Equal(60, summary.Decisions.Count);
        Assert.Equal(60, summary.Decisions.Select(d => d.Text).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AFoldWithNothingLeftToMergeStopsRatherThanDiscarding()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(LongMeeting());

        // Sixty decisions that are not duplicates of each other, and a fold four wide. There is
        // no merge available, so no number of passes would make them fit — and the answer to
        // that is to return all sixty, never to keep the first few.
        SummaryRunResult result = await Coordinator().SummarizeAsync(
            SessionId, new SummaryOptions { SynthesisGroupSize = 4 });

        Assert.True(result.Succeeded, result.Message);

        SummaryDocument summary = _summaries.ReadSummary(SessionId, 1)!;

        Assert.Equal(1, summary.Synthesis!.Levels);
        Assert.Equal(0, summary.Synthesis.MergedItems);
        Assert.Equal(60, summary.Decisions.Count);
    }

    [Fact]
    public void ASynthesisThatCouldNotHaveHappenedIsRefused()
    {
        TranscriptDocument transcript = Transcript(Segment(1, "We will ship on Friday", 10));

        // Every pass folds at least one group, so fewer groups than passes did not happen.
        SummaryVerdict verdict = SummaryValidator.Validate(
            Summary() with { Synthesis = new SummarySynthesis(Levels: 3, Groups: 1, MergedItems: 0, ReachedLevelCap: false) },
            transcript);

        Assert.False(verdict.IsValid);

        Assert.False(SummaryValidator.Validate(
            Summary() with { Synthesis = new SummarySynthesis(0, 0, 0, false) }, transcript).IsValid);
    }

    [Fact]
    public async Task TheJournalRemembersTheFoldAndTheRepairAcrossARestart()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(MeetingSegments());

        Assert.True((await Summarise(Coordinator(), "malformed_summary_once")).Result.Succeeded);

        // A fresh store, reading nothing but the journal and the files.
        SummaryRevisionRecord record = new FileSummaryStore(new FileSessionStore(_temp.Path))
            .Read(SessionId).Selected!;

        Assert.Equal(1, record.RepairAttempt);
        Assert.True(record.SynthesisLevels >= 1);
    }

    // -- the surface -----------------------------------------------------------------------------------

    [Fact]
    public void ThePanelWarnsAboutThePlaceholderBeforeAnythingHasRun()
    {
        SummaryViewModel viewModel = new(Coordinator());
        _disposables.Add(viewModel);

        viewModel.UpdateHost(SessionId, transcriptRevision: 1, recordingActive: false, hostReady: true, shuttingDown: false);

        Assert.True(viewModel.IsPlaceholderBackend);
        Assert.Contains("not a summary", viewModel.PlaceholderWarning, StringComparison.Ordinal);
        Assert.True(viewModel.CanGenerate);
    }

    [Fact]
    public void GeneratingIsUnavailableWithoutATranscriptOrWhileBusy()
    {
        SummaryViewModel viewModel = new(Coordinator());
        _disposables.Add(viewModel);

        viewModel.UpdateHost(SessionId, transcriptRevision: null, recordingActive: false, hostReady: true, shuttingDown: false);
        Assert.False(viewModel.CanGenerate);
        Assert.Equal("No transcript yet", viewModel.StageText);

        viewModel.UpdateHost(SessionId, 1, recordingActive: true, hostReady: true, shuttingDown: false);
        Assert.False(viewModel.CanGenerate);

        viewModel.UpdateHost(SessionId, 1, recordingActive: false, hostReady: true, shuttingDown: true);
        Assert.False(viewModel.CanGenerate);
        Assert.False(viewModel.CanCancel);
    }

    [Fact]
    public async Task GeneratingDoesNotBlockTheCallingThread()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);

        SummaryViewModel viewModel = new(Coordinator());
        _disposables.Add(viewModel);
        viewModel.UpdateHost(SessionId, 1, false, true, false);

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        viewModel.GenerateCommand.Execute(null);
        clock.Stop();

        // On a real window this is the thread painting the recording indicator.
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(250), $"Execute blocked for {clock.Elapsed}");

        await Task.Delay(200);
    }

    [Fact]
    public async Task TheProgressLineNamesTheRepairRatherThanLookingLikeAStall()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(MeetingSegments());

        SummaryCoordinator coordinator = Coordinator();
        List<string> descriptions = [];
        coordinator.ProgressChanged += (_, _) => { };

        SummaryViewModel viewModel = new(coordinator);
        _disposables.Add(viewModel);
        viewModel.UpdateHost(SessionId, 1, false, true, false);

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SummaryViewModel.ProgressDescription))
            {
                descriptions.Add(viewModel.ProgressDescription);
            }
        };

        await coordinator.SummarizeAsync(SessionId, new SummaryOptions { TestMode = "malformed_summary_once" });

        // A user watching the same job appear to start over is owed the reason.
        Assert.Contains(descriptions, text => text.Contains("once more", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARepairedRevisionSaysSoInTheVersionList()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        PlantTranscript(MeetingSegments());

        SummaryCoordinator coordinator = Coordinator();
        Assert.True((await Summarise(coordinator, "malformed_summary_once")).Result.Succeeded);

        SummaryViewModel viewModel = new(coordinator);
        _disposables.Add(viewModel);
        viewModel.UpdateHost(SessionId, 1, false, true, false);

        SummaryRevisionOption option = Assert.Single(viewModel.Revisions);

        Assert.True(option.WasRepaired);
        Assert.Contains("refusal", option.Label, StringComparison.Ordinal);
    }

    private sealed class SwitchableGate : ICaptureActivityGate
    {
        public bool IsCaptureActive { get; set; }
    }
}
