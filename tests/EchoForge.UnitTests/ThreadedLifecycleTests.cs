using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Recording;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// A capture engine that behaves like the real one: a background writer thread raises
/// <see cref="ChunkFinalized"/> synchronously, and <see cref="Stop"/> joins that thread.
///
/// <para>
/// This is the shape that deadlocks. If the controller's chunk handler takes the lifecycle lock
/// while Pause or Stop holds it and waits on this join, the app hangs mid-recording. A purely
/// synchronous fake cannot reproduce it.
/// </para>
/// </summary>
public sealed class ThreadedCaptureEngine : ICaptureEngine
{
    private readonly List<AudioChunkMetadata> _chunks = [];
    private readonly Lock _chunkLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _emitted = new(false);
    private Thread? _writer;
    private bool _stopped;

    public ThreadedCaptureEngine(CaptureRequest request) => Request = request;

    public CaptureRequest Request { get; }

    public bool Stopped => _stopped;

    public event EventHandler<ChunkFinalizedEventArgs>? ChunkFinalized;

    public event EventHandler<TrackFaultedEventArgs>? TrackFaulted;

    public IReadOnlyList<AudioChunkMetadata> CompletedChunks
    {
        get { lock (_chunkLock) { return [.. _chunks]; } }
    }

    /// <summary>Blocks until at least one chunk has been raised from the writer thread.</summary>
    public bool WaitForFirstChunk(TimeSpan timeout) => _emitted.Wait(timeout);

    public void Start()
    {
        _writer = new Thread(() =>
        {
            int index = Request.FirstChunkIndex;
            while (!_cts.IsCancellationRequested)
            {
                AudioChunkMetadata chunk = new(
                    index,
                    $"tracks/microphone/chunks/{index:D6}.wav",
                    SourceTrack.Microphone,
                    0, 1, 48_000, 2, 48_000, $"hash-{index:D6}", [], Request.EpochIndex);

                lock (_chunkLock)
                {
                    _chunks.Add(chunk);
                }

                // Raised synchronously on this thread, exactly like PcmChunkWriter does.
                ChunkFinalized?.Invoke(this, new ChunkFinalizedEventArgs(chunk));
                _emitted.Set();

                index++;
                Thread.Sleep(2);
            }
        })
        { IsBackground = true, Name = "threaded fake writer" };

        _writer.Start();
    }

    public void Stop(long stopQpc)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _cts.Cancel();

        // Join, exactly like the real engine. Deadlocks here if the handler needs a lock the
        // caller of Stop is already holding.
        _writer?.Join(TimeSpan.FromSeconds(10));
        _writer = null;
    }

    public void RaiseFault(SourceTrack track, string fault) =>
        TrackFaulted?.Invoke(this, new TrackFaultedEventArgs(track, fault));

    public RecorderStatus Status() => new(
        !_stopped, TimeSpan.Zero, 0,
        [
            new TrackLiveStatus(SourceTrack.Microphone, "capture-id", "Mic", new CaptureFormat(48_000, 2, 16), !_stopped, null, 0, 0, CompletedChunks.Count, 0, 0),
            new TrackLiveStatus(SourceTrack.System, "render-id", "Out", new CaptureFormat(48_000, 2, 16), !_stopped, null, 0, 0, 0, 0, 0),
        ]);

    public void Dispose()
    {
        Stop(0);
        _cts.Dispose();
        _emitted.Dispose();
    }
}

public sealed class ThreadedEngineFactory : ICaptureEngineFactory
{
    public List<ThreadedCaptureEngine> Created { get; } = [];

    public ThreadedCaptureEngine Latest => Created[^1];

    public ICaptureEngine Create(CaptureRequest request)
    {
        ThreadedCaptureEngine engine = new(request);
        Created.Add(engine);
        return engine;
    }
}

