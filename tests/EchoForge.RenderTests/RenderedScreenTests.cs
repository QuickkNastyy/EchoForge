using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EchoForge.App;
using EchoForge.App.Library;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Exports;
using EchoForge.Core.Recording;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Summaries;
using EchoForge.Infrastructure.Workers;
using EchoForge.UnitTests;

namespace EchoForge.RenderTests;

/// <summary>
/// The screens, rendered, and checked against their own pixels.
///
/// <para>
/// Three interface passes shipped a visibly broken window while their smoke tests passed, because
/// those tests only asked whether a control existed in the tree. A button whose label is the same
/// colour as its own fill exists, holds the right string, and is unreadable. A ComboBox whose
/// template forgets to paint the selection box exists too, and shows nothing at all. A panel bound
/// through a re-scoped DataContext is <em>visible</em> when it should be collapsed, and lands on
/// top of the screen underneath it.
/// </para>
///
/// <para>
/// So these build each window against the real application resources with real view models, lay it
/// out at a real size, render it with <see cref="RenderTargetBitmap"/>, write the PNG for a human to
/// look at, and then read the bitmap back. A label must put marks on its own plate; a picker must
/// paint its selection; nothing may overlap anything; and no record's <c>ToString</c> may ever reach
/// the screen.
/// </para>
/// </summary>
public sealed class RenderedScreenTests
{
    // ---------------------------------------------------------------- the recording window

    [Fact]
    public void TheReadyScreenRendersEveryControlItOffers() => UiThread.Run(() =>
    {
        using RecorderHarness harness = new();

        using Screen screen = UiThread.Render("01-ready", () =>
            new MainWindow { DataContext = harness.Ready() }, 1180, 900);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.EveryComboBoxPaintsItsSelection();
        screen.ButtonIsReadable("Start recording");
        screen.ButtonIsEnabled("Start recording");
        screen.TextIsPainted("Headphones");
        screen.TextIsPainted("Headset Microphone");
        screen.NoTextOverlaps();
    });

    [Fact]
    public void TheCapturingScreenRendersTheClockAndTheRibbon() => UiThread.Run(() =>
    {
        using RecorderHarness harness = new();

        using Screen screen = UiThread.Render("02-capturing", () =>
            new MainWindow { DataContext = harness.Capturing() }, 1180, 900);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.ButtonIsReadable("Stop recording");
        screen.ClockIsSolid();
        screen.NoTextOverlaps();
    });

    [Fact]
    public void TheCompactRecorderRendersItsClockAndTransport() => UiThread.Run(() =>
    {
        using RecorderHarness harness = new();

        using Screen screen = UiThread.Render("04-compact", () =>
            new CompactRecorderWindow { DataContext = harness.Capturing() }, 372, 150);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.NoTextOverlaps();
    });

    [Fact]
    public void AMissingDeviceLeavesAPromptAndAnExplanation() => UiThread.Run(() =>
    {
        using RecorderHarness harness = new();

        using Screen screen = UiThread.Render("01b-device-missing", () =>
            new MainWindow { DataContext = harness.WithADeviceThatIsGone() }, 1180, 900);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();

        // The two pickers cannot show a device, so they must show what to do instead, and the
        // screen must say why Start is unavailable rather than leaving three dead controls.
        screen.EveryComboBoxPaintsItsSelection();
        screen.TextIsPainted("is not available. Choose another before recording");
        screen.NoTextOverlaps();
    });

    /// <summary>
    /// The recorder at the narrowest it may be. The paired panels have to stack here, and a
    /// UniformGrid told to use one column while also being told to use one row would draw them on
    /// top of each other instead — which is exactly what it did until this caught it.
    /// </summary>
    [Fact]
    public void TheRecordingScreenStacksItsPanelsAtItsNarrowest() => UiThread.Run(() =>
    {
        using RecorderHarness harness = new();

        using Screen screen = UiThread.Render("01c-ready-narrow", () =>
            new MainWindow { DataContext = harness.Ready() }, 820, 700);

        screen.EveryVisibleButtonShowsItsLabel();
        screen.NoTextOverlaps();
    });

    // ---------------------------------------------------------------- the library window

    [Fact]
    public void TheRecordingsListRendersItsRows() => UiThread.Run(() =>
    {
        using LibraryHarness harness = new();

        using Screen screen = UiThread.Render("05-all-recordings", harness.Window, 1440, 900);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.TextIsPainted("Deployment planning");
        screen.NoTextOverlaps();

        // The list is the only thing on screen: the detail workspace must be collapsed, not
        // merely behind it. This is the defect that put the back button over the list heading.
        screen.DetailWorkspaceIsCollapsed();
    });

    [Fact]
    public void AnOpenedRecordingRendersItsWorkspace() => UiThread.Run(() =>
    {
        using LibraryHarness harness = new();
        harness.Open("processed");

        using Screen screen = UiThread.Render("06-one-recording", harness.Window, 1920, 1080);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.EveryComboBoxPaintsItsSelection();
        screen.NoTextOverlaps();
        screen.PlaybackPositionIsNeverABareSeparator();
        screen.TextIsPainted("Deployment planning");
    });

