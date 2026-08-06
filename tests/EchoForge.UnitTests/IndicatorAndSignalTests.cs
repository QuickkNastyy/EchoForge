using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Recording;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>A capture engine whose Stop blocks, standing in for slow capture-thread joins.</summary>
public sealed class DelayedStopEngine : ICaptureEngine
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly ManualResetEventSlim _stopEntered = new(false);

    public DelayedStopEngine(CaptureRequest request) => Request = request;

    public CaptureRequest Request { get; }

    public bool Stopped { get; private set; }

    public event EventHandler<ChunkFinalizedEventArgs>? ChunkFinalized;

    public event EventHandler<TrackFaultedEventArgs>? TrackFaulted;

    public IReadOnlyList<AudioChunkMetadata> CompletedChunks => [];

    public void Start()
    {
    }

    /// <summary>Blocks until <see cref="ReleaseStop"/> is called, like a slow thread join.</summary>
    public void Stop(long stopQpc)
    {
        if (Stopped)
        {
            return;
        }

        _stopEntered.Set();
        _release.Wait(TimeSpan.FromSeconds(20));
        Stopped = true;
    }

    public bool WaitUntilStopping(TimeSpan timeout) => _stopEntered.Wait(timeout);

    public void ReleaseStop() => _release.Set();

    public RecorderStatus Status() => new(
        !Stopped, TimeSpan.Zero, 0,
        [
            new TrackLiveStatus(SourceTrack.Microphone, "capture-id", "Mic", new CaptureFormat(48_000, 2, 16), !Stopped, null, 0, 0, 0, 0, 0),
            new TrackLiveStatus(SourceTrack.System, "render-id", "Out", new CaptureFormat(48_000, 2, 16), !Stopped, null, 0, 0, 0, 0, 0),
        ]);

    public void Dispose()
    {
        ReleaseStop();
        _release.Dispose();
        _stopEntered.Dispose();
        _ = ChunkFinalized;
        _ = TrackFaulted;
    }
}

public sealed class DelayedStopEngineFactory : ICaptureEngineFactory
{
    public List<DelayedStopEngine> Created { get; } = [];

    public DelayedStopEngine Latest => Created[^1];

    public ICaptureEngine Create(CaptureRequest request)
    {
        DelayedStopEngine engine = new(request);
        Created.Add(engine);
        return engine;
    }
}

/// <summary>
/// The indicator must tell the truth about the hardware, and OS callbacks must not do lifecycle
/// work on the caller's thread.
/// </summary>
public sealed class IndicatorAndSignalTests : IDisposable
{
    private const string RenderId = "render-id";
    private const string CaptureId = "capture-id";

    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FakeEndpointMonitor _devices = new();
    private readonly FakePowerMonitor _power = new();
    private readonly FileSessionStore _store;

    public IndicatorAndSignalTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private static RecordingRequest Request => new(RenderId, "Headphones", CaptureId, "Microphone");

    // ---- item 12: the indicator ----

    [Fact]
    public async Task TheIndicatorStaysLitWhileCaptureIsStillStopping()
    {
        DelayedStopEngineFactory engines = new();
        using RecordingController controller = new(_store, engines, _clock, _disk);
        controller.Start(Request);

        Assert.Equal(CapturePhase.Capturing, controller.Phase);
        Assert.True(controller.CaptureMayBeLive);

        Task stop = Task.Run(controller.Stop);

        // Stop has been asked for and the engine is winding down, but the threads are still live.
        Assert.True(engines.Latest.WaitUntilStopping(TimeSpan.FromSeconds(5)));
        Assert.Equal(CapturePhase.StoppingCapture, controller.Phase);
        Assert.True(controller.CaptureMayBeLive);

        engines.Latest.ReleaseStop();
        Task finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(stop, finished);
        await stop;

        // Only now that capture has genuinely stopped does the indicator clear.
        Assert.Equal(CapturePhase.Stopped, controller.Phase);
        Assert.False(controller.CaptureMayBeLive);
        Assert.Equal(SessionState.Recorded, controller.State);
    }

    [Fact]
    public void ThePhaseIsIdleBeforeAnythingStarts()
    {
        FakeCaptureEngineFactory engines = new();
        using RecordingController controller = new(_store, engines, _clock, _disk);

        Assert.Equal(CapturePhase.Idle, controller.Phase);
        Assert.False(controller.CaptureMayBeLive);
    }

    [Fact]
    public void PausingClearsTheIndicatorBecauseCaptureHasActuallyStopped()
    {
        FakeCaptureEngineFactory engines = new();
        using RecordingController controller = new(_store, engines, _clock, _disk);
        controller.Start(Request);
        controller.Pause();

        Assert.False(controller.CaptureMayBeLive);

        controller.Resume();
        Assert.True(controller.CaptureMayBeLive);
        Assert.Equal(CapturePhase.Capturing, controller.Phase);
    }

    // ---- item 10: callbacks stay light ----

