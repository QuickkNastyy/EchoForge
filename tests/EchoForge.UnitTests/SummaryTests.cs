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
            transcript, new SummaryOptions { PromptVersion = "meeting-summary-v2" });

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

    private sealed class SwitchableGate : ICaptureActivityGate
    {
        public bool IsCaptureActive { get; set; }
    }
}
