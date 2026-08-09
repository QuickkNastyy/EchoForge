using System.Security.Cryptography;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Workers;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Summaries;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.UnitTests;

/// <summary>A gate a test can open and close, standing in for a live recorder.</summary>
public sealed class ToggleCaptureGate : ICaptureActivityGate
{
    public bool IsCaptureActive { get; set; }
}

/// <summary>
/// Running transcription and summarisation again from the meeting library.
///
/// <para>
/// Against the real coordinators and a real worker, because the whole claim is that reprocessing
/// from the library is the <i>same</i> operation as reprocessing from the main window. A fake
/// coordinator would demonstrate that the fake allocates revision numbers.
/// </para>
///
/// <para>
/// The properties that matter are conservative ones: the audio is not touched, previous revisions
/// stay exactly where they are, and a new transcript makes the summary written from the old one
/// visibly out of date rather than quietly wrong.
/// </para>
/// </summary>
public sealed class LibraryReprocessTests : IDisposable
{
    private const string SessionId = "01JREPROCESS";

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _sessions;
    private readonly FileTranscriptionStore _transcripts;
    private readonly FileSummaryStore _summaries;
    private readonly ToggleCaptureGate _gate = new();
    private readonly List<IDisposable> _disposables = [];

    public LibraryReprocessTests()
    {
        _sessions = new FileSessionStore(_temp.Path);
        _transcripts = new FileTranscriptionStore(_sessions);
        _summaries = new FileSummaryStore(_sessions);
    }

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _temp.Dispose();
    }

    private TranscriptionCoordinator NewTranscription()
    {
        TranscriptionCoordinator coordinator = new(
            _sessions, _transcripts, new WorkerSupervisor(WorkerTestEnvironment.Options()), _gate);

        _disposables.Add(coordinator);
        return coordinator;
    }

    private SummaryCoordinator NewSummaries(Func<bool>? otherJobRunning = null)
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

    private static TranscriptionOptions Mock() => new() { Backend = WorkerProtocol.MockBackend };

    private Dictionary<string, string> AudioDigests()
    {
        Dictionary<string, string> digests = new(StringComparer.Ordinal);
        string tracks = Path.Combine(_sessions.Resolve(SessionId).Root, "tracks");

        foreach (string file in Directory.EnumerateFiles(tracks, "*.wav", SearchOption.AllDirectories).Order())
        {
            using FileStream stream = File.OpenRead(file);
            digests[Path.GetFileName(file)] = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        return digests;
    }

    // -- transcribing again --------------------------------------------------------------------

    [WorkerFact]
    public async Task TranscribingAgainCreatesANewRevisionAndKeepsTheOldOne()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        Dictionary<string, string> before = AudioDigests();

        CoordinatorReprocessor reprocessor = new(NewTranscription(), null, Mock);

        ReprocessOutcome first = await reprocessor.TranscribeAgainAsync(SessionId);
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(1, first.Revision);

        ReprocessOutcome second = await reprocessor.TranscribeAgainAsync(SessionId);
        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(2, second.Revision);

        TranscriptionState state = _transcripts.Read(SessionId);

        // Both are there, and the new one is the one being read.
        Assert.Equal([1, 2], state.Revisions.Select(r => r.Revision).Order());
        Assert.All(state.Revisions, revision => Assert.True(revision.FileExists));
        Assert.Equal(2, state.SelectedRevision);

        Assert.NotNull(_transcripts.ReadTranscript(SessionId, 1));
        Assert.NotNull(_transcripts.ReadTranscript(SessionId, 2));

        // The recording is what it always was. Reprocessing reads audio and writes transcripts.
        Assert.Equal(before, AudioDigests());
    }

    [WorkerFact]
    public async Task ANewTranscriptMakesTheSummaryWrittenFromTheOldOneStale()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);

        CoordinatorReprocessor reprocessor = new(NewTranscription(), NewSummaries(), Mock);

        Assert.True((await reprocessor.TranscribeAgainAsync(SessionId)).Succeeded);

        ReprocessOutcome summarised = await reprocessor.SummarizeAgainAsync(SessionId);
        Assert.True(summarised.Succeeded, summarised.Message);

        SummaryRevisionRecord summary = _summaries.Read(SessionId).Selected!;
        Assert.Equal(1, summary.TranscriptRevision);
        Assert.False(summary.IsStaleAgainst(1));

        // Transcribe again, and the summary is about a transcript that is no longer selected.
        Assert.True((await reprocessor.TranscribeAgainAsync(SessionId)).Succeeded);

        Assert.Equal(2, _transcripts.Read(SessionId).SelectedRevision);
        Assert.True(_summaries.Read(SessionId).Selected!.IsStaleAgainst(2));

        // And it is still perfectly readable about the revision it came from.
        Assert.NotNull(_summaries.ReadSummary(SessionId, summary.Revision));
    }

    [WorkerFact]
    public async Task SummarisingAgainUsesTheSelectedTranscriptAndKeepsEveryEarlierSummary()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);

        CoordinatorReprocessor reprocessor = new(NewTranscription(), NewSummaries(), Mock);

        await reprocessor.TranscribeAgainAsync(SessionId);
        await reprocessor.SummarizeAgainAsync(SessionId);
        await reprocessor.TranscribeAgainAsync(SessionId);

        ReprocessOutcome regenerated = await reprocessor.SummarizeAgainAsync(SessionId);

        Assert.True(regenerated.Succeeded, regenerated.Message);
        Assert.Equal(2, regenerated.Revision);

        SummaryState summaries = _summaries.Read(SessionId);

        Assert.Equal([1, 2], summaries.Revisions.Select(r => r.Revision).Order());
        Assert.All(summaries.Revisions, revision => Assert.True(revision.FileExists));

        // Generated from the transcript the reader had selected, which is the new one.
        Assert.Equal(2, summaries.Selected!.TranscriptRevision);
        Assert.False(summaries.Selected.IsStaleAgainst(2));

        // The earlier summary still points at the earlier transcript, and still resolves.
        Assert.Equal(1, summaries.Revisions.First(r => r.Revision == 1).TranscriptRevision);
    }

    // -- refusals ------------------------------------------------------------------------------

    [WorkerFact]
    public async Task RecordingStillOutranksReprocessingFromTheLibrary()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        _gate.IsCaptureActive = true;

        TranscriptionCoordinator transcription = NewTranscription();
        CoordinatorReprocessor reprocessor = new(transcription, NewSummaries(), Mock);

        // Summarising refuses outright while capture is live.
        ReprocessOutcome summary = await reprocessor.SummarizeAgainAsync(SessionId);
        Assert.False(summary.Succeeded);
        Assert.Contains("recording", summary.Message, StringComparison.OrdinalIgnoreCase);

        // Transcription queues instead of running, and the request is not lost.
        Task<ReprocessOutcome> queued = reprocessor.TranscribeAgainAsync(SessionId);
        Assert.False(queued.IsCompleted);

        _gate.IsCaptureActive = false;
        transcription.CaptureStateChanged();

        ReprocessOutcome outcome = await queued;
        Assert.True(outcome.Succeeded, outcome.Message);
    }

    [WorkerFact]
    public async Task OnlyOneHeavyJobRunsAtATimeHoweverItWasAskedFor()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);

        TranscriptionCoordinator transcription = NewTranscription();
        CoordinatorReprocessor reprocessor = new(
            transcription,
            NewSummaries(otherJobRunning: () => transcription.IsRunning),
            () => new TranscriptionOptions
            {
                Backend = WorkerProtocol.MockBackend,
                TestMode = WorkerTestModes.Delay,
                TestDelaySeconds = 1.5,
            });

        Task<ReprocessOutcome> running = reprocessor.TranscribeAgainAsync(SessionId);

        // Wait for the first request to actually reach the coordinator. Asking again before it
        // has would be testing the scheduler rather than the rule.
        while (!transcription.IsRunning && !transcription.IsQueued)
        {
            await Task.Delay(10);
        }

        // A second request while the first holds the coordinator.
        ReprocessOutcome refused = await reprocessor.TranscribeAgainAsync(SessionId);

        Assert.False(refused.Succeeded);
        Assert.Equal("busy", refused.Code);

        await running;
    }

    [WorkerFact]
    public async Task CancellingLeavesEveryEarlierRevisionExactlyWhereItWas()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);

        CoordinatorReprocessor first = new(NewTranscription(), null, Mock);
        Assert.True((await first.TranscribeAgainAsync(SessionId)).Succeeded);

        Dictionary<string, string> audio = AudioDigests();
        string revisionOne = _transcripts.RevisionPath(SessionId, 1);
        byte[] before = await File.ReadAllBytesAsync(revisionOne);

        TranscriptionCoordinator coordinator = NewTranscription();
        CoordinatorReprocessor slow = new(
            coordinator,
            null,
            () => new TranscriptionOptions
            {
                Backend = WorkerProtocol.MockBackend,
                TestMode = WorkerTestModes.Delay,
                TestDelaySeconds = 5,
            });

        using CancellationTokenSource cancellation = new();
        Task<ReprocessOutcome> work = slow.TranscribeAgainAsync(SessionId, cancellation.Token);

        // Waited for, not slept through. A fixed delay assumes the worker process is up and
        // watching its input within that time, and the first launch after a build is not: the
        // cancel then arrives before anything is listening for it, the job runs to completion,
        // and the test fails for a reason that has nothing to do with cancellation.
        while (!coordinator.IsRunning)
        {
            await Task.Delay(25);
        }

        await cancellation.CancelAsync();

        ReprocessOutcome outcome = await work;

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, _transcripts.Read(SessionId).SelectedRevision);
        Assert.Equal(before, await File.ReadAllBytesAsync(revisionOne));
        Assert.Equal(audio, AudioDigests());
    }

    [Fact]
    public async Task AnInstallationWithoutProcessingSaysSoRatherThanFailing()
    {
        CoordinatorReprocessor reprocessor = new(null, null);

        Assert.False(reprocessor.CanTranscribe);
        Assert.False(reprocessor.CanSummarize);

        ReprocessOutcome transcribe = await reprocessor.TranscribeAgainAsync(SessionId);
        ReprocessOutcome summarize = await reprocessor.SummarizeAgainAsync(SessionId);

        Assert.False(transcribe.Succeeded);
        Assert.False(summarize.Succeeded);
        Assert.Equal("unavailable", transcribe.Code);
        Assert.Equal("unavailable", summarize.Code);
    }

    // -- the library catches up ------------------------------------------------------------------

    [WorkerFact]
    public async Task TheLibraryShowsTheNewRevisionOnceProcessingFinishes()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);

        FileSpeakerAliasStore aliases = new(_sessions);
        LibraryProjection projection = new(_sessions, _transcripts, _summaries, aliases);

        using SqliteLibraryIndex index = new(Path.Combine(_temp.Path, "library.db"), projection);
        await index.EnsureReadyAsync();

        using LibraryIndexMaintainer maintenance = new(index);

        TranscriptionCoordinator transcription = NewTranscription();
        transcription.SessionChanged += (_, session) => maintenance.Invalidate(session);

        Assert.False(index.Meetings().Single().HasTranscript);

        CoordinatorReprocessor reprocessor = new(transcription, null, Mock);
        Assert.True((await reprocessor.TranscribeAgainAsync(SessionId)).Succeeded);

        await maintenance.WaitAsync(SessionId);

        // Nobody pressed Refresh.
        Contracts.Library.LibraryEntry entry = index.Meetings().Single();
        Assert.True(entry.HasTranscript);
        Assert.Equal(1, entry.SelectedTranscriptRevision);
    }
}
