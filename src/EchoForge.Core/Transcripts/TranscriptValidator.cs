using System.Globalization;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Core.Transcripts;

/// <summary>The verdict on one transcript. Empty problems means it may be activated.</summary>
public sealed record TranscriptVerdict(IReadOnlyList<string> Problems)
{
    public static readonly TranscriptVerdict Valid = new([]);

    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Checks a transcript against the invariants the JSON Schema cannot express.
///
/// <para>
/// The schema can say a segment has a start and an end. It cannot say that the end is after the
/// start, that both fall inside a capture epoch, that words are contained by their segment, or
/// that microphone content is attributed to You. Those are the properties that make a transcript
/// safe to seek audio with and safe to cite as evidence, so they are checked here before any
/// revision is activated — including on transcripts this host wrote itself.
/// </para>
/// </summary>
public static class TranscriptValidator
{
    /// <summary>
    /// Floating-point slack. Times cross a JSON round trip and a Python/C# boundary, so exact
    /// equality at an epoch edge would reject correct output.
    /// </summary>
    private const double Tolerance = 1e-6;

    public static TranscriptVerdict Validate(TranscriptDocument transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        List<string> problems = [];

        if (transcript.SchemaVersion != 1)
        {
            problems.Add(Invariant($"schema_version {transcript.SchemaVersion} is not supported"));
        }

        if (string.IsNullOrWhiteSpace(transcript.SessionId))
        {
            problems.Add("session_id is empty");
        }

        if (transcript.TranscriptRevision < 1)
        {
            problems.Add("transcript_revision must be at least 1");
        }

        if (transcript.DurationSeconds < 0)
        {
            problems.Add("duration_seconds is negative");
        }

        ValidateEpochs(transcript, problems);
        ValidateSpeakers(transcript, problems);
        ValidateSegments(transcript, problems);

        return problems.Count == 0 ? TranscriptVerdict.Valid : new TranscriptVerdict(problems);
    }

    private static void ValidateEpochs(TranscriptDocument transcript, List<string> problems)
    {
        double previousEnd = double.NegativeInfinity;
        int previousIndex = int.MinValue;

        foreach (TranscriptEpoch epoch in transcript.Epochs)
        {
            if (epoch.EndSeconds < epoch.StartSeconds - Tolerance)
            {
                problems.Add(Invariant($"epoch {epoch.Index} ends before it starts"));
            }

            if (epoch.Index <= previousIndex)
            {
                problems.Add(Invariant($"epoch {epoch.Index} is out of order"));
            }

            if (epoch.StartSeconds < previousEnd - Tolerance)
            {
                problems.Add(Invariant($"epoch {epoch.Index} starts before the previous epoch ends"));
            }

            previousIndex = epoch.Index;
            previousEnd = epoch.EndSeconds;
        }

        if (transcript.Epochs.Count > 0 && transcript.DurationSeconds < previousEnd - Tolerance)
        {
            problems.Add("duration_seconds is shorter than the last epoch");
        }
    }

    private static void ValidateSpeakers(TranscriptDocument transcript, List<string> problems)
    {
        foreach (TranscriptSpeaker speaker in transcript.Speakers)
        {
            (string expectedId, string expectedName) = speaker.SourceTrack switch
            {
                TranscriptSpeakers.MicrophoneTrack => (TranscriptSpeakers.YouId, TranscriptSpeakers.YouName),
                TranscriptSpeakers.SystemTrack => (TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName),
                _ => (string.Empty, string.Empty),
            };

            if (expectedId.Length == 0)
            {
                problems.Add(Invariant($"speaker '{speaker.Id}' names an unknown source track"));
                continue;
            }

            if (!string.Equals(speaker.Id, expectedId, StringComparison.Ordinal) ||
                !string.Equals(speaker.Name, expectedName, StringComparison.Ordinal))
            {
                problems.Add(Invariant(
                    $"the {speaker.SourceTrack} track must be attributed to {expectedName}, not '{speaker.Name}'"));
            }
        }
    }

    private static void ValidateSegments(TranscriptDocument transcript, List<string> problems)
    {
        Dictionary<int, TranscriptEpoch> epochs = [];
        foreach (TranscriptEpoch epoch in transcript.Epochs)
        {
            epochs[epoch.Index] = epoch;
        }

        HashSet<string> ids = [];
        TranscriptSegment? previous = null;

        foreach (TranscriptSegment segment in transcript.Segments)
        {
            if (!ids.Add(segment.Id))
            {
                problems.Add(Invariant($"segment id '{segment.Id}' appears more than once"));
            }

            if (previous is not null && Compare(previous, segment) > 0)
            {
                problems.Add(Invariant($"segment '{segment.Id}' is out of order"));
            }

            previous = segment;

            if (segment.EndSeconds < segment.StartSeconds - Tolerance)
            {
                problems.Add(Invariant($"segment '{segment.Id}' ends before it starts"));
            }

            if (segment.StartSeconds < 0)
            {
                problems.Add(Invariant($"segment '{segment.Id}' starts before the session"));
            }

            // A segment outside every epoch points at a moment where no audio was captured,
            // so nothing could seek to it and nothing could cite it.
            if (!epochs.TryGetValue(segment.Epoch, out TranscriptEpoch? epoch))
            {
                problems.Add(Invariant($"segment '{segment.Id}' names epoch {segment.Epoch}, which is not in this transcript"));
            }
            else if (!epoch.Contains(segment.StartSeconds, segment.EndSeconds, Tolerance))
            {
                problems.Add(Invariant($"segment '{segment.Id}' falls outside epoch {segment.Epoch}"));
            }

            if (segment.EndSeconds > transcript.DurationSeconds + Tolerance)
            {
                problems.Add(Invariant($"segment '{segment.Id}' ends after the session does"));
            }

            ValidateAttribution(segment, problems);
            ValidateWords(segment, problems);
            ValidateOverlaps(transcript, segment, problems);
        }
    }

