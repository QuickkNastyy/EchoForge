using EchoForge.App;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Recovery;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// Startup recovery used to run after the app became usable, so a scan could repair chunks a
/// freshly started recording was still writing. Two mechanisms stop that: Start is gated on
/// recovery finishing, and a live session holds an exclusive lease that recovery honours.
/// </summary>
public sealed class ReadinessAndLeaseTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FakeDeviceCatalog _catalog = new();
    private readonly FakeSettingsStore _settings = new();
    private readonly FileSessionStore _store;
    private readonly FileSessionLeaseProvider _leases;

    public ReadinessAndLeaseTests()
    {
        _store = new FileSessionStore(_temp.Path);
        _leases = new FileSessionLeaseProvider(_store);
    }

    public void Dispose() => _temp.Dispose();

    private RecordingController NewController() =>
        new(_store, _engines, _clock, _disk, null, null, null, _leases);

    private static RecordingRequest Request => new("render-id", "Headphones", "capture-id", "Microphone");

    // ---- readiness ----

    [Fact]
    public void StartIsDisabledUntilRecoveryFinishes()
    {
        using RecordingController controller = NewController();
        using MainViewModel vm = new(controller, _catalog, _settings);

        Assert.False(vm.IsReady);
        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Contains("Checking", vm.ReadinessMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartIsEnabledOnceRecoveryFinishes()
    {
        using RecordingController controller = NewController();
        using MainViewModel vm = new(controller, _catalog, _settings);

        vm.MarkReady();

        Assert.True(vm.IsReady);
        Assert.True(vm.CanStart);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.ReadinessMessage);
    }

    [Fact]
    public void RecoveryFailureStillUnlocksTheAppButWarns()
    {
        using RecordingController controller = NewController();
        using MainViewModel vm = new(controller, _catalog, _settings);

        vm.MarkReady(warning: "EchoForge could not finish checking earlier recordings.");

        // A recovery problem must not lock the user out of recording.
        Assert.True(vm.IsReady);
        Assert.True(vm.CanStart);
        Assert.Contains("could not finish", vm.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PressingStartBeforeReadinessDoesNothing()
    {
        using RecordingController controller = NewController();
        using MainViewModel vm = new(controller, _catalog, _settings);

        vm.StartCommand.Execute(null);
        Thread.Sleep(100);

        // Recovery is still walking the session folders. Starting now could have the recorder and
        // the scan touching the same session, so the button does nothing at all until it finishes.
        Assert.False(vm.CanStart);
        Assert.Equal(SessionState.New, controller.State);
        Assert.Empty(_engines.Created);
    }

    // ---- leases ----

    [Fact]
    public void ALeaseIsExclusiveWhileHeldAndFreeAfterRelease()
    {
        _store.Create("leased");

        ISessionLease? first = _leases.TryAcquire("leased");
        Assert.NotNull(first);
        Assert.True(_leases.IsLeased("leased"));
        Assert.Null(_leases.TryAcquire("leased"));

        first.Dispose();

        Assert.False(_leases.IsLeased("leased"));
        using ISessionLease? second = _leases.TryAcquire("leased");
        Assert.NotNull(second);
    }

    [Fact]
    public void AnUnknownSessionIsNotLeased() => Assert.False(_leases.IsLeased("never-existed"));

    [Fact]
    public void ARecordingHoldsItsLeaseForTheSessionLifetime()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        string sessionId = controller.SessionId!;
        Assert.True(_leases.IsLeased(sessionId));

        controller.Pause();
        Assert.True(_leases.IsLeased(sessionId));

        controller.Resume();
        Assert.True(_leases.IsLeased(sessionId));

        controller.Stop();
        Assert.False(_leases.IsLeased(sessionId));
    }

    [Fact]
    public void RecoverySkipsALeasedSessionAndLeavesItUntouched()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        controller.FlushPendingWrites(TimeSpan.FromSeconds(5));

        string sessionId = controller.SessionId!;
        DirectorySnapshot before = DirectorySnapshot.Capture(_temp.Path);

        SessionRecoveryService recovery = new(_store, new FakeChunkRepairer(), null, _leases);
        RecoveryOutcome outcome = recovery.Recover(sessionId);

        Assert.True(outcome.Skipped);
        Assert.False(outcome.ChangedAnything);
        Assert.Contains(outcome.Notes, n => n.Contains("in use", StringComparison.OrdinalIgnoreCase));

        // Nothing on disk changed, so the live recorder's files were not disturbed.
        Assert.True(DirectorySnapshot.Capture(_temp.Path).Matches(before));

        controller.Stop();
    }

    [Fact]
    public void ScanAllOmitsLeasedSessions()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        controller.FlushPendingWrites(TimeSpan.FromSeconds(5));

        SessionRecoveryService recovery = new(_store, new FakeChunkRepairer(), null, _leases);
        IReadOnlyList<RecoveryOutcome> outcomes = recovery.ScanAll();

        Assert.DoesNotContain(outcomes, o => o.SessionId == controller.SessionId);

        controller.Stop();
    }

    [Fact]
    public void AReleasedSessionCanBeRecoveredAfterwards()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        string sessionId = controller.SessionId!;
        controller.Stop();

        Assert.False(_leases.IsLeased(sessionId));

        SessionRecoveryService recovery = new(_store, new FakeChunkRepairer(), null, _leases);
        RecoveryOutcome outcome = recovery.Recover(sessionId);

        Assert.False(outcome.Skipped);
        Assert.Equal(SessionState.Recorded, outcome.State);
    }

    [Fact]
    public void RecoveryAndAStartingRecordingCannotTouchTheSameSession()
    {
        // A session left behind by a crash, with no lease.
        const string Abandoned = "abandoned";
        SessionPaths paths = _store.Create(Abandoned);
        _store.Append(Abandoned, JournalEvent.Create(JournalEventTypes.SessionCreated, DateTimeOffset.UnixEpoch));

        // Something else claims it first — as a live recorder would.
        using ISessionLease? held = _leases.TryAcquire(Abandoned);
        Assert.NotNull(held);

        SessionRecoveryService recovery = new(_store, new FakeChunkRepairer(), null, _leases);
        Assert.True(recovery.Recover(Abandoned).Skipped);

        // And the reverse: a session recovery is working on cannot be claimed underneath it.
        using ISessionLease? recoveryHold = _leases.TryAcquire("other");
        Assert.NotNull(recoveryHold);
        Assert.Null(_leases.TryAcquire("other"));

        Assert.True(File.Exists(paths.LeasePath));
    }

    [Fact]
    public void AStartThatCannotClaimItsSessionFailsRatherThanSharing()
    {
        // Force the collision by holding the lease for the ID the controller is about to use.
        BlockingLeaseProvider blocking = new();
        using RecordingController controller =
            new(_store, _engines, _clock, _disk, null, null, null, blocking);

        Assert.Throws<InvalidOperationException>(() => controller.Start(Request));
        Assert.Equal(SessionState.Failed, controller.State);
    }

    /// <summary>A provider that never grants a lease, standing in for a session already in use.</summary>
    private sealed class BlockingLeaseProvider : ISessionLeaseProvider
    {
        public ISessionLease? TryAcquire(string sessionId) => null;

        public bool IsLeased(string sessionId) => true;
    }
}
