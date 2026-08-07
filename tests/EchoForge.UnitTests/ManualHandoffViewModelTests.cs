using EchoForge.App.Library;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Exports;
using EchoForge.Core.ManualCopy;

namespace EchoForge.UnitTests;

/// <summary>
/// The manual-handoff dialog's logic: it copies only on demand, reports a busy clipboard rather than
/// crashing, keeps its counts honest as the preview is edited, and saves what the preview shows.
/// </summary>
public sealed class ManualHandoffViewModelTests
{
    private sealed class FakeClipboard : IClipboardWriter
    {
        public int Attempts { get; private set; }
        public string? LastText { get; private set; }
        public bool Fail { get; set; }

        public ClipboardResult TrySetText(string text)
        {
            Attempts++;
            if (Fail)
            {
                return ClipboardResult.Fail("The clipboard is in use by another application.");
            }

            LastText = text;
            return ClipboardResult.Ok();
        }
    }

    private static TranscriptDocument Transcript() => BuildTranscript(
        ("microphone", "We will ship on Friday"),
        ("system", "I will write the notes"));

    private static TranscriptDocument BuildTranscript(params (string Track, string Text)[] lines)
    {
        List<TranscriptSegment> segments = [];
        for (int i = 0; i < lines.Length; i++)
        {
            (string track, string text) = lines[i];
            (string speakerId, string speakerName) = TranscriptSpeakers.For(track);
            segments.Add(new TranscriptSegment
            {
                Id = $"segment-{i + 1:D6}",
                Epoch = 1,
                StartSeconds = i * 10.0,
                EndSeconds = (i * 10.0) + 8.0,
                SpeakerId = speakerId,
                SpeakerName = speakerName,
                SourceTrack = track,
                Text = text,
                Confidence = null,
                Language = "en",
                Words = [],
            });
        }

        return new TranscriptDocument
        {
            SessionId = "01JVM",
            TranscriptRevision = 2,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            DurationSeconds = Math.Max(1, lines.Length * 10.0),
            Model = new TranscriptModel("echoforge", "faster-whisper", "large-v3-turbo", "rev", "cuda-fp16", true, "1.0.0"),
            Epochs = [new TranscriptEpoch(1, 0, Math.Max(1, lines.Length * 10.0))],
            Speakers = [],
            Languages = [],
            Segments = segments,
        };
    }

    private static ManualHandoffViewModel ViewModel(FakeClipboard clipboard, string? summary = null)
    {
        TranscriptDocument transcript = Transcript();
        return new ManualHandoffViewModel(
            options => ManualHandoffComposer.Compose(transcript, null, options, summary),
            clipboard,
            "01JVM-handoff-v2.md");
    }

    [Fact]
    public void OpeningComposesAPreviewButCopiesNothing()
    {
        FakeClipboard clipboard = new();
        ManualHandoffViewModel model = ViewModel(clipboard);

        Assert.True(model.IsAvailable);
        Assert.True(model.CanCopy);
        Assert.Contains("segment-000001", model.PreviewText, StringComparison.Ordinal);
        // The dialog composed a preview and touched the clipboard zero times.
        Assert.Equal(0, clipboard.Attempts);
    }

    [Fact]
    public void CancellingWithoutCopyingLeavesTheClipboardUntouched()
    {
        FakeClipboard clipboard = new();
        ManualHandoffViewModel model = ViewModel(clipboard);

        // A user who opens the dialog and closes it (never calling Copy) copies nothing.
        _ = model.PreviewText;
        Assert.Equal(0, clipboard.Attempts);
    }

    [Fact]
    public void CopyPlacesThePreviewOnTheClipboardOnlyWhenAsked()
    {
        FakeClipboard clipboard = new();
        ManualHandoffViewModel model = ViewModel(clipboard);

        ClipboardResult result = model.Copy();

        Assert.True(result.Succeeded);
        Assert.Equal(1, clipboard.Attempts);
        Assert.Equal(model.PreviewText, clipboard.LastText);
    }

    [Fact]
    public void CopyUsesTheEditedPreview()
    {
        FakeClipboard clipboard = new();
        ManualHandoffViewModel model = ViewModel(clipboard);

        model.PreviewText = "only this, redacted by hand";
        model.Copy();

        Assert.Equal("only this, redacted by hand", clipboard.LastText);
    }

    [Fact]
    public void ABusyClipboardIsReportedNotThrown()
    {
        FakeClipboard clipboard = new() { Fail = true };
        ManualHandoffViewModel model = ViewModel(clipboard);

        ClipboardResult result = model.Copy();

        Assert.False(result.Succeeded);
        Assert.Equal(1, clipboard.Attempts);
        Assert.True(model.HasStatus);
        Assert.Contains("in use", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CountsFollowTheEditedPreview()
    {
        FakeClipboard clipboard = new();
        ManualHandoffViewModel model = ViewModel(clipboard);

        model.PreviewText = "abcd"; // 4 chars -> about 1 token
        Assert.Equal(4, model.CharacterCount);
        Assert.Equal(1, model.ApproximateTokenCount);

        model.PreviewText = string.Empty;
        Assert.Equal(0, model.CharacterCount);
        Assert.False(model.CanCopy);
    }

    [Fact]
    public void TogglingSummaryReferenceRecomposesThePreview()
    {
        FakeClipboard clipboard = new();
        ManualHandoffViewModel model = ViewModel(clipboard, summary: "The local overview.");

        Assert.DoesNotContain("The local overview.", model.PreviewText, StringComparison.Ordinal);

        model.IncludeSummaryReference = true;

        Assert.Contains("The local overview.", model.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveWritesTheEditedPreviewToAFile()
    {
        using TempDirectory temp = new();
        FakeClipboard clipboard = new();
        ManualHandoffViewModel model = ViewModel(clipboard);

        model.PreviewText = "the exact bytes to save";
        string path = Path.Combine(temp.Path, model.SuggestedFileName);

        ExportResult result = model.Save(path);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("the exact bytes to save", File.ReadAllText(path));
    }

    [Fact]
    public void WhenThereIsNoTranscriptNothingCanBeCopied()
    {
        FakeClipboard clipboard = new();
        ManualHandoffViewModel model = new(
            _ => ManualHandoffResult.Fail("no_transcript", "There is no transcript to copy."),
            clipboard,
            "handoff.md");

        Assert.False(model.IsAvailable);
        Assert.False(model.CanCopy);

        ClipboardResult result = model.Copy();

        Assert.False(result.Succeeded);
        Assert.Equal(0, clipboard.Attempts);
    }
}