public sealed class ThreadedLifecycleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ThreadedEngineFactory _engines = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FileSessionStore _store;

    public ThreadedLifecycleTests() => _store = new FileSessionStore(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private RecordingController NewController() =>
        new(_store, _engines, new SystemClock(), _disk);

    private static RecordingRequest Request => new("render-id", "Headphones", "capture-id", "Microphone");

    /// <summary>A real clock; these tests exercise threading, not time arithmetic.</summary>
    private sealed class SystemClock : ICaptureClock
    {
        public long NowQpc() => DateTimeOffset.UtcNow.UtcTicks;

        public DateTimeOffset UtcNow() => DateTimeOffset.UtcNow;
    }

    [Fact]
    public async Task StopDoesNotDeadlockAgainstAWriterThreadRaisingChunks()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        Assert.True(_engines.Latest.WaitForFirstChunk(TimeSpan.FromSeconds(5)), "no chunk was ever raised");

        // Stop must return promptly. If the chunk handler took the lifecycle lock, this hangs.
        Task stop = Task.Run(controller.Stop);
        Task finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(stop, finished);
        await stop;

        Assert.Equal(SessionState.Recorded, controller.State);
        Assert.True(_engines.Latest.Stopped);
    }

    [Fact]
    public async Task PauseDoesNotDeadlockAgainstAWriterThreadRaisingChunks()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        Assert.True(_engines.Latest.WaitForFirstChunk(TimeSpan.FromSeconds(5)));

        Task pause = Task.Run(controller.Pause);
        Task finished = await Task.WhenAny(pause, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(pause, finished);
        await pause;

        Assert.Equal(SessionState.Paused, controller.State);
    }

    [Fact]
    public void EveryChunkRaisedBeforeStopIsAccountedFor()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        Assert.True(_engines.Latest.WaitForFirstChunk(TimeSpan.FromSeconds(5)));

        Thread.Sleep(60);
        controller.Stop();

        int raised = _engines.Latest.CompletedChunks.Count;
        int recorded = controller.Snapshot().Tracks.Sum(t => t.Chunks.Count);

        // Closing the epoch drains the notification queue, so nothing raised before the join
        // is dropped on the floor.
        Assert.True(raised > 0);
        Assert.Equal(raised, recorded);
    }

    [Fact]
    public async Task PollUnderConcurrentChunkTrafficDoesNotThrowOrHang()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        Assert.True(_engines.Latest.WaitForFirstChunk(TimeSpan.FromSeconds(5)));

        // Hammer the read paths while the writer thread keeps raising chunks.
        Task poller = Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                controller.Poll();
                controller.Totals();
                controller.Snapshot();
            }
        });

        Task done = await Task.WhenAny(poller, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(poller, done);
        await poller;

        controller.Stop();
    }

    [Fact]
    public void ChunksFromAPreviousSessionDoNotContaminateTheNextOne()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        Assert.True(_engines.Latest.WaitForFirstChunk(TimeSpan.FromSeconds(5)));

        ThreadedCaptureEngine first = _engines.Latest;
        controller.Stop();

        // A late callback from the finished session's engine, arriving after a new one started.
        controller.Start(Request);
        first.RaiseFault(SourceTrack.Microphone, "late fault from the old session");

        Assert.Equal(SessionState.Recording, controller.State);

        controller.Stop();
        Assert.Equal(SessionState.Recorded, controller.State);
    }

    [Fact]
    public void APersistingFaultIsJournalledOncePerEpochNotOncePerPoll()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);

        // The same fault surfaces on every tick; the journal must not grow with the ticks.
        _engines.Latest.RaiseFault(SourceTrack.Microphone, "COMException (HRESULT 0x88890004)");
        for (int i = 0; i < 25; i++)
        {
            controller.Poll();
        }

        Assert.True(controller.FlushPendingWrites(TimeSpan.FromSeconds(5)));

        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        Assert.Single(journal.Events, e => e.Type == JournalEventTypes.TrackFailed);

        controller.Stop();
    }

    [Fact]
    public void FlushReportsAnExactBarrierRatherThanAnEmptyQueue()
    {
        using RecordingController controller = NewController();
        controller.Start(Request);
        Assert.True(_engines.Latest.WaitForFirstChunk(TimeSpan.FromSeconds(5)));

        controller.Stop();

        // Every write accepted up to this point is durable, so the journal already holds the
        // chunk events rather than merely having dequeued them.
        Assert.True(controller.FlushPendingWrites(TimeSpan.FromSeconds(10)));

        JournalReadResult journal = _store.ReadJournal(controller.SessionId!);
        int journalled = journal.Events.Count(e => e.Type == JournalEventTypes.ChunkCompleted);
        Assert.Equal(_engines.Latest.CompletedChunks.Count, journalled);
    }
}