    /// <summary>
    /// Scene 07: selecting a claim marks it, marks the transcript line it cites, drops a marker on
    /// the ribbon at that moment, and recedes everything it did not cite.
    /// </summary>
    [Fact]
    public void FollowingAClaimMarksItsSourceEverywhere() => UiThread.Run(() =>
    {
        using LibraryHarness harness = new();
        harness.Open("processed");

        // Selected through the list rather than by calling the view model, so the click path — and
        // the mark the selected claim itself carries — is what gets photographed.
        using Screen screen = UiThread.Render(
            "07-following-a-claim", harness.Window, 1920, 1080, harness.SelectFirstClaim);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.NoTextOverlaps();

        // The transcript says where it went, and the cited moment is marked on the timeline.
        screen.TextIsPainted("jumped to");
        screen.TextIsPainted("Evidence for the selected claim");

        // A slot the meeting never filled is drawn as an absence, not as the word "unknown".
        screen.TextIsPainted("no date stated");
        screen.NoTextMatches("Due: unknown");
        screen.NoTextMatches("Owner: unknown");
    });

    [Fact]
    public void AnOpenedRecordingStillFitsAt1366By768() => UiThread.Run(() =>
    {
        using LibraryHarness harness = new();
        harness.Open("processed");

        using Screen screen = UiThread.Render("07-one-recording-1366", harness.Window, 1366, 768);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.NoTextOverlaps();
    });

    // ---------------------------------------------------------------- setup

    /// <summary>
    /// Setup, against this machine's real manifest and whatever is actually installed on it.
    ///
    /// <para>
    /// Skipped rather than failed where the manifest cannot be opened: the window is honest about
    /// that case too, but a build with no artifact manifest beside it has nothing to photograph.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSetupScreenRendersItsComponents() => UiThread.Run(() =>
    {
        using SetupHarness harness = new();
        if (!harness.Available)
        {
            return;
        }

        using Screen screen = UiThread.Render("09-setup", harness.Window, 1180, 900, harness.WaitForScan);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.ButtonIsReadable("Install what is recommended");
        screen.TextIsPainted("WHAT ECHOFORGE CAN DO ON THIS MACHINE");
        screen.TextIsPainted("COMPONENTS");
        screen.NoTextOverlaps();
    });

    [Fact]
    public void TheSetupScreenStillFitsAtItsNarrowest() => UiThread.Run(() =>
    {
        using SetupHarness harness = new();
        if (!harness.Available)
        {
            return;
        }

        using Screen screen = UiThread.Render("09-setup-narrow", harness.Window, 820, 620, harness.WaitForScan);

        screen.EveryVisibleButtonShowsItsLabel();
        screen.NoTextOverlaps();
    });

    // ---------------------------------------------------------------- the other palette

    [Fact]
    public void TheReadyScreenIsReadableInTheLightTheme() => UiThread.Run(() =>
    {
        using ThemeScope light = ThemeScope.Light();
        using RecorderHarness harness = new();

        using Screen screen = UiThread.Render("01-ready-light", () =>
            new MainWindow { DataContext = harness.Ready() }, 1180, 900);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.EveryComboBoxPaintsItsSelection();
        screen.ButtonIsReadable("Start recording");
        screen.TextIsPainted("Headphones");
        screen.NoTextOverlaps();

        // The point of a second palette is that it is genuinely a light one, not the dark screen
        // with different accents: the page has to be light and its writing dark.
        screen.PageIsLight();
    });

    [Fact]
    public void TheRecordingsListIsReadableInTheLightTheme() => UiThread.Run(() =>
    {
        using ThemeScope light = ThemeScope.Light();
        using LibraryHarness harness = new();

        using Screen screen = UiThread.Render("05-all-recordings-light", harness.Window, 1440, 900);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.TextIsPainted("Deployment planning");
        screen.NoTextOverlaps();
        screen.PageIsLight();
    });

    [Fact]
    public void AnOpenedRecordingIsReadableInTheLightTheme() => UiThread.Run(() =>
    {
        using ThemeScope light = ThemeScope.Light();
        using LibraryHarness harness = new();
        harness.Open("processed");

        using Screen screen = UiThread.Render("06-one-recording-light", harness.Window, 1920, 1080);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.EveryComboBoxPaintsItsSelection();
        screen.NoTextOverlaps();
        screen.PageIsLight();
    });

    [Fact]
    public void AnUnprocessedRecordingShowsAQuietEmptyStateRatherThanBrokenPanels() => UiThread.Run(() =>
    {
        using LibraryHarness harness = new();
        harness.Open("bare");

        using Screen screen = UiThread.Render("06b-unprocessed", harness.Window, 1440, 900);

        screen.NoRecordToStringAnywhere();
        screen.EveryVisibleButtonShowsItsLabel();
        screen.NoTextOverlaps();

        // A bordered panel holding a button and no words at all is the empty "stale summary"
        // warning that shipped. It must not be drawn for a recording that has no summary.
        screen.NoWordlessPanelWithControls();
    });
}

/// <summary>Paints in the light palette for the length of a test, and puts it back afterwards.</summary>
internal sealed class ThemeScope : IDisposable
{
    private readonly AppTheme _restore = Theme.Current;

