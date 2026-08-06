using System.Diagnostics;
using EchoForge.App;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Exports;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.UnitTests;

/// <summary>
/// The transcription surface.
///
/// <para>
/// These exercise the view model rather than the window, which is where every decision worth
/// testing lives: what is enabled, what the user is told, and whether anything slow happens on
/// the thread that paints the recording indicator.
/// </para>
/// </summary>
public sealed class TranscriptionViewModelTests : IDisposable
{
    private const string SessionId = "01JVIEWMODEL";

    private readonly TempDirectory _temp = new();
    private readonly FileSessionStore _sessions;
    private readonly FileTranscriptionStore _transcripts;
    private readonly SwitchableGate _gate = new();
    private readonly RecordingPrompt _prompt = new();
    private TranscriptionCoordinator? _coordinator;
    private TranscriptionViewModel? _viewModel;

    public TranscriptionViewModelTests()
    {
        _sessions = new FileSessionStore(_temp.Path);
        _transcripts = new FileTranscriptionStore(_sessions);
    }

    public void Dispose()
    {
        _viewModel?.Dispose();
        _coordinator?.Dispose();
        _temp.Dispose();
    }

    private TranscriptionViewModel Given(bool realWorker = true, string? moduleName = null)
    {
        WorkerLaunchOptions options = realWorker
            ? WorkerTestEnvironment.Options(
                workerRoot: moduleName is null ? null : WorkerTestEnvironment.StubRoot,
                moduleName: moduleName)
            : new WorkerLaunchOptions
            {
                PythonExecutable = Path.Combine(_temp.Path, "no-python.exe"),
                WorkerRoot = _temp.Path,
            };

        _coordinator = new TranscriptionCoordinator(
            _sessions, _transcripts, new WorkerSupervisor(options), _gate);

        _viewModel = new TranscriptionViewModel(_coordinator, _prompt);
        return _viewModel;
    }

