using System.Diagnostics;
using System.Text;
using EchoForge.App.Library;
using EchoForge.Contracts.Audio;
using EchoForge.Core.Recording;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Exports;
using EchoForge.Infrastructure.Library;

namespace EchoForge.UnitTests;

/// <summary>The library surface, and the files it writes out.</summary>
public sealed class LibraryUiAndExportTests : IDisposable
{
    private readonly LibraryFixture _fixture = new();
    private readonly TempDirectory _output = new();

    public void Dispose()
    {
        _fixture.Dispose();
        _output.Dispose();
    }

    private MeetingViewModel Open(string sessionId) => new(
        _fixture.Entry(sessionId), _fixture.Transcripts, _fixture.Summaries, _fixture.Aliases);

    private string Destination(string name) => Path.Combine(_output.Path, name);

    // -- the meeting surface --------------------------------------------------------------------

    [Fact]
    public void OpeningAMeetingShowsItsTranscriptAndSummary()
    {
        _fixture.AddSession("01JA", "Planning");
        _fixture.AddTranscript("01JA",
            ("microphone", "We will ship the beta on Friday"),
            ("system", "Alex will prepare the release notes"));
        _fixture.AddSummary("01JA", 1,
            decisions: [("Ship the beta on Friday", "segment-000001")],
            actions: [("Prepare the release notes", "segment-000002", "Alex")]);

        MeetingViewModel meeting = Open("01JA");

        Assert.True(meeting.HasTranscript);
        Assert.True(meeting.HasSummary);
        Assert.Equal(2, meeting.Lines.Count);
        Assert.Equal("You", meeting.Lines[0].Speaker);
        Assert.Equal(TranscriptSpeakers.YouId, meeting.Lines[0].SpeakerId);
        Assert.Equal(TranscriptSpeakers.RemoteId, meeting.Lines[1].SpeakerId);
        Assert.Equal("00:00:00", meeting.Lines[0].Timestamp);
        Assert.Equal(2, meeting.SummaryLines.Count);
        Assert.Contains(meeting.SummaryLines, l => l.Section == "Decisions");
        // Owner and date are separate facts now, so that a slot the meeting never filled can be
        // drawn as an empty slot rather than as the word "unknown" in the same ink as a real name.
        SummaryLine action = Assert.Single(meeting.SummaryLines, l => l.Section == "Action items");
        Assert.True(action.HasAssignment);
        Assert.Equal("Alex", action.Owner);
        Assert.False(action.OwnerMissing);
        Assert.True(action.DueMissing);

        // The citation carries the track it came from, so the chip can be drawn in its colour.
        EvidenceChip cited = Assert.Single(action.Chips);
        Assert.False(cited.IsYou);
        Assert.True(cited.IsResolved);
    }

    [Fact]
    public void AdjacentAsrChunksFromOneSpeakerDisplayAsOneReadableUtterance()
    {
        _fixture.AddSession("01JUTTERANCE", "Conversation");
        _fixture.AddTimedTranscript("01JUTTERANCE",
            ("microphone", "Alright, so", 156.0, 157.0),
            ("microphone", "official action, Hash Daddy Ray", 157.1, 159.0),
            ("microphone", "has been switched from the janitor position", 159.1, 161.0),
            ("microphone", "to the private", 161.1, 163.0),
            ("microphone", "chef.", 163.62, 164.0),
            ("microphone", "What kind of great meals are you good at cooking?", 166.54, 168.68),
            ("system", "What do you want?", 170.0, 171.0));

        MeetingViewModel meeting = Open("01JUTTERANCE");

        Assert.Equal(2, meeting.Lines.Count);
        TranscriptLine utterance = meeting.Lines[0];
        Assert.Equal("You", utterance.Speaker);
        Assert.Equal("00:02:36", utterance.Timestamp);
        Assert.Equal(
            "Alright, so official action, Hash Daddy Ray has been switched from the janitor position to the private chef. What kind of great meals are you good at cooking?",
            utterance.Text);
        Assert.Equal(6, utterance.SegmentIds.Count);
        Assert.Same(utterance, meeting.LocateSegment(1, "segment-000006"));
    }

