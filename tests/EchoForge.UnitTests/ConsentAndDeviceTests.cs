using EchoForge.App;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Settings;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>A device catalog the test controls, so no real endpoints are needed.</summary>
public sealed class FakeDeviceCatalog : IAudioDeviceCatalog
{
    public List<AudioEndpointInfo> Render { get; } =
    [
        new("render-id", "Headphones", true, new CaptureFormat(48_000, 2, 16)),
        new("render-2", "Speakers", false, new CaptureFormat(48_000, 2, 16)),
    ];

    public List<AudioEndpointInfo> Capture { get; } =
    [
        new("capture-id", "Headset Microphone", true, new CaptureFormat(48_000, 1, 16)),
    ];

    public IReadOnlyList<AudioEndpointInfo> GetRenderEndpoints() => [.. Render];

    public IReadOnlyList<AudioEndpointInfo> GetCaptureEndpoints() => [.. Capture];

    public AudioEndpointInfo? FindById(string endpointId) =>
        Render.Concat(Capture).FirstOrDefault(d => d.Id == endpointId);
}

/// <summary>An in-memory settings store.</summary>
public sealed class FakeSettingsStore : ISettingsStore
{
    public AppSettings Current { get; set; } = new();

    public AppSettings Load() => Current;

    public void Save(AppSettings settings) => Current = settings;
}

