#:project ../src/EchoForge.App/EchoForge.App.csproj
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// The manual ChatGPT/Claude handoff dialog, opened for real against the real application resources.
//
// The unit suite covers the composer, the writer and the view model. What it cannot cover is the
// window itself: a missing StaticResource is not a compile error and not a binding warning; it
// throws when the window loads. This constructs the real ManualHandoffWindow, confirms it renders,
// drives copy and save through a fake clipboard, and prints the payload so a human can read exactly
// what would leave. Everything here is synthetic; no network call is made, and nothing is pasted
// into a real assistant.
//
//   dotnet run scripts/smoke-manual-handoff.cs

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EchoForge.App.Library;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.ManualCopy;

List<string> failures = [];
void Check(bool condition, string what)
{
    Console.WriteLine((condition ? "  ok    " : "  FAIL  ") + what);
    if (!condition) { failures.Add(what); }
}

Console.WriteLine("EchoForge manual-handoff smoke test");
Console.WriteLine();

// -- a synthetic transcript, built in memory (no store, no files) -----------------------------
TranscriptSegment Segment(int i, string track, string text)
{
    (string speakerId, string speakerName) = TranscriptSpeakers.For(track);
    return new TranscriptSegment
    {
        Id = $"segment-{i:D6}",
        Epoch = 1,
        StartSeconds = (i - 1) * 12.0,
        EndSeconds = ((i - 1) * 12.0) + 9.0,
        SpeakerId = speakerId,
        SpeakerName = speakerName,
        SourceTrack = track,
        Text = text,
        Confidence = null,
        Language = "en",
        Words = [],
    };
}

TranscriptDocument transcript = new()
{
    SessionId = "01JSMOKEHANDOFF",
    TranscriptRevision = 4,
    CreatedAtUtc = new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero),
    DurationSeconds = 48,
    Model = new TranscriptModel("echoforge", "faster-whisper", "large-v3-turbo", "rev", "cuda-fp16", true, "1.0.0"),
    Epochs = [new TranscriptEpoch(1, 0, 48)],
    Speakers =
    [
        new TranscriptSpeaker(TranscriptSpeakers.YouId, TranscriptSpeakers.YouName, TranscriptSpeakers.MicrophoneTrack),
        new TranscriptSpeaker(TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName, TranscriptSpeakers.SystemTrack),
    ],
    Languages = [new TranscriptLanguage(TranscriptSpeakers.MicrophoneTrack, "en", null)],
    Segments =
    [
        Segment(1, "microphone", "Let's decide the beta date — I say Friday."),
        Segment(2, "system", "Friday works. I'll write the release notes."),
        Segment(3, "microphone", "Actually, let's move it to Monday to be safe."),
        Segment(4, "system", "Someone should also update the changelog — déjà-vu, 日本語."),
    ],
};

string scratch = Path.Combine(Path.GetTempPath(), "echoforge-smoke-handoff", Guid.NewGuid().ToString("n"));
Directory.CreateDirectory(scratch);

// A fake clipboard, so the smoke never fights the real one and we can prove what was copied.
CapturingClipboard clipboard = new();

Exception? windowFailure = null;
List<string> checks = [];
string previewSnapshot = string.Empty;

Thread ui = new(() =>
{
    try
    {
        EchoForge.App.App application = new();
        application.InitializeComponent();

        ManualHandoffViewModel model = new(
            options => ManualHandoffComposer.Compose(transcript, null, options, "Local overview: the team debated the beta date."),
            clipboard,
            ManualHandoffComposer.SuggestFileName(transcript));

        ManualHandoffWindow window = new(model)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
        };

        window.Show();
        Pump();
        checks.Add("ok:the manual-handoff window loads against the real application resources");

        // The preview is present and full.
        List<DependencyObject> tree = [.. Descendants(window)];
        bool hasPreview = tree.Any(n => n is TextBox tb && tb.Text.Contains("segment-000001", StringComparison.Ordinal));
        checks.Add((hasPreview ? "ok:" : "FAIL:") + "the editable preview shows the transcript");
        checks.Add((model.IsAvailable && model.CanCopy ? "ok:" : "FAIL:") + "the dialog is ready to copy");
        checks.Add((clipboard.Attempts == 0 ? "ok:" : "FAIL:") + "opening the dialog copied nothing");

        previewSnapshot = model.PreviewText;

        // Copy on demand.
        model.Copy();
        checks.Add((clipboard.Attempts == 1 && clipboard.LastText == model.PreviewText ? "ok:" : "FAIL:") + "copy places the exact preview on the clipboard");

        // Save to a file.
        string savePath = Path.Combine(scratch, model.SuggestedFileName);
        var save = model.Save(savePath);
        bool saved = save.Succeeded && File.Exists(savePath) && File.ReadAllText(savePath) == model.PreviewText;
        checks.Add((saved ? "ok:" : "FAIL:") + "save writes the preview to a UTF-8 file");

        window.Close();
        Pump();
    }
    catch (Exception ex)
    {
        windowFailure = ex;
    }
    finally
    {
        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        Dispatcher.Run();
    }
});
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
if (!ui.Join(TimeSpan.FromSeconds(60)))
{
    Console.WriteLine("  FAIL  the manual-handoff window did not finish in time");
    Environment.Exit(1);
}

if (windowFailure is not null)
{
    Console.WriteLine("  FAIL  the window threw: " + windowFailure);
    Environment.Exit(1);
}

foreach (string check in checks)
{
    bool ok = check.StartsWith("ok:", StringComparison.Ordinal);
    Check(ok, check[(check.IndexOf(':') + 1)..]);
}

// Also compose a subset directly, to prove selection narrows the payload.
ManualHandoffResult subset = ManualHandoffComposer.Compose(
    transcript,
    null,
    new ManualHandoffOptions { IncludedSegmentIds = new HashSet<string>(StringComparer.Ordinal) { "segment-000003" } });
Check(subset.Succeeded && subset.Payload!.IncludedSegmentCount == 1 && subset.Payload.IsSubset, "a selected subset narrows to just the chosen segment");

// And prove the empty selection is refused.
ManualHandoffResult empty = ManualHandoffComposer.Compose(
    transcript, null, new ManualHandoffOptions { IncludedSegmentIds = new HashSet<string>(StringComparer.Ordinal) });
Check(!empty.Succeeded, "an empty selection is refused");

Console.WriteLine();
Console.WriteLine("---- payload as it would be copied (synthetic, terminal only) ----");
Console.WriteLine(previewSnapshot);
Console.WriteLine("---- end payload ----");

try { Directory.Delete(scratch, true); } catch { }

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("  result  PASS");
    Environment.Exit(0);
}
Console.WriteLine("  result  FAIL");
Environment.Exit(1);

// -- helpers ----------------------------------------------------------------------------------

void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

static IEnumerable<DependencyObject> Descendants(DependencyObject root)
{
    Queue<DependencyObject> queue = new();
    queue.Enqueue(root);
    while (queue.Count > 0)
    {
        DependencyObject node = queue.Dequeue();
        yield return node;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
    }
}

sealed class CapturingClipboard : IClipboardWriter
{
    public int Attempts { get; private set; }
    public string? LastText { get; private set; }

    public ClipboardResult TrySetText(string text)
    {
        Attempts++;
        LastText = text;
        return ClipboardResult.Ok();
    }
}
