using System.Globalization;
using System.Text;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Core.Exports;

/// <summary>What an export is written as.</summary>
public enum TranscriptExportFormat
{
    /// <summary>The canonical revision, copied byte for byte.</summary>
    Json,

    /// <summary>Readable plain text with speaker labels and timestamps.</summary>
    Text,

    /// <summary>SubRip subtitles.</summary>
    Srt,

    /// <summary>WebVTT subtitles.</summary>
    Vtt,
}

/// <summary>The outcome of one export. A refusal never touches the canonical revision.</summary>
public sealed record ExportResult(bool Succeeded, string? Code, string Message, string? Path)
{
    public static ExportResult Ok(string path) => new(true, null, "Exported.", path);

    public static ExportResult Fail(string code, string message) => new(false, code, message, null);
}

/// <summary>
/// Writes a validated transcript revision out in the formats people actually paste into other
/// tools.
///
/// <para>
/// Three rules shape the output. It is <b>deterministic</b>: no local time, no culture-dependent
/// formatting, and a fixed line ending, so exporting the same revision twice on two machines
/// gives identical bytes. It is <b>chronological and non-degenerate</b>: cues are sorted, never
/// negative, and never zero-length, because a subtitle player given a backwards cue behaves in
/// ways that look like a transcript bug. And it is <b>additive</b>: an export reads the canonical
/// revision and writes somewhere else, so a failed export cannot damage what it was reading.
/// </para>
///
/// <para>
/// Canonical JSON is exported by copying the activated bytes rather than re-serialising the
/// document. Re-rendering could change number formatting or key order, and the exported file
/// would then no longer hash to the digest the revision was activated under.
/// </para>
/// </summary>
public static class TranscriptExporter
{
    /// <summary>
    /// One line ending everywhere, chosen rather than inherited. WebVTT and SubRip both accept
    /// it, and a platform-dependent one would make byte-for-byte determinism impossible.
    /// </summary>
    private const char NewLine = '\n';

    /// <summary>
    /// The shortest cue a player will reliably render. A segment shorter than this is widened
    /// rather than emitted as an instant that flashes and vanishes.
    /// </summary>
    private const double MinimumCueSeconds = 0.001;

