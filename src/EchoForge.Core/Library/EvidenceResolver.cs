using System.Globalization;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Core.Library;

/// <summary>
/// Follows a citation back to the speech it came from.
///
/// <para>
/// <b>The durable identity is the pair, not the segment ID.</b> A segment ID is unique only inside
/// one transcript revision; reprocessing the same audio produces a new revision whose
/// <c>segment-000004</c> is a different piece of speech. So a citation is resolved against the
/// revision it names and against no other, and a caller that hands over a different revision gets
/// a refusal rather than a plausible answer.
/// </para>
///
/// <para>
/// When the named revision is gone the stored timestamp is used and the result is marked
/// degraded. That is deliberately worse-looking than silently searching the newest transcript for
/// the same ID: a link that admits it is broken can be checked, and one that points confidently
/// at the wrong sentence cannot.
/// </para>
/// </summary>
public static class EvidenceResolver
{
    /// <summary>Resolves one citation against a transcript the caller has already loaded.</summary>
    /// <param name="transcript">
    /// The revision the citation names, or null when it could not be loaded. Passing a
    /// <i>different</i> revision is treated as not having it: the answer is degraded, never
    /// rebased.
    /// </param>
    public static EvidenceLocation Resolve(
        string sessionId,
        SummaryEvidence evidence,
        TranscriptDocument? transcript,
        SpeakerAliases? aliases = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(evidence);

        SpeakerAliases overlay = aliases ?? SpeakerAliases.None;

        if (transcript is null)
        {
            return Degraded(sessionId, evidence, "That transcript version is no longer on disk, so this points at the time it was recorded rather than the words.");
        }

        // The one check that makes the pair meaningful. A caller who loaded the selected revision
        // instead of the cited one is answered honestly rather than accommodated.
        if (transcript.TranscriptRevision != evidence.TranscriptRevision)
        {
            return Degraded(
                sessionId,
                evidence,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"This came from transcript version {evidence.TranscriptRevision}, and version {transcript.TranscriptRevision} was opened instead."));
        }

        TranscriptSegment? segment = transcript.Segments
            .FirstOrDefault(s => string.Equals(s.Id, evidence.SegmentId, StringComparison.Ordinal));

        if (segment is null)
        {
            return Degraded(sessionId, evidence, "That passage is not in this transcript version any more.");
        }

        return new EvidenceLocation
        {
            SessionId = sessionId,
            TranscriptRevision = transcript.TranscriptRevision,
            SegmentId = segment.Id,
            Resolution = EvidenceResolution.Resolved,
            // Times come from the segment, not from the citation. The citation's copy is a
            // fallback for when the segment is gone, not a second opinion about where it is.
            StartSeconds = segment.StartSeconds,
            EndSeconds = segment.EndSeconds,
            SourceTrack = segment.SourceTrack,
            SpeakerName = SpeakerPresentation.Present(segment, overlay),
            Text = segment.Text,
            Epoch = segment.Epoch,
        };
    }

    /// <summary>Resolves every citation on one item, in order.</summary>
    public static IReadOnlyList<EvidenceLocation> ResolveAll(
        string sessionId,
        IEnumerable<SummaryEvidence> evidence,
        TranscriptDocument? transcript,
        SpeakerAliases? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return [.. evidence.Select(e => Resolve(sessionId, e, transcript, aliases))];
    }

    private static EvidenceLocation Degraded(string sessionId, SummaryEvidence evidence, string explanation) => new()
    {
        SessionId = sessionId,
        TranscriptRevision = evidence.TranscriptRevision,
        SegmentId = evidence.SegmentId,
        Resolution = EvidenceResolution.Degraded,
        // The stored time is all that is left. It was derived from the segment when the summary
        // was made, so it is still the right neighbourhood even though the words are gone.
        StartSeconds = evidence.StartSeconds,
        EndSeconds = evidence.EndSeconds,
        SourceTrack = evidence.SourceTrack,
        SpeakerName = string.Empty,
        Text = null,
        Explanation = explanation,
    };
}