    [Fact]
    public void DefaultRecordingLabelsUseTheSystemsLocalTwelveHourClock()
    {
        _fixture.AddSession("01JLOCALTIME");
        LibraryEntry entry = _fixture.Entry("01JLOCALTIME");
        MeetingRow row = new(entry);

        Assert.Matches(@"\b\d{1,2}:\d{2}\s(?:AM|PM)\b", entry.Title);
        Assert.Matches(@"\b\d{1,2}:\d{2}\s(?:AM|PM)\b", row.When);
        Assert.DoesNotContain("→", row.Sub, StringComparison.Ordinal);
        Assert.Equal(row.DurationLabel, row.Sub);
    }

    [Fact]
    public void ARecordingRenameIsPresentationOnlyAndSurvivesAProjectionRestart()
    {
        string session = _fixture.AddSession("01JRENAME");
        string snapshotPath = _fixture.Sessions.Resolve(session).SnapshotPath;
        byte[] snapshotBefore = File.ReadAllBytes(snapshotPath);

        Assert.True(_fixture.Titles.Rename(session, "  Client   kickoff  "));

        LibraryEntry renamed = _fixture.RestartedProjection().Build(session)!.Entry;
        Assert.Equal("Client kickoff", renamed.Title);
        Assert.Equal(snapshotBefore, File.ReadAllBytes(snapshotPath));

        // Clearing the overlay restores the system-local date/time title without touching recovery.
        Assert.True(_fixture.Titles.Rename(session, null));
        LibraryEntry restored = _fixture.RestartedProjection().Build(session)!.Entry;
        Assert.NotEqual("Client kickoff", restored.Title);
        Assert.Equal(snapshotBefore, File.ReadAllBytes(snapshotPath));
    }