    private ThemeScope(AppTheme theme) => Theme.Apply(theme);

    public static ThemeScope Light() => new(AppTheme.Light);

    public void Dispose() => Theme.Apply(_restore);
}

// ==================================================================== harnesses

/// <summary>A real recorder view model over fake devices and a temporary session root.</summary>
internal sealed class RecorderHarness : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeCaptureEngineFactory _engines = new();
    private readonly FakeCaptureClock _clock = new();
    private readonly FakeDiskSpaceProbe _disk = new();
    private readonly RecordingController _controller;
    private readonly List<IDisposable> _owned = [];

    public RecorderHarness()
    {
        FileSessionStore sessions = new(_temp.Path);
        _controller = new RecordingController(sessions, _engines, _clock, _disk);
        Sessions = sessions;
    }

    public FileSessionStore Sessions { get; }

    /// <summary>Ready, with both endpoints auto-selected and the processing panels attached.</summary>
    public MainViewModel Ready()
    {
        MainViewModel model = new(_controller, new FakeDeviceCatalog(), new FakeSettingsStore());
        _owned.Add(model);
        model.MarkReady();
        Attach(model);
        return model;
    }

    /// <summary>
    /// Ready, but the endpoints remembered from last time are not on this machine any more.
    ///
    /// <para>
    /// The state the real application was found in, and the one the fakes hid: two empty pickers
    /// and a dead Start button, because the recorder refuses to substitute a different device.
    /// </para>
    /// </summary>
    public MainViewModel WithADeviceThatIsGone()
    {
        FakeSettingsStore settings = new()
        {
            Current = new Contracts.Settings.AppSettings
            {
                RenderEndpointId = "an-endpoint-that-has-since-been-unplugged",
                CaptureEndpointId = "another-one-that-has-gone",
            },
        };

        MainViewModel model = new(_controller, new FakeDeviceCatalog(), settings);
        _owned.Add(model);
        model.MarkReady();
        Attach(model);
        return model;
    }

    /// <summary>Mid-recording, with a populated ribbon and a running clock.</summary>
    public MainViewModel Capturing()
    {
        MainViewModel model = Ready();

        _controller.Start(new RecordingRequest(
            "render-id", "Headphones", "capture-id", "Headset Microphone"));

        _clock.Advance(TimeSpan.FromMinutes(47));
        _engines.Latest.EmitChunk(Contracts.Audio.SourceTrack.Microphone);
        _engines.Latest.EmitChunk(Contracts.Audio.SourceTrack.System);

        // The ribbon is fed from the same refresh that reads the meters. Give it a conversation.
        for (int second = 0; second < 900; second += 3)
        {
            model.RibbonHistory.Add(
                second,
                Math.Max(0, Math.Sin(second / 41.0) * 0.72),
                true,
                Math.Max(0, Math.Cos(second / 29.0) * 0.85),
                true);
        }

        // The view model reads the recorder on a 200 ms timer, so give it a tick before the
        // shutter opens; otherwise the clock and the byte counts photograph as zero.
        UiThread.Wait(Task.Delay(400));
        return model;
    }

    private void Attach(MainViewModel model)
    {
        FileTranscriptionStore transcripts = new(Sessions);
        FileSummaryStore summaries = new(Sessions);

        // A supervisor pointed at a Python that is not there: the panels render exactly as they do
        // on a machine where the worker has not been installed, which is the state being fixed.
        WorkerLaunchOptions options = new()
        {
            PythonExecutable = Path.Combine(_temp.Path, "no-python.exe"),
            WorkerRoot = _temp.Path,
        };

        TranscriptionCoordinator transcription = new(
            Sessions, transcripts, new WorkerSupervisor(options), new IdleGate());
        _owned.Add(transcription);

        TranscriptionViewModel transcriptionModel = new(transcription, new NoExportPrompt());
        _owned.Add(transcriptionModel);
        model.AttachTranscription(transcriptionModel);

        SummaryCoordinator summary = new(
            Sessions, summaries, transcripts, new WorkerSupervisor(options), new IdleGate());
        _owned.Add(summary);

        SummaryViewModel summaryModel = new(summary);
        _owned.Add(summaryModel);
        model.AttachSummary(summaryModel);
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }

        _controller.Dispose();
        _temp.Dispose();
    }

    private sealed class IdleGate : ICaptureActivityGate
    {
        public bool IsCaptureActive => false;
    }

    private sealed class NoExportPrompt : IExportDestinationPrompt
    {
        public ExportDestination? Ask(string suggestedFileName, TranscriptExportFormat format) => null;
    }
}

/// <summary>A real library over real session folders, with one processed and one bare recording.</summary>
internal sealed class LibraryHarness : IDisposable
{
    private readonly LibraryFixture _fixture = new();
    private readonly SqliteLibraryIndex _index;
    private readonly LibraryViewModel _library;
    private LibraryWindow? _window;

