using System.Text;
using System.Text.RegularExpressions;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Exports;
using EchoForge.Core.ManualCopy;

namespace EchoForge.UnitTests;

/// <summary>
/// The manual handoff: the document a user copies to paste into ChatGPT or Claude.
///
/// <para>
/// The rules these tests hold are the ones that make a manual summary trustworthy and safe: the
/// segment IDs are the exact IDs of the exact revision and are never decorated, the evidence
/// discipline of the local pipeline is carried across intact, the speaker labels are presentation
/// only, and nothing outside the selected transcript text — no audio, no paths, no other revision —
/// ever reaches the payload. And the whole thing is a pure function that makes no network request.
/// </para>
/// </summary>
public sealed class ManualHandoffTests
{
    private const string Session = "01JMANUAL";

    private static TranscriptDocument Transcript(
        bool recognizesSpeech = true,
        params (string Track, string Text)[] lines)
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
            SessionId = Session,
            TranscriptRevision = 3,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            SourceManifestSha256 = new string('a', 64),
            DurationSeconds = Math.Max(1, lines.Length * 10.0),
            Model = new TranscriptModel("echoforge", "faster-whisper", "large-v3-turbo", "rev", "cuda-fp16", recognizesSpeech, "1.0.0"),
            Epochs = [new TranscriptEpoch(1, 0, Math.Max(1, lines.Length * 10.0))],
            Speakers =
            [
                new TranscriptSpeaker(TranscriptSpeakers.YouId, TranscriptSpeakers.YouName, TranscriptSpeakers.MicrophoneTrack),
                new TranscriptSpeaker(TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName, TranscriptSpeakers.SystemTrack),
            ],
            Languages = [new TranscriptLanguage(TranscriptSpeakers.MicrophoneTrack, "en", null)],
            Segments = segments,
        };
    }

    private static TranscriptDocument TwoLine() => Transcript(
        true,
        ("microphone", "We will ship the beta on Friday"),
        ("system", "I will prepare the release notes"));

    /// <summary>The transcript lines only, past the instructions (which legitimately show anti-examples).</summary>
    private static string TranscriptSection(string payload) =>
        payload[payload.IndexOf("## Transcript", StringComparison.Ordinal)..];

    /// <summary>Collapses wrapping whitespace so a phrase can be found regardless of line breaks.</summary>
    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    private static ManualHandoffPayload Compose(TranscriptDocument transcript, SpeakerAliases? aliases = null, ManualHandoffOptions? options = null, string? summary = null)
    {
        ManualHandoffResult result = ManualHandoffComposer.Compose(
            transcript, aliases, options ?? ManualHandoffOptions.WholeTranscript, summary);
        Assert.True(result.Succeeded, result.Message);
        return result.Payload!;
    }

    // -- content identity --------------------------------------------------------------------------

    [Fact]
    public void ThePayloadCarriesTheExactRevisionAndItsSegmentIds()
    {
        ManualHandoffPayload payload = Compose(TwoLine());

        Assert.Contains("Transcript revision: 3", payload.Text, StringComparison.Ordinal);
        Assert.Equal(3, payload.TranscriptRevision);
        Assert.Contains("segment-000001", payload.Text, StringComparison.Ordinal);
        Assert.Contains("segment-000002", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentIdsAreWrittenBareWithNoBrackets()
    {
        // The exact defect the plan warns about: an ID wrapped in brackets was cited with the
        // brackets as part of the ID. The transcript lines must carry the ID bare.
        ManualHandoffPayload payload = Compose(TwoLine());

        // In the transcript itself the IDs are bare. (The instructions do show `[segment-000123]`
        // once, as the anti-example of what never to do — that is the point of it being there.)
        string transcript = TranscriptSection(payload.Text);
        Assert.DoesNotContain("[segment-", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("(segment-", transcript, StringComparison.Ordinal);
        // The line begins with the bare ID followed by two spaces and a bracketed time.
        Assert.Contains("segment-000001  [00:00:00]", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionRelativeTimestampsArePreserved()
    {
        ManualHandoffPayload payload = Compose(TwoLine());
        Assert.Contains("[00:00:00]", payload.Text, StringComparison.Ordinal);
        Assert.Contains("[00:00:10]", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeakerAliasesAreAppliedAsPresentationOnlyAndYouIsUnchanged()
    {
        SpeakerAliases aliases = new()
        {
            BySpeakerId = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TranscriptSpeakers.RemoteId] = "Priya",
                // An alias for You must be ignored, however it got into the file.
                [TranscriptSpeakers.YouId] = "Somebody Else",
            },
        };

        ManualHandoffPayload payload = Compose(TwoLine(), aliases);

        Assert.Contains("Priya: I will prepare the release notes", payload.Text, StringComparison.Ordinal);
        Assert.Contains("You: We will ship the beta on Friday", payload.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Somebody Else", payload.Text, StringComparison.Ordinal);
        // Aliasing changes the label, never the evidence anchor.
        Assert.Contains("segment-000002", payload.Text, StringComparison.Ordinal);
    }

    // -- scope -------------------------------------------------------------------------------------

    [Fact]
    public void WholeTranscriptIncludesEverySegment()
    {
        ManualHandoffPayload payload = Compose(TwoLine());
        Assert.Equal(2, payload.IncludedSegmentCount);
        Assert.Equal(2, payload.TotalSegmentCount);
        Assert.False(payload.IsSubset);
    }

    [Fact]
    public void ASubsetIncludesOnlyTheSelectedSegments()
    {
        ManualHandoffOptions options = new()
        {
            IncludedSegmentIds = new HashSet<string>(StringComparer.Ordinal) { "segment-000002" },
        };

        ManualHandoffPayload payload = Compose(TwoLine(), options: options);

        Assert.Equal(1, payload.IncludedSegmentCount);
        Assert.Equal(2, payload.TotalSegmentCount);
        Assert.True(payload.IsSubset);
        Assert.Contains("segment-000002", payload.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("segment-000001", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownSegmentIdInTheSubsetIsIgnoredNeverInvented()
    {
        ManualHandoffOptions options = new()
        {
            IncludedSegmentIds = new HashSet<string>(StringComparer.Ordinal) { "segment-000001", "segment-999999" },
        };

        ManualHandoffPayload payload = Compose(TwoLine(), options: options);

        Assert.Equal(1, payload.IncludedSegmentCount);
        Assert.DoesNotContain("segment-999999", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySelectionIsRefused()
    {
        ManualHandoffOptions options = new() { IncludedSegmentIds = new HashSet<string>(StringComparer.Ordinal) };
        ManualHandoffResult result = ManualHandoffComposer.Compose(TwoLine(), null, options);

        Assert.False(result.Succeeded);
        Assert.Equal("empty_selection", result.Code);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void ASelectionThatMatchesNothingInTheRevisionIsRefused()
    {
        ManualHandoffOptions options = new()
        {
            IncludedSegmentIds = new HashSet<string>(StringComparer.Ordinal) { "segment-not-here" },
        };
        ManualHandoffResult result = ManualHandoffComposer.Compose(TwoLine(), null, options);

        Assert.False(result.Succeeded);
        Assert.Equal("empty_selection", result.Code);
    }

    [Fact]
    public void AVeryLongTranscriptComposesAndKeepsEverySegment()
    {
        (string, string)[] lines = new (string, string)[5000];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = (i % 2 == 0 ? "microphone" : "system", $"line number {i}");
        }

        ManualHandoffPayload payload = Compose(Transcript(true, lines));

        Assert.Equal(5000, payload.IncludedSegmentCount);
        Assert.Contains("segment-005000", payload.Text, StringComparison.Ordinal);
        Assert.True(payload.CharacterCount > 100_000);
        Assert.True(payload.ApproximateTokenCount > 0);
    }

    [Fact]
    public void UnicodeAndMarkdownCharactersSurviveVerbatim()
    {
        TranscriptDocument transcript = Transcript(
            true,
            ("microphone", "Ship the *déjà-vu* build — 日本語 — #1 priority"),
            ("system", "Costs ≈ €5k; owner: @nobody"));

        ManualHandoffPayload payload = Compose(transcript);

        Assert.Contains("*déjà-vu*", payload.Text, StringComparison.Ordinal);
        Assert.Contains("日本語", payload.Text, StringComparison.Ordinal);
        Assert.Contains("≈ €5k", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentTextWithNewlinesStaysOnOneLine()
    {
        TranscriptDocument transcript = Transcript(true, ("microphone", "first\nsecond\r\nthird"));
        ManualHandoffPayload payload = Compose(transcript);

        // The segment line has no interior newline, so the ID stays the first token of its line.
        string transcriptSection = payload.Text[payload.Text.IndexOf("## Transcript", StringComparison.Ordinal)..];
        string segmentLine = transcriptSection.Split('\n').First(l => l.StartsWith("segment-", StringComparison.Ordinal));
        Assert.Contains("first second third", segmentLine, StringComparison.Ordinal);
    }

    // -- evidence rules carried into the prompt ----------------------------------------------------

    [Fact]
    public void ThePromptRequiresExactSegmentIdCitationsWithNoBrackets()
    {
        string text = Flat(Compose(TwoLine()).Text);
        Assert.Contains("cite", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("segment-000123", text, StringComparison.Ordinal);
        Assert.Contains("never `[segment-000123]`", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePromptCarriesTheCertaintyLadderAndOwnerDateRules()
    {
        string text = Flat(Compose(TwoLine()).Text);
        Assert.Contains("explicit", text, StringComparison.Ordinal);
        Assert.Contains("inferred", text, StringComparison.Ordinal);
        Assert.Contains("unknown", text, StringComparison.Ordinal);
        Assert.Contains("Never compute a calendar date", text, StringComparison.Ordinal);
        Assert.Contains("an unknown owner has no name", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePromptPreservesContradictionsAndSeparatesDiscussionFromDecisions()
    {
        string text = Compose(TwoLine()).Text;
        Assert.Contains("record **both**", text, StringComparison.Ordinal);
        Assert.Contains("changed its mind", text, StringComparison.Ordinal);
        Assert.Contains("discussed but not settled", text, StringComparison.Ordinal);
        Assert.Contains("actually committed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePromptTemplateVersionIsRecorded()
    {
        ManualHandoffPayload payload = Compose(TwoLine());
        Assert.Equal("manual-summary-v1", payload.TemplateVersion);
        Assert.Contains("manual-summary-v1", payload.Text, StringComparison.Ordinal);
    }

    // -- privacy -----------------------------------------------------------------------------------

    [Fact]
    public void ThePayloadCarriesNoAudioOrFilesystemPaths()
    {
        string text = Compose(TwoLine()).Text;
        Assert.DoesNotContain(".wav", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppData", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sessions", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".json", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePayloadStatesThePrivacyBoundary()
    {
        string text = Compose(TwoLine()).Text;
        Assert.Contains("EchoForge does not send", text, StringComparison.Ordinal);
        Assert.Contains("no audio", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoLocalSummaryIsIncludedUnlessAskedFor()
    {
        // Default: even with an overview available, nothing is added.
        ManualHandoffPayload defaultPayload = Compose(TwoLine(), summary: "A local overview of the meeting.");
        Assert.False(defaultPayload.IncludesSummaryReference);
        Assert.DoesNotContain("A local overview of the meeting.", defaultPayload.Text, StringComparison.Ordinal);

        // Opt in, and it is included and clearly labelled as reference.
        ManualHandoffOptions options = new() { IncludeSummaryReference = true };
        ManualHandoffPayload withSummary = Compose(TwoLine(), options: options, summary: "A local overview of the meeting.");
        Assert.True(withSummary.IncludesSummaryReference);
        Assert.Contains("A local overview of the meeting.", withSummary.Text, StringComparison.Ordinal);
        Assert.Contains("reference only", withSummary.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APlaceholderBackendIsFlaggedInThePayload()
    {
        ManualHandoffPayload payload = Compose(Transcript(recognizesSpeech: false, ("microphone", "placeholder text")));
        Assert.Contains("placeholder backend", payload.Text, StringComparison.OrdinalIgnoreCase);
    }

    // -- determinism -------------------------------------------------------------------------------

    [Fact]
    public void ComposingTheSameInputsTwiceProducesIdenticalText()
    {
        Assert.Equal(Compose(TwoLine()).Text, Compose(TwoLine()).Text);
    }

    // -- file export -------------------------------------------------------------------------------

    [Fact]
    public void SaveWritesUtf8WithoutABomAndRoundTrips()
    {
        using TempDirectory temp = new();
        ManualHandoffPayload payload = Compose(Transcript(true, ("microphone", "ünïcode 日本語")));
        string path = Path.Combine(temp.Path, "handoff.md");

        ExportResult result = ManualHandoffWriter.Save(payload.Text, path);
        Assert.True(result.Succeeded, result.Message);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "a BOM was written");
        Assert.Equal(payload.Text, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void SaveRefusesToOverwriteByDefaultAndLeavesTheFileUnchanged()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "handoff.md");
        File.WriteAllText(path, "original");

        ExportResult result = ManualHandoffWriter.Save("new content", path);

        Assert.False(result.Succeeded);
        Assert.Equal("exists", result.Code);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void SaveWithOverwriteReplaces()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "handoff.md");
        File.WriteAllText(path, "original");

        ExportResult result = ManualHandoffWriter.Save("new content", path, overwrite: true);

        Assert.True(result.Succeeded);
        Assert.Equal("new content", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"), "the temporary neighbour was left behind");
    }

    [Fact]
    public void SuggestedFileNameIsSafeAndCarriesTheRevision()
    {
        TranscriptDocument transcript = TwoLine() with { SessionId = "a/b\\c:*?d" };
        string name = ManualHandoffComposer.SuggestFileName(transcript);

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            Assert.DoesNotContain(c.ToString(), name, StringComparison.Ordinal);
        }

        Assert.EndsWith("-handoff-v3.md", name, StringComparison.Ordinal);
    }

    // -- the guarantee that makes it an MVP --------------------------------------------------------

    [Fact]
    public void TheManualHandoffCodeHasNoNetworkDependency()
    {
        // The composer, the template and the writer all live in EchoForge.Core, whose compiled
        // assembly references no HTTP stack at all. There is no client to make a request with.
        IEnumerable<string?> referenced = typeof(ManualHandoffComposer).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name);

        Assert.DoesNotContain("System.Net.Http", referenced);
        Assert.DoesNotContain("System.Net.Requests", referenced);
        Assert.DoesNotContain("System.Net.WebClient", referenced);
    }
}
