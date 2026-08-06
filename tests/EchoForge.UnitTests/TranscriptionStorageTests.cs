using System.Text.Json;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Transcripts;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// Revision storage: what becomes canonical, what never does, and what survives a crash.
///
/// <para>
/// These are deliberately store-level rather than end-to-end. The interesting cases — a staged
/// file whose digest does not match, an activation that dies between the rename and the journal —
/// are ones a working worker will never produce, so they have to be constructed.
/// </para>
/// </summary>
public sealed class TranscriptionStorageTests : IDisposable
{
    private const string SessionId = "01JSTORAGE";

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _sessions;
    private readonly FileTranscriptionStore _transcripts;
    private DateTimeOffset _now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    public TranscriptionStorageTests()
    {
        _sessions = new FileSessionStore(_temp.Path);
        _transcripts = new FileTranscriptionStore(_sessions);
        _sessions.Create(SessionId);
    }

    public void Dispose() => _temp.Dispose();

    private DateTimeOffset Tick() => _now = _now.AddSeconds(1);

    private static TranscriptionOptions Options => new() { Backend = "mock" };

    /// <summary>Stages a transcript exactly as a worker would, and returns its digest.</summary>
    private static (TranscriptDocument Document, string Sha256) Stage(TranscriptionAttempt attempt, int segments = 1)
    {
        TranscriptDocument document = BuildTranscript(attempt.Revision, segments);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, TranscriptDocument.Json);

        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
        File.WriteAllBytes(attempt.StagingPath, payload);

