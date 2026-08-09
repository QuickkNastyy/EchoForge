using EchoForge.App;
using EchoForge.App.Library;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Playback;
using EchoForge.Contracts.Sessions;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Playback;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.UnitTests;

/// <summary>A reprocessor that records what it was asked and answers however a test wants.</summary>
public sealed class FakeReprocessor : IMeetingReprocessor
{
    public List<string> Transcribed { get; } = [];

    public List<string> Summarized { get; } = [];

    public ReprocessOutcome Outcome { get; set; } = new(true, "transcribed", "Done.", 2);

    public bool CanTranscribe { get; set; } = true;

    public bool CanSummarize { get; set; } = true;

    public Task<ReprocessOutcome> TranscribeAgainAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Transcribed.Add(sessionId);
        return Task.FromResult(Outcome);
    }

    public Task<ReprocessOutcome> SummarizeAgainAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Summarized.Add(sessionId);
        return Task.FromResult(Outcome);
    }

    public Task<ReprocessOutcome> ProcessMeetingAsync(
        string sessionId,
        IProgress<string>? stage = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReprocessOutcome(true, "processed", "processed", 1));
}

/// <summary>A confirmation whose answer the test chooses.</summary>
public sealed class ScriptedConfirmation(bool answer) : IDeleteConfirmation
{
    public int Asked { get; private set; }

    public DeletionEligibility? LastSeen { get; private set; }

    public bool Confirm(DeletionEligibility eligibility)
    {
        Asked++;
        LastSeen = eligibility;
        return answer;
    }
}