public sealed class ConsentAndDeviceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FakeDeviceCatalog _catalog = new();
    private readonly FakeSettingsStore _settings = new();
    private readonly FileSessionStore _store;
    private readonly RecordingController _controller;

    public ConsentAndDeviceTests()
    {
        _store = new FileSessionStore(_temp.Path);
        _controller = new RecordingController(_store, _engines, _clock, _disk);
    }

    public void Dispose()
    {
        _controller.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    /// These tests are about starting and about device selection, so the readiness gate is opened
    /// up front. Gating itself is covered by <see cref="ReadinessAndLeaseTests"/>.
    /// </summary>
    private MainViewModel NewViewModel()
    {
        MainViewModel vm = new(_controller, _catalog, _settings);
        vm.MarkReady();
        return vm;
    }

    /// <summary>
    /// Start starts. There is no confirmation step between the button and the recorder, and the
    /// pair of endpoints is remembered so the next session opens on the same two devices.
    /// </summary>
    [Fact]
    public void StartBeginsRecordingAndRemembersTheDevices()
    {
        using MainViewModel vm = NewViewModel();

        vm.StartCommand.Execute(null);
        SpinUntil(() => _controller.State == SessionState.Recording);

        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.Equal("render-id", _settings.Current.RenderEndpointId);
        Assert.Equal("capture-id", _settings.Current.CaptureEndpointId);
    }

    /// <summary>
    /// A recording still cannot begin without a pair of endpoints, and says which is missing
    /// rather than starting on something it picked itself.
    /// </summary>
    [Fact]
    public void StartRefusesWithoutBothEndpointsAndSaysSo()
    {
        using MainViewModel vm = NewViewModel();
        vm.SelectedCapture = null;

        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));

        vm.StartCommand.Execute(null);

        Assert.Empty(_engines.Created);
        Assert.Equal(SessionState.New, _controller.State);
    }

    /// <summary>
    /// The red indicator reports capture and only capture: lit while a device may still be live,
    /// and out once the capture threads have genuinely stopped.
    /// </summary>
    [Fact]
    public void TheIndicatorFollowsCaptureRatherThanTheStopRequest()
    {
        using MainViewModel vm = NewViewModel();

        Assert.False(vm.IndicatorVisible);

        vm.StartCommand.Execute(null);
        SpinUntil(() => _controller.State == SessionState.Recording);

        Assert.True(vm.IndicatorVisible);

        _controller.Stop();
        SpinUntil(() => !_controller.CaptureMayBeLive);

        Assert.False(vm.IndicatorVisible);
    }

    [Fact]
    public void RefreshReplacesTheSelectedObjectWithOneFromTheNewCollection()
    {
        using MainViewModel vm = NewViewModel();
        AudioEndpointInfo before = vm.SelectedRender!;

        vm.RefreshDevices();

        Assert.NotNull(vm.SelectedRender);
        Assert.Equal(before.Id, vm.SelectedRender.Id);

        // The selection must be an item that is actually in the collection, not a stale instance.
        Assert.Contains(vm.SelectedRender, vm.RenderDevices);
    }

    [Fact]
    public void RefreshClearsAndWarnsWhenTheSelectedEndpointDisappeared()
    {
        using MainViewModel vm = NewViewModel();
        Assert.Equal("capture-id", vm.SelectedCapture!.Id);

        _catalog.Capture.Clear();
        vm.RefreshDevices();

        Assert.Null(vm.SelectedCapture);
        Assert.Contains("microphone", vm.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public void RefreshNeverSilentlySubstitutesADifferentEndpoint()
    {
        using MainViewModel vm = NewViewModel();

        // The previously chosen playback device is replaced by a different one.
        _catalog.Render.Clear();
        _catalog.Render.Add(new AudioEndpointInfo("something-else", "A Different Device", true, new CaptureFormat(48_000, 2, 16)));

        vm.RefreshDevices();

        Assert.Null(vm.SelectedRender);
        Assert.DoesNotContain("A Different Device", vm.SelectedRender?.FriendlyName ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// A device from last time that is no longer here leaves both pickers empty and Start dead,
    /// which is correct — the recorder never silently substitutes another endpoint. What was
    /// missing is the sentence saying so: the constructor worked it out and then assigned the
    /// recovery summary, usually null, straight over the top of it.
    /// </summary>
    [Fact]
    public void AMissingDeviceFromLastTimeIsExplainedRatherThanLeftBlank()
    {
        _settings.Current = _settings.Current with
        {
            RenderEndpointId = "an-endpoint-that-has-since-been-unplugged",
            CaptureEndpointId = "capture-id",
        };

        using MainViewModel vm = NewViewModel();

        Assert.Null(vm.SelectedRender);
        Assert.False(vm.CanStart);

        Assert.True(vm.HasNotice, "the window would show two empty pickers and no explanation");
        Assert.Contains("playback device", vm.Notice, StringComparison.Ordinal);

        // And it survives the readiness scan finishing, which used to overwrite it.
        vm.MarkReady("Checked 3 interrupted recordings.");

        Assert.True(vm.HasNotice);
        Assert.Contains("playback device", vm.Notice, StringComparison.Ordinal);
        Assert.Contains("Checked 3 interrupted recordings.", vm.Notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The meters are read logarithmically, because amplitude is the wrong scale to draw one on.
    /// Ordinary speech peaks around a tenth of full scale, which as a linear bar barely leaves the
    /// left edge - the reason the recorder looked as though it were hearing almost nothing.
    /// </summary>
    [Fact]
    public void TheMetersReadInDecibelsRatherThanRawAmplitude()
    {
        // -20 dBFS is unremarkable speech. On a linear bar it is one tenth; it has to read as most
        // of the way up a meter, or nobody can see themselves talking.
        Assert.InRange(MeterFor(0.1), 0.6, 0.75);

        // A shout is at the top, and silence is the floor rather than something faintly lit.
        Assert.InRange(MeterFor(1.0), 0.99, 1.0);
        Assert.Equal(0, MeterFor(0));

        // Quieter than the floor is still the floor, not a negative bar.
        Assert.Equal(0, MeterFor(0.0001));

        // And the reading itself is reported truthfully, in the units meters are read in.
        Assert.Equal("-20 dB", TextFor(0.1));
        Assert.Equal("\u2014", TextFor(0));
    }

    private static double MeterFor(double amplitude) => Invoke<double>("Meter", amplitude);

    private static string TextFor(double amplitude) => Invoke<string>("Decibels", amplitude);

    /// <summary>
    /// Reached by reflection on purpose. The mapping is presentation detail with no business
    /// being public, and it is worth a test precisely because getting it wrong is invisible in
    /// every structural check and obvious the moment somebody speaks.
    /// </summary>
    private static T Invoke<T>(string name, double amplitude) =>
        (T)typeof(MainViewModel)
            .GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [amplitude])!;

    [Fact]
    public void DevicesCannotBeRefreshedDuringARecording()
    {
        using MainViewModel vm = NewViewModel();
        vm.StartCommand.Execute(null);
        SpinUntil(() => _controller.State == SessionState.Recording);

        string? renderId = vm.SelectedRender?.Id;
        _catalog.Render.Clear();
        vm.RefreshDevices();

        Assert.Equal(renderId, vm.SelectedRender?.Id);
        Assert.False(vm.DevicesEditable);
    }

    [Fact]
    public void StorageRateIsDerivedFromTheSelectedFormats()
    {
        using MainViewModel vm = NewViewModel();
        vm.StartCommand.Execute(null);
        SpinUntil(() => _controller.State == SessionState.Recording);

        // Two 48 kHz stereo tracks = 384,000 B/s = 1.38 GB/hr, not a hard-coded 1.04.
        double gigabytesPerHour = _controller.EstimatedBytesPerSecond() * 3600.0 / 1_000_000_000.0;
        Assert.Equal(1.38, gigabytesPerHour, 2);
    }

    private static void SpinUntil(
        Func<bool> condition,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? description = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        // Returning quietly after the deadline turns a stall into a confusing assertion failure
        // somewhere further down, about a count rather than about the wait that never finished.
        Assert.True(condition(), $"timed out waiting for: {description}");
    }
}
