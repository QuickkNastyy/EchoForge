using EchoForge.Contracts.Library;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Transcripts;
using EchoForge.Infrastructure.Library;

namespace EchoForge.UnitTests;

/// <summary>
/// The meeting library index, and the one property everything else rests on: it is disposable.
///
/// <para>
/// The risk this phase introduces is that SQLite quietly becomes the source of truth — that some
/// piece of a meeting ends up only in the database, and deleting it loses data. These tests exist
/// to make that impossible to do accidentally: the database is deleted, corrupted, and written by
/// a wrong schema version, and after each the library is expected to be whole again.
/// </para>
/// </summary>
public sealed class LibraryIndexTests : IDisposable
{
    private readonly LibraryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // -- the projection is the authority --------------------------------------------------------

    [Fact]
    public async Task AnEmptyLibraryIsEmptyRatherThanBroken()
    {
        using SqliteLibraryIndex index = _fixture.NewIndex();

        IndexHealth health = await index.EnsureReadyAsync();

        Assert.True(health.Usable);
        Assert.Empty(index.Meetings());
        Assert.Empty(index.Search(new SearchQuery { Text = "anything" }).Hits);
    }

    [Fact]
    public async Task TheIndexIsBuiltFromWhatIsOnDisk()
    {
        _fixture.AddSession("01JA", "Planning");
        _fixture.AddTranscript("01JA", ("microphone", "We should ship the beta on Friday"));

        _fixture.AddSession("01JB", "Retro");

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        IReadOnlyList<LibraryEntry> meetings = index.Meetings();

        Assert.Equal(2, meetings.Count);
        Assert.Contains(meetings, m => m.SessionId == "01JA" && m.HasTranscript);
        Assert.Contains(meetings, m => m.SessionId == "01JB" && !m.HasTranscript);
    }

    [Fact]
    public async Task DeletingTheDatabaseLosesNothing()
    {
        _fixture.AddSession("01JA", "Planning");
        _fixture.AddTranscript("01JA", ("microphone", "The vendor contract needs signing"));

        using (SqliteLibraryIndex first = _fixture.NewIndex())
        {
            await first.EnsureReadyAsync();
            Assert.Single(first.Meetings());
        }

        File.Delete(_fixture.IndexPath);

        using SqliteLibraryIndex second = _fixture.NewIndex();
        IndexHealth health = await second.EnsureReadyAsync();

        Assert.True(health.Usable);
        Assert.True(health.Rebuilt);
        Assert.Single(second.Meetings());
        Assert.Single(second.Search(new SearchQuery { Text = "vendor" }).Hits);
    }

    [Fact]
    public async Task ACorruptDatabaseIsRebuiltRatherThanReported()
    {
        _fixture.AddSession("01JA", "Planning");
        _fixture.AddTranscript("01JA", ("microphone", "The migration guide is still unassigned"));

        using (SqliteLibraryIndex first = _fixture.NewIndex())
        {
            await first.EnsureReadyAsync();
        }

        // Not a database at all any more. A user should never have to understand this.
        File.WriteAllText(_fixture.IndexPath, "this is not a database, it is a text file");

        using SqliteLibraryIndex second = _fixture.NewIndex();
        IndexHealth health = await second.EnsureReadyAsync();

        Assert.True(health.Usable, health.Detail);
        Assert.True(health.Rebuilt, health.Detail);
        Assert.Single(second.Meetings());
        Assert.Single(second.Search(new SearchQuery { Text = "migration" }).Hits);
    }