    public LibraryHarness()
    {
        _fixture.AddSession("01PROCESSED", "Deployment planning");
        _fixture.AddTranscript(
            "01PROCESSED",
            ("system", "So the flag stays off in production until we've had a clean internal day."),
            ("microphone", "Agreed. And we should have something written down rather than doing it from memory."),
            ("system", "Alex, can you prepare the deployment checklist and get it out before Thursday's standup?"),
            ("system", "Yeah, I'll have it in the channel by Wednesday night."),
            ("microphone", "Perfect. I'll take the rollback runbook and link it from the ticket."),
            ("system", "One thing we still haven't sorted is the load test — staging is booked solid this week."));

        _fixture.AddSummary(
            "01PROCESSED",
            fromTranscriptRevision: 1,
            decisions: [("Ship behind a feature flag, enabled for internal users first.", "segment-000001")],
            actions:
            [
                ("Prepare the deployment checklist and circulate it before Thursday's standup.", "segment-000003", "Alex"),
                ("Book the staging cluster for a four-hour load test.", "segment-000006", null),
            ],
            overview: "The team settled on a staged rollout behind a feature flag, with the database " +
                      "migration running the night before. Rollback criteria were agreed. The load-test " +
                      "window is still unresolved because the staging cluster is booked.");

        _fixture.AddSession("01BARE", "Vendor call — Northwind");

        _index = _fixture.NewIndex();
        UiThread.Wait(_index.RebuildAsync());

        _library = new LibraryViewModel(
            _index, _fixture.Projection, _fixture.Transcripts, _fixture.Summaries, _fixture.Aliases);

        UiThread.Wait(_library.InitializeAsync());
    }

    /// <summary>Built on the UI thread, because a Window may only be created there.</summary>
    public Func<Window> Window => () => _window ??= new LibraryWindow(_library);

    /// <summary>Opens a recording into the detail workspace, exactly as selecting a row does.</summary>
    public void Open(string which)
    {
        string sessionId = which == "processed" ? "01PROCESSED" : "01BARE";
        MeetingRow row = _library.Meetings.First(m => m.SessionId == sessionId);
        _library.SelectedMeeting = row;
    }

    /// <summary>Selects the first cited claim in the open window, as a click on it would.</summary>
    public void SelectFirstClaim(Window window)
    {
        MeetingViewModel meeting = _library.OpenMeeting
            ?? throw new InvalidOperationException("open a recording first");

        if (window.FindName("SummaryList") is not System.Windows.Controls.ListBox claims)
        {
            throw new InvalidOperationException("the workspace has no claim list");
        }

        claims.SelectedItem = meeting.SummaryLines.First(line => line.HasEvidence);
    }

    public void Dispose()
    {
        _library.Dispose();
        _index.Dispose();
        _fixture.Dispose();
    }
}

/// <summary>Setup over the real manifest that ships beside the build.</summary>
internal sealed class SetupHarness : IDisposable
{
    private readonly EchoForge.Infrastructure.Setup.SetupServices? _services;
    private readonly EchoForge.App.Setup.SetupViewModel? _model;

    public SetupHarness()
    {
        _services = EchoForge.Infrastructure.Setup.SetupServices.TryOpen(out _);
        if (_services is null)
        {
            return;
        }

        // The real endpoint catalogue, so the screen reports this machine rather than a machine
        // with no audio at all. A build that cannot open it still renders; it just says so.
        EchoForge.Contracts.Audio.IAudioDeviceCatalog? audio = null;
        try
        {
            audio = new EchoForge.Audio.Windows.AudioDeviceCatalog();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            audio = null;
        }

        _model = new EchoForge.App.Setup.SetupViewModel(_services, audio);
    }

    public bool Available => _model is not null && _services is not null;

    public Func<Window> Window => () =>
        new EchoForge.App.Setup.SetupWindow(_model!, _services!) { DataContext = _model };

    /// <summary>
    /// Waits for the scan the window kicks off when it loads.
    ///
    /// <para>
    /// Setup probes the machine on Loaded, so photographing it immediately catches a page of empty
    /// cards — which is a real state, but not the one worth checking. Pumped rather than blocked,
    /// because the scan finishes on this same thread.
    /// </para>
    /// </summary>
    public void WaitForScan(Window window)
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            UiThread.Wait(Task.Delay(100));

            if (_model!.Components.Count > 0 && !_model.IsBusy)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _model?.Dispose();
        _services?.Dispose();
    }
}

// ==================================================================== rendering