    public static string Extension(TranscriptExportFormat format) => format switch
    {
        TranscriptExportFormat.Json => ".json",
        TranscriptExportFormat.Text => ".txt",
        TranscriptExportFormat.Srt => ".srt",
        TranscriptExportFormat.Vtt => ".vtt",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "unknown export format"),
    };

    public static string Describe(TranscriptExportFormat format) => format switch
    {
        TranscriptExportFormat.Json => "Canonical JSON",
        TranscriptExportFormat.Text => "Plain text",
        TranscriptExportFormat.Srt => "SubRip (.srt)",
        TranscriptExportFormat.Vtt => "WebVTT (.vtt)",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "unknown export format"),
    };

    /// <summary>A filename that cannot contain a meeting title or an invalid path character.</summary>
    public static string SuggestFileName(TranscriptDocument transcript, TranscriptExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        StringBuilder id = new();
        foreach (char c in transcript.SessionId)
        {
            id.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{id}-transcript-v{transcript.TranscriptRevision}{Extension(format)}");
    }

    /// <summary>
    /// Renders a text format. Canonical JSON is deliberately not renderable: it is copied, not
    /// regenerated.
    /// </summary>
    public static string Render(TranscriptDocument transcript, TranscriptExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        return format switch
        {
            TranscriptExportFormat.Text => RenderText(transcript),
            TranscriptExportFormat.Srt => RenderSrt(transcript),
            TranscriptExportFormat.Vtt => RenderVtt(transcript),
            TranscriptExportFormat.Json => throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "canonical JSON is exported by copying the activated revision, not by re-rendering it"),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "unknown export format"),
        };
    }

    /// <summary>
    /// Writes an export to a destination the user chose.
    ///
    /// <para>
    /// An existing file is never replaced unless the caller says so explicitly, and the write
    /// goes to a temporary neighbour first, so an interrupted export cannot leave a truncated
    /// file wearing the name of a complete one.
    /// </para>
    /// </summary>
    /// <param name="canonicalPath">
    /// The activated revision on disk. Required for <see cref="TranscriptExportFormat.Json"/>,
    /// which copies it verbatim.
    /// </param>
    public static ExportResult Export(
        TranscriptDocument transcript,
        string? canonicalPath,
        TranscriptExportFormat format,
        string destinationPath,
        bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (File.Exists(destinationPath) && !overwrite)
        {
            return ExportResult.Fail(
                "exists",
                "A file of that name is already there. Choose another name, or confirm replacing it.");
        }

        byte[] payload;
        if (format == TranscriptExportFormat.Json)
        {
            if (canonicalPath is null || !File.Exists(canonicalPath))
            {
                return ExportResult.Fail("source_missing", "The transcript file could not be found.");
            }

            try
            {
                payload = File.ReadAllBytes(canonicalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ExportResult.Fail("source_unreadable", $"The transcript could not be read ({ex.GetType().Name}).");
            }
        }
        else
        {
            // No byte-order mark: the canonical JSON has none, and matching it keeps every
            // export from this transcript byte-comparable with every other.
            payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Render(transcript, format));
        }

        string temporary = destinationPath + ".tmp";

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destinationPath, overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            TryDelete(temporary);
            return ExportResult.Fail("write_failed", $"The export could not be written ({ex.GetType().Name}).");
        }

        return ExportResult.Ok(destinationPath);
    }

    // -- renderers ------------------------------------------------------------------------

    private static string RenderText(TranscriptDocument transcript)
    {
        StringBuilder text = new();

        text.Append("EchoForge transcript").Append(NewLine);
        text.Append(CultureInfo.InvariantCulture, $"Session: {transcript.SessionId}").Append(NewLine);
        text.Append(CultureInfo.InvariantCulture, $"Revision: {transcript.TranscriptRevision}").Append(NewLine);
        text.Append(CultureInfo.InvariantCulture, $"Created: {transcript.CreatedAtUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}").Append(NewLine);
        text.Append(CultureInfo.InvariantCulture, $"Duration: {Clock(transcript.DurationSeconds, '.')[..8]}").Append(NewLine);
        text.Append(CultureInfo.InvariantCulture, $"Backend: {transcript.Model.Backend} ({transcript.Model.ModelId})").Append(NewLine);

        if (!transcript.Model.RecognizesSpeech)
        {
            // Said in the file itself, not only in the app. An exported transcript travels, and
            // whoever opens it next has no other way to know the text is placeholder output.
            text.Append("NOTE: this transcript was produced by a deterministic placeholder backend. ")
                .Append("It performs no speech recognition and its text is not a record of what was said.")
                .Append(NewLine);
        }

        text.Append(NewLine);

        IReadOnlyList<TranscriptSegment> segments = Ordered(transcript);
        if (segments.Count == 0)
        {
            text.Append("(this transcript has no segments)").Append(NewLine);
            return text.ToString();
        }

        foreach (TranscriptSegment segment in segments)
        {
            text.Append(CultureInfo.InvariantCulture, $"[{Clock(segment.StartSeconds, '.')[..8]}] ")
                .Append(segment.SpeakerName)
                .Append(": ")
                .Append(segment.Text)
                .Append(NewLine);
        }

        return text.ToString();
    }

    private static string RenderSrt(TranscriptDocument transcript)
    {
        StringBuilder text = new();
        int index = 1;

        foreach (TranscriptSegment segment in Ordered(transcript))
        {
            (double start, double end) = CueRange(segment);

            text.Append(index.ToString(CultureInfo.InvariantCulture)).Append(NewLine);
            text.Append(Clock(start, ',')).Append(" --> ").Append(Clock(end, ',')).Append(NewLine);
            text.Append(segment.SpeakerName).Append(": ").Append(OneLine(segment.Text)).Append(NewLine);
            text.Append(NewLine);
            index++;
        }

        return text.ToString();
    }

    private static string RenderVtt(TranscriptDocument transcript)
    {
        StringBuilder text = new();
        text.Append("WEBVTT").Append(NewLine).Append(NewLine);

        foreach (TranscriptSegment segment in Ordered(transcript))
        {
            (double start, double end) = CueRange(segment);

            // The segment ID is the cue identifier, so a cue can be traced back to the exact
            // segment of the exact revision it came from.
            text.Append(segment.Id).Append(NewLine);
            text.Append(Clock(start, '.')).Append(" --> ").Append(Clock(end, '.')).Append(NewLine);
            text.Append(segment.SpeakerName).Append(": ").Append(OneLine(segment.Text)).Append(NewLine);
            text.Append(NewLine);
        }

        return text.ToString();
    }

    // -- shared -------------------------------------------------------------------------------

    /// <summary>
    /// Chronological order, applied here as well as in the validator. An exporter that assumed
    /// its input was sorted would produce silently broken subtitles from a transcript that was
    /// not, and the cost of sorting an already-sorted list is nothing.
    /// </summary>
    private static IReadOnlyList<TranscriptSegment> Ordered(TranscriptDocument transcript) =>
    [
        .. transcript.Segments
            .OrderBy(s => s.StartSeconds)
            .ThenBy(s => s.EndSeconds)
            .ThenBy(s => s.SourceTrack == TranscriptSpeakers.MicrophoneTrack ? 0 : 1)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
    ];

    /// <summary>A cue range that is never negative, never backwards, and never zero-length.</summary>
    private static (double Start, double End) CueRange(TranscriptSegment segment)
    {
        double start = Math.Max(0, segment.StartSeconds);
        double end = Math.Max(segment.EndSeconds, start + MinimumCueSeconds);
        return (start, end);
    }

    /// <summary>
    /// Cue payloads stay on one line. A newline inside one would end the cue early and turn the
    /// rest of the text into a malformed timing line.
    /// </summary>
    private static string OneLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    private static string Clock(double seconds, char decimalSeparator)
    {
        long milliseconds = (long)Math.Round(Math.Max(0, seconds) * 1000.0, MidpointRounding.AwayFromZero);

        long hours = milliseconds / 3_600_000;
        milliseconds -= hours * 3_600_000;
        long minutes = milliseconds / 60_000;
        milliseconds -= minutes * 60_000;
        long wholeSeconds = milliseconds / 1000;
        milliseconds -= wholeSeconds * 1000;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{minutes:00}:{wholeSeconds:00}{decimalSeparator}{milliseconds:000}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp is untidy, not dangerous, and it never wears the real name.
        }
    }
}