    [Fact]
    public async Task AnIndexFromAnOlderSchemaIsThrownAwayRatherThanMigrated()
    {
        _fixture.AddSession("01JA", "Planning");
        _fixture.AddTranscript("01JA", ("microphone", "Budget review moved to next quarter"));

        using (SqliteLibraryIndex first = _fixture.NewIndex())
        {
            await first.EnsureReadyAsync();
        }

        // Pretend a previous build wrote it. There is deliberately no migration path.
        using (Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={_fixture.IndexPath}"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE index_meta SET version = 0 WHERE id = 1;";
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using SqliteLibraryIndex second = _fixture.NewIndex();
        IndexHealth health = await second.EnsureReadyAsync();

        Assert.True(health.Rebuilt);
        Assert.Single(second.Search(new SearchQuery { Text = "budget" }).Hits);
    }

    [Fact]
    public async Task TheFilesWinWhenTheDatabaseDisagrees()
    {
        _fixture.AddSession("01JA", "Planning");
        _fixture.AddTranscript("01JA", ("microphone", "The original wording"));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        // A second transcript revision lands. The database still describes the first.
        _fixture.AddTranscript("01JA", ("microphone", "The replacement wording"));

        Assert.Equal(1, index.Meetings()[0].SelectedTranscriptRevision);

        await index.UpdateAsync("01JA");

        // Re-reading the folder is what settles it, always.
        Assert.Equal(2, index.Meetings()[0].SelectedTranscriptRevision);
        Assert.Single(index.Search(new SearchQuery { Text = "replacement" }).Hits);
        Assert.Empty(index.Search(new SearchQuery { Text = "original" }).Hits);
    }

    [Fact]
    public async Task AnIncrementalUpdateLeavesOtherMeetingsAlone()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "Alpha discussion"));
        _fixture.AddSession("01JB");
        _fixture.AddTranscript("01JB", ("microphone", "Beta discussion"));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        await index.UpdateAsync("01JA");

        Assert.Equal(2, index.Meetings().Count);
        Assert.Single(index.Search(new SearchQuery { Text = "beta" }).Hits);
        Assert.Single(index.Search(new SearchQuery { Text = "alpha" }).Hits);
    }

    [Fact]
    public async Task ASessionWithNoSnapshotIsNotOfferedAsAMeeting()
    {
        // A folder that exists but holds nothing openable. Showing it as an empty row would be
        // a meeting the user cannot open and cannot explain.
        Directory.CreateDirectory(Path.Combine(_fixture.Root, "01JEMPTY"));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        Assert.DoesNotContain(index.Meetings(), m => m.SessionId == "01JEMPTY");
    }

    [Fact]
    public async Task ASessionThatNeverFinishedIsShownAndMarked()
    {
        _fixture.AddSession("01JA", "Interrupted", SessionState.Recording);

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        LibraryEntry entry = Assert.Single(index.Meetings());

        // Included, not hidden. A library that silently omits a broken meeting looks exactly
        // like one that lost it.
        Assert.True(entry.NeedsAttention);
        Assert.NotNull(entry.AttentionReason);
    }

    [Fact]
    public async Task ASelectedRevisionWhoseFileIsGoneIsFlagged()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "Something was said"));

        File.Delete(_fixture.Transcripts.RevisionPath("01JA", 1));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        LibraryEntry entry = Assert.Single(index.Meetings());

        Assert.True(entry.NeedsAttention);
        Assert.Contains("missing from disk", entry.AttentionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARebuildCanBeCancelledWithoutLeavingAPartialIndex()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "Anything"));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => index.RebuildAsync(null, null, cancelled.Token));

        // A half-built index would answer searches with a subset of the library and no way to
        // tell that is what it was doing.
        Assert.False(File.Exists(_fixture.IndexPath));
    }

    // -- revisions --------------------------------------------------------------------------------

    [Fact]
    public async Task EveryTranscriptAndSummaryRevisionIsListed()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "First pass"));
        _fixture.AddSummary("01JA", 1, decisions: [("Ship it", "segment-000001")]);
        _fixture.AddTranscript("01JA", ("microphone", "Second pass"));
        _fixture.AddSummary("01JA", 2, decisions: [("Ship it later", "segment-000001")]);

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        LibraryEntry entry = Assert.Single(index.Meetings());

        Assert.Equal([1, 2], entry.TranscriptRevisions);
        Assert.Equal([1, 2], entry.SummaryRevisions);
        Assert.Equal(2, entry.SelectedTranscriptRevision);
        Assert.Equal(2, entry.SelectedSummaryRevision);
    }

    [Fact]
    public async Task ASummaryFromAnOlderTranscriptIsStaleAndStillThere()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "We will ship on Friday"));
        _fixture.AddSummary("01JA", 1, decisions: [("Ship on Friday", "segment-000001")]);

        Assert.False(_fixture.Entry("01JA").SummaryIsStale);

        // Reprocessing the audio produces a new transcript. The summary is untouched.
        _fixture.AddTranscript("01JA", ("microphone", "We will ship on Friday"));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        LibraryEntry entry = Assert.Single(index.Meetings());

        Assert.True(entry.SummaryIsStale);
        Assert.Equal(1, entry.SummarySourceTranscriptRevision);
        Assert.Equal(2, entry.SelectedTranscriptRevision);

        // Preserved, not rewritten and not deleted.
        Assert.NotNull(_fixture.Summaries.ReadSummary("01JA", 1));
    }

    [Fact]
    public async Task SelectingAnOlderTranscriptDoesNotMutateAnySummary()
    {
        _fixture.AddSession("01JA");
        _fixture.AddTranscript("01JA", ("microphone", "Original"));
        _fixture.AddSummary("01JA", 1, decisions: [("A decision", "segment-000001")]);
        _fixture.AddTranscript("01JA", ("microphone", "Reprocessed"));

        byte[] before = File.ReadAllBytes(_fixture.Summaries.PathFor("01JA", 1));

        Assert.True(_fixture.Transcripts.SelectRevision("01JA", 1, DateTimeOffset.UtcNow));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        Assert.False(index.Meetings()[0].SummaryIsStale);
        Assert.Equal(before, File.ReadAllBytes(_fixture.Summaries.PathFor("01JA", 1)));
    }

    [Fact]
    public async Task SurvivesARestartWithTheSameAnswers()
    {
        _fixture.AddSession("01JA", "Quarterly planning");
        _fixture.AddTranscript("01JA", ("microphone", "Revenue targets were discussed"));
        _fixture.AddSummary("01JA", 1, decisions: [("Raise the target", "segment-000001")]);

        using (SqliteLibraryIndex first = _fixture.NewIndex())
        {
            await first.EnsureReadyAsync();
        }

        SqliteLibraryIndex restarted = new(_fixture.IndexPath, _fixture.RestartedProjection());
        using (restarted)
        {
            IndexHealth health = await restarted.EnsureReadyAsync();

            // Nothing to rebuild: the schema matched and the file was sound.
            Assert.False(health.Rebuilt);
            Assert.Single(restarted.Meetings());
            Assert.Single(restarted.Search(new SearchQuery { Text = "revenue" }).Hits);
        }
    }
}
