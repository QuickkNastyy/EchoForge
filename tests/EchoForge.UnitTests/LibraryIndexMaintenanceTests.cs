using EchoForge.Contracts.Library;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Summaries;
using EchoForge.Infrastructure.Library;

namespace EchoForge.UnitTests;

/// <summary>
/// Keeping the index in step with the folders, without ever being able to affect them.
///
/// <para>
/// The rule these tests exist to hold is one sentence: <b>a failed index update must never undo a
/// canonical operation.</b> A transcript that activated, activated — and if re-indexing it then
/// fails, the only consequence permitted is that search is briefly out of date and a rebuild fixes
/// it. Everything else here follows from that: updates are fire-and-forget, failures are recorded
/// rather than thrown, and a full rebuild is always the way back.
/// </para>
/// </summary>
public sealed class LibraryIndexMaintenanceTests : IDisposable
{
    private readonly LibraryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<SqliteLibraryIndex> IndexedAsync()
    {
        SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();
        return index;
    }

    private static LibraryEntry Entry(SqliteLibraryIndex index, string sessionId) =>
        index.Meetings().Single(m => m.SessionId == sessionId);

    // -- catching up ---------------------------------------------------------------------------

    [Fact]
    public async Task ActivatingATranscriptShowsUpWithoutAnybodyPressingRefresh()
    {
        string session = _fixture.AddSession("01JACTIVATE", "Planning");

        using SqliteLibraryIndex index = await IndexedAsync();
        using LibraryIndexMaintainer maintenance = new(index);

        Assert.False(Entry(index, session).HasTranscript);

        _fixture.AddTranscript(session, ("microphone", "We should look at the throughput numbers."));
        await maintenance.UpdateNowAsync(session);

        Assert.True(Entry(index, session).HasTranscript);
        Assert.NotEmpty(index.Search(new SearchQuery { Text = "throughput" }).Hits);
    }

    [Fact]
    public async Task SelectingADifferentTranscriptVersionIsReflected()
    {
        string session = _fixture.AddSession("01JSELECT");
        _fixture.AddTranscript(session, ("microphone", "The first attempt mentioned marmosets."));
        _fixture.AddTranscript(session, ("microphone", "The second attempt mentioned capybaras."));

        using SqliteLibraryIndex index = await IndexedAsync();
        using LibraryIndexMaintainer maintenance = new(index);

        Assert.Equal(2, Entry(index, session).SelectedTranscriptRevision);
        Assert.NotEmpty(index.Search(new SearchQuery { Text = "capybaras" }).Hits);

        _fixture.Transcripts.SelectRevision(session, 1, DateTimeOffset.UtcNow);
        await maintenance.UpdateNowAsync(session);

        Assert.Equal(1, Entry(index, session).SelectedTranscriptRevision);
        Assert.NotEmpty(index.Search(new SearchQuery { Text = "marmosets" }).Hits);
        Assert.Empty(index.Search(new SearchQuery { Text = "capybaras" }).Hits);
    }

    [Fact]
    public async Task ActivatingAndSelectingASummaryIsReflected()
    {
        string session = _fixture.AddSession("01JSUMMARY");
        _fixture.AddTranscript(session, ("microphone", "We decided to postpone the migration."));

        using SqliteLibraryIndex index = await IndexedAsync();
        using LibraryIndexMaintainer maintenance = new(index);

        Assert.False(Entry(index, session).HasSummary);

        _fixture.AddSummary(session, 1, decisions: [("Postpone the migration", "segment-000001")]);
        await maintenance.UpdateNowAsync(session);

        Assert.True(Entry(index, session).HasSummary);
        Assert.NotEmpty(index.Search(new SearchQuery { Text = "postpone migration" }).Hits);

        _fixture.AddSummary(session, 1, decisions: [("Proceed with the migration", "segment-000001")]);
        _fixture.Summaries.SelectRevision(session, 2, DateTimeOffset.UtcNow);
        await maintenance.UpdateNowAsync(session);

        Assert.Equal(2, Entry(index, session).SelectedSummaryRevision);
        Assert.NotEmpty(index.Search(new SearchQuery { Text = "proceed migration" }).Hits);
    }

    [Fact]
    public async Task RenamingASpeakerIsReflectedInSearchResults()
    {
        string session = _fixture.AddSession("01JALIAS");
        _fixture.AddTranscript(session, ("system", "I will send the contract over."));

        using SqliteLibraryIndex index = await IndexedAsync();
        using LibraryIndexMaintainer maintenance = new(index);

        Assert.Equal("Remote", index.Search(new SearchQuery { Text = "contract" }).Hits[0].SpeakerName);

        _fixture.Aliases.Rename(session, EchoForge.Contracts.Transcripts.TranscriptSpeakers.RemoteId, "Priya");
        await maintenance.UpdateNowAsync(session);

        // Presentation only: the segment ID a citation points at has not moved.
        SearchHit hit = index.Search(new SearchQuery { Text = "contract" }).Hits[0];
        Assert.Equal("Priya", hit.SpeakerName);
        Assert.Equal("segment-000001", hit.SegmentId);
    }

    [Fact]
    public async Task AMeetingWhoseFolderHasGoneIsRemovedFromTheIndex()
    {
        string session = _fixture.AddSession("01JVANISH");
        _fixture.AddTranscript(session, ("microphone", "Something about okapis."));

        using SqliteLibraryIndex index = await IndexedAsync();
        using LibraryIndexMaintainer maintenance = new(index);

        Assert.Single(index.Meetings());

        Directory.Delete(_fixture.Sessions.Resolve(session).Root, recursive: true);
        await maintenance.UpdateNowAsync(session);

        Assert.Empty(index.Meetings());
        Assert.Empty(index.Search(new SearchQuery { Text = "okapis" }).Hits);
    }