    [Fact]
    public void AnEndpointCallbackReturnsWithoutDoingLifecycleWork()
    {
        DelayedStopEngineFactory engines = new();
        using RecordingController controller =
            new(_store, engines, _clock, _disk, null, _devices, _power);

        controller.Start(Request);

        // Losing both endpoints closes the epoch, which joins the engine. If that happened on the
        // callback thread, this call would block for the whole join.
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        _devices.Lose(CaptureId);
        _devices.Lose(RenderId);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2),
            $"the COM callback blocked for {watch.ElapsedMilliseconds} ms");

        engines.Latest.ReleaseStop();
        Assert.True(controller.WaitForSignals(TimeSpan.FromSeconds(15)));
        Assert.Equal(SessionState.Failed, controller.State);
    }

    [Fact]
    public void APowerCallbackReturnsWithoutDoingLifecycleWork()
    {
        DelayedStopEngineFactory engines = new();
        using RecordingController controller =
            new(_store, engines, _clock, _disk, null, _devices, _power);

        controller.Start(Request);

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        _power.Suspend();
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2),
            $"the power callback blocked for {watch.ElapsedMilliseconds} ms");

        engines.Latest.ReleaseStop();
        Assert.True(controller.WaitForSignals(TimeSpan.FromSeconds(15)));
        Assert.Equal(SessionState.Paused, controller.State);
    }

    [Fact]
    public void SignalsAreProcessedInOrderOnOneThread()
    {
        List<int> observed = [];
        using LifecycleSignalQueue queue = new();

        for (int i = 0; i < 50; i++)
        {
            int captured = i;
            queue.Post(() => observed.Add(captured));
        }

        Assert.True(queue.Drain(TimeSpan.FromSeconds(5)));
        Assert.Equal(Enumerable.Range(0, 50), observed);
    }

    [Fact]
    public void AFaultySignalDoesNotStopLaterSignals()
    {
        bool ranAfter = false;
        using LifecycleSignalQueue queue = new();

        queue.Post(() => throw new InvalidOperationException("boom"));
        queue.Post(() => ranAfter = true);

        Assert.True(queue.Drain(TimeSpan.FromSeconds(5)));
        Assert.True(ranAfter);
    }

    // ---- item 9: endpoint-loss state is per epoch ----

    [Fact]
    public void AReopenedEndpointClearsItsLossAndIsJournalled()
    {
        FakeCaptureEngineFactory engines = new();
        using RecordingController controller =
            new(_store, engines, _clock, _disk, null, _devices, _power);

        controller.Start(Request);

        _devices.Lose(CaptureId, EndpointChange.Unplugged);
        Assert.True(controller.WaitForSignals(TimeSpan.FromSeconds(5)));

        Assert.Equal(SessionState.Degraded, controller.State);
        Assert.Contains(CaptureId, controller.LostEndpoints);

        // The user reconnects and resumes: the endpoint reopens, so its loss is resolved.
        controller.Pause();
        controller.Resume();

        Assert.Empty(controller.LostEndpoints);
        Assert.Equal(SessionState.Recording, controller.State);

        Assert.True(controller.FlushPendingWrites(TimeSpan.FromSeconds(5)));
        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        Assert.Contains(journal.Events, e =>
            e.Type == JournalEventTypes.TrackRestored && e.Field("device_id") == CaptureId);
    }

    [Fact]
    public void LosingTheOtherEndpointAfterARestoreIsStillReported()
    {
        FakeCaptureEngineFactory engines = new();
        using RecordingController controller =
            new(_store, engines, _clock, _disk, null, _devices, _power);

        controller.Start(Request);

        // Lose the microphone, restore it through a new epoch, then lose the render endpoint.
        _devices.Lose(CaptureId);
        Assert.True(controller.WaitForSignals(TimeSpan.FromSeconds(5)));
        controller.Pause();
        controller.Resume();
        Assert.Empty(controller.LostEndpoints);

        _devices.Lose(RenderId);
        Assert.True(controller.WaitForSignals(TimeSpan.FromSeconds(5)));

        // Stale state from the first loss must not make this look like both endpoints are gone.
        Assert.Equal(SessionState.Degraded, controller.State);
        Assert.True(controller.IsCapturing);
        Assert.Single(controller.LostEndpoints);
        Assert.Contains(RenderId, controller.LostEndpoints);
    }

    [Fact]
    public void AwaitingResumeIsClearedOnlyOnceTheNewEpochIsRunning()
    {
        FakeCaptureEngineFactory engines = new();
        using RecordingController controller =
            new(_store, engines, _clock, _disk, null, _devices, _power);

        controller.Start(Request);
        _power.Suspend();
        Assert.True(controller.WaitForSignals(TimeSpan.FromSeconds(5)));
        Assert.True(controller.AwaitingResumeAfterSuspend);

        // A Resume that cannot reopen the endpoints leaves the flag and the pause intact.
        engines.FailNextStart = true;
        Assert.Throws<InvalidOperationException>(controller.Resume);

        Assert.True(controller.AwaitingResumeAfterSuspend);
        Assert.Equal(SessionState.Paused, controller.State);
        Assert.Single(controller.Epochs);

        // A successful Resume clears it.
        controller.Resume();
        Assert.False(controller.AwaitingResumeAfterSuspend);
        Assert.Equal(2, controller.Epochs.Count);
    }

    [Fact]
    public void AFailedResumeKeepsTheSessionPausedAndItsReason()
    {
        FakeCaptureEngineFactory engines = new();
        using RecordingController controller = new(_store, engines, _clock, _disk);

        controller.Start(Request);
        controller.Pause();

        engines.FailNextStart = true;
        Assert.Throws<InvalidOperationException>(controller.Resume);

        Assert.Equal(SessionState.Paused, controller.State);
        Assert.Single(controller.Epochs);
        Assert.Equal(EpochEndReason.Paused, controller.Epochs[0].EndReason);
        Assert.Null(controller.EndedUtc);
    }
}
