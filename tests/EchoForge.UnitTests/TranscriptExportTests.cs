using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Exports;

namespace EchoForge.UnitTests;

/// <summary>
/// Exports: what leaves EchoForge and gets opened somewhere else.
///
/// <para>
/// The cue formats are checked against their actual grammar rather than against themselves. A
/// subtitle file that only this project can parse is not an export, and a player given a
/// backwards cue misbehaves in ways that read as a transcript bug.
/// </para>
/// </summary>
public sealed class TranscriptExportTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static TranscriptSegment Segment(
        string id,
        double start,
        double end,
        string track,
        string text)
    {
        (string speakerId, string speakerName) = TranscriptSpeakers.For(track);
        return new TranscriptSegment
        {
            Id = id,
            Epoch = 1,
            StartSeconds = start,
            EndSeconds = end,
            SpeakerId = speakerId,
            SpeakerName = speakerName,
            SourceTrack = track,
            Text = text,
            Confidence = null,
            Language = "und",
            Words = [new TranscriptWord(text, start, end, null)],
        };
    }

    private static TranscriptDocument Document(
        IEnumerable<TranscriptSegment>? segments = null,
        bool recognizesSpeech = false) => new()
    {
        SessionId = "01JEXPORT",
        TranscriptRevision = 2,
        CreatedAtUtc = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
        SourceManifestSha256 = new string('a', 64),
        DurationSeconds = 4000,
        Model = new TranscriptModel("echoforge-mock", "mock", "mock-v1", "mock-v1", "none", recognizesSpeech, "0.1.0"),
        Epochs = [new TranscriptEpoch(1, 0, 4000)],
        Speakers =
        [
            new TranscriptSpeaker(TranscriptSpeakers.YouId, TranscriptSpeakers.YouName, TranscriptSpeakers.MicrophoneTrack),
            new TranscriptSpeaker(TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName, TranscriptSpeakers.SystemTrack),
        ],
        Languages = [new TranscriptLanguage(TranscriptSpeakers.MicrophoneTrack, "und", null)],
        Segments = segments is null ? Standard() : [.. segments],
    };

    private static List<TranscriptSegment> Standard() =>
    [
        Segment("segment-000001", 0, 2.5, TranscriptSpeakers.MicrophoneTrack, "morning everyone"),
        Segment("segment-000002", 2.0, 5.25, TranscriptSpeakers.SystemTrack, "morning"),
        Segment("segment-000003", 6, 9.125, TranscriptSpeakers.MicrophoneTrack, "shall we start"),
    ];

    private string Destination(string name) => Path.Combine(_temp.Path, name);

    // -- canonical JSON --------------------------------------------------------------------

    [Fact]
    public void CanonicalJsonIsCopiedByteForByteAndStillParses()
    {
        TranscriptDocument document = Document();
        string canonical = Destination("transcript.v2.json");
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(document, TranscriptDocument.Json);
        File.WriteAllBytes(canonical, original);

        string destination = Destination("exported.json");
        ExportResult result = TranscriptExporter.Export(document, canonical, TranscriptExportFormat.Json, destination);

        Assert.True(result.Succeeded, result.Message);

        byte[] exported = File.ReadAllBytes(destination);
        Assert.Equal(original, exported);

        // Round trip: what came out is the transcript that went in.
        TranscriptDocument? parsed = JsonSerializer.Deserialize<TranscriptDocument>(exported, TranscriptDocument.Json);
        Assert.NotNull(parsed);
        Assert.Equal(document.SessionId, parsed!.SessionId);
        Assert.Equal(document.Segments.Count, parsed.Segments.Count);
        Assert.Equal("You", parsed.Segments[0].SpeakerName);
    }

    [Fact]
    public void JsonIsNeverRenderedFromTheDocument()
    {
        // Re-serialising could change number formatting or key order, and the exported file
        // would no longer hash to the digest its revision was activated under.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TranscriptExporter.Render(Document(), TranscriptExportFormat.Json));
    }

    [Fact]
    public void ExportingJsonWithoutTheCanonicalFileIsRefused()
    {
        ExportResult result = TranscriptExporter.Export(
            Document(), Destination("missing.json"), TranscriptExportFormat.Json, Destination("out.json"));

        Assert.False(result.Succeeded);
        Assert.Equal("source_missing", result.Code);
    }

    // -- plain text ---------------------------------------------------------------------------

    [Fact]
    public void TextListsEverySpeakerTurnInOrderWithItsLabel()
    {
        string text = TranscriptExporter.Render(Document(), TranscriptExportFormat.Text);
        string[] lines = text.Split('\n');

        int first = Array.FindIndex(lines, l => l.StartsWith('['));
        string[] turns = [.. lines.Skip(first).Where(l => l.StartsWith('['))];

        Assert.Equal(3, turns.Length);
        Assert.Equal("[00:00:00] You: morning everyone", turns[0]);
        Assert.Equal("[00:00:02] Remote: morning", turns[1]);
        Assert.Equal("[00:00:06] You: shall we start", turns[2]);
    }

    [Fact]
    public void TextSaysInTheFileItselfThatThePlaceholderRecognisesNoSpeech()
    {
        string text = TranscriptExporter.Render(Document(), TranscriptExportFormat.Text);

        // An exported transcript travels. Whoever opens it next has no other way to know.
        Assert.Contains("performs no speech recognition", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TextFromARealRecogniserCarriesNoPlaceholderWarning()
    {
        string text = TranscriptExporter.Render(Document(recognizesSpeech: true), TranscriptExportFormat.Text);

        Assert.DoesNotContain("performs no speech recognition", text, StringComparison.Ordinal);
    }

    // -- SubRip -------------------------------------------------------------------------------

    [Fact]
    public void SrtIsWellFormedAndChronological()
    {
        string srt = TranscriptExporter.Render(Document(), TranscriptExportFormat.Srt);

        MatchCollection cues = Regex.Matches(
            srt,
            @"^(?<index>\d+)\n(?<start>\d{2}:\d{2}:\d{2},\d{3}) --> (?<end>\d{2}:\d{2}:\d{2},\d{3})\n(?<text>[^\n]+)\n\n",
            RegexOptions.Multiline);

        Assert.Equal(3, cues.Count);

        TimeSpan previousStart = TimeSpan.MinValue;
        for (int i = 0; i < cues.Count; i++)
        {
            Assert.Equal((i + 1).ToString(CultureInfo.InvariantCulture), cues[i].Groups["index"].Value);

            TimeSpan start = ParseSrt(cues[i].Groups["start"].Value);
            TimeSpan end = ParseSrt(cues[i].Groups["end"].Value);

            Assert.True(end > start, $"cue {i + 1} is not positive");
            Assert.True(start >= TimeSpan.Zero);
            Assert.True(start >= previousStart, $"cue {i + 1} goes backwards");
            previousStart = start;
        }

        Assert.StartsWith("You: ", cues[0].Groups["text"].Value, StringComparison.Ordinal);
        Assert.StartsWith("Remote: ", cues[1].Groups["text"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void SrtTimestampsUseACommaAndThreeDecimalPlaces()
    {
        string srt = TranscriptExporter.Render(Document(), TranscriptExportFormat.Srt);

        Assert.Contains("00:00:00,000 --> 00:00:02,500", srt, StringComparison.Ordinal);
        Assert.Contains("00:00:06,000 --> 00:00:09,125", srt, StringComparison.Ordinal);
    }

    [Fact]
    public void ATimestampBeyondAnHourStillFormatsCorrectly()
    {
        TranscriptDocument document = Document(
        [
            Segment("segment-000001", 3661.5, 3665.25, TranscriptSpeakers.MicrophoneTrack, "much later"),
        ]);

        string srt = TranscriptExporter.Render(document, TranscriptExportFormat.Srt);

        Assert.Contains("01:01:01,500 --> 01:01:05,250", srt, StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroLengthSegmentBecomesACueAPlayerCanActuallyShow()
    {
        TranscriptDocument document = Document(
        [
            Segment("segment-000001", 5, 5, TranscriptSpeakers.MicrophoneTrack, "blink"),
        ]);

        string srt = TranscriptExporter.Render(document, TranscriptExportFormat.Srt);

        Assert.Contains("00:00:05,000 --> 00:00:05,001", srt, StringComparison.Ordinal);
    }

    // -- WebVTT --------------------------------------------------------------------------------

    [Fact]
    public void VttStartsWithItsSignatureAndUsesDotSeparatedTimestamps()
    {
        string vtt = TranscriptExporter.Render(Document(), TranscriptExportFormat.Vtt);

        Assert.StartsWith("WEBVTT\n\n", vtt, StringComparison.Ordinal);
        Assert.Contains("00:00:00.000 --> 00:00:02.500", vtt, StringComparison.Ordinal);
        Assert.DoesNotContain(",", vtt.Split("\n\n")[1], StringComparison.Ordinal);
    }

    [Fact]
    public void EveryVttCueIsIdentifiedByItsSegmentSoItCanBeTracedBack()
    {
        string vtt = TranscriptExporter.Render(Document(), TranscriptExportFormat.Vtt);

        MatchCollection cues = Regex.Matches(
            vtt,
            @"^(?<id>segment-\d{6})\n(?<start>\d{2}:\d{2}:\d{2}\.\d{3}) --> (?<end>\d{2}:\d{2}:\d{2}\.\d{3})\n(?<text>[^\n]+)\n\n",
            RegexOptions.Multiline);

        Assert.Equal(3, cues.Count);
        Assert.Equal("segment-000001", cues[0].Groups["id"].Value);
        Assert.Equal("segment-000003", cues[2].Groups["id"].Value);
    }

    [Fact]
    public void ACueIdentifierNeverContainsTheArrowThatWouldBreakParsing()
    {
        string vtt = TranscriptExporter.Render(Document(), TranscriptExportFormat.Vtt);

        foreach (string line in vtt.Split('\n'))
        {
            if (line.StartsWith("segment-", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("-->", line, StringComparison.Ordinal);
            }
        }
    }

    // -- awkward content --------------------------------------------------------------------------

    [Fact]
    public void AnEmptyTranscriptExportsCleanlyInEveryFormat()
    {
        TranscriptDocument empty = Document([]);

        Assert.Equal(string.Empty, TranscriptExporter.Render(empty, TranscriptExportFormat.Srt));
        Assert.Equal("WEBVTT\n\n", TranscriptExporter.Render(empty, TranscriptExportFormat.Vtt));
        Assert.Contains("(this transcript has no segments)", TranscriptExporter.Render(empty, TranscriptExportFormat.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeSurvivesTheRoundTripToDiskAsUtf8()
    {
        TranscriptDocument document = Document(
        [
            Segment("segment-000001", 0, 2, TranscriptSpeakers.MicrophoneTrack, "こんにちは、Ωmega — naïve café"),
            Segment("segment-000002", 2, 4, TranscriptSpeakers.SystemTrack, "Привет 👋"),
        ]);

        string destination = Destination("unicode.srt");
        Assert.True(TranscriptExporter.Export(document, null, TranscriptExportFormat.Srt, destination).Succeeded);

        string read = File.ReadAllText(destination, Encoding.UTF8);
        Assert.Contains("こんにちは、Ωmega — naïve café", read, StringComparison.Ordinal);
        Assert.Contains("Привет 👋", read, StringComparison.Ordinal);

        // No byte-order mark, matching the canonical JSON.
        byte[] bytes = File.ReadAllBytes(destination);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void ANewlineInsideASegmentNeverBreaksACue()
    {
        TranscriptDocument document = Document(
        [
            Segment("segment-000001", 0, 2, TranscriptSpeakers.MicrophoneTrack, "first\nsecond\r\nthird"),
        ]);

        string srt = TranscriptExporter.Render(document, TranscriptExportFormat.Srt);

        // The whole cue, exactly: an index, a timing line, one line of text, and the separator.
        Assert.Equal("1\n00:00:00,000 --> 00:00:02,000\nYou: first second third\n\n", srt);
    }

    [Fact]
    public void AnUnorderedTranscriptIsStillExportedChronologically()
    {
        TranscriptDocument document = Document(
        [
            Segment("segment-000003", 6, 9, TranscriptSpeakers.MicrophoneTrack, "third"),
            Segment("segment-000001", 0, 2, TranscriptSpeakers.MicrophoneTrack, "first"),
            Segment("segment-000002", 2, 4, TranscriptSpeakers.SystemTrack, "second"),
        ]);

        string text = TranscriptExporter.Render(document, TranscriptExportFormat.Text);
        int first = text.IndexOf("first", StringComparison.Ordinal);
        int second = text.IndexOf("second", StringComparison.Ordinal);
        int third = text.IndexOf("third", StringComparison.Ordinal);

        Assert.True(first < second && second < third);
    }

    // -- determinism ------------------------------------------------------------------------------

    [Theory]
    [InlineData(TranscriptExportFormat.Text)]
    [InlineData(TranscriptExportFormat.Srt)]
    [InlineData(TranscriptExportFormat.Vtt)]
    public void ExportingTheSameRevisionTwiceGivesIdenticalBytes(TranscriptExportFormat format)
    {
        TranscriptDocument document = Document();

        string a = Destination($"a{TranscriptExporter.Extension(format)}");
        string b = Destination($"b{TranscriptExporter.Extension(format)}");

        Assert.True(TranscriptExporter.Export(document, null, format, a).Succeeded);
        Assert.True(TranscriptExporter.Export(document, null, format, b).Succeeded);

        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }

    [Fact]
    public void LineEndingsAreFixedRatherThanInheritedFromThePlatform()
    {
        string srt = TranscriptExporter.Render(Document(), TranscriptExportFormat.Srt);

        Assert.DoesNotContain("\r", srt, StringComparison.Ordinal);
    }

    // -- writing ------------------------------------------------------------------------------------

    [Fact]
    public void AnExistingFileIsNeverReplacedWithoutBeingAskedTo()
    {
        string destination = Destination("existing.srt");
        File.WriteAllText(destination, "something the user still wants");

        ExportResult result = TranscriptExporter.Export(Document(), null, TranscriptExportFormat.Srt, destination);

        Assert.False(result.Succeeded);
        Assert.Equal("exists", result.Code);
        Assert.Equal("something the user still wants", File.ReadAllText(destination));
    }

    [Fact]
    public void AnExistingFileIsReplacedWhenReplacingIsExplicitlyAllowed()
    {
        string destination = Destination("existing.srt");
        File.WriteAllText(destination, "old");

        ExportResult result = TranscriptExporter.Export(
            Document(), null, TranscriptExportFormat.Srt, destination, overwrite: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("You: morning everyone", File.ReadAllText(destination), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedWriteLeavesNothingBehindAndDoesNotTouchTheCanonicalRevision()
    {
        TranscriptDocument document = Document();

        string canonical = Destination("transcript.v2.json");
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(document, TranscriptDocument.Json);
        File.WriteAllBytes(canonical, original);

        // A file where a directory would have to be: the write cannot succeed.
        string blocker = Destination("blocked");
        File.WriteAllText(blocker, "not a directory");
        string destination = Path.Combine(blocker, "nested", "out.srt");

        ExportResult result = TranscriptExporter.Export(document, canonical, TranscriptExportFormat.Srt, destination);

        Assert.False(result.Succeeded);
        Assert.Equal("write_failed", result.Code);
        Assert.False(File.Exists(destination + ".tmp"));

        // The transcript it was reading is exactly as it was.
        Assert.Equal(original, File.ReadAllBytes(canonical));
    }

    [Fact]
    public void ExportCreatesTheDestinationDirectoryWhenItIsMissing()
    {
        string destination = Path.Combine(_temp.Path, "new", "folder", "out.vtt");

        Assert.True(TranscriptExporter.Export(Document(), null, TranscriptExportFormat.Vtt, destination).Succeeded);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public void TheSuggestedFileNameCannotContainAnInvalidPathCharacter()
    {
        TranscriptDocument document = Document() with { SessionId = @"bad/name:with*chars" };

        string name = TranscriptExporter.SuggestFileName(document, TranscriptExportFormat.Srt);

        Assert.Equal("bad-name-with-chars-transcript-v2.srt", name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    private static TimeSpan ParseSrt(string value) =>
        TimeSpan.ParseExact(value, @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
}