    [Fact]
    public void RowTranscriptCopyTextUsesReadableDisplayedUtterances()
    {
        _fixture.AddSession("01JCOPY");
        _fixture.AddTranscript("01JCOPY",
            ("microphone", "We should ship Friday"),
            ("system", "I can handle the release notes"));

        using LibraryViewModel library = new(
            _fixture.NewIndex(), _fixture.Projection, _fixture.Transcripts, _fixture.Summaries, _fixture.Aliases);
        MeetingRow row = new(_fixture.Entry("01JCOPY"));

        string text = Assert.IsType<string>(library.BuildTranscriptText(row));
        Assert.Contains("You: We should ship Friday", text, StringComparison.Ordinal);
        Assert.Contains("Remote: I can handle the release notes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("segment-", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongTranscriptDoesNotBecomeThousandsOfHeavyweightObjects()
    {
        _fixture.AddSession("01JA");

        (string, string)[] lines = [.. Enumerable.Range(0, 6000)
            .Select(i => (i % 2 == 0 ? "microphone" : "system", $"This is line number {i} of a very long meeting"))];

        _fixture.AddTranscript("01JA", lines);

        Stopwatch clock = Stopwatch.StartNew();
        MeetingViewModel meeting = Open("01JA");
        clock.Stop();

        Assert.Equal(6000, meeting.Lines.Count);

        // Plain records, not view models: no change notification, no commands, nothing that has
        // to be unsubscribed. The controls themselves are virtualized in XAML, and this is the
        // half of that decision the test can actually hold.
        Assert.IsType<TranscriptLine>(meeting.Lines[0]);
        Assert.DoesNotContain(
            typeof(System.ComponentModel.INotifyPropertyChanged),
            typeof(TranscriptLine).GetInterfaces());

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"loading took {clock.Elapsed}");
    }

    [Fact]
    public void SwitchingTranscriptRevisionChangesWhatIsShownAndNothingElse()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "The first attempt"));
        _fixture.AddSummary("01JA", 1, decisions: [("A decision", "segment-000001")]);
        _fixture.AddTranscript("01JA", ("microphone", "The second attempt"));

        byte[] summaryBefore = File.ReadAllBytes(_fixture.Summaries.PathFor("01JA", 1));

        MeetingViewModel meeting = Open("01JA");
        Assert.Equal("The second attempt", meeting.Lines[0].Text);

        meeting.SelectedTranscriptRevision = 1;

        Assert.Equal("The first attempt", meeting.Lines[0].Text);

        // Reading a different transcript version must never rewrite a summary.
        Assert.Equal(summaryBefore, File.ReadAllBytes(_fixture.Summaries.PathFor("01JA", 1)));
    }

    [Fact]
    public void AStaleSummaryIsMarkedAndSaysWhichTranscriptItBelongsTo()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "We will ship on Friday"));
        _fixture.AddSummary("01JA", 1, decisions: [("Ship on Friday", "segment-000001")]);
        _fixture.AddTranscript("01JA", ("microphone", "We will ship on Friday"));

        MeetingViewModel meeting = Open("01JA");

        Assert.True(meeting.SummaryIsStale);
        Assert.Contains("transcript changed", meeting.StaleNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Generate the brief again", meeting.StaleNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("version", meeting.StaleNotice, StringComparison.OrdinalIgnoreCase);

        // Still readable, and its citations still resolve against its own revision.
        SummaryLine decision = Assert.Single(meeting.SummaryLines);
        Assert.True(decision.Evidence[0].IsResolved);
        Assert.Equal(1, decision.Evidence[0].TranscriptRevision);
    }

    [Fact]
    public void FollowingACitationOpensTheRevisionItNames()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA",
            ("microphone", "First line of the original"),
            ("system", "Second line of the original"));
        _fixture.AddSummary("01JA", 1, decisions: [("A decision", "segment-000002")]);
        _fixture.AddTranscript("01JA", ("microphone", "A completely different transcript"));

        MeetingViewModel meeting = Open("01JA");
        Assert.Equal(2, meeting.SelectedTranscriptRevision);

        SummaryLine decision = Assert.Single(meeting.SummaryLines);
        TranscriptLine? located = meeting.LocateEvidence(decision.Evidence[0]);

        // The citation named version 1, so version 1 is what opens - not the selected one.
        Assert.NotNull(located);
        Assert.Equal(1, meeting.SelectedTranscriptRevision);
        Assert.Equal("segment-000002", located!.SegmentId);
        Assert.Equal("Second line of the original", located.Text);
    }

    [Fact]
    public void AnUnresolvableCitationExplainsItselfRatherThanJumpingSomewhere()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "Only line"));
        _fixture.AddSummary("01JA", 1, decisions: [("A decision", "segment-000001")]);

        MeetingViewModel meeting = Open("01JA");

        EvidenceLocation broken = new()
        {
            SessionId = "01JA",
            TranscriptRevision = 99,
            SegmentId = "segment-000001",
            Resolution = EvidenceResolution.Degraded,
            StartSeconds = 12,
            Explanation = "That transcript version is no longer on disk.",
        };

        Assert.Null(meeting.LocateEvidence(broken));
        Assert.True(meeting.HasNotice);
    }

    [Fact]
    public void RenamingASpeakerChangesTheDisplayedTranscriptOnly()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("system", "Something they said"), ("microphone", "Something I said"));

        byte[] before = File.ReadAllBytes(_fixture.Transcripts.RevisionPath("01JA", 1));

        MeetingViewModel meeting = Open("01JA");
        meeting.Rename(TranscriptSpeakers.RemoteId, "Priya");

        Assert.Equal("Priya", meeting.Lines[0].Speaker);
        Assert.Equal("You", meeting.Lines[1].Speaker);
        Assert.Equal(before, File.ReadAllBytes(_fixture.Transcripts.RevisionPath("01JA", 1)));

        meeting.Rename(TranscriptSpeakers.RemoteId, null);
        Assert.Equal("Remote", meeting.Lines[0].Speaker);
    }

    [Fact]
    public async Task TheLibraryListsMeetingsAndSearchStaysUsable()
    {
        _fixture.AddSession("01JA", "Planning");
        _fixture.AddTranscript("01JA", ("microphone", "The vendor contract needs signing"));
        _fixture.AddSession("01JB", "Retro");
        _fixture.AddTranscript("01JB", ("microphone", "Nothing about contracts here"));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        using LibraryViewModel library = new(
            index, _fixture.Projection, _fixture.Transcripts, _fixture.Summaries, _fixture.Aliases);

        await library.InitializeAsync();

        Assert.Equal(2, library.Meetings.Count);

        library.SearchText = "vendor";

        Stopwatch clock = Stopwatch.StartNew();
        library.SearchCommand.Execute(null);
        clock.Stop();

        // Execute must hand back the thread immediately; the search itself runs elsewhere.
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(250), $"Execute blocked for {clock.Elapsed}");

        await Task.Delay(600);

        Assert.Single(library.Results);
        Assert.Equal("Planning", library.Results[0].MeetingTitle);
    }

    [Fact]
    public async Task SelectingAMeetingOpensIt()
    {
        _fixture.AddSession("01JA", "Planning");
        _fixture.AddTranscript("01JA", ("microphone", "Something worth reading"));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        using LibraryViewModel library = new(
            index, _fixture.Projection, _fixture.Transcripts, _fixture.Summaries, _fixture.Aliases);

        await library.InitializeAsync();

        Assert.Null(library.OpenMeeting);

        library.SelectedMeeting = library.Meetings[0];

        Assert.NotNull(library.OpenMeeting);
        Assert.Equal("01JA", library.OpenMeeting!.SessionId);
        Assert.Single(library.OpenMeeting.Lines);
    }

    [Fact]
    public async Task StoppingARecordingWhileTheLibraryIsOpenAddsItsRow()
    {
        using SqliteLibraryIndex index = _fixture.NewIndex();
        using LibraryViewModel library = new(
            index, _fixture.Projection, _fixture.Transcripts, _fixture.Summaries, _fixture.Aliases);
        await library.InitializeAsync();
        Assert.Empty(library.Meetings);

        FakeCaptureEngineFactory engines = new();
        using RecordingController controller = new(
            _fixture.Sessions, engines, new FakeCaptureClock(), new FakeDiskSpaceProbe());

        RecordingStateChangedEventArgs? terminal = null;
        controller.StateChanged += (_, e) =>
        {
            if (e.State is Contracts.Sessions.SessionState.Recorded
                or Contracts.Sessions.SessionState.NeedsAttention
                or Contracts.Sessions.SessionState.Failed)
            {
                terminal = e;
            }
        };

        controller.Start(new RecordingRequest(
            "render-id", "Headphones", "capture-id", "Microphone"));
        string sessionId = Assert.IsType<string>(controller.SessionId);
        engines.Latest.EmitChunk(SourceTrack.Microphone);
        controller.Stop();

        Assert.NotNull(terminal);
        Assert.Equal(sessionId, terminal!.SessionId);
        Assert.True(await library.RefreshMeetingAsync(terminal.SessionId!));
        Assert.Contains(library.Meetings, row => row.SessionId == sessionId);
    }

    // -- exports -----------------------------------------------------------------------------------

    private SummaryDocument SummaryWith(string decisionText)
    {
        _fixture.AddSession("01JA", "Quarterly planning");
        _fixture.AddTranscript("01JA",
            ("microphone", "We will ship the beta on Friday"),
            ("system", "Someone should write the migration guide"));

        return _fixture.AddSummary("01JA", 1,
            decisions: [(decisionText, "segment-000001")],
            actions: [("Write the migration guide", "segment-000002", null)]);
    }

    [Fact]
    public void SummaryMarkdownCarriesEveryClaimWithItsEvidence()
    {
        SummaryDocument summary = SummaryWith("Ship the beta on Friday");

        string markdown = SummaryExporter.Render(summary, SummaryExportFormat.Markdown, title: "Quarterly planning");

        Assert.Contains("# Quarterly planning", markdown, StringComparison.Ordinal);
        Assert.Contains("## Decisions", markdown, StringComparison.Ordinal);
        Assert.Contains("## Action items", markdown, StringComparison.Ordinal);
        Assert.Contains("Ship the beta on Friday", markdown, StringComparison.Ordinal);

        // Provenance, so a reader can check the claims rather than take them.
        Assert.Contains("Summary version 1", markdown, StringComparison.Ordinal);
        Assert.Contains("from transcript version 1", markdown, StringComparison.Ordinal);
        Assert.Contains("gemma-4-12b", markdown, StringComparison.Ordinal);

        // Timestamps travel with the claim.
        Assert.Contains("00:00:00", markdown, StringComparison.Ordinal);

        // An unassigned action stays visibly unassigned in a document somebody pastes into email.
        Assert.Contains("Owner: unknown", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownEscapesWhatWouldOtherwiseBecomeFormatting()
    {
        SummaryDocument summary = SummaryWith("Reduce *scope* by 50% [see plan](nowhere) # not a heading");

        string markdown = SummaryExporter.Render(summary, SummaryExportFormat.Markdown);

        // A transcript can contain anything somebody said. Left raw, an asterisk turns a decision
        // italic and the exported document says something the summary did not.
        Assert.Contains(@"\*scope\*", markdown, StringComparison.Ordinal);
        Assert.Contains(@"\[see plan\]", markdown, StringComparison.Ordinal);

        // A hash mid-sentence is a hash. Escaping it everywhere would also mangle every model id
        // and decimal in the document for no benefit.
        Assert.Contains("# not a heading", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\# not a heading", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownEscapesStructureOnlyWhereItWouldBeStructure()
    {
        // At the start of an item these begin a heading, a list or a quote, so they are escaped.
        Assert.Equal(@"\# not a heading", SummaryExporter.Escape("# not a heading"));
        Assert.Equal(@"\- not a bullet", SummaryExporter.Escape("- not a bullet"));

        // Anywhere else they are ordinary punctuation and are left alone.
        Assert.Equal("gemma-4-12b at 99.5%", SummaryExporter.Escape("gemma-4-12b at 99.5%"));
        Assert.Equal("issue #42 was closed", SummaryExporter.Escape("issue #42 was closed"));
    }

    [Fact]
    public void MarkdownKeepsUnicodeIntactAndFlattensNewlines()
    {
        Assert.Equal("四半期の計画", SummaryExporter.Escape("四半期の計画"));
        Assert.DoesNotContain('\n', SummaryExporter.Escape("first line\nsecond line"));
    }

    [Fact]
    public void ExportsAreDeterministic()
    {
        SummaryDocument summary = SummaryWith("Ship the beta on Friday");

        Assert.Equal(
            SummaryExporter.Render(summary, SummaryExportFormat.Markdown),
            SummaryExporter.Render(summary, SummaryExportFormat.Markdown));
    }

    [Fact]
    public void EveryFormatWritesAndTheCanonicalJsonIsCopiedByte()
    {
        SummaryWith("Ship the beta on Friday");
        MeetingViewModel meeting = Open("01JA");

        foreach (SummaryExportFormat format in (SummaryExportFormat[])
                 [SummaryExportFormat.Json, SummaryExportFormat.Text, SummaryExportFormat.Markdown])
        {
            string path = Destination("summary" + SummaryExporter.Extension(format));
            ExportResult result = meeting.ExportSummary(format, path);

            Assert.True(result.Succeeded, result.Message);
            Assert.True(File.Exists(path));
        }

        // Canonical JSON is copied, never re-serialised: re-rendering could change key order and
        // the file would stop hashing to the digest the revision was activated under.
        Assert.Equal(
            File.ReadAllBytes(_fixture.Summaries.PathFor("01JA", 1)),
            File.ReadAllBytes(Destination("summary.json")));

        foreach (TranscriptExportFormat format in (TranscriptExportFormat[])
                 [TranscriptExportFormat.Json, TranscriptExportFormat.Text, TranscriptExportFormat.Srt, TranscriptExportFormat.Vtt])
        {
            string path = Destination("transcript" + TranscriptExporter.Extension(format));
            Assert.True(meeting.ExportTranscript(format, path).Succeeded);
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public void AnExistingFileIsNeverReplacedWithoutBeingAsked()
    {
        SummaryWith("Ship the beta on Friday");
        MeetingViewModel meeting = Open("01JA");

        string path = Destination("summary.md");
        File.WriteAllText(path, "something the user already had");

        ExportResult refused = meeting.ExportSummary(SummaryExportFormat.Markdown, path);

        Assert.False(refused.Succeeded);
        Assert.Equal("exists", refused.Code);
        Assert.Equal("something the user already had", File.ReadAllText(path));

        Assert.True(meeting.ExportSummary(SummaryExportFormat.Markdown, path, overwrite: true).Succeeded);
        Assert.Contains("Quarterly planning", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedExportLeavesTheCanonicalRevisionAlone()
    {
        SummaryWith("Ship the beta on Friday");
        MeetingViewModel meeting = Open("01JA");

        byte[] before = File.ReadAllBytes(_fixture.Summaries.PathFor("01JA", 1));

        // A directory where the file should go: the write cannot succeed.
        string path = Destination("blocked.md");
        Directory.CreateDirectory(path);

        ExportResult result = meeting.ExportSummary(SummaryExportFormat.Markdown, path, overwrite: true);

        Assert.False(result.Succeeded);
        Assert.Equal(before, File.ReadAllBytes(_fixture.Summaries.PathFor("01JA", 1)));

        // And no half-written neighbour left behind.
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ExportPresentationUsesTheSpeakerAlias()
    {
        SummaryWith("Ship the beta on Friday");
        _fixture.Aliases.Rename("01JA", TranscriptSpeakers.RemoteId, "Priya");

        MeetingViewModel meeting = Open("01JA");
        string path = Destination("aliased.md");

        Assert.True(meeting.ExportSummary(SummaryExportFormat.Markdown, path).Succeeded);

        string markdown = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains("Priya", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("00:00:10 Remote", markdown, StringComparison.Ordinal);
    }

    // -- file names ---------------------------------------------------------------------------------

    [Fact]
    public void FileNamesAreSafeWithoutBecomingUnrecognisable()
    {
        Assert.Equal("Q3-planning-review", ExportNaming.Sanitize("Q3/planning:review"));

        // Unicode is kept. Reducing a Japanese title to underscores would leave a file its owner
        // cannot find.
        Assert.Equal("四半期の計画", ExportNaming.Sanitize("四半期の計画"));

        // Windows refuses these names whatever the extension follows.
        Assert.Equal("_CON", ExportNaming.Sanitize("CON"));
        Assert.Equal("_nul", ExportNaming.Sanitize("nul"));

        // And silently drops trailing dots and spaces, which resolves to a different file.
        Assert.Equal("meeting", ExportNaming.Sanitize("meeting.  "));

        Assert.Equal("meeting", ExportNaming.Sanitize("   "));
        Assert.Equal("meeting", ExportNaming.Sanitize(null));
        Assert.True(ExportNaming.Sanitize(new string('x', 400)).Length <= 120);
    }

    [Fact]
    public void ASuggestedNameIsSafeAndNamesItsRevision()
    {
        SummaryDocument summary = SummaryWith("Ship the beta on Friday");

        string name = SummaryExporter.SuggestFileName(summary, SummaryExportFormat.Markdown);

        Assert.EndsWith("-summary-v1.md", name, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetInvalidFileNameChars(), name.Contains);
    }
}
