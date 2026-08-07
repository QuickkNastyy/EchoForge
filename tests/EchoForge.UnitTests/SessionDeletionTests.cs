using EchoForge.Contracts.Sessions;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// A Recycle Bin that can be watched, and told to fail.
///
/// <para>
/// The tests must never call the real shell. Deleting arbitrary files to prove a delete works is
/// how a test suite ends up in somebody's Recycle Bin, and the interesting behaviour — the
/// refusals, the ordering, the race — is all on this side of the shell anyway.
/// </para>
/// </summary>
public sealed class FakeRecycleBin(string bin) : IRecycleBin
{
    public bool Available { get; set; } = true;

    public string? FailWith { get; set; }

    public List<string> Recycled { get; } = [];

    public bool IsAvailableFor(string path) => Available;

    public RecycleOutcome Recycle(string directoryPath)
    {
        if (FailWith is { } code)
        {
            return RecycleOutcome.Fail(code, "the fake refused");
        }

        // Moved rather than deleted, which is what recycling is: still there, not where it was.
        string destination = Path.Combine(bin, Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(bin);
        Directory.Move(directoryPath, destination);

        Recycled.Add(directoryPath);
        return RecycleOutcome.Ok;
    }
}

/// <summary>An authority a test can change between the confirmation and the deletion.</summary>
public sealed class SwitchableAuthority : ISessionDeletionAuthority
{
    public DeletionRefusal? Refusal { get; set; }

    public int Asked { get; private set; }

    public DeletionRefusal? Refuse(string sessionId)
    {
        Asked++;
        return Refusal;
    }
}

/// <summary>
/// Deleting a meeting.
///
/// <para>
/// This is the one destructive thing EchoForge does, so the tests are about what it refuses rather
/// than what it removes: a session that is recording, finalizing, recovering, transcribing,
/// summarising or simply held by another process, a drive with no Recycle Bin, and — the case a
/// disabled button cannot cover — work that starts <i>after</i> the user has been asked and before
/// they answer.
/// </para>
/// </summary>
public sealed class SessionDeletionTests : IDisposable
{
    private readonly LibraryFixture _fixture = new();
    private readonly TempDirectory _bin = new();

    public void Dispose()
    {
        _fixture.Dispose();
        _bin.Dispose();
    }

    private FakeRecycleBin NewBin() => new(_bin.Path);

    private SessionDeletionService Service(
        ISessionDeletionAuthority authority,
        IRecycleBin bin,
        Func<string, Task>? forget = null) =>
        new(_fixture.Sessions, _fixture.Root, authority, bin, forget);

    private static SwitchableAuthority Idle() => new();

    // -- the ordinary path -------------------------------------------------------------------------

    [Fact]
    public async Task DeletingTakesTheWholeMeetingToTheRecycleBin()
    {
        string session = _fixture.AddSession("01JDELETE", "Quarterly review");
        _fixture.AddTranscript(session, ("microphone", "We agreed on the budget."));
        _fixture.AddSummary(session, 1, decisions: [("Budget agreed", "segment-000001")]);
        _fixture.Aliases.Rename(session, EchoForge.Contracts.Transcripts.TranscriptSpeakers.RemoteId, "Sam");

        string root = _fixture.Sessions.Resolve(session).Root;
        FakeRecycleBin bin = NewBin();

        DeletionResult result = await Service(Idle(), bin).DeleteAsync(session);

        Assert.True(result.Deleted);
        Assert.Contains("Recycle Bin", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));

        // Restorable as a set: the transcript, the summary and the aliases went with the audio.
        string recycled = Path.Combine(_bin.Path, session);
        Assert.True(File.Exists(Path.Combine(recycled, "session.json")));
        Assert.True(Directory.Exists(Path.Combine(recycled, "transcript")));
    }

    [Fact]
    public void TheConfirmationIsGivenSomethingAPersonCanRecognise()
    {
        string session = _fixture.AddSession("01JNAMED", "Design sync");

        DeletionEligibility eligibility = Service(Idle(), NewBin()).Check(session);

        Assert.True(eligibility.Allowed);
        Assert.Equal("Design sync", eligibility.Title);
        Assert.NotNull(eligibility.RecordedUtc);
    }