    private static void Ready(TranscriptionViewModel viewModel, bool recording = false, bool shuttingDown = false) =>
        viewModel.UpdateHost(SessionId, sessionSettled: true, recording, hostReady: true, shuttingDown);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 90)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    // -- availability ------------------------------------------------------------------------

    [Fact]
    public void TranscribeIsUnavailableWithoutARecording()
    {
        TranscriptionViewModel viewModel = Given(realWorker: false);

        Assert.False(viewModel.CanTranscribe);
        Assert.False(viewModel.TranscribeCommand.CanExecute(null));
        Assert.Equal("No recording selected", viewModel.StageText);
    }

    [Fact]
    public void TranscribeBecomesAvailableOnceARecordingHasSettled()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given(realWorker: false);

        Ready(viewModel);

        Assert.True(viewModel.CanTranscribe);
        Assert.True(viewModel.TranscribeCommand.CanExecute(null));
        Assert.Equal("Not transcribed", viewModel.StageText);
    }

    [Fact]
    public void TranscribeIsUnavailableWhileCaptureIsLive()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given(realWorker: false);

        viewModel.UpdateHost(SessionId, sessionSettled: false, recordingActive: true, hostReady: true, shuttingDown: false);

        Assert.False(viewModel.CanTranscribe);
        Assert.False(viewModel.TranscribeCommand.CanExecute(null));
    }

    [Fact]
    public void TranscribeIsUnavailableWhileStartupRecoveryIsStillRunning()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given(realWorker: false);

        viewModel.UpdateHost(SessionId, sessionSettled: true, recordingActive: false, hostReady: false, shuttingDown: false);

        Assert.False(viewModel.CanTranscribe);
    }

    [Fact]
    public void EveryActionIsUnavailableOnceTheAppIsClosing()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given(realWorker: false);

        Ready(viewModel, shuttingDown: true);

        Assert.False(viewModel.CanTranscribe);
        Assert.False(viewModel.CanTranscribeAgain);
        Assert.False(viewModel.CanCancel);
        Assert.False(viewModel.CanExport);
    }

    [Fact]
    public void ExportIsUnavailableUntilSomethingHasBeenActivated()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given(realWorker: false);

        Ready(viewModel);

        Assert.False(viewModel.CanExport);
        Assert.False(viewModel.ExportCommand.CanExecute(null));
    }

    // -- the placeholder warning ------------------------------------------------------------------

    [Fact]
    public void ThePlaceholderWarningIsShownBeforeAnythingHasEvenRun()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given(realWorker: false);

        Ready(viewModel);

        // The warning is about what the user is about to get, not only about what they have.
        Assert.True(viewModel.IsPlaceholderBackend);
        Assert.Contains("no speech recognition", viewModel.PlaceholderWarning, StringComparison.Ordinal);
    }

    // -- a real run ---------------------------------------------------------------------------------

    [WorkerFact]
    public async Task TranscribingReportsProgressAndEndsWithAnActivatedRevision()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given();
        Ready(viewModel);

        List<double> progress = [];
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptionViewModel.ProgressPercent))
            {
                lock (progress) { progress.Add(viewModel.ProgressPercent); }
            }
        };

        viewModel.TranscribeCommand.Execute(null);

        Assert.True(await WaitUntilAsync(() => viewModel.HasTranscript));

        Assert.Equal("Transcribed", viewModel.StageText);
        Assert.Equal(1, viewModel.SelectedRevision!.Revision);
        Assert.Contains("Version 1", viewModel.TranscriptSummary, StringComparison.Ordinal);
        Assert.True(viewModel.CanExport);
        Assert.False(viewModel.HasError);

        lock (progress)
        {
            Assert.NotEmpty(progress);
        }
    }

    [WorkerFact]
    public async Task StartingATranscriptionDoesNotBlockTheCallingThread()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given();
        Ready(viewModel);

        // The command is what a button click reaches. It must hand off and return, because on a
        // real window this thread is the one painting the recording indicator.
        Stopwatch clock = Stopwatch.StartNew();
        viewModel.TranscribeCommand.Execute(null);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(250), $"Execute blocked for {clock.Elapsed}");

        Assert.True(await WaitUntilAsync(() => viewModel.HasTranscript));
    }

    [WorkerFact]
    public async Task ASecondRunProducesASecondVersionAndBothStaySelectable()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given();
        Ready(viewModel);

        viewModel.TranscribeCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => viewModel.HasTranscript));

        Assert.True(viewModel.CanTranscribeAgain);
        viewModel.TranscribeAgainCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => viewModel.Revisions.Count == 2));

        Assert.Equal(2, viewModel.SelectedRevision!.Revision);

        // Choosing an older version is a real, durable choice.
        TranscriptRevisionOption older = viewModel.Revisions.First(r => r.Revision == 1);
        viewModel.SelectedRevision = older;

        Assert.Equal(1, viewModel.SelectedRevision!.Revision);
        Assert.Equal(1, _transcripts.Read(SessionId).SelectedRevision);
    }

    [WorkerFact]
    public async Task EveryVersionIsLabelledAsAPlaceholderWhileThatIsWhatProducedIt()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given();
        Ready(viewModel);

        viewModel.TranscribeCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => viewModel.HasTranscript));

        Assert.True(viewModel.IsPlaceholderBackend);
        Assert.Contains("placeholder", viewModel.Revisions[0].Label, StringComparison.Ordinal);
        Assert.Contains("mock", viewModel.BackendSummary, StringComparison.Ordinal);
        Assert.Contains("worker", viewModel.BackendSummary, StringComparison.Ordinal);
    }

    // -- cancellation and errors ---------------------------------------------------------------------

    [WorkerFact]
    public async Task CancellingStopsTheJobAndSaysSoWithoutRaisingAnError()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given();
        Ready(viewModel);

        _coordinator!.Request(SessionId, new TranscriptionOptions
        {
            TestMode = "delay",
            TestDelaySeconds = 60,
        });

        Assert.True(await WaitUntilAsync(() => viewModel.IsWorking || _coordinator.IsRunning));
        Assert.True(viewModel.CanCancel);

        viewModel.CancelCommand.Execute(null);

        Assert.True(await WaitUntilAsync(() => !_coordinator.IsRunning));
        Ready(viewModel);

        Assert.False(viewModel.HasError);
        Assert.False(viewModel.HasTranscript);
    }

    [WorkerFact]
    public async Task AFailedRunShowsAnActionableMessageAndNamesNoPath()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given(moduleName: "stub_lying_result");
        Ready(viewModel);

        viewModel.TranscribeCommand.Execute(null);

        Assert.True(await WaitUntilAsync(() => viewModel.HasError));

        Assert.Contains("EchoForge", viewModel.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(_temp.Path, viewModel.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SessionId, viewModel.Error!, StringComparison.Ordinal);
        Assert.False(viewModel.HasTranscript);
    }

    [Fact]
    public async Task ARecordingWhoseAudioChangedIsRefusedWithAPlainExplanation()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        string chunk = Path.Combine(_sessions.Resolve(SessionId).Root, "tracks", "microphone", "chunks", "000001.wav");
        byte[] bytes = File.ReadAllBytes(chunk);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(chunk, bytes);

        TranscriptionViewModel viewModel = Given(realWorker: false);
        Ready(viewModel);

        viewModel.TranscribeCommand.Execute(null);

        Assert.True(await WaitUntilAsync(() => viewModel.HasError, timeoutSeconds: 20));
        Assert.Contains("no longer matches", viewModel.Error!, StringComparison.Ordinal);
    }

    // -- exporting ---------------------------------------------------------------------------------------

    [WorkerFact]
    public async Task ExportingWritesTheChosenFormatToTheChosenPlace()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given();
        Ready(viewModel);

        viewModel.TranscribeCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => viewModel.HasTranscript));

        string destination = Path.Combine(_temp.Path, "exported.srt");
        _prompt.Answer = new ExportDestination(destination, OverwriteConfirmed: false);
        viewModel.SelectedExportFormat = viewModel.ExportFormats.First(f => f.Format == TranscriptExportFormat.Srt);

        viewModel.ExportCommand.Execute(null);

        Assert.True(await WaitUntilAsync(() => File.Exists(destination), timeoutSeconds: 30));
        Assert.Contains("-->", await File.ReadAllTextAsync(destination), StringComparison.Ordinal);
        Assert.Contains("Exported", viewModel.Notice!, StringComparison.Ordinal);
    }

    [WorkerFact]
    public async Task CancellingTheSaveDialogWritesNothing()
    {
        WorkerTestEnvironment.CreateRecordedSession(_sessions, SessionId);
        TranscriptionViewModel viewModel = Given();
        Ready(viewModel);

        viewModel.TranscribeCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => viewModel.HasTranscript));

        _prompt.Answer = null;
        viewModel.ExportCommand.Execute(null);

        await Task.Delay(300);

        Assert.Empty(Directory.GetFiles(_temp.Path, "*.srt", SearchOption.TopDirectoryOnly));
        Assert.False(viewModel.HasError);
    }

    private sealed class SwitchableGate : EchoForge.Contracts.Workers.ICaptureActivityGate
    {
        public bool IsCaptureActive { get; set; }
    }

    private sealed class RecordingPrompt : IExportDestinationPrompt
    {
        public ExportDestination? Answer { get; set; }

        public string? LastSuggestion { get; private set; }

        public ExportDestination? Ask(string suggestedFileName, TranscriptExportFormat format)
        {
            LastSuggestion = suggestedFileName;
            return Answer;
        }
    }
}