/// <summary>
/// The library surface: following a citation into the audio, filtering by date, reprocessing, and
/// deleting.
///
/// <para>
/// View-model level rather than window level, because that is where the decisions are. The window
/// moves clicks and scrolls a list; everything worth asserting — which revision a citation opens,
/// whether a seek is presented as exact, whether a deletion was confirmed — happens here and is
/// testable without a window.
/// </para>
/// </summary>
public sealed class LibraryPlaybackUiTests : IDisposable
{
    private readonly LibraryFixture _fixture = new();
    private readonly TempDirectory _bin = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _bin.Dispose();
        _fixture.Dispose();
    }

    private async Task<LibraryViewModel> OpenLibraryAsync(LibraryServices? services = null)
    {
        SqliteLibraryIndex index = _fixture.NewIndex();
        LibraryViewModel library = new(
            index, _fixture.Projection, _fixture.Transcripts, _fixture.Summaries, _fixture.Aliases, services);

        _disposables.Add(library);
        await library.InitializeAsync();
        return library;
    }

    private MeetingViewModel Meeting(string sessionId, PlaybackViewModel? playback = null)
    {
        MeetingViewModel meeting = new(
            _fixture.Entry(sessionId), _fixture.Transcripts, _fixture.Summaries, _fixture.Aliases, playback);

        _disposables.Add(meeting);
        return meeting;
    }

    // -- evidence into the audio -----------------------------------------------------------------

    [Fact]
    public void AResolvedCitationOpensItsOwnRevisionAndProducesAnExactSeek()
    {
        string session = _fixture.AddSession("01JEVIDENCE");
        _fixture.AddTranscript(session, ("microphone", "We will ship on the fourteenth."));
        _fixture.AddSummary(session, 1, decisions: [("Ship on the fourteenth", "segment-000001")]);

        // A second transcript, selected, so "the selected one" and "the cited one" differ.
        _fixture.AddTranscript(session, ("microphone", "Entirely different words."));

        MeetingViewModel meeting = Meeting(session);
        Assert.Equal(2, meeting.SelectedTranscriptRevision);

        SummaryLine line = meeting.SummaryLines.First(l => l.HasEvidence);
        EvidenceLocation evidence = line.Evidence[0];

        MeetingViewModel.EvidenceFollow follow = meeting.FollowEvidence(evidence);

        // Opened against the revision the summary names, not the one that happened to be selected.
        Assert.Equal(1, meeting.SelectedTranscriptRevision);
        Assert.NotNull(follow.Line);
        Assert.Equal("segment-000001", follow.Line!.SegmentId);

        Assert.False(follow.IsApproximate);
        Assert.Equal(evidence.StartSeconds, follow.Request.StartSeconds);
        Assert.Equal(session, follow.Request.SessionId);
    }

    [Fact]
    public void AnUnresolvedCitationSeeksApproximatelyAndDoesNotRebaseOntoTheSelectedTranscript()
    {
        string session = _fixture.AddSession("01JDEGRADED");
        _fixture.AddTranscript(session, ("microphone", "The only transcript there is."));

        MeetingViewModel meeting = Meeting(session);

        // A citation naming a revision that is not there. Its stored time is all that survives.
        EvidenceLocation evidence = new()
        {
            SessionId = session,
            TranscriptRevision = 7,
            SegmentId = "segment-000001",
            Resolution = EvidenceResolution.Degraded,
            StartSeconds = 123.5,
            Explanation = "That transcript version is no longer on disk.",
        };

        MeetingViewModel.EvidenceFollow follow = meeting.FollowEvidence(evidence);

        Assert.True(follow.IsApproximate);
        Assert.Equal(123.5, follow.Request.StartSeconds);
        Assert.Null(follow.Line);

        // The dangerous thing would have been finding segment-000001 in revision 1 and calling it
        // the cited passage. The selected revision is untouched and no line was highlighted.
        Assert.Equal(1, meeting.SelectedTranscriptRevision);
        Assert.Contains("no longer on disk", meeting.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void ATranscriptTimestampProducesAnExactSeek()
    {
        string session = _fixture.AddSession("01JTIMESTAMP");
        _fixture.AddTranscript(session, ("microphone", "First"), ("system", "Second"), ("microphone", "Third"));

        MeetingViewModel meeting = Meeting(session);
        TranscriptLine third = meeting.Lines[2];

        PlaybackRequest request = meeting.RequestPlayback(third);

        Assert.False(request.IsApproximate);
        Assert.Equal(third.StartSeconds, request.StartSeconds);
    }

    [Fact]
    public void CueingAnApproximateRequestSaysSoAndCueingAnExactOneDoesNot()
    {
        using PlaybackViewModel playback = NewPlayback(out FakePlaybackDevice _, out PlaybackEngineHarness harness);

        playback.Cue(new PlaybackRequest { SessionId = "s", StartSeconds = 12, IsApproximate = true });
        Assert.True(playback.HasSeekNotice);
        Assert.Contains("approximate", playback.SeekNotice, StringComparison.OrdinalIgnoreCase);

        playback.Cue(new PlaybackRequest { SessionId = "s", StartSeconds = 20 });
        Assert.False(playback.HasSeekNotice);

        harness.Dispose();
    }

    /// <summary>Keeps the fixture the playback view model needs alive for the length of a test.</summary>
    private sealed class PlaybackEngineHarness(PlaybackFixture fixture) : IDisposable
    {
        public void Dispose() => fixture.Dispose();
    }

    private static PlaybackViewModel NewPlayback(out FakePlaybackDevice device, out PlaybackEngineHarness harness)
    {
        PlaybackFixture fixture = new("01JPLAYVM");
        harness = new PlaybackEngineHarness(fixture);

        fixture.Build(new PlaybackChunkSpec(SourceTrack.Microphone, 1, 30.0, 3000));

        FakePlaybackDevice created = new();
        device = created;

        PlaybackViewModel playback = new(
            fixture.SessionId, new PlaybackPreparer(fixture.Sessions), () => created, startTimer: false);

        playback.PrepareAsync().GetAwaiter().GetResult();

        Assert.Equal(PlaybackState.Ready, playback.State);
        return playback;
    }

    [Fact]
    public void PreparingRealAudioGivesATransportWithADurationAndAWorkingSeek()
    {
        using PlaybackViewModel playback = NewPlayback(out FakePlaybackDevice device, out PlaybackEngineHarness harness);

        Assert.Equal(30.0, playback.DurationSeconds, 2);
        Assert.True(playback.HasYouTrack);
        Assert.False(playback.HasRemoteTrack);

        playback.SeekTo(12.5);
        Assert.Equal(12.5, playback.PositionSeconds, 3);

        playback.TogglePlay();
        Assert.True(playback.IsPlaying);
        Assert.True(device.IsPlaying);

        playback.TogglePlay();
        Assert.False(playback.IsPlaying);

        playback.Stop();
        Assert.Equal(0, playback.PositionSeconds, 6);

        harness.Dispose();
    }

    [Fact]
    public async Task AnUnprocessedMeetingGetsAnAudioShapeAndKeepsASeekMadeBeforePreparation()
    {
        using PlaybackFixture fixture = new("01JRAWMEETING");
        fixture.Build(new PlaybackChunkSpec(SourceTrack.Microphone, 1, 30.0, 3000));

        FileTranscriptionStore transcripts = new(fixture.Sessions);
        FileSummaryStore summaries = new(fixture.Sessions);
        FileSpeakerAliasStore aliases = new(fixture.Sessions);
        LibraryProjection projection = new(fixture.Sessions, transcripts, summaries, aliases);
        LibraryEntry entry = Assert.IsType<LibraryDocument>(projection.Build(fixture.SessionId)).Entry;

        using PlaybackViewModel playback = new(
            fixture.SessionId,
            new PlaybackPreparer(fixture.Sessions),
            () => new FakePlaybackDevice(),
            startTimer: false);
        using MeetingViewModel meeting = new(entry, transcripts, summaries, aliases, playback);

        Assert.False(meeting.HasTranscript);
        Assert.False(meeting.Shape.HasData);
        Assert.Equal(entry.Duration.TotalSeconds, meeting.TimelineSeconds, 3);

        playback.SeekTo(meeting.TimelineSeconds / 2);
        await playback.PrepareAsync();

        Assert.NotNull(playback.EnergyEnvelope);
        Assert.True(meeting.Shape.HasData);
        Assert.Contains(meeting.Shape.You, level => level > 0);
        Assert.Equal(15.0, playback.PositionSeconds, 2);
    }

    [Fact]
    public async Task AMeetingWhoseAudioIsMissingSaysSoRatherThanFailingSilently()
    {
        // The library fixture writes chunk metadata without the audio behind it, which is exactly
        // what a session whose files were removed underneath it looks like.
        string session = _fixture.AddSession("01JNOAUDIO");

        PlaybackViewModel playback = new(
            session, new PlaybackPreparer(_fixture.Sessions), () => new FakePlaybackDevice(), startTimer: false);

        _disposables.Add(playback);

        await playback.PrepareAsync();

        Assert.Equal(PlaybackState.Failed, playback.State);
        Assert.True(playback.HasMessage);
        Assert.DoesNotContain(_fixture.Root, playback.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClosingAMeetingClosesItsTransport()
    {
        using PlaybackViewModel playback = NewPlayback(out FakePlaybackDevice device, out PlaybackEngineHarness harness);

        string session = _fixture.AddSession("01JCLOSE");
        MeetingViewModel meeting = Meeting(session, playback);

        meeting.Dispose();

        Assert.True(device.IsDisposed);
        harness.Dispose();
    }

    [Fact]
    public async Task OnlyOneMeetingIsEverOpen()
    {
        _fixture.AddSession("01JONE", "One");
        _fixture.AddSession("01JTWO", "Two");

        LibraryViewModel library = await OpenLibraryAsync();

        library.SelectedMeeting = library.Meetings[0];
        MeetingViewModel first = library.OpenMeeting!;

        library.SelectedMeeting = library.Meetings[1];
        MeetingViewModel second = library.OpenMeeting!;

        // A new one, and the previous one closed with its transport.
        Assert.NotSame(first, second);

        // Closing the window releases the transport without throwing the library away.
        library.CloseOpenMeeting();

        Assert.Null(library.OpenMeeting);
        Assert.Null(library.SelectedMeeting);
        Assert.Equal(2, library.Meetings.Count);
    }

    // -- date filtering in the library -----------------------------------------------------------

    [Fact]
    public async Task TheDateRangeNarrowsTheListAndClearingItRestoresIt()
    {
        _fixture.AddSession("d-early", "Early", createdUtc: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero));
        _fixture.AddSession("d-late", "Late", createdUtc: new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero));

        LibraryViewModel library = await OpenLibraryAsync();
        Assert.Equal(2, library.Meetings.Count);

        library.FromDate = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Local);

        Assert.True(library.HasDateFilter);
        Assert.Single(library.Meetings);
        Assert.Equal("d-late", library.Meetings[0].SessionId);

        library.ClearDatesCommand.Execute(null);

        Assert.False(library.HasDateFilter);
        Assert.Equal(2, library.Meetings.Count);
    }

    [Fact]
    public async Task AReversedRangeShowsNothingAndExplainsWhy()
    {
        _fixture.AddSession("d-one", "One", createdUtc: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero));

        LibraryViewModel library = await OpenLibraryAsync();

        library.FromDate = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Local);
        library.ToDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Local);

        Assert.True(library.Filter.IsReversed);
        Assert.Empty(library.Meetings);
        Assert.Contains("after", library.Status, StringComparison.OrdinalIgnoreCase);
    }

    // -- reprocessing from the library -----------------------------------------------------------

    [Fact]
    public async Task ReprocessingAsksTheCoordinatorAndThenRefreshesWhatIsOnScreen()
    {
        string session = _fixture.AddSession("01JAGAIN", "Retry me");
        _fixture.AddTranscript(session, ("microphone", "The first pass."));

        FakeReprocessor reprocessor = new();
        LibraryViewModel library = await OpenLibraryAsync(new LibraryServices { Reprocessor = reprocessor });

        library.SelectedMeeting = library.Meetings.Single();

        Assert.True(library.TranscribeAgainCommand.CanExecute(null));
        Assert.True(library.SummarizeAgainCommand.CanExecute(null));

        await RunAsync(library.TranscribeAgainCommand, () => reprocessor.Transcribed.Count == 1 && !library.IsBusy);
        await RunAsync(library.SummarizeAgainCommand, () => reprocessor.Summarized.Count == 1 && !library.IsBusy);

        Assert.Equal([session], reprocessor.Transcribed);
        Assert.Equal([session], reprocessor.Summarized);
        Assert.Equal("Done.", library.Status);

        // The user did not have to leave the library, and did not have to press Refresh.
        Assert.NotNull(library.OpenMeeting);
        Assert.True(library.OpenMeeting!.HasTranscript);
    }

    [Fact]
    public async Task AnInstallationWithoutProcessingDoesNotOfferToReprocess()
    {
        _fixture.AddSession("01JNOPROC");

        LibraryViewModel library = await OpenLibraryAsync();
        library.SelectedMeeting = library.Meetings.Single();

        Assert.False(library.CanReprocessHere);
        Assert.False(library.TranscribeAgainCommand.CanExecute(null));
        Assert.False(library.SummarizeAgainCommand.CanExecute(null));
    }

    // -- deleting from the library ----------------------------------------------------------------

    private SessionDeletionService Deletion(FakeRecycleBin bin, ISessionDeletionAuthority? authority = null) =>
        new(_fixture.Sessions,
            _fixture.Root,
            authority ?? new SessionStateDeletionAuthority(_fixture.Sessions),
            bin,
            _ => Task.CompletedTask);

    [Fact]
    public async Task DeletingRequiresConfirmationAndThenRemovesTheMeetingFromTheLibrary()
    {
        string session = _fixture.AddSession("01JBIN", "Delete me");
        _fixture.AddTranscript(session, ("microphone", "Something about wombats."));

        FakeRecycleBin bin = new(_bin.Path);
        ScriptedConfirmation confirmation = new(answer: true);

        LibraryViewModel library = await OpenLibraryAsync(new LibraryServices
        {
            Deletion = Deletion(bin),
            Confirmation = confirmation,
        });

        library.SelectedMeeting = library.Meetings.Single();
        library.SearchText = "wombats";
        await RunAsync(library.SearchCommand, () => library.Results.Count > 0);
        Assert.NotEmpty(library.Results);

        await RunAsync(library.DeleteMeetingCommand, () => library.Meetings.Count == 0 && !library.IsBusy);

        Assert.Equal(1, confirmation.Asked);
        Assert.Equal("Delete me", confirmation.LastSeen!.Title);

        Assert.Single(bin.Recycled);
        Assert.Empty(library.Meetings);
        Assert.Empty(library.Results);

        // The open meeting closed rather than staying readable from the Recycle Bin.
        Assert.Null(library.OpenMeeting);
        Assert.Null(library.SelectedMeeting);
    }

    [Fact]
    public async Task CancellingTheConfirmationChangesNothingAtAll()
    {
        string session = _fixture.AddSession("01JKEEP", "Keep me");
        string root = _fixture.Sessions.Resolve(session).Root;

        FakeRecycleBin bin = new(_bin.Path);
        ScriptedConfirmation confirmation = new(answer: false);

        LibraryViewModel library = await OpenLibraryAsync(new LibraryServices
        {
            Deletion = Deletion(bin),
            Confirmation = confirmation,
        });

        library.SelectedMeeting = library.Meetings.Single();
        await RunAsync(library.DeleteMeetingCommand, () => confirmation.Asked == 1 && !library.IsBusy);

        Assert.Equal(1, confirmation.Asked);
        Assert.Empty(bin.Recycled);
        Assert.True(Directory.Exists(root));
        Assert.Single(library.Meetings);
        Assert.NotNull(library.OpenMeeting);
    }

    [Fact]
    public async Task ARefusalIsShownAndNothingIsEvenAskedAbout()
    {
        _fixture.AddSession("01JLIVE", "Recording now", Contracts.Sessions.SessionState.Recording);

        FakeRecycleBin bin = new(_bin.Path);
        ScriptedConfirmation confirmation = new(answer: true);

        LibraryViewModel library = await OpenLibraryAsync(new LibraryServices
        {
            Deletion = Deletion(bin),
            Confirmation = confirmation,
        });

        library.SelectedMeeting = library.Meetings.Single();
        await RunAsync(library.DeleteMeetingCommand, () => library.Status is not null && !library.IsBusy);

        Assert.Equal(0, confirmation.Asked);
        Assert.Empty(bin.Recycled);
        Assert.Contains("not deleted", library.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Single(library.Meetings);
    }

    /// <summary>
    /// Runs an async command and waits for the effect it was invoked for.
    ///
    /// <para>
    /// <c>ICommand.Execute</c> returns void, which is the framework's shape rather than a choice,
    /// so the only thing a test can wait on is the outcome. Waiting for the command to become
    /// executable again would not work either: a command that deletes the selected meeting is
    /// correctly disabled once there is nothing selected.
    /// </para>
    /// </summary>
    private static async Task RunAsync(AsyncRelayCommand command, Func<bool> until)
    {
        command.Execute(null);

        for (int i = 0; i < 500; i++)
        {
            if (until())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("the command did not finish");
    }
}
