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

/// <summary>A consent prompt whose answer the test chooses, and which counts how often it ran.</summary>
public sealed class FakeConsentPrompt : IConsentPrompt
{
    public bool Answer { get; set; } = true;

    public int Asked { get; private set; }

    public Task<bool> ConfirmAsync()
    {
        Asked++;
        return Task.FromResult(Answer);
    }
}

public sealed class ConsentAndDeviceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly FakeDeviceCatalog _catalog = new();
    private readonly FakeSettingsStore _settings = new();
    private readonly FakeConsentPrompt _consent = new();
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

    private MainViewModel NewViewModel() => new(_controller, _catalog, _settings, _consent);

    [Fact]
    public void ClickingStartIsNotItselfConsent()
    {
        using MainViewModel vm = NewViewModel();
        _consent.Answer = false;

        vm.StartCommand.Execute(null);
        SpinUntil(() => _consent.Asked > 0);

        Assert.Equal(1, _consent.Asked);
        Assert.Equal(SessionState.New, _controller.State);
        Assert.False(vm.IsRecording);
    }

    [Fact]
    public void CancellingConsentLeavesTheRecorderUntouchedAndSaysSo()
    {
        using MainViewModel vm = NewViewModel();
        _consent.Answer = false;

        vm.StartCommand.Execute(null);
        SpinUntil(() => vm.Notice is not null);

        Assert.Contains("cancelled", vm.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_engines.Created);
        Assert.Null(_settings.Current.RenderEndpointId);
    }

    [Fact]
    public void AcceptingConsentStartsTheRecording()
    {
        using MainViewModel vm = NewViewModel();
        _consent.Answer = true;

        vm.StartCommand.Execute(null);
        SpinUntil(() => _controller.State == SessionState.Recording);

        Assert.Equal(1, _consent.Asked);
        Assert.Equal(SessionState.Recording, _controller.State);
        Assert.Equal("render-id", _settings.Current.RenderEndpointId);
        Assert.Equal("capture-id", _settings.Current.CaptureEndpointId);
    }

    [Fact]
    public void ConsentIsAskedAgainForEveryRecording()
    {
        using MainViewModel vm = NewViewModel();

        vm.StartCommand.Execute(null);
        SpinUntil(() => _controller.State == SessionState.Recording);
        _controller.Stop();

        vm.StartCommand.Execute(null);
        SpinUntil(() => _consent.Asked >= 2);

        // A remembered preference must not bypass the per-recording reminder.
        Assert.True(_settings.Current.ConsentAcknowledged);
        Assert.Equal(2, _consent.Asked);
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

    private static void SpinUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }
    }
}
