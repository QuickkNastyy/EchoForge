using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Recording;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>An endpoint monitor the test drives, so no device has to be physically unplugged.</summary>
public sealed class FakeEndpointMonitor : IEndpointMonitor
{
    public bool Started { get; private set; }

    public event EventHandler<EndpointChangedEventArgs>? EndpointLost;

    public event EventHandler<DefaultEndpointChangedEventArgs>? DefaultChanged;

    public void Start() => Started = true;

    public void Lose(string endpointId, EndpointChange change = EndpointChange.Removed) =>
        EndpointLost?.Invoke(this, new EndpointChangedEventArgs(endpointId, change));

    public void ChangeDefault(string endpointId, bool isRender) =>
        DefaultChanged?.Invoke(this, new DefaultEndpointChangedEventArgs(endpointId, isRender));

    public void Dispose()
    {
    }
}

/// <summary>A power monitor the test drives, so the machine never actually sleeps.</summary>
public sealed class FakePowerMonitor : IPowerMonitor
{
    public bool Started { get; private set; }

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Start() => Started = true;

    public void Suspend() => Suspending?.Invoke(this, EventArgs.Empty);

    public void Resume() => Resumed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
    }
}

public sealed class DeviceAndPowerLifecycleTests : IDisposable
{
    private const string RenderId = "render-id";
    private const string CaptureId = "capture-id";

    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FakeEndpointMonitor _devices = new();
    private readonly FakePowerMonitor _power = new();
    private readonly FileSessionStore _store;

    public DeviceAndPowerLifecycleTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private RecordingController NewController() =>
        new(_store, _engines, _clock, _disk, null, _devices, _power);

    private static RecordingRequest Request => new(RenderId, "Headphones", CaptureId, "Microphone");

    [Fact]
    public void MonitorsAreStartedWithTheController()
    {
        using RecordingController controller = NewController();

        Assert.True(_devices.Started);
        Assert.True(_power.Started);
    }

    [Fact]
    public void LosingTheMicrophoneDegradesTheSessionAndNamesTheTrack()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        string? reason = null;
        controller.StateChanged += (_, e) => reason = e.Reason;

        _devices.Lose(CaptureId, EndpointChange.Unplugged);

        Assert.Equal(SessionState.Degraded, controller.State);
        Assert.True(controller.IsCapturing);
        Assert.Contains("microphone", reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(_engines.Latest.Stopped);
    }

    [Fact]
    public void LosingTheRenderEndpointNamesSystemAudio()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        string? reason = null;
        controller.StateChanged += (_, e) => reason = e.Reason;

        _devices.Lose(RenderId, EndpointChange.Disabled);

        Assert.Equal(SessionState.Degraded, controller.State);
        Assert.Contains("system audio", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnrelatedDeviceChangeDoesNotDisturbTheRecording()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        _devices.Lose("some-other-device");
        _devices.Lose("yet-another");

        Assert.Equal(SessionState.Recording, controller.State);
        Assert.Empty(controller.LostEndpoints);
    }

    [Fact]
    public void ADefaultDeviceChangeIsNeverFollowed()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        _devices.ChangeDefault("a-completely-different-endpoint", isRender: true);

        Assert.Equal(SessionState.Recording, controller.State);

        // The engine is still capturing the endpoints pinned at Start.
        Assert.Equal(RenderId, _engines.Latest.Request.RenderEndpointId);
        Assert.Equal(CaptureId, _engines.Latest.Request.CaptureEndpointId);
    }

    [Fact]
    public void LosingBothEndpointsStopsSafelyAndKeepsFinalizedAudio()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        _engines.Latest.EmitChunk(SourceTrack.System);

        _devices.Lose(CaptureId);
        _devices.Lose(RenderId);

        Assert.Equal(SessionState.Failed, controller.State);
        Assert.True(_engines.Latest.Stopped);
        Assert.Equal(2, controller.Snapshot().Tracks.Sum(t => t.Chunks.Count));
        Assert.NotNull(controller.EndedUtc);
    }

    [Fact]
    public void AnEndpointLossIsJournalledWithTheReason()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        _devices.Lose(CaptureId, EndpointChange.NotPresent);

        controller.FlushPendingWrites(TimeSpan.FromSeconds(5));

        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        JournalEvent failure = Assert.Single(journal.Events, e => e.Type == JournalEventTypes.TrackFailed);
        Assert.Contains("notpresent", failure.Field("fault"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuspendFinalizesTheEpochAndPausesRatherThanLosingAudio()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        _clock.Advance(TimeSpan.FromMinutes(4));
        _power.Suspend();

        Assert.Equal(SessionState.Paused, controller.State);
        Assert.True(_engines.Latest.Stopped);
        Assert.True(controller.AwaitingResumeAfterSuspend);

        SessionEpoch epoch = Assert.Single(controller.Epochs);
        Assert.Equal(EpochEndReason.Suspended, epoch.EndReason);
        Assert.NotNull(epoch.EndedUtc);

        // The audio captured before sleep is intact and the session is not over.
        Assert.Equal(1, controller.Snapshot().Tracks.Sum(t => t.Chunks.Count));
        Assert.Null(controller.EndedUtc);
    }

    [Fact]
    public void ResumeAfterWakingDoesNotHappenOnItsOwn()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _power.Suspend();

        string? notice = null;
        controller.Notice += (_, message) => notice = message;

        _clock.Advance(TimeSpan.FromMinutes(30));
        _power.Resume();

        // Still paused. The user has to choose.
        Assert.Equal(SessionState.Paused, controller.State);
        Assert.False(controller.IsCapturing);
        Assert.Single(controller.Epochs);
        Assert.Contains("Resume", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitResumeAfterSleepOpensANewEpochWithTheSameEndpoints()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        _power.Suspend();
        _power.Resume();

        _clock.Advance(TimeSpan.FromMinutes(10));
        controller.Resume();

        Assert.Equal(SessionState.Recording, controller.State);
        Assert.False(controller.AwaitingResumeAfterSuspend);
        Assert.Equal(2, controller.Epochs.Count);
        Assert.Equal(RenderId, _engines.Latest.Request.RenderEndpointId);
        Assert.Equal(CaptureId, _engines.Latest.Request.CaptureEndpointId);

        // The sleep shows up as a gap, not as recorded silence.
        TimeSpan gap = controller.Epochs[1].StartedUtc - controller.Epochs[0].EndedUtc!.Value;
        Assert.Equal(TimeSpan.FromMinutes(10), gap);
    }

    [Fact]
    public void SuspendWhileAlreadyPausedChangesNothing()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        controller.Pause();

        int epochs = controller.Epochs.Count;
        _power.Suspend();

        Assert.Equal(SessionState.Paused, controller.State);
        Assert.Equal(epochs, controller.Epochs.Count);
    }
}
