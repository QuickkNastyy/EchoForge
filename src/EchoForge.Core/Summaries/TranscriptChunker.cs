using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Core.Summaries;

/// <summary>
/// Splits a transcript into the pieces a summariser can hold at once.
///
/// <para>
/// Cuts fall on segment boundaries, never inside one. A segment is the unit evidence is cited by,
/// so splitting one would produce a chunk containing text whose segment ID refers to something
/// larger than what was shown — and every citation out of that chunk would be subtly wrong.
/// </para>
///
/// <para>
/// Adjacent chunks share a few segments, for the same reason transcription windows overlap: a
/// decision stated across a boundary should be seen whole by at least one extraction. The
/// duplicate that produces is removed later, conservatively.
/// </para>
///
/// <para>
/// The plan asks for 8K–12K transcript tokens per chunk. This measures characters instead. The
/// placeholder backend has no tokenizer, and a character budget is stable, cheap, and wrong in a
/// predictable direction; a real tokenizer arrives with the model that needs one.
/// </para>
/// </summary>
public static class TranscriptChunker
{
    /// <summary>
    /// A chunk always holds at least one segment, however long that segment is. Refusing to emit
    /// an oversized segment would silently drop it from the summary, which is worse than handing
    /// the model something bigger than it asked for and letting it truncate visibly.
    /// </summary>
    public static IReadOnlyList<SummaryChunk> Plan(
        TranscriptDocument transcript,
        SummaryOptions options)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ChunkCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.ChunkCharacters, "a chunk must have a size");
        }

        if (options.OverlapSegments < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.OverlapSegments, "overlap cannot be negative");
        }

        IReadOnlyList<TranscriptSegment> segments = transcript.Segments;
        if (segments.Count == 0)
        {
            return [];
        }

        List<SummaryChunk> chunks = [];
        int start = 0;
        int index = 0;

        while (start < segments.Count)
        {
            int end = start;
            int characters = 0;

            while (end < segments.Count)
            {
                int cost = Cost(segments[end]);

                // Always take at least one, so a single enormous segment cannot stall the loop.
                if (end > start && characters + cost > options.ChunkCharacters)
                {
                    break;
                }

                characters += cost;
                end++;
            }

            int overlapBefore = index == 0 ? 0 : Math.Min(options.OverlapSegments, start == 0 ? 0 : end - start);
            bool last = end >= segments.Count;

            // Where the next chunk will begin, so this one can record what it shares forward.
            int next = last ? segments.Count : Math.Max(start + 1, end - options.OverlapSegments);
            int overlapAfter = last ? 0 : end - next;

            chunks.Add(new SummaryChunk
            {
                Index = index,
                FirstSegmentId = segments[start].Id,
                LastSegmentId = segments[end - 1].Id,
                SegmentCount = end - start,
                EstimatedCharacters = characters,
                OverlapBefore = overlapBefore,
                OverlapAfter = overlapAfter,
                InputFingerprint = Fingerprint(transcript, options, segments[start].Id, segments[end - 1].Id),
            });

            if (last)
            {
                break;
            }

            start = next;
            index++;
        }

        return chunks;
    }

    /// <summary>
    /// The segments a chunk covers, resolved back out of the transcript.
    ///
    /// <para>
    /// Chunks name their first and last segment rather than carrying text, so the transcript is
    /// the only copy of what was said and a chunk cannot drift away from it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Resolve(TranscriptDocument transcript, SummaryChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(chunk);

        int first = IndexOf(transcript, chunk.FirstSegmentId);
        int last = IndexOf(transcript, chunk.LastSegmentId);

        if (first < 0 || last < first)
        {
            return [];
        }

        return [.. transcript.Segments.Skip(first).Take(last - first + 1)];
    }

    private static int IndexOf(TranscriptDocument transcript, string segmentId)
    {
        for (int i = 0; i < transcript.Segments.Count; i++)
        {
            if (string.Equals(transcript.Segments[i].Id, segmentId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The speaker label counts towards the budget because it is sent to the model, and the model
    /// needs it: "Alex will do it" and "You will do it" are different facts.
    /// </summary>
    private static int Cost(TranscriptSegment segment) =>
        segment.Text.Length + segment.SpeakerName.Length + 2;

    /// <summary>
    /// Everything a chunk's extraction depends on, in one digest.
    ///
    /// <para>
    /// A checkpoint may be reused only when this matches, so it cannot survive a change to the
    /// transcript, the boundaries, the prompt, or the inference settings. Matching on the chunk
    /// index alone would eventually reuse a result produced from different text.
    /// </para>
    /// </summary>
    private static string Fingerprint(
        TranscriptDocument transcript,
        SummaryOptions options,
        string firstSegmentId,
        string lastSegmentId)
    {
        StringBuilder builder = new();
        builder.Append(transcript.SessionId).Append('');
        builder.Append(transcript.TranscriptRevision.ToString(CultureInfo.InvariantCulture)).Append('');
        builder.Append(transcript.SourceManifestSha256 ?? string.Empty).Append('');
        builder.Append(firstSegmentId).Append('').Append(lastSegmentId).Append('');
        builder.Append(options.PromptVersion).Append('');

        // The backend is part of the input identity, not a detail of how the input was
        // processed. The same transcript through the placeholder and through Gemma are two
        // different extractions, and a checkpoint from one must never be reused for the other.
        // The tokenizer travels with the backend - it lives inside the pinned GGUF - so naming
        // the backend and its runtime profile names the tokenizer too.
        builder.Append(options.Backend).Append('');
        builder.Append(options.SummaryProfile).Append('');
        builder.Append(options.Seed.ToString(CultureInfo.InvariantCulture)).Append('');
        builder.Append(options.ChunkCharacters.ToString(CultureInfo.InvariantCulture)).Append('');
        builder.Append(options.OverlapSegments.ToString(CultureInfo.InvariantCulture)).Append('');
        builder.Append(options.InferOwners ? '1' : '0').Append(options.InferDueDates ? '1' : '0').Append('');
        builder.Append(options.MeetingDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
