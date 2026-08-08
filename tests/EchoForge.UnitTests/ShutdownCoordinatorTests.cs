using EchoForge.App;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>A shutdown prompt whose answer the test chooses.</summary>
public sealed class FakeShutdownPrompt : IShutdownPrompt
{
    public ShutdownDecision Answer { get; set; } = ShutdownDecision.SaveAndClose;

    public int Asked { get; private set; }

    public bool LastAskedAboutRecording { get; private set; }

    public ShutdownDecision Ask(bool isRecording)
    {
        Asked++;
        LastAskedAboutRecording = isRecording;
        return Answer;
    }
}

/// <summary>
/// Closing used to fire Stop and continue immediately, so the process could exit while chunks
/// were still being finalized. Every exit path now awaits a durable save.
/// </summary>
public sealed class ShutdownCoordinatorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FakeDeviceCatalog _catalog = new();
    private readonly FakeSettingsStore _settings = new();
    private readonly FakeShutdownPrompt _prompt = new();
    private readonly List<string> _errors = [];
    private readonly FileSessionStore _store;
    private readonly RecordingController _controller;

    public ShutdownCoordinatorTests()
    {
        _store = new FileSessionStore(_temp.Path);
        _controller = new RecordingController(_store, _engines, _clock, _disk);
    }

    public void Dispose()
    {
        _controller.Dispose();
        _temp.Dispose();
    }

    private MainViewModel NewViewModel()
    {
        MainViewModel vm = new(_controller, _catalog, _settings);
        vm.MarkReady();
        return vm;
    }

    private ShutdownCoordinator NewCoordinator(MainViewModel vm) =>
        new(vm, _prompt, _errors.Add);

    private static RecordingRequest Request => new("render-id", "Headphones", "capture-id", "Microphone");

    [Fact]
    public async Task ClosingWithNothingRunningDoesNotAskAnything()
    {
        using MainViewModel vm = NewViewModel();
        ShutdownCoordinator coordinator = NewCoordinator(vm);

        Assert.True(await coordinator.TryShutdownAsync());

        Assert.Equal(0, _prompt.Asked);
        Assert.True(coordinator.IsShuttingDown);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task ClosingDuringARecordingAsksAndSavesBeforeAllowingTheClose()
    {
        using MainViewModel vm = NewViewModel();
        _controller.Start(Request);
        _engines.Latest.EmitChunk(SourceTrack.Microphone);

        ShutdownCoordinator coordinator = NewCoordinator(vm);
        Assert.True(await coordinator.TryShutdownAsync());

        Assert.Equal(1, _prompt.Asked);
        Assert.True(_prompt.LastAskedAboutRecording);

        // The session is durable before the app is allowed to close.
        Assert.Equal(SessionState.Recorded, _controller.State);
        Assert.NotNull(_controller.EndedUtc);

        SessionSnapshot persisted = _store.ReadSnapshot(_controller.SessionId!)!;
        Assert.Equal(SessionState.Recorded, persisted.State);
        Assert.Equal(1, persisted.Tracks.Sum(t => t.Chunks.Count));
    }

    [Fact]
    public async Task CancellingKeepsTheAppOpenAndTheRecordingRunning()
    {
        using MainViewModel vm = NewViewModel();
        _controller.Start(Request);
        _prompt.Answer = ShutdownDecision.Cancel;

        ShutdownCoordinator coordinator = NewCoordinator(vm);

        Assert.False(await coordinator.TryShutdownAsync());
        Assert.False(coordinator.IsShuttingDown);
        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.True(_controller.CaptureMayBeLive);
    }

    [Fact]
    public async Task APausedSessionIsAskedAboutTooAndUsesDifferentWording()
    {
        using MainViewModel vm = NewViewModel();
        _controller.Start(Request);
        _controller.Pause();

        ShutdownCoordinator coordinator = NewCoordinator(vm);
        Assert.True(await coordinator.TryShutdownAsync());

        Assert.Equal(1, _prompt.Asked);
        Assert.False(_prompt.LastAskedAboutRecording);
        Assert.Equal(SessionState.Recorded, _controller.State);
    }

    [Fact]
    public async Task AFailedSaveKeepsTheAppOpenAndExplainsWhy()
    {
        // A store whose terminal write fails makes finalization throw.
        BrokenStore broken = new(_store);
        using RecordingController controller = new(broken, _engines, _clock, _disk);
        using MainViewModel vm = new(controller, _catalog, _settings);
        vm.MarkReady();

        controller.Start(Request);
        broken.FailEverything = true;

        ShutdownCoordinator coordinator = new(vm, _prompt, _errors.Add);

        Assert.False(await coordinator.TryShutdownAsync());
        Assert.False(coordinator.IsShuttingDown);
        Assert.Single(_errors);
        Assert.Contains("stayed open", _errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASecondShutdownRequestDoesNotStartASecondSequence()
    {
        using MainViewModel vm = NewViewModel();
        _controller.Start(Request);

        ShutdownCoordinator coordinator = NewCoordinator(vm);
        Assert.True(await coordinator.TryShutdownAsync());

        // Once approved, later requests short-circuit rather than asking or saving again.
        Assert.True(await coordinator.TryShutdownAsync());
        Assert.Equal(1, _prompt.Asked);
    }

    [Fact]
    public async Task TheIndicatorIsClearOnceShutdownHasCompleted()
    {
        using MainViewModel vm = NewViewModel();
        _controller.Start(Request);
        Assert.True(vm.IndicatorVisible);

        ShutdownCoordinator coordinator = NewCoordinator(vm);
        Assert.True(await coordinator.TryShutdownAsync());

        Assert.False(vm.IndicatorVisible);
        Assert.Equal(CapturePhase.Stopped, _controller.Phase);
    }

    /// <summary>A store that can be made to fail every write.</summary>
    private sealed class BrokenStore(ISessionStore inner) : ISessionStore
    {
        private readonly ISessionStore _inner = inner;

        public bool FailEverything { get; set; }

        public SessionPaths Create(string sessionId) => _inner.Create(sessionId);

        public SessionPaths Resolve(string sessionId) => _inner.Resolve(sessionId);

        public void Append(string sessionId, JournalEvent journalEvent)
        {
            if (FailEverything)
            {
                throw new IOException("simulated disk failure");
            }

            _inner.Append(sessionId, journalEvent);
        }

        public JournalReadResult ReadJournal(string sessionId) => _inner.ReadJournal(sessionId);

        public void WriteSnapshot(SessionSnapshot snapshot)
        {
            if (FailEverything)
            {
                throw new IOException("simulated disk failure");
            }

            _inner.WriteSnapshot(snapshot);
        }

        public SessionSnapshot? ReadSnapshot(string sessionId) => _inner.ReadSnapshot(sessionId);

        public IReadOnlyList<string> EnumerateSessions() => _inner.EnumerateSessions();
    }
}
