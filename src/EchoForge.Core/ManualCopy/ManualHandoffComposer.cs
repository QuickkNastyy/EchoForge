using System.Globalization;
using System.Text;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Library;

namespace EchoForge.Core.ManualCopy;

/// <summary>
/// Builds the document a user pastes into ChatGPT or Claude: EchoForge's instructions, the exact
/// selected transcript, and nothing else.
///
/// <para>
/// It is a pure function of its inputs. It opens no file, holds no service, and — the property the
/// whole feature turns on — makes <b>no network request</b> and depends on nothing that could. The
/// same transcript, aliases and options always produce the same bytes.
/// </para>
///
/// <para>
/// Two rules are load-bearing. The segment IDs written here are the exact IDs of the exact revision
/// passed in, never transformed into a display decoration, so a citation an assistant returns can be
/// checked against EchoForge's own evidence. And the speaker labels are presentation names applied by
/// <see cref="SpeakerPresentation"/> — the immutable transcript is untouched, and the payload says
/// plainly that the label is a name for reading and the ID is the anchor.
/// </para>
/// </summary>
public static class ManualHandoffComposer
{
    private const char NewLine = '\n';

    /// <summary>
    /// Composes a handoff, or refuses with a reason.
    /// </summary>
    /// <param name="transcript">The exact revision to export. Its IDs are the ones written.</param>
    /// <param name="aliases">Presentation names for remote speakers. You is never renamed.</param>
    /// <param name="options">What to include.</param>
    /// <param name="localSummaryOverview">
    /// EchoForge's own summary overview, appended only when <see cref="ManualHandoffOptions.IncludeSummaryReference"/>
    /// is set. Never included silently.
    /// </param>
    public static ManualHandoffResult Compose(
        TranscriptDocument transcript,
        SpeakerAliases? aliases,
        ManualHandoffOptions options,
        string? localSummaryOverview = null)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(options);

        SpeakerAliases overlay = SpeakerPresentation.Sanitize(aliases);

        IReadOnlyList<TranscriptSegment> ordered = Ordered(transcript);

        // A subset keeps only the requested IDs, and only those that actually belong to this
        // revision. An ID the revision does not contain is dropped, never fetched from elsewhere.
        IReadOnlyList<TranscriptSegment> included = options.IncludedSegmentIds is { } wanted
            ? [.. ordered.Where(s => wanted.Contains(s.Id))]
            : ordered;

        if (included.Count == 0)
        {
            return ManualHandoffResult.Fail(
                "empty_selection",
                "Nothing is selected to copy. Choose at least one line of the transcript.");
        }

        bool includeSummary = options.IncludeSummaryReference && !string.IsNullOrWhiteSpace(localSummaryOverview);

        StringBuilder text = new();

        Line(text, "# EchoForge — transcript for manual summarisation");
        Blank(text);
        Line(text, "You are copying this so you can paste it into ChatGPT, Claude or another assistant");
        Line(text, "yourself. EchoForge does not send it anywhere. Once you paste it into another service,");
        Line(text, "that service's privacy and retention policy applies, not EchoForge's. This document");
        Line(text, "contains only the meeting text shown below — no audio, no files, and nothing outside");
        Line(text, "what you selected.");
        Blank(text);

        Line(text, string.Create(CultureInfo.InvariantCulture, $"Session: {transcript.SessionId}"));
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Transcript revision: {transcript.TranscriptRevision}"));
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Prompt template: {ManualPromptTemplate.Version}"));
        Line(text, string.Create(
            CultureInfo.InvariantCulture,
            $"Segments included: {included.Count} of {ordered.Count}{(included.Count < ordered.Count ? " (a selected subset)" : string.Empty)}"));

        if (!transcript.Model.RecognizesSpeech)
        {
            Blank(text);
            Line(text, "NOTE: this transcript was produced by a deterministic placeholder backend. It performs");
            Line(text, "no speech recognition and its text is not a record of what was said. Summarising it is");
            Line(text, "only meaningful as a test.");
        }

        Blank(text);
        Line(text, "## Instructions");
        Blank(text);
        text.Append(ManualPromptTemplate.Instructions.Replace("\r\n", "\n", StringComparison.Ordinal));
        Blank(text);

        Blank(text);
        Line(text, "## Transcript");
        Blank(text);

        foreach (TranscriptSegment segment in included)
        {
            string speaker = SpeakerPresentation.Present(segment, overlay);
            text.Append(segment.Id)
                .Append("  [")
                .Append(Clock(segment.StartSeconds))
                .Append("]  ")
                .Append(speaker)
                .Append(": ")
                .Append(OneLine(segment.Text))
                .Append(NewLine);
        }

        if (includeSummary)
        {
            Blank(text);
            Line(text, "## EchoForge's existing local summary (reference only — not part of the transcript)");
            Blank(text);
            text.Append(OneLineParagraph(localSummaryOverview!)).Append(NewLine);
        }

        ManualHandoffPayload payload = new()
        {
            Text = text.ToString(),
            TemplateVersion = ManualPromptTemplate.Version,
            SessionId = transcript.SessionId,
            TranscriptRevision = transcript.TranscriptRevision,
            IncludedSegmentCount = included.Count,
            TotalSegmentCount = ordered.Count,
            IncludesSummaryReference = includeSummary,
        };

        return ManualHandoffResult.Ok(payload);
    }

    /// <summary>A safe filename for a saved handoff. Carries the session and the revision, no title.</summary>
    public static string SuggestFileName(TranscriptDocument transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        StringBuilder id = new();
        foreach (char c in transcript.SessionId)
        {
            id.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{id}-handoff-v{transcript.TranscriptRevision}.md");
    }

    private static IReadOnlyList<TranscriptSegment> Ordered(TranscriptDocument transcript) =>
    [
        .. transcript.Segments
            .OrderBy(s => s.StartSeconds)
            .ThenBy(s => s.EndSeconds)
            .ThenBy(s => s.SourceTrack == TranscriptSpeakers.MicrophoneTrack ? 0 : 1)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
    ];

    private static void Line(StringBuilder builder, string content) => builder.Append(content).Append(NewLine);

    private static void Blank(StringBuilder builder) => builder.Append(NewLine);

    /// <summary>
    /// Keeps a segment's text on one physical line, so "one line per segment" holds even if the text
    /// contains a newline. The segment ID stays the unambiguous first token of every line.
    /// </summary>
    private static string OneLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    private static string OneLineParagraph(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    private static string Clock(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}");
    }
}
