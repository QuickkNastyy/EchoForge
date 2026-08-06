using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Recording;
using EchoForge.Core.Storage;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

public sealed class RecordingControllerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FileSessionStore _store;

    public RecordingControllerTests() => _store = new FileSessionStore(_temp.Path);

    private RecordingController NewController(DiskPolicy? policy = null) =>
        new(_store, _engines, _clock, _disk, policy);

    private static RecordingRequest Request => new(
        "render-id", "Fake Headphones", "capture-id", "Fake Microphone");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void StartOpensTheFirstEpochAndPinsBothEndpoints()
    {
        using RecordingController controller = NewController();

        controller.Start(Request);

        Assert.Equal(SessionState.Recording, controller.State);
        Assert.NotNull(controller.SessionId);
        Assert.Single(controller.Epochs);
        Assert.True(_engines.Latest.Started);
        Assert.Equal("render-id", _engines.Latest.Request.RenderEndpointId);
        Assert.Equal("capture-id", _engines.Latest.Request.CaptureEndpointId);
    }

    [Fact]
    public void PauseFinalizesTheEpochAndStopsCapturing()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        _engines.Latest.EmitChunk(SourceTrack.System);

        _clock.Advance(TimeSpan.FromMinutes(2));
        controller.Pause();

        Assert.Equal(SessionState.Paused, controller.State);
        Assert.False(controller.IsCapturing);
        Assert.True(_engines.Latest.Stopped);
        Assert.True(_engines.Latest.Disposed);

        SessionEpoch epoch = Assert.Single(controller.Epochs);
        Assert.Equal(EpochEndReason.Paused, epoch.EndReason);
        Assert.NotNull(epoch.EndedUtc);
    }

    [Fact]
    public void ResumeOpensANewEpochAndContinuesChunkNumbering()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        // Two chunks in the first epoch: indices 1 and 2.
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        _engines.Latest.EmitChunk(SourceTrack.System);
        controller.Pause();

        _clock.Advance(TimeSpan.FromMinutes(5));
        controller.Resume();

        Assert.Equal(SessionState.Recording, controller.State);
        Assert.Equal(2, controller.Epochs.Count);
        Assert.Equal(2, _engines.Created.Count);

        // The new epoch must not reuse an index a finalized chunk already holds.
        Assert.Equal(3, _engines.Latest.Request.FirstChunkIndex);
        Assert.NotEqual(_engines.Created[0].Request.EpochQpc, _engines.Latest.Request.EpochQpc);
    }

    [Fact]
    public void ResumeRecordsTheGapBetweenEpochs()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _clock.Advance(TimeSpan.FromMinutes(1));
        controller.Pause();
        _clock.Advance(TimeSpan.FromMinutes(4));
        controller.Resume();

        SessionEpoch first = controller.Epochs[0];
        SessionEpoch second = controller.Epochs[1];

        Assert.NotNull(first.EndedUtc);
        Assert.Equal(TimeSpan.FromMinutes(4), second.StartedUtc - first.EndedUtc!.Value);
        Assert.True(second.StartQpc > first.EndQpc);
    }

    [Fact]
    public void StopFinalizesTheSessionAndIsIdempotent()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        controller.Stop();
        SessionSnapshot afterStop = controller.Snapshot();
        int stopCount = _engines.Latest.StopCount;

        controller.Stop();
        controller.Stop();

        Assert.Equal(SessionState.Recorded, controller.State);
        Assert.Equal(stopCount, _engines.Latest.StopCount);
        Assert.Equal(afterStop.Tracks.Sum(t => t.Chunks.Count), controller.Snapshot().Tracks.Sum(t => t.Chunks.Count));
    }

    [Fact]
    public void DisposeAfterStopDoesNotChangeTheSession()
    {
        RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        controller.Stop();

        string sessionId = controller.SessionId!;
        DirectorySnapshot before = DirectorySnapshot.Capture(_temp.Path);
        int stops = _engines.Latest.StopCount;

        controller.Dispose();

        Assert.Equal(stops, _engines.Latest.StopCount);
        Assert.True(DirectorySnapshot.Capture(_temp.Path).Matches(before));
        Assert.NotNull(_store.ReadSnapshot(sessionId));
    }

    [Fact]
    public void AFailedTrackMakesTheSessionDegradedWithoutStopping()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        _engines.Latest.FailTrack(SourceTrack.Microphone, "device removed");
        controller.Poll();

        Assert.Equal(SessionState.Degraded, controller.State);
        Assert.True(controller.IsCapturing);
        Assert.False(_engines.Latest.Stopped);

        // Fault events go through the persistence queue, so wait on the exact barrier.
        Assert.True(controller.FlushPendingWrites(TimeSpan.FromSeconds(5)));

        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        Assert.Contains(journal.Events, e => e.Type == JournalEventTypes.TrackFailed);
    }

    [Fact]
    public void LosingEveryTrackFailsTheSessionAndPreservesWhatWasCaptured()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        _engines.Latest.FailTrack(SourceTrack.Microphone, "gone");
        _engines.Latest.FailTrack(SourceTrack.System, "gone");
        controller.Poll();

        Assert.Equal(SessionState.Failed, controller.State);
        Assert.True(_engines.Latest.Stopped);
        Assert.Equal(1, controller.Snapshot().Tracks.Sum(t => t.Chunks.Count));
    }

    [Fact]
    public void StartIsRefusedWhenThereIsNotEnoughFreeSpace()
    {
        _disk.Available = 1_000_000_000;
        using RecordingController controller = NewController();

        (bool allowed, string? reason) = controller.CanStart(_temp.Path);

        Assert.False(allowed);
        Assert.Contains("GB", reason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => controller.Start(Request));
        Assert.Equal(SessionState.Failed, controller.State);
    }

    [Fact]
    public void CrossingTheWarnThresholdJournalsOnceAndKeepsRecording()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        _disk.Available = 4_000_000_000;
        controller.Poll();
        controller.Poll();
        controller.Poll();

        Assert.Equal(SessionState.Recording, controller.State);

        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        Assert.Single(journal.Events, e => e.Type == JournalEventTypes.DiskWarning);
    }

    [Fact]
    public void CrossingTheStopThresholdStopsInAControlledWay()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        _disk.Available = 1_000_000_000;
        controller.Poll();

        Assert.Equal(SessionState.Recorded, controller.State);
        Assert.True(_engines.Latest.Stopped);

        // Every chunk captured before the stop is still in the session.
        Assert.Equal(1, controller.Snapshot().Tracks.Sum(t => t.Chunks.Count));

        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        Assert.Contains(journal.Events, e => e.Type == JournalEventTypes.DiskControlledStop);
        Assert.Contains(journal.Events, e => e.Type == JournalEventTypes.SessionEnded);
    }

    [Fact]
    public void AnEndpointThatRefusesToOpenLeavesTheControllerFailedNotHalfStarted()
    {
        using RecordingController controller = NewController();
        _engines.FailNextStart = true;

        Assert.Throws<InvalidOperationException>(() => controller.Start(Request));

        Assert.Equal(SessionState.Failed, controller.State);
        Assert.True(_engines.Latest.Disposed);
    }

    [Fact]
    public void TheJournalRecordsEveryEpochBoundary()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        controller.Pause();
        controller.Resume();
        controller.Stop();

        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        List<string> types = [.. journal.Events.Select(e => e.Type)];

        Assert.Equal(2, types.Count(t => t == JournalEventTypes.EpochStarted));
        Assert.Equal(2, types.Count(t => t == JournalEventTypes.EpochEnded));
        Assert.Single(types, t => t == JournalEventTypes.SessionEnded);
    }

    [Fact]
    public void ChunksCarryTheEpochThatProducedThem()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        controller.Pause();
        controller.Resume();
        _engines.Latest.EmitChunk(SourceTrack.Microphone);
        controller.Stop();

        List<AudioChunkMetadata> chunks =
            [.. controller.Snapshot().Tracks.SelectMany(t => t.Chunks).OrderBy(c => c.Index)];

        Assert.Equal(2, chunks.Count);
        Assert.Equal(1, chunks[0].EpochIndex);
        Assert.Equal(2, chunks[1].EpochIndex);
        Assert.NotEqual(chunks[0].Index, chunks[1].Index);
    }
}