    private static void ValidateAttribution(TranscriptSegment segment, List<string> problems)
    {
        (string expectedId, string expectedName) = segment.SourceTrack switch
        {
            TranscriptSpeakers.MicrophoneTrack => (TranscriptSpeakers.YouId, TranscriptSpeakers.YouName),
            TranscriptSpeakers.SystemTrack => (TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName),
            _ => (string.Empty, string.Empty),
        };

        if (expectedId.Length == 0)
        {
            problems.Add(Invariant($"segment '{segment.Id}' names an unknown source track '{segment.SourceTrack}'"));
            return;
        }

        if (!string.Equals(segment.SpeakerId, expectedId, StringComparison.Ordinal) ||
            !string.Equals(segment.SpeakerName, expectedName, StringComparison.Ordinal))
        {
            problems.Add(Invariant(
                $"segment '{segment.Id}' is on the {segment.SourceTrack} track and must be attributed to {expectedName}"));
        }
    }

    private static void ValidateWords(TranscriptSegment segment, List<string> problems)
    {
        double previousStart = double.NegativeInfinity;

        foreach (TranscriptWord word in segment.Words)
        {
            if (word.EndSeconds < word.StartSeconds - Tolerance)
            {
                problems.Add(Invariant($"a word in segment '{segment.Id}' ends before it starts"));
            }

            if (word.StartSeconds < segment.StartSeconds - Tolerance ||
                word.EndSeconds > segment.EndSeconds + Tolerance)
            {
                problems.Add(Invariant($"a word in segment '{segment.Id}' falls outside its segment"));
            }

            if (word.StartSeconds < previousStart - Tolerance)
            {
                problems.Add(Invariant($"words in segment '{segment.Id}' are not in timestamp order"));
            }

            if (word.Probability is < 0 or > 1)
            {
                problems.Add(Invariant($"a word probability in segment '{segment.Id}' is outside 0..1"));
            }

            previousStart = word.StartSeconds;
        }

        if (segment.Confidence is < 0 or > 1)
        {
            problems.Add(Invariant($"segment '{segment.Id}' has a confidence outside 0..1"));
        }
    }

    private static void ValidateOverlaps(
        TranscriptDocument transcript,
        TranscriptSegment segment,
        List<string> problems)
    {
        foreach (string id in segment.OverlapsSegmentIds)
        {
            if (string.Equals(id, segment.Id, StringComparison.Ordinal))
            {
                problems.Add(Invariant($"segment '{segment.Id}' overlaps itself"));
                continue;
            }

            TranscriptSegment? other = transcript.Segments.FirstOrDefault(s => s.Id == id);
            if (other is null)
            {
                problems.Add(Invariant($"segment '{segment.Id}' cites unknown overlap '{id}'"));
                continue;
            }

            // Cross-track only: two segments on one track cannot overlap by construction, and
            // recording them as if they could would misdescribe what overlap means here.
            if (string.Equals(other.SourceTrack, segment.SourceTrack, StringComparison.Ordinal))
            {
                problems.Add(Invariant($"segment '{segment.Id}' cites a same-track overlap '{id}'"));
            }
        }
    }

    /// <summary>
    /// The total order segments are written in: start, then end, then track (microphone first),
    /// then id. It is total so identical input produces an identical file.
    /// </summary>
    private static int Compare(TranscriptSegment left, TranscriptSegment right)
    {
        int byStart = left.StartSeconds.CompareTo(right.StartSeconds);
        if (byStart != 0)
        {
            return byStart;
        }

        int byEnd = left.EndSeconds.CompareTo(right.EndSeconds);
        if (byEnd != 0)
        {
            return byEnd;
        }

        int byTrack = TrackRank(left.SourceTrack).CompareTo(TrackRank(right.SourceTrack));
        return byTrack != 0 ? byTrack : string.CompareOrdinal(left.Id, right.Id);
    }

    private static int TrackRank(string sourceTrack) => sourceTrack switch
    {
        TranscriptSpeakers.MicrophoneTrack => 0,
        TranscriptSpeakers.SystemTrack => 1,
        _ => 2,
    };

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