        return (document, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)));
    }

    private static TranscriptDocument BuildTranscript(int revision, int segments)
    {
        List<TranscriptSegment> list = [];
        for (int i = 1; i <= segments; i++)
        {
            list.Add(new TranscriptSegment
            {
                Id = $"segment-{i:D6}",
                Epoch = 1,
                StartSeconds = i - 1,
                EndSeconds = i,
                SpeakerId = TranscriptSpeakers.YouId,
                SpeakerName = TranscriptSpeakers.YouName,
                SourceTrack = TranscriptSpeakers.MicrophoneTrack,
                Text = $"line {i}",
                Confidence = null,
                Language = "und",
                Words = [],
            });
        }

        return new TranscriptDocument
        {
            SessionId = SessionId,
            TranscriptRevision = revision,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
            SourceManifestSha256 = new string('a', 64),
            DurationSeconds = 60,
            Model = new TranscriptModel("echoforge-mock", "mock", "mock-v1", "mock-v1", "none", false, "0.1.0"),
            Epochs = [new TranscriptEpoch(1, 0, 60)],
            Speakers = [new TranscriptSpeaker(TranscriptSpeakers.YouId, TranscriptSpeakers.YouName, TranscriptSpeakers.MicrophoneTrack)],
            Languages = [new TranscriptLanguage(TranscriptSpeakers.MicrophoneTrack, "und", null)],
            Segments = list,
        };
    }

    private TranscriptRevisionRecord ActivateOne(int segments = 1)
    {
        TranscriptionAttempt attempt = _transcripts.BeginAttempt(SessionId, Guid.NewGuid().ToString("n"), new string('a', 64), Options, Tick());
        _transcripts.MarkStarted(attempt, Options, Tick());
        (TranscriptDocument document, string sha) = Stage(attempt, segments);

        ActivationOutcome outcome = _transcripts.Activate(
            new ActivationRequest(attempt, sha, document, 1, null), Tick());

        Assert.True(outcome.Activated, outcome.Refusal);
        return outcome.Revision!;
    }

    // -- the happy path -------------------------------------------------------------------

    [Fact]
    public void TheFirstSuccessfulRunBecomesRevisionOneAndIsSelected()
    {
        TranscriptRevisionRecord record = ActivateOne();

        Assert.Equal(1, record.Revision);

        TranscriptionState state = _transcripts.Read(SessionId);
        Assert.Equal(ProcessingStageState.Succeeded, state.Stage);
        Assert.Equal(1, state.SelectedRevision);
        Assert.Single(state.Revisions);
        Assert.True(File.Exists(_transcripts.RevisionPath(SessionId, 1)));
    }

    [Fact]
    public void ReprocessingAddsARevisionAndLeavesTheOlderOneWhereItWas()
    {
        TranscriptRevisionRecord first = ActivateOne(segments: 1);
        byte[] firstBytes = File.ReadAllBytes(_transcripts.RevisionPath(SessionId, first.Revision));

        TranscriptRevisionRecord second = ActivateOne(segments: 3);

        Assert.Equal(2, second.Revision);

        TranscriptionState state = _transcripts.Read(SessionId);
        Assert.Equal(2, state.SelectedRevision);
        Assert.Equal(2, state.Revisions.Count);

        // Revision 1 is byte-identical to what it was. A summary written from it still opens
        // exactly the text it was written from.
        Assert.Equal(firstBytes, File.ReadAllBytes(_transcripts.RevisionPath(SessionId, 1)));
    }

    [Fact]
    public void AnOlderRevisionCanBeSelectedAndTheChoiceSurvivesReading()
    {
        ActivateOne();
        ActivateOne();

        Assert.True(_transcripts.SelectRevision(SessionId, 1, Tick()));
        Assert.Equal(1, _transcripts.Read(SessionId).SelectedRevision);

        // And it can be moved back.
        Assert.True(_transcripts.SelectRevision(SessionId, 2, Tick()));
        Assert.Equal(2, _transcripts.Read(SessionId).SelectedRevision);
    }

    [Fact]
    public void ARevisionThatDoesNotExistCannotBeSelected()
    {
        ActivateOne();

        Assert.False(_transcripts.SelectRevision(SessionId, 7, Tick()));
        Assert.Equal(1, _transcripts.Read(SessionId).SelectedRevision);
    }

    [Fact]
    public void ActivationMovesTheStagedFileRatherThanCopyingIt()
    {
        TranscriptionAttempt attempt = _transcripts.BeginAttempt(SessionId, "job-1", new string('a', 64), Options, Tick());
        (TranscriptDocument document, string sha) = Stage(attempt);

        Assert.True(File.Exists(attempt.StagingPath));
        _transcripts.Activate(new ActivationRequest(attempt, sha, document, 1, null), Tick());

        // Nothing is left behind that could later be mistaken for a revision.
        Assert.False(File.Exists(attempt.StagingPath));
        Assert.True(File.Exists(_transcripts.RevisionPath(SessionId, attempt.Revision)));
    }

    // -- what must never become canonical ---------------------------------------------------

    [Fact]
    public void AStagedTranscriptWhoseDigestDoesNotMatchIsRefused()
    {
        TranscriptRevisionRecord good = ActivateOne();

        TranscriptionAttempt attempt = _transcripts.BeginAttempt(SessionId, "job-bad", new string('a', 64), Options, Tick());
        (TranscriptDocument document, _) = Stage(attempt);

        ActivationOutcome outcome = _transcripts.Activate(
            new ActivationRequest(attempt, new string('b', 64), document, 1, null), Tick());

        Assert.False(outcome.Activated);
        Assert.Contains("digest", outcome.Refusal!, StringComparison.Ordinal);

        // The good revision is still the selected one, and no second revision appeared.
        TranscriptionState state = _transcripts.Read(SessionId);
        Assert.Equal(good.Revision, state.SelectedRevision);
        Assert.Single(state.Revisions);
        Assert.False(File.Exists(_transcripts.RevisionPath(SessionId, attempt.Revision)));
    }

    [Fact]
    public void AnEmptyStagedTranscriptIsRefused()
    {
        TranscriptionAttempt attempt = _transcripts.BeginAttempt(SessionId, "job-empty", new string('a', 64), Options, Tick());
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
        File.WriteAllBytes(attempt.StagingPath, []);

        string emptyDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([]));
        ActivationOutcome outcome = _transcripts.Activate(
            new ActivationRequest(attempt, emptyDigest, BuildTranscript(1, 0), 1, null), Tick());

        Assert.False(outcome.Activated);
    }

    [Fact]
    public void AMissingStagedTranscriptIsRefused()
    {
        TranscriptionAttempt attempt = _transcripts.BeginAttempt(SessionId, "job-gone", new string('a', 64), Options, Tick());

        ActivationOutcome outcome = _transcripts.Activate(
            new ActivationRequest(attempt, new string('a', 64), BuildTranscript(1, 1), 1, null), Tick());

        Assert.False(outcome.Activated);
        Assert.Contains("not there", outcome.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void ARevisionIsNeverOverwritten()
    {
        TranscriptRevisionRecord first = ActivateOne();

        // Force a second attempt onto the same revision number, which allocation would never do.
        TranscriptionAttempt collision = new(
            SessionId,
            "job-collide",
            first.Revision,
            _sessions.Resolve(SessionId).TranscriptStagingPath(first.Revision),
            new string('a', 64));

        (TranscriptDocument document, string sha) = Stage(collision);
        ActivationOutcome outcome = _transcripts.Activate(
            new ActivationRequest(collision, sha, document, 1, null), Tick());

        Assert.False(outcome.Activated);
        Assert.Contains("already exists", outcome.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionNumbersAreNeverReusedEvenAfterAFailedAttempt()
    {
        ActivateOne();

        TranscriptionAttempt failed = _transcripts.BeginAttempt(SessionId, "job-fail", new string('a', 64), Options, Tick());
        Assert.Equal(2, failed.Revision);
        _transcripts.MarkFailed(failed, "worker_crashed", "It failed.", Tick());

        TranscriptionAttempt next = _transcripts.BeginAttempt(SessionId, "job-next", new string('a', 64), Options, Tick());
        Assert.Equal(3, next.Revision);
    }

    // -- failure and cancellation -------------------------------------------------------------

    [Fact]
    public void AFailedAttemptLeavesThePreviousRevisionSelected()
    {
        TranscriptRevisionRecord good = ActivateOne();

        TranscriptionAttempt attempt = _transcripts.BeginAttempt(SessionId, "job-fail", new string('a', 64), Options, Tick());
        _transcripts.MarkStarted(attempt, Options, Tick());
        Stage(attempt);
        _transcripts.MarkFailed(attempt, "worker_crashed", "The worker stopped unexpectedly.", Tick());

        TranscriptionState state = _transcripts.Read(SessionId);

        Assert.Equal(ProcessingStageState.Failed, state.Stage);
        Assert.Equal(good.Revision, state.SelectedRevision);
        Assert.Equal("worker_crashed", state.CurrentJob!.FailureCode);

        // The staged bytes are gone, so nothing survives that could later look like a revision.
        Assert.False(File.Exists(attempt.StagingPath));
    }

    [Fact]
    public void ACancelledAttemptLeavesThePreviousRevisionSelected()
    {
        TranscriptRevisionRecord good = ActivateOne();

        TranscriptionAttempt attempt = _transcripts.BeginAttempt(SessionId, "job-cancel", new string('a', 64), Options, Tick());
        _transcripts.MarkStarted(attempt, Options, Tick());
        Stage(attempt);
        _transcripts.MarkCancelled(attempt, Tick());

        TranscriptionState state = _transcripts.Read(SessionId);

        Assert.Equal(ProcessingStageState.Cancelled, state.Stage);
        Assert.Equal(good.Revision, state.SelectedRevision);
        Assert.False(File.Exists(attempt.StagingPath));
    }

    // -- crash recovery -------------------------------------------------------------------------

    [Fact]
    public void AStagedTranscriptFromACrashedAttemptIsDiscardedAndTheOldRevisionStaysActive()
    {
        TranscriptRevisionRecord good = ActivateOne();

        // A process that died between staging and activation leaves exactly this.
        TranscriptionAttempt attempt = _transcripts.BeginAttempt(SessionId, "job-crashed", new string('a', 64), Options, Tick());
        _transcripts.MarkStarted(attempt, Options, Tick());
        Stage(attempt);
        Assert.True(File.Exists(attempt.StagingPath));

        int discarded = _transcripts.DiscardOrphanStaging(SessionId, Tick());

        Assert.Equal(1, discarded);
        Assert.False(File.Exists(attempt.StagingPath));

        TranscriptionState state = _transcripts.Read(SessionId);
        Assert.Equal(good.Revision, state.SelectedRevision);
        Assert.Single(state.Revisions);
    }

    [Fact]
    public void AnAttemptStillMarkedRunningAfterARestartIsReportedAsInterrupted()
    {
        ActivateOne();
        TranscriptionAttempt attempt = _transcripts.BeginAttempt(SessionId, "job-interrupted", new string('a', 64), Options, Tick());
        _transcripts.MarkStarted(attempt, Options, Tick());

        // Everything the crashed process held is gone; only the journal remains.
        FileTranscriptionStore afterRestart = new(new FileSessionStore(_temp.Path));
        TranscriptionState state = afterRestart.Read(SessionId);

        Assert.Equal(ProcessingStageState.Failed, state.Stage);
        Assert.Equal("interrupted", state.CurrentJob!.FailureCode);
        Assert.Equal(1, state.SelectedRevision);
    }

    [Fact]
    public void ARestartReconstructsEveryRevisionAndTheSelectedOneFromTheJournal()
    {
        ActivateOne();
        ActivateOne();
        Assert.True(_transcripts.SelectRevision(SessionId, 1, Tick()));

        // A new store, with no memory of anything, reading the same folder.
        FileTranscriptionStore reopened = new(new FileSessionStore(_temp.Path));
        TranscriptionState state = reopened.Read(SessionId);

        Assert.Equal(2, state.Revisions.Count);
        Assert.Equal(1, state.SelectedRevision);
        Assert.All(state.Revisions, r => Assert.True(r.FileExists));
    }

    [Fact]
    public void TheProjectionIsRebuildableAndNeverTheAuthority()
    {
        ActivateOne();
        ActivateOne();

        SessionPaths paths = _sessions.Resolve(SessionId);
        Assert.True(File.Exists(paths.ProcessingPath));

        // Throwing away the projection loses nothing: the journal carries all of it.
        File.Delete(paths.ProcessingPath);
        TranscriptionState state = _transcripts.Read(SessionId);

        Assert.Equal(2, state.Revisions.Count);
        Assert.Equal(2, state.SelectedRevision);
    }

    [Fact]
    public void AProjectionThatDisagreesWithTheJournalDoesNotWin()
    {
        ActivateOne();

        // Someone, or something, claims a revision the journal never activated.
        SessionPaths paths = _sessions.Resolve(SessionId);
        File.WriteAllText(paths.ProcessingPath, """
            { "schema_version": 1, "state": "succeeded", "selected_revision": 99, "revisions": [] }
            """);

        TranscriptionState state = _transcripts.Read(SessionId);

        Assert.Equal(1, state.SelectedRevision);
        Assert.Single(state.Revisions);
    }

    [Fact]
    public void ARevisionWhoseFileHasGoneIsNotSelectable()
    {
        ActivateOne();
        ActivateOne();

        File.Delete(_transcripts.RevisionPath(SessionId, 2));

        TranscriptionState state = _transcripts.Read(SessionId);

        // Still listed, because it genuinely happened, but no longer chosen.
        Assert.Equal(2, state.Revisions.Count);
        Assert.False(state.Revisions[1].FileExists);
        Assert.Equal(1, state.SelectedRevision);
    }

    // -- reading back ---------------------------------------------------------------------------

    [Fact]
    public void AnActivatedRevisionReadsBackAsTheDocumentThatWasWritten()
    {
        TranscriptRevisionRecord record = ActivateOne(segments: 3);

        TranscriptDocument? read = _transcripts.ReadTranscript(SessionId, record.Revision);

        Assert.NotNull(read);
        Assert.Equal(3, read!.Segments.Count);
        Assert.Equal(SessionId, read.SessionId);
        Assert.False(read.Model.RecognizesSpeech);
    }

    [Fact]
    public void ATamperedRevisionFileWillNotBeRead()
    {
        TranscriptRevisionRecord record = ActivateOne();

        File.WriteAllText(_transcripts.RevisionPath(SessionId, record.Revision), "{}");

        // The digest recorded at activation is the revision's identity. This file is not it.
        Assert.Null(_transcripts.ReadTranscript(SessionId, record.Revision));
    }

    [Fact]
    public void ProgressIsPersistedWithoutEverEnteringTheJournal()
    {
        TranscriptionAttempt attempt = _transcripts.BeginAttempt(SessionId, "job-progress", new string('a', 64), Options, Tick());
        _transcripts.MarkStarted(attempt, Options, Tick());
        _transcripts.RecordProgress(attempt, "transcribing_microphone", 3, 8);

        TranscriptionState state = _transcripts.Read(SessionId);
        Assert.Equal(3, state.CurrentJob!.CompletedUnits);
        Assert.Equal(8, state.CurrentJob.TotalUnits);
        Assert.Equal("transcribing_microphone", state.CurrentJob.Stage);

        // The journal is a recovery ledger, not a log. Progress does not belong in it.
        Assert.DoesNotContain(
            _sessions.ReadJournal(SessionId).Events,
            e => e.Type.Contains("progress", StringComparison.Ordinal));
    }

    [Fact]
    public void NoJournalEventCarriesTranscriptText()
    {
        ActivateOne(segments: 3);

        foreach (JournalEvent entry in _sessions.ReadJournal(SessionId).Events)
        {
            foreach (string value in entry.Fields.Values)
            {
                Assert.DoesNotContain("line 1", value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ASessionThatWasNeverTranscribedReportsNotRequested()
    {
        TranscriptionState state = _transcripts.Read(SessionId);

        Assert.Equal(ProcessingStageState.NotRequested, state.Stage);
        Assert.Null(state.SelectedRevision);
        Assert.Empty(state.Revisions);
        Assert.Null(state.CurrentJob);
    }
}