    [Fact]
    public async Task DeletingTouchesOnlyTheMeetingItWasAskedAbout()
    {
        string target = _fixture.AddSession("01JTARGET");
        string other = _fixture.AddSession("01JOTHER");
        _fixture.AddTranscript(other, ("microphone", "Still here."));

        string otherRoot = _fixture.Sessions.Resolve(other).Root;

        await Service(Idle(), NewBin()).DeleteAsync(target);

        Assert.True(Directory.Exists(otherRoot));
        Assert.NotNull(_fixture.Sessions.ReadSnapshot(other));
    }

    [Fact]
    public async Task ModelsAndRuntimesAreNotInsideAMeetingAndAreNotTouched()
    {
        string session = _fixture.AddSession("01JMODELS");

        // Artifacts live beside the sessions root, not under any one session.
        string artifacts = Path.Combine(_fixture.Root, "artifacts");
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "model.bin"), "pretend weights");

        await Service(Idle(), NewBin()).DeleteAsync(session);

        Assert.True(File.Exists(Path.Combine(artifacts, "model.bin")));
    }

    [Theory]
    [InlineData("01JUNICODE-会議-Ω")]
    [InlineData("01J with spaces and (brackets)")]
    public async Task AwkwardSessionNamesDeleteJustTheSame(string sessionId)
    {
        string session = _fixture.AddSession(sessionId, "Awkward");
        string root = _fixture.Sessions.Resolve(session).Root;

        Assert.True(Directory.Exists(root));

        DeletionResult result = await Service(Idle(), NewBin()).DeleteAsync(session);

        Assert.True(result.Deleted, result.Message);
        Assert.False(Directory.Exists(root));
    }

    // -- refusals --------------------------------------------------------------------------------

    [Theory]
    [InlineData(SessionState.Recording, "session_active")]
    [InlineData(SessionState.Finalizing, "session_active")]
    [InlineData(SessionState.Recovering, "session_active")]
    [InlineData(SessionState.Paused, "session_active")]
    public async Task ASessionThatIsStillBeingWrittenIsRefused(SessionState state, string code)
    {
        string session = _fixture.AddSession("01JBUSY", "Live", state);
        string root = _fixture.Sessions.Resolve(session).Root;

        FakeRecycleBin bin = NewBin();
        SessionDeletionService service = new(
            _fixture.Sessions, _fixture.Root, new SessionStateDeletionAuthority(_fixture.Sessions), bin);

        DeletionEligibility eligibility = service.Check(session);
        Assert.False(eligibility.Allowed);
        Assert.Equal(code, eligibility.Code);

        DeletionResult result = await service.DeleteAsync(session);

        Assert.False(result.Deleted);
        Assert.Equal(code, result.Code);
        Assert.True(Directory.Exists(root));
        Assert.Empty(bin.Recycled);
    }

    [Fact]
    public async Task ASessionSomebodyElseHoldsIsRefused()
    {
        string session = _fixture.AddSession("01JLEASED");
        FileSessionLeaseProvider leases = new(_fixture.Sessions);

        using ISessionLease? held = leases.TryAcquire(session);
        Assert.NotNull(held);

        SessionDeletionService service = new(
            _fixture.Sessions, _fixture.Root, new LeaseDeletionAuthority(leases), NewBin());

        DeletionResult result = await service.DeleteAsync(session);

        Assert.False(result.Deleted);
        Assert.Equal("session_in_use", result.Code);
        Assert.True(Directory.Exists(_fixture.Sessions.Resolve(session).Root));
    }

    [Theory]
    [InlineData("transcribing")]
    [InlineData("summarizing")]
    public async Task WorkTheHostKnowsAboutIsRefusedToo(string code)
    {
        string session = _fixture.AddSession("01JWORKING");

        SessionDeletionService service = new(
            _fixture.Sessions,
            _fixture.Root,
            new CompositeDeletionAuthority(
                new SessionStateDeletionAuthority(_fixture.Sessions),
                new DelegateDeletionAuthority(_ => new DeletionRefusal(code, "busy with " + code))),
            NewBin());

        DeletionResult result = await service.DeleteAsync(session);

        Assert.False(result.Deleted);
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public async Task ADriveWithNoRecycleBinIsRefusedRatherThanDeletedPermanently()
    {
        string session = _fixture.AddSession("01JNOBIN");
        string root = _fixture.Sessions.Resolve(session).Root;

        FakeRecycleBin bin = NewBin();
        bin.Available = false;

        SessionDeletionService service = Service(Idle(), bin);

        Assert.False(service.Check(session).Allowed);

        DeletionResult result = await service.DeleteAsync(session);

        Assert.False(result.Deleted);
        Assert.Equal("recycle_unavailable", result.Code);
        Assert.Contains("permanently", result.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task AFolderThatIsNotASessionIsNeverDeleted()
    {
        // A directory under the store that holds no journal and no snapshot: whatever it is, it
        // is not a meeting, and a delete is not the moment to be approximately right.
        string stray = Path.Combine(_fixture.Root, "2026", "08", "01JSTRAY");
        Directory.CreateDirectory(stray);
        await File.WriteAllTextAsync(Path.Combine(stray, "notes.txt"), "something else");

        DeletionResult result = await Service(Idle(), NewBin()).DeleteAsync("01JSTRAY");

        Assert.False(result.Deleted);
        Assert.Equal("not_a_session", result.Code);
        Assert.True(Directory.Exists(stray));
    }

    // -- the race --------------------------------------------------------------------------------

    [Fact]
    public async Task WorkThatStartsWhileTheQuestionIsOnScreenStopsTheDeletion()
    {
        string session = _fixture.AddSession("01JRACE", "About to be recorded over");
        string root = _fixture.Sessions.Resolve(session).Root;

        SwitchableAuthority authority = Idle();
        FakeRecycleBin bin = NewBin();
        SessionDeletionService service = Service(authority, bin);

        // The user opens the confirmation while everything is quiet.
        Assert.True(service.Check(session).Allowed);

        // While they are reading it, a recording starts.
        authority.Refusal = new DeletionRefusal("recording_active", "That recording is running right now.");

        // They press Delete. The answer they gave was to a different situation.
        DeletionResult result = await service.DeleteAsync(session);

        Assert.False(result.Deleted);
        Assert.Equal("recording_active", result.Code);
        Assert.True(Directory.Exists(root));
        Assert.Empty(bin.Recycled);

        // Asked once for the confirmation and again at the moment of truth.
        Assert.Equal(2, authority.Asked);
    }

    // -- the index -------------------------------------------------------------------------------

    [Fact]
    public async Task TheIndexIsOnlyToldAfterTheFolderHasActuallyGone()
    {
        string session = _fixture.AddSession("01JINDEXED", "Findable");
        _fixture.AddTranscript(session, ("microphone", "A distinctive sentence about aardvarks."));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        FakeRecycleBin bin = NewBin();
        bin.FailWith = "still_there";

        List<string> forgotten = [];

        SessionDeletionService service = Service(Idle(), bin, id =>
        {
            forgotten.Add(id);
            return index.UpdateAsync(id);
        });

        DeletionResult failed = await service.DeleteAsync(session);

        Assert.False(failed.Deleted);
        Assert.Empty(forgotten);
        Assert.Contains(index.Meetings(), m => m.SessionId == session);
        Assert.NotEmpty(index.Search(new Contracts.Library.SearchQuery { Text = "aardvarks" }).Hits);

        // Now let it work.
        bin.FailWith = null;
        DeletionResult ok = await service.DeleteAsync(session);

        Assert.True(ok.Deleted);
        Assert.Equal([session], forgotten);
        Assert.DoesNotContain(index.Meetings(), m => m.SessionId == session);
        Assert.Empty(index.Search(new Contracts.Library.SearchQuery { Text = "aardvarks" }).Hits);
    }

    [Fact]
    public async Task ARebuildDoesNotBringADeletedMeetingBack()
    {
        string session = _fixture.AddSession("01JGONE", "Recycled");
        _fixture.AddTranscript(session, ("microphone", "Something memorable about pangolins."));

        using SqliteLibraryIndex index = _fixture.NewIndex();
        await index.EnsureReadyAsync();

        await Service(Idle(), NewBin(), id => index.UpdateAsync(id)).DeleteAsync(session);

        // A rebuild reads the folders. The folder is in the Recycle Bin, so there is nothing to
        // find — which is exactly why deletion had to be a real move rather than an index change.
        await index.RebuildAsync();

        Assert.DoesNotContain(index.Meetings(), m => m.SessionId == session);
        Assert.Empty(index.Search(new Contracts.Library.SearchQuery { Text = "pangolins" }).Hits);
    }
}
