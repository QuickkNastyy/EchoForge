using EchoForge.Contracts.Library;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Library;
using EchoForge.Infrastructure.Library;

namespace EchoForge.UnitTests;

/// <summary>
/// Finding things, following citations, and renaming people without rewriting anything.
/// </summary>
public sealed class LibrarySearchTests : IDisposable
{
    private readonly LibraryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<SqliteLibraryIndex> IndexedAsync()
    {
        SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();
        return index;
    }

    // -- search ------------------------------------------------------------------------------------

    [Fact]
    public async Task AWordIsFoundInATranscript()
    {
        _fixture.AddSession("01JA", "Planning");
        _fixture.AddTranscript("01JA",
            ("microphone", "We should ship the beta on Friday"),
            ("system", "Support would rather we waited"));

        using SqliteLibraryIndex index = await IndexedAsync();

        SearchHit hit = Assert.Single(index.Search(new SearchQuery { Text = "beta" }).Hits);

        Assert.Equal(SearchHitKind.TranscriptSegment, hit.Kind);
        Assert.Equal("01JA", hit.SessionId);
        Assert.Equal(1, hit.TranscriptRevision);
        Assert.Equal("segment-000001", hit.SegmentId);
        Assert.Equal("You", hit.SpeakerName);
    }

    [Fact]
    public async Task EveryWordMustAppear()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA",
            ("microphone", "The vendor contract is late"),
            ("system", "The support rota is late"));

        using SqliteLibraryIndex index = await IndexedAsync();

        // Two words means both, not either. Otherwise the meeting somebody wanted is buried
        // under everything that mentioned one common word.
        Assert.Single(index.Search(new SearchQuery { Text = "vendor late" }).Hits);
        Assert.Equal(2, index.Search(new SearchQuery { Text = "late" }).Hits.Count);
    }

    [Fact]
    public async Task AQuotedPhraseMatchesOnlyWordsInThatOrder()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA",
            ("microphone", "We will ship the beta on Friday"),
            ("system", "Friday is when we ship the beta, apparently"));

        using SqliteLibraryIndex index = await IndexedAsync();

        Assert.Equal(2, index.Search(new SearchQuery { Text = "ship beta" }).Hits.Count);

        SearchHit phrase = Assert.Single(index.Search(new SearchQuery { Text = "\"ship the beta on Friday\"" }).Hits);
        Assert.Equal("segment-000001", phrase.SegmentId);
    }

    [Fact]
    public async Task SummaryTextIsSearchableAndSaysWhichPartItCameFrom()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "We will ship the beta on Friday"));
        _fixture.AddSummary(
            "01JA", 1,
            decisions: [("Ship the beta on Friday", "segment-000001")],
            actions: [("Prepare the release notes", "segment-000001", "Alex")],
            overview: "A discussion about scheduling the release.");

        using SqliteLibraryIndex index = await IndexedAsync();

        Assert.Equal(SearchHitKind.SummaryDecision,
            Assert.Single(index.Search(new SearchQuery { Text = "\"Ship the beta on Friday\"", Kinds = [SearchHitKind.SummaryDecision] }).Hits).Kind);

        SearchHit action = Assert.Single(index.Search(new SearchQuery { Text = "release notes" }).Hits);
        Assert.Equal(SearchHitKind.SummaryAction, action.Kind);
        Assert.Equal(1, action.SummaryRevision);

        Assert.Equal(SearchHitKind.SummaryOverview,
            Assert.Single(index.Search(new SearchQuery { Text = "scheduling" }).Hits).Kind);
    }

    [Fact]
    public async Task SearchSpansEveryMeetingAndCanBeNarrowedToOne()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "The roadmap needs revising"));
        _fixture.AddSession("01JB");
        _fixture.AddTranscript("01JB", ("microphone", "The roadmap is fine as it is"));

        using SqliteLibraryIndex index = await IndexedAsync();

        Assert.Equal(2, index.Search(new SearchQuery { Text = "roadmap" }).Hits.Count);
        Assert.Single(index.Search(new SearchQuery { Text = "roadmap", SessionId = "01JB" }).Hits);
    }

    [Fact]
    public async Task ResultsCarryTheRangesThatMatched()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "The migration guide is unassigned"));

        using SqliteLibraryIndex index = await IndexedAsync();

        SearchHit hit = Assert.Single(index.Search(new SearchQuery { Text = "migration" }).Hits);
        HighlightRange range = Assert.Single(hit.Highlights);

        // The offsets index the returned text, so a caller can slice without re-searching and
        // without disagreeing with the tokenizer about where a word starts.
        Assert.Equal("migration", hit.Text.Substring(range.Start, range.Length));
        Assert.Equal("The migration guide is unassigned", hit.Text);
    }

    [Fact]
    public async Task OrderingIsTheSameEveryTimeAndAcrossARebuild()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA",
            ("microphone", "Budget review"),
            ("system", "Budget review"),
            ("microphone", "Budget review"));

        using SqliteLibraryIndex index = await IndexedAsync();

        string[] first = [.. index.Search(new SearchQuery { Text = "budget" }).Hits.Select(h => h.HitId)];
        string[] again = [.. index.Search(new SearchQuery { Text = "budget" }).Hits.Select(h => h.HitId)];

        Assert.Equal(first, again);

        await index.RebuildAsync();

        // bm25 ties are ordinary - identical text scores identically - so without a total order
        // the same search would shuffle its rows on every rebuild.
        string[] afterRebuild = [.. index.Search(new SearchQuery { Text = "budget" }).Hits.Select(h => h.HitId)];
        Assert.Equal(first, afterRebuild);
    }

    [Fact]
    public async Task UnicodeIsSearchableAndComesBackIntact()
    {
        _fixture.AddSession("01JA", "四半期の計画");
        _fixture.AddTranscript("01JA",
            ("microphone", "来週までに設計をまとめます"),
            ("system", "Se acordó revisar el presupuesto mañana"));

        using SqliteLibraryIndex index = await IndexedAsync();

        Assert.Single(index.Search(new SearchQuery { Text = "設計" }).Hits);
        Assert.Equal("四半期の計画", Assert.Single(index.Meetings()).Title);

        // remove_diacritics means an accented word is findable without the accent, which is what
        // somebody typing quickly on an English keyboard will do.
        Assert.Single(index.Search(new SearchQuery { Text = "acordo" }).Hits);
    }

    [Fact]
    public async Task PunctuationInTheSearchBoxIsNotAQueryLanguage()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "Revenue was up 40% in Q3 (revised)"));

        using SqliteLibraryIndex index = await IndexedAsync();

        // A search box is not a query console. None of these may be a syntax error.
        Assert.Single(index.Search(new SearchQuery { Text = "Q3 (revised)" }).Hits);
        Assert.Single(index.Search(new SearchQuery { Text = "revenue: up" }).Hits);
        Assert.Empty(index.Search(new SearchQuery { Text = "NOT revenue" }).Hits);
    }

    [Fact]
    public async Task AnEmptySearchAsksForNothingRatherThanEverything()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "Anything at all"));

        using SqliteLibraryIndex index = await IndexedAsync();

        Assert.Empty(index.Search(new SearchQuery { Text = "   " }).Hits);
        Assert.Empty(index.Search(new SearchQuery { Text = "\"\"" }).Hits);
    }

    [Fact]
    public async Task SearchStillWorksAfterTheIndexIsRebuilt()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "The vendor sandbox is blocked"));

        using SqliteLibraryIndex index = await IndexedAsync();
        Assert.Single(index.Search(new SearchQuery { Text = "sandbox" }).Hits);

        index.Discard();
        await index.EnsureReadyAsync();

        Assert.Single(index.Search(new SearchQuery { Text = "sandbox" }).Hits);
    }

    // -- evidence ------------------------------------------------------------------------------------

    [Fact]
    public void EvidenceResolvesAgainstTheRevisionItNames()
    {
        _fixture.AddSession("01JA");
        TranscriptDocument transcript = _fixture.AddTranscript("01JA",
            ("microphone", "We will ship the beta on Friday"),
            ("system", "Alex will prepare the release notes"));

        SummaryDocument summary = _fixture.AddSummary("01JA", 1, decisions: [("Ship on Friday", "segment-000001")]);
        SummaryEvidence citation = summary.Decisions[0].Evidence[0];

        EvidenceLocation location = EvidenceResolver.Resolve("01JA", citation, transcript);

        Assert.True(location.IsResolved);
        Assert.Equal(1, location.TranscriptRevision);
        Assert.Equal("segment-000001", location.SegmentId);
        Assert.Equal(0.0, location.StartSeconds);
        Assert.Equal("microphone", location.SourceTrack);
        Assert.Equal("You", location.SpeakerName);
        Assert.Equal("We will ship the beta on Friday", location.Text);
        Assert.Equal(1, location.Epoch);
    }

    [Fact]
    public void ACitationIsNeverRebasedOntoADifferentRevision()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "The original sentence"));
        SummaryDocument summary = _fixture.AddSummary("01JA", 1, decisions: [("A decision", "segment-000001")]);

        // Reprocessing produces a revision whose segment-000001 is a different piece of speech.
        TranscriptDocument reprocessed = _fixture.AddTranscript("01JA", ("microphone", "A completely different sentence"));

        EvidenceLocation location = EvidenceResolver.Resolve("01JA", summary.Decisions[0].Evidence[0], reprocessed);

        // Following the ID into the newer transcript would show the reader a sentence the
        // summary never saw, and it would look authoritative.
        Assert.False(location.IsResolved);
        Assert.Null(location.Text);
        Assert.Equal(1, location.TranscriptRevision);
        Assert.Contains("version 1", location.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRevisionFallsBackToTheStoredTimeAndSaysSo()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA",
            ("microphone", "First line"),
            ("system", "Second line"));

        SummaryDocument summary = _fixture.AddSummary("01JA", 1, decisions: [("A decision", "segment-000002")]);
        SummaryEvidence citation = summary.Decisions[0].Evidence[0];

        EvidenceLocation location = EvidenceResolver.Resolve("01JA", citation, transcript: null);

        Assert.False(location.IsResolved);
        Assert.Equal(citation.StartSeconds, location.StartSeconds);
        Assert.Equal("system", location.SourceTrack);
        Assert.NotNull(location.Explanation);

        // A seek built from a fallback time must not present itself as exact.
        Assert.True(location.ToPlaybackRequest().IsApproximate);
    }

    [Fact]
    public void ASegmentThatVanishedFromItsOwnRevisionIsDegradedNotGuessed()
    {
        _fixture.AddSession("01JA");
        TranscriptDocument transcript = _fixture.AddTranscript("01JA", ("microphone", "Only one line here"));

        SummaryEvidence dangling = new()
        {
            TranscriptRevision = 1,
            SegmentId = "segment-000099",
            SourceTrack = "system",
            StartSeconds = 42,
            EndSeconds = 48,
            DisplayTimestamp = "00:00:42",
        };

        EvidenceLocation location = EvidenceResolver.Resolve("01JA", dangling, transcript);

        Assert.Equal(EvidenceResolution.Degraded, location.Resolution);
        Assert.Equal(42, location.StartSeconds);
    }

    [Fact]
    public void AResolvedCitationCarriesEnoughForPlaybackToSeekLater()
    {
        _fixture.AddSession("01JA");
        TranscriptDocument transcript = _fixture.AddTranscript("01JA",
            ("microphone", "First"),
            ("system", "Second"));

        SummaryDocument summary = _fixture.AddSummary("01JA", 1, decisions: [("A decision", "segment-000002")]);

        PlaybackRequest request = EvidenceResolver
            .Resolve("01JA", summary.Decisions[0].Evidence[0], transcript)
            .ToPlaybackRequest();

        Assert.Equal("01JA", request.SessionId);
        Assert.Equal(10.0, request.StartSeconds);
        Assert.Equal("system", request.SourceTrack);
        Assert.Equal(1, request.Epoch);
        Assert.False(request.IsApproximate);
    }

    // -- speaker aliases ------------------------------------------------------------------------------

    [Fact]
    public void RenamingARemoteSpeakerDoesNotTouchTheTranscript()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("system", "Something the other person said"));

        string path = _fixture.Transcripts.RevisionPath("01JA", 1);
        byte[] before = File.ReadAllBytes(path);

        Assert.True(_fixture.Aliases.Rename("01JA", TranscriptSpeakers.RemoteId, "Priya"));

        // The revision is immutable and is still the bytes its digest was taken over.
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("Priya", _fixture.Aliases.Read("01JA").Present(TranscriptSpeakers.RemoteId, "Remote"));
    }

    [Fact]
    public void AnAliasSurvivesARestart()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("system", "Anything"));
        _fixture.Aliases.Rename("01JA", TranscriptSpeakers.RemoteId, "Priya");

        LibraryProjection restarted = _fixture.RestartedProjection();

        Assert.Equal("Priya", restarted.Build("01JA")!.Entry.Aliases.Present(TranscriptSpeakers.RemoteId, "Remote"));
    }

    [Fact]
    public void RenamingIsReversibleBecauseNothingWasOverwritten()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("system", "Anything"));

        _fixture.Aliases.Rename("01JA", TranscriptSpeakers.RemoteId, "Priya");
        _fixture.Aliases.Rename("01JA", TranscriptSpeakers.RemoteId, null);

        Assert.True(_fixture.Aliases.Read("01JA").IsEmpty);
        Assert.Equal("Remote", _fixture.Aliases.Read("01JA").Present(TranscriptSpeakers.RemoteId, "Remote"));
    }

    [Fact]
    public void YouCannotBeRenamed()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "Something I said"));

        _fixture.Aliases.Rename("01JA", TranscriptSpeakers.YouId, "Somebody Else");

        // Microphone attribution is derived from the device, not inferred. It is the one speaker
        // fact EchoForge is certain about, and it stays that way.
        Assert.True(_fixture.Aliases.Read("01JA").IsEmpty);
        Assert.False(SpeakerPresentation.IsAliasable(TranscriptSpeakers.YouId));
        Assert.Equal("You", _fixture.Aliases.Read("01JA").Present(TranscriptSpeakers.YouId, "You"));
    }

    [Fact]
    public void AnAliasForYouIsRefusedEvenIfItSomehowReachesTheFile()
    {
        SpeakerAliases smuggled = new()
        {
            BySpeakerId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TranscriptSpeakers.YouId] = "Somebody Else",
                [TranscriptSpeakers.RemoteId] = "Priya",
            },
        };

        SpeakerAliases sanitized = SpeakerPresentation.Sanitize(smuggled);

        Assert.False(sanitized.BySpeakerId.ContainsKey(TranscriptSpeakers.YouId));
        Assert.Equal("Priya", sanitized.BySpeakerId[TranscriptSpeakers.RemoteId]);
    }

    [Fact]
    public async Task SearchResultsShowTheAliasedName()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("system", "The vendor has not confirmed"));
        _fixture.Aliases.Rename("01JA", TranscriptSpeakers.RemoteId, "Priya");

        using SqliteLibraryIndex index = await IndexedAsync();

        SearchHit hit = Assert.Single(index.Search(new SearchQuery { Text = "vendor" }).Hits);

        Assert.Equal("Priya", hit.SpeakerName);

        // Identity is still the segment, which no alias can touch.
        Assert.Equal("segment-000001", hit.SegmentId);
    }

    [Fact]
    public void OnlyRemoteSpeakersAreOfferedForRenaming()
    {
        _fixture.AddSession("01JA");
        TranscriptDocument transcript = _fixture.AddTranscript("01JA", ("microphone", "Mine"), ("system", "Theirs"));

        IReadOnlyList<(string SpeakerId, string OriginalName, string DisplayName)> renameable =
            SpeakerPresentation.Renameable(transcript, SpeakerAliases.None);

        (string speakerId, string original, string display) = Assert.Single(renameable);

        Assert.Equal(TranscriptSpeakers.RemoteId, speakerId);
        Assert.Equal("Remote", original);
        Assert.Equal("Remote", display);
    }
}