/// <summary>
/// One STA thread for every rendered screen.
///
/// <para>
/// WPF resources belong to the thread that made them, so the application object, its brushes, and
/// every window built against them have to live on a single thread. Tests marshal onto it, which
/// also serialises them without an xUnit collection.
/// </para>
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Owner = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Dispatcher Start()
    {
        TaskCompletionSource<Dispatcher> ready = new();

        Thread thread = new(() =>
        {
            // The real application object, so every StaticResource in the XAML resolves against the
            // dictionary that actually ships. OnStartup is never called, so nothing is composed and
            // the single-instance mutex is never taken.
            EchoForge.App.App application = new();
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "EchoForge render host",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>Runs a whole test on the render thread: building, rendering and inspecting alike.</summary>
    public static void Run(Action body) => Owner.Value.Invoke(body, DispatcherPriority.Normal);

    public static Screen Render(string name, Func<Window> build, int width, int height, Action<Window>? then = null) =>
        Screen.Capture(name, build(), width, height, then);

    /// <summary>
    /// Waits for a task while still pumping this thread's dispatcher, so work that continues on
    /// the UI thread can finish instead of deadlocking against a blocking wait.
    /// </summary>
    public static void Wait(Task task)
    {
        DispatcherFrame frame = new();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}

/// <summary>A rendered window: the visual tree that produced it, and the bitmap it produced.</summary>
internal sealed class Screen : IDisposable
{
    private readonly string _name;
    private readonly FrameworkElement _root;
    private Window? _window;
    private readonly byte[] _pixels;
    private readonly int _stride;
    private List<FrameworkElement>? _tree;

    private Screen(string name, FrameworkElement root, BitmapSource bitmap)
    {
        _name = name;
        _root = root;
        Width = bitmap.PixelWidth;
        Height = bitmap.PixelHeight;
        _stride = Width * 4;
        _pixels = new byte[_stride * Height];
        bitmap.CopyPixels(_pixels, _stride, 0);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Shows the window off-screen, photographs it, and closes it again.
    ///
    /// <para>
    /// Actually shown, rather than measured and arranged in the abstract, because the parts most
    /// worth checking only exist in a real window: the application's own title bar lives in the
    /// window's <em>template</em>, and a template is not applied to a window that was never shown.
    /// Laying out the content alone would photograph everything except the chrome.
    /// </para>
    /// </summary>
    /// <param name="then">
    /// Run against the window once it is shown, for anything that needs a live visual tree — a
    /// list selection, say, which is how a reader actually gets to the state being photographed.
    /// </param>
    public static Screen Capture(string name, Window window, int width, int height, Action<Window>? then = null)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Left = -32000;
        window.Top = -32000;
        window.Width = width;
        window.Height = height;

        window.Show();
        Settle();

        if (then is not null)
        {
            then(window);
            Settle();
        }

        // The window may refuse a size it has a minimum for; photograph what it actually became.
        FrameworkElement root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        int pixelWidth = Math.Max(1, (int)Math.Round(root.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Round(root.ActualHeight));

        RenderTargetBitmap bitmap = new(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();

        Write(name, bitmap);

        // The window stays open until the caller has finished with the screen. Closing it here
        // tore down the visual tree every check reads, so the checks saw an empty window and said
        // so — which is the same false negative as the false positives they exist to catch.
        return new Screen(name, root, bitmap) { _window = window };
    }

    /// <summary>Closes the window that was photographed.</summary>
    public void Dispose()
    {
        _window?.Close();
        _window = null;
        Settle();
    }

    /// <summary>Lets layout, bindings and the render pass finish before the shutter opens.</summary>
    private static void Settle()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        foreach (DispatcherPriority priority in (DispatcherPriority[])
            [DispatcherPriority.Loaded, DispatcherPriority.Render, DispatcherPriority.ContextIdle])
        {
            dispatcher.Invoke(() => { }, priority);
        }
    }

    /// <summary>Writes the PNG next to the repository, where a person can open it.</summary>
    private static void Write(string name, BitmapSource bitmap)
    {
        string directory = Path.Combine(RepositoryRoot(), "artifacts", "ui");
        Directory.CreateDirectory(directory);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EchoForge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    // ---------------------------------------------------------------- checks

    /// <summary>Every button on screen must put marks on its own plate. A blank plate is the bug.</summary>
    public void EveryVisibleButtonShowsItsLabel()
    {
        List<string> blank = [];

        foreach (ButtonBase button in Visible<ButtonBase>())
        {
            // A picker's chevron toggle and other purely decorative parts carry no label; what
            // they draw is checked where the control itself is checked.
            string label = Label(button);
            if (label.Length == 0 || !MostlyOnScreen(button))
            {
                continue;
            }

            long marks = Marks(BoundsOf(button));
            if (marks < MinimumGlyphPixels(label.Length))
            {
                blank.Add($"\"{label}\" [{marks} glyph pixels]");
            }
        }

        Assert.True(blank.Count == 0, $"{_name}: buttons rendered blank: {string.Join(", ", blank)}");
    }

    public void ButtonIsReadable(string label)
    {
        ButtonBase button = Visible<ButtonBase>().FirstOrDefault(b => Label(b) == label)
            ?? throw new Xunit.Sdk.XunitException($"{_name}: no visible button labelled \"{label}\"");

        long marks = Marks(BoundsOf(button));
        Assert.True(
            marks >= MinimumGlyphPixels(label.Length),
            $"{_name}: the \"{label}\" button drew no label ({marks} contrasting pixels)");
    }

    public void ButtonIsEnabled(string label)
    {
        ButtonBase button = All<ButtonBase>().FirstOrDefault(b => Label(b) == label)
            ?? throw new Xunit.Sdk.XunitException($"{_name}: no button labelled \"{label}\"");

        Assert.True(button.IsEnabled, $"{_name}: the \"{label}\" button is disabled");
    }

    /// <summary>A closed picker must paint the item it has selected.</summary>
    public void EveryComboBoxPaintsItsSelection()
    {
        List<string> empty = [];

        foreach (ComboBox box in Visible<ComboBox>())
        {
            // A picker with nothing selected and nothing to ask for — an empty revision list —
            // is legitimately blank. One that has a selection, or a prompt to show instead, is
            // not allowed to be.
            if (box.SelectedItem is null && box.Tag is not string { Length: > 0 })
            {
                continue;
            }

            // The chevron lives on the right; the selection box is what has to carry the text.
            Rect bounds = BoundsOf(box);
            Rect selection = new(bounds.Left, bounds.Top, Math.Max(1, bounds.Width - 28), bounds.Height);

            long marks = Marks(selection);
            if (marks < MinimumGlyphPixels(1))
            {
                empty.Add($"{box.SelectedItem?.GetType().Name ?? "prompt"} [{marks} glyph pixels]");
            }
        }

        Assert.True(empty.Count == 0, $"{_name}: pickers with an unpainted selection: {string.Join(", ", empty)}");
    }

    /// <summary>No screen may ever show a record's compiler-generated ToString.</summary>
    public void NoRecordToStringAnywhere()
    {
        string[] tells = ["{ Id =", "{ Code =", "{ Format =", "{ Revision =", "Option {", "Info {", "{ SampleRate ="];

        List<string> dumps =
        [
            .. All<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .Where(text => tells.Any(tell => text.Contains(tell, StringComparison.Ordinal)))
                .Select(text => text.Length > 70 ? text[..70] + "…" : text)
        ];

        Assert.True(dumps.Count == 0, $"{_name}: raw DTO text on screen: {string.Join(" | ", dumps)}");
    }

    /// <summary>A phrase that must not appear anywhere on the screen.</summary>
    public void NoTextMatches(string text)
    {
        List<string> found =
        [
            .. All<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .Where(t => t.Contains(text, StringComparison.Ordinal))
        ];

        Assert.True(found.Count == 0, $"{_name}: \"{text}\" is on screen: {string.Join(" | ", found)}");
    }

    public void TextIsPainted(string text)
    {
        TextBlock block = Visible<TextBlock>()
                .FirstOrDefault(t => (t.Text ?? string.Empty).Contains(text, StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"{_name}: \"{text}\" is not on screen");

        Rect where = BoundsOf(block);
        long marks = Marks(where);
        Assert.True(
            marks >= MinimumGlyphPixels(text.Length),
            $"{_name}: \"{text}\" was not painted ({marks} glyph pixels at {where}; {Describe(where)})");
    }

    /// <summary>
    /// The clock rendered as a hairline when Cascadia Mono was absent and the weight was Light.
    /// A solid face covers a real share of its box; a wireframe fallback covers almost none.
    /// </summary>
    public void ClockIsSolid()
    {
        TextBlock clock = Visible<TextBlock>()
                .Where(t => (t.Text ?? string.Empty).Length == 8 && (t.Text ?? string.Empty)[2] == ':')
                .OrderByDescending(t => t.FontSize)
                .FirstOrDefault()
            ?? throw new Xunit.Sdk.XunitException($"{_name}: no clock on screen");

        Rect bounds = BoundsOf(clock);
        double coverage = Marks(bounds) / Math.Max(1.0, bounds.Width * bounds.Height);
        Assert.True(coverage >= 0.10, $"{_name}: the clock \"{clock.Text}\" rendered thin ({coverage:P1} coverage)");
    }

    /// <summary>
    /// The transport either is not there, or it reads a real position. What shipped was a visible
    /// transport with a lone separator in it, because the row was drawn without its data context.
    /// </summary>
    public void PlaybackPositionIsNeverABareSeparator()
    {
        // "<position> / <duration>", and nothing else — prose that happens to contain a slash,
        // such as "Copy for ChatGPT / Claude…", is not a transport readout.
        foreach (TextBlock position in Visible<TextBlock>()
            .Where(t => System.Text.RegularExpressions.Regex.IsMatch(
                t.Text ?? string.Empty, @"^\s*[\d:.]*\s*/\s*[\d:.]*\s*$")))
        {
            Assert.True(
                position.Text.Any(char.IsDigit),
                $"{_name}: the transport shows a position with no time in it: \"{position.Text.Trim()}\"");
        }
    }

    /// <summary>
    /// Both halves of the library live in one Grid cell. The one that is not showing must be
    /// collapsed, or it lands on top of the one that is.
    /// </summary>
    public void DetailWorkspaceIsCollapsed()
    {
        bool backButtonShowing = Visible<ButtonBase>()
            .Any(b => Label(b).Contains("All recordings", StringComparison.Ordinal));

        Assert.False(
            backButtonShowing,
            $"{_name}: the recording workspace is drawn over the list — its back button is visible");
    }

    /// <summary>Text may not be drawn on top of other text.</summary>
    public void NoTextOverlaps()
    {
        List<(TextBlock Block, Rect Bounds)> blocks =
        [
            .. Visible<TextBlock>()
                .Where(t => !string.IsNullOrWhiteSpace(t.Text))
                .Select(t => (t, InkBoundsOf(t)))
                .Where(p => p.Item2.Width > 1 && p.Item2.Height > 1)
        ];

        List<string> collisions = [];

        for (int i = 0; i < blocks.Count; i++)
        {
            for (int j = i + 1; j < blocks.Count; j++)
            {
                Rect a = blocks[i].Bounds;
                Rect b = blocks[j].Bounds;
                if (!a.IntersectsWith(b))
                {
                    continue;
                }

                Rect shared = Rect.Intersect(a, b);
                double smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);

                // A hairline touch is kerning between neighbours, not a collision.
                if (shared.Width * shared.Height < smaller * 0.3)
                {
                    continue;
                }

                if (Contains(blocks[i].Block, blocks[j].Block) || Contains(blocks[j].Block, blocks[i].Block))
                {
                    continue;
                }

                collisions.Add($"\"{Short(blocks[i].Block.Text)}\" over \"{Short(blocks[j].Block.Text)}\"");
            }
        }

        Assert.True(collisions.Count == 0, $"{_name}: overlapping text: {string.Join(" | ", collisions.Take(8))}");
    }

    /// <summary>
    /// The light palette has to be an actually light page with dark writing on it.
    ///
    /// <para>
    /// Checked on the pixels because the failure this catches is a control that kept a colour from
    /// the other palette — a frozen brush, or a hex spelled into a template — which leaves a dark
    /// slab on a white page that no structural check would notice.
    /// </para>
    /// </summary>
    public void PageIsLight()
    {
        long light = 0;
        long total = 0;

        for (int y = 0; y < Height; y += 3)
        {
            for (int x = 0; x < Width; x += 3)
            {
                total++;
                if (Level(x, y) > 150)
                {
                    light++;
                }
            }
        }

        double fraction = (double)light / Math.Max(1, total);
        Assert.True(fraction > 0.55, $"{_name}: only {fraction:P0} of the page is light — the light theme did not take");
    }

    /// <summary>A bordered panel offering a button and no words at all looks broken, and is.</summary>
    public void NoWordlessPanelWithControls()
    {
        List<string> empties = [];

        foreach (Border border in Visible<Border>())
        {
            if (border.BorderThickness.Top < 0.5 || border.BorderBrush is null)
            {
                continue;
            }

            Rect bounds = BoundsOf(border);
            if (bounds.Width < 140 || bounds.Height < 28)
            {
                continue;
            }

            List<FrameworkElement> inside = [.. Descendants(border)];
            bool words = inside.OfType<TextBlock>().Any(t => Shown(t) && !string.IsNullOrWhiteSpace(t.Text));
            bool controls = inside.OfType<ButtonBase>().Any(Shown);

            if (controls && !words)
            {
                empties.Add($"{bounds.Width:0}x{bounds.Height:0} at {bounds.Left:0},{bounds.Top:0}");
            }
        }

        Assert.True(empties.Count == 0, $"{_name}: bordered panels with a button and no text: {string.Join(", ", empties)}");
    }

    // ---------------------------------------------------------------- pixels

    /// <summary>
    /// True when a rectangle holds glyph-like marks: pixels far enough from the rectangle's own
    /// dominant colour to read as text or an icon against it. This is the check that catches light
    /// text on a light button, which every structural test in this repository missed.
    /// </summary>
    /// <summary>
    /// Enough anti-aliased pixels to be writing rather than a stray edge, scaled to how much
    /// writing there is: a revision picker showing "1" cannot be held to the same count as a
    /// button reading "Start recording".
    /// </summary>
    private static int MinimumGlyphPixels(int characters) => Math.Max(8, Math.Min(120, 7 * characters));

    private long Marks(Rect rect)
    {
        int x0 = Math.Max(0, (int)Math.Ceiling(rect.Left));
        int y0 = Math.Max(0, (int)Math.Ceiling(rect.Top));
        int x1 = Math.Min(Width, (int)Math.Floor(rect.Right));
        int y1 = Math.Min(Height, (int)Math.Floor(rect.Bottom));

        if (x1 - x0 < 4 || y1 - y0 < 4)
        {
            return long.MaxValue;
        }

        int[] histogram = new int[256];
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                histogram[Level(x, y)]++;
            }
        }

        int fill = 0;
        for (int i = 1; i < 256; i++)
        {
            if (histogram[i] > histogram[fill])
            {
                fill = i;
            }
        }

        long marks = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                if (Math.Abs(Level(x, y) - fill) >= 40)
                {
                    marks++;
                }
            }
        }

        return marks;
    }

    private string Describe(Rect rect)
    {
        int x0 = Math.Max(0, (int)rect.Left), y0 = Math.Max(0, (int)rect.Top);
        int x1 = Math.Min(Width, (int)rect.Right), y1 = Math.Min(Height, (int)rect.Bottom);
        int lo = 255, hi = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int v = Level(x, y);
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
        }

        return $"levels {lo}..{hi} over {x1 - x0}x{y1 - y0}";
    }

    private int Level(int x, int y)
    {
        int i = (y * _stride) + (x * 4);
        return (int)Math.Clamp((0.2126 * _pixels[i + 2]) + (0.7152 * _pixels[i + 1]) + (0.0722 * _pixels[i]), 0, 255);
    }

    // ---------------------------------------------------------------- tree

    private IReadOnlyList<FrameworkElement> Tree => _tree ??= [.. Walk(_root)];

    private IEnumerable<T> All<T>() where T : FrameworkElement => Tree.OfType<T>();

    /// <summary>
    /// On screen: laid out, not collapsed anywhere up its chain, and inside the bitmap.
    ///
    /// <para>
    /// Deliberately not <see cref="UIElement.IsVisible"/>. That property is false for every element
    /// of a window that was never shown, which would quietly empty every check here and hand back
    /// the same false pass the structural smoke tests gave.
    /// </para>
    /// </summary>
    private IEnumerable<T> Visible<T>() where T : FrameworkElement =>
        Tree.OfType<T>().Where(e =>
        {
            if (e.ActualWidth < 2 || e.ActualHeight < 2 || !Shown(e))
            {
                return false;
            }

            Rect b = BoundsOf(e);
            return b.Right > 0 && b.Bottom > 0 && b.Left < Width && b.Top < Height;
        });

    /// <summary>
    /// Enough of an element is inside its scrolling viewport to be judged on what it draws.
    ///
    /// <para>
    /// A button whose top two pixels peek above the bottom of a scroller really does show no
    /// label, and saying so would be a complaint about scrolling rather than about rendering.
    /// </para>
    /// </summary>
    private bool MostlyOnScreen(FrameworkElement element)
    {
        Rect clipped = BoundsOf(element);
        return !clipped.IsEmpty && clipped.Height >= element.ActualHeight * 0.7;
    }

    private bool Shown(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }

            if (ReferenceEquals(node, _root))
            {
                break;
            }
        }

        return true;
    }

    private Rect BoundsOf(FrameworkElement element)
    {
        try
        {
            Rect bounds = element.TransformToAncestor(_root)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

            // Anything scrolled out of its viewport is not on screen, however far its layout slot
            // runs. Without this, prose clipped by a ScrollViewer reads as colliding with whatever
            // is drawn below the scroller.
            for (DependencyObject? node = VisualTreeHelper.GetParent(element);
                 node is not null && !ReferenceEquals(node, _root);
                 node = VisualTreeHelper.GetParent(node))
            {
                if (node is not FrameworkElement { } ancestor)
                {
                    continue;
                }

                if (ancestor is not ScrollViewer and not ScrollContentPresenter && !ancestor.ClipToBounds)
                {
                    continue;
                }

                bounds.Intersect(ancestor.TransformToAncestor(_root)
                    .TransformBounds(new Rect(0, 0, ancestor.ActualWidth, ancestor.ActualHeight)));

                if (bounds.IsEmpty)
                {
                    return Rect.Empty;
                }
            }

            // RenderTargetBitmap draws the root at its own visual offset — the margin the window
            // gives its content — while TransformToAncestor stops at the root's origin. Without
            // this the measured rectangles sit a margin up and to the left of the pixels they are
            // meant to be reading, which reads as "painted nothing".
            Vector origin = VisualTreeHelper.GetOffset(_root);
            bounds.Offset(origin.X, origin.Y);
            return bounds;
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    /// <summary>
    /// Where a TextBlock's glyphs actually land, rather than the slot it was given.
    ///
    /// <para>
    /// A stretched TextBlock owns the full width of its cell while painting a short word at the
    /// left of it, so comparing layout slots reports collisions that nobody can see and hides the
    /// real ones in the noise.
    /// </para>
    /// </summary>
    private Rect InkBoundsOf(TextBlock block)
    {
        Rect slot = BoundsOf(block);
        if (slot.IsEmpty || slot.Width <= 1)
        {
            return slot;
        }

        FormattedText text = new(
            block.Text,
            CultureInfo.CurrentCulture,
            block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(block).PixelsPerDip);

        double width = Math.Min(slot.Width, text.Width + block.Padding.Left + block.Padding.Right + 1);

        double left = block.TextAlignment switch
        {
            TextAlignment.Right => slot.Right - width,
            TextAlignment.Center => slot.Left + ((slot.Width - width) / 2),
            _ => slot.Left,
        };

        return new Rect(left, slot.Top, width, slot.Height);
    }

    /// <summary>
    /// The words a button puts on its own plate, or empty for one that carries an icon instead.
    ///
    /// <para>
    /// Only text content counts. An icon-only button — a picker's chevron, a calendar glyph, a rail
    /// destination — says what it is through its tooltip and its accessibility name, which is a
    /// different claim and is checked elsewhere.
    /// </para>
    /// </summary>
    private static string Label(ButtonBase button) => button.Content switch
    {
        string s => s,
        TextBlock t => t.Text ?? string.Empty,
        _ => string.Empty,
    };

    private static string Short(string text) =>
        (text.Length > 36 ? text[..36] + "…" : text).Replace('\n', ' ').Replace('\r', ' ');

    private static bool Contains(DependencyObject child, DependencyObject ancestor)
    {
        for (DependencyObject? node = child; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<FrameworkElement> Walk(DependencyObject root)
    {
        Queue<DependencyObject> queue = new();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            DependencyObject node = queue.Dequeue();
            if (node is FrameworkElement element)
            {
                yield return element;
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root) => Walk(root).Skip(1);
}