    // -- coalescing ----------------------------------------------------------------------------

    [Fact]
    public async Task ManyNotificationsAboutOneMeetingBecomeFewPasses()
    {
        string session = _fixture.AddSession("01JBURST");
        _fixture.AddTranscript(session, ("microphone", "A burst of changes about narwhals."));

        using SqliteLibraryIndex index = await IndexedAsync();
        using LibraryIndexMaintainer maintenance = new(index);

        int passes = 0;
        maintenance.Updated += (_, _) => Interlocked.Increment(ref passes);

        // Activating, selecting and renaming in quick succession is three notifications about one
        // session. Three overlapping re-reads of the same folder would be both wasteful and a race.
        for (int i = 0; i < 50; i++)
        {
            maintenance.Invalidate(session);
        }

        await maintenance.WaitAsync(session);

        Assert.False(maintenance.IsBusy);
        Assert.InRange(passes, 1, 50);

        // However many were folded together, the last pass read the final state.
        Assert.True(Entry(index, session).HasTranscript);
        Assert.NotEmpty(index.Search(new SearchQuery { Text = "narwhals" }).Hits);
    }

    [Fact]
    public async Task ConcurrentNotificationsFromSeveralThreadsSettleCleanly()
    {
        string[] sessions =
        [
            _fixture.AddSession("01JA"),
            _fixture.AddSession("01JB"),
            _fixture.AddSession("01JC"),
        ];

        foreach (string session in sessions)
        {
            _fixture.AddTranscript(session, ("microphone", "Every meeting mentions echidnas."));
        }

        using SqliteLibraryIndex index = await IndexedAsync();
        using LibraryIndexMaintainer maintenance = new(index);

        await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(() =>
            maintenance.Invalidate(sessions[i % sessions.Length]))));

        await maintenance.WaitAsync();

        Assert.Equal(3, index.Meetings().Count);
        Assert.Equal(3, index.Search(new SearchQuery { Text = "echidnas" }).Hits.Count);
        Assert.Empty(maintenance.NeedingRetry);
    }

    // -- failure -------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedIndexUpdateLeavesTheCanonicalChangeIntact()
    {
        string session = _fixture.AddSession("01JINDEXFAIL");

        // An index whose file cannot be opened, because a directory is sitting where it should be.
        string blocked = Path.Combine(_fixture.Root, "blocked.db");
        Directory.CreateDirectory(blocked);

        using SqliteLibraryIndex broken = new(blocked, _fixture.Projection);
        using LibraryIndexMaintainer maintenance = new(broken);

        // The canonical operation: a transcript is activated and is durable on disk.
        _fixture.AddTranscript(session, ("microphone", "This transcript exists regardless."));

        bool ok = await maintenance.UpdateNowAsync(session);

        Assert.False(ok);
        Assert.Contains(session, maintenance.NeedingRetry);

        // The transcript did not care. It is activated, selected, readable, and unchanged.
        TranscriptionState state = _fixture.Transcripts.Read(session);
        Assert.Equal(1, state.SelectedRevision);
        Assert.NotNull(_fixture.Transcripts.ReadTranscript(session, 1));
        Assert.True(_fixture.Projection.Build(session)!.Entry.HasTranscript);
    }

    [Fact]
    public async Task NotifyingADeadIndexNeverThrowsAtTheCaller()
    {
        string session = _fixture.AddSession("01JDISPOSED");

        SqliteLibraryIndex index = await IndexedAsync();
        using LibraryIndexMaintainer maintenance = new(index);

        index.Dispose();

        // The operation that raised this has already happened. Throwing here would let a cache
        // failure surface as though the canonical change had gone wrong.
        maintenance.Invalidate(session);
        bool ok = await maintenance.UpdateNowAsync(session);

        Assert.False(ok);
        Assert.Contains(session, maintenance.NeedingRetry);
    }

    [Fact]
    public async Task AFullRebuildRepairsWhateverTheAutomaticUpdatesMissed()
    {
        string session = _fixture.AddSession("01JREPAIR", "Missed");

        using SqliteLibraryIndex index = await IndexedAsync();

        // Changes nobody told the index about: exactly what a failed update leaves behind.
        _fixture.AddTranscript(session, ("microphone", "Nobody indexed this mention of tapirs."));
        _fixture.AddSummary(session, 1, decisions: [("Tapirs are agreed", "segment-000001")]);

        Assert.False(Entry(index, session).HasTranscript);
        Assert.Empty(index.Search(new SearchQuery { Text = "tapirs" }).Hits);

        IndexHealth health = await index.RebuildAsync();

        Assert.True(health.Usable);
        Assert.True(Entry(index, session).HasTranscript);
        Assert.True(Entry(index, session).HasSummary);
        Assert.NotEmpty(index.Search(new SearchQuery { Text = "tapirs" }).Hits);
    }

    [Fact]
    public async Task RecoveringFromAFailureIsJustTheNextSuccessfulUpdate()
    {
        string session = _fixture.AddSession("01JRETRY");

        SqliteLibraryIndex index = await IndexedAsync();
        using LibraryIndexMaintainer maintenance = new(index);

        index.Dispose();
        Assert.False(await maintenance.UpdateNowAsync(session));
        Assert.Contains(session, maintenance.NeedingRetry);

        using SqliteLibraryIndex reopened = await IndexedAsync();
        using LibraryIndexMaintainer second = new(reopened);

        _fixture.AddTranscript(session, ("microphone", "A mention of dugongs, indexed this time."));

        Assert.True(await second.UpdateNowAsync(session));
        Assert.Empty(second.NeedingRetry);
        Assert.NotEmpty(reopened.Search(new SearchQuery { Text = "dugongs" }).Hits);
    }
}
