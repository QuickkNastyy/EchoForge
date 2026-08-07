namespace EchoForge.Contracts.Library;

/// <summary>How well a citation could be followed.</summary>
public enum EvidenceResolution
{
    /// <summary>The segment was found in the exact revision the citation named.</summary>
    Resolved,

    /// <summary>
    /// The revision the citation named is gone, or no longer holds that segment. The stored time
    /// is used instead and the result says so.
    ///
    /// <para>
    /// Degraded is never silently upgraded by looking the segment up in a different revision.
    /// Segment IDs are only unique within a revision, so the "same" ID in a newer transcript is a
    /// different piece of speech — following it would send a reader somewhere confidently wrong,
    /// which is worse than admitting the link is broken.
    /// </para>
    /// </summary>
    Degraded,
}

/// <summary>
/// Where a piece of evidence points.
///
/// <para>
/// Resolution is always against the pair <c>transcript_revision</c> + <c>segment_id</c>, never
/// against "the transcript that happens to be selected". A summary belongs to the revision it was
/// generated from, and opening it against a newer one would quietly re-attribute every claim it
/// makes.
/// </para>
/// </summary>
public sealed record EvidenceLocation
{
    public required string SessionId { get; init; }

    /// <summary>The revision the citation named. Not the selected one.</summary>
    public required int TranscriptRevision { get; init; }

    public required string SegmentId { get; init; }

    public required EvidenceResolution Resolution { get; init; }

    /// <summary>Session-relative, in seconds. From the segment when resolved, stored when not.</summary>
    public required double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    public string SourceTrack { get; init; } = string.Empty;

    /// <summary>After aliases. Empty when the segment could not be found.</summary>
    public string SpeakerName { get; init; } = string.Empty;

    /// <summary>The segment's text, when it resolved. Null when it did not.</summary>
    public string? Text { get; init; }

    /// <summary>Which epoch the moment falls in, so playback can map it to a chunk later.</summary>
    public int? Epoch { get; init; }

    /// <summary>Why the link is degraded, for the reader rather than for the log.</summary>
    public string? Explanation { get; init; }

    public bool IsResolved => Resolution == EvidenceResolution.Resolved;

    /// <summary>
    /// What a later playback layer needs to seek, without it having to understand summaries.
    ///
    /// <para>
    /// A seam on purpose: this pass locates evidence in the transcript and stops. Nothing here
    /// pretends to play audio, and there is no half-working player behind it.
    /// </para>
    /// </summary>
    public PlaybackRequest ToPlaybackRequest() => new()
    {
        SessionId = SessionId,
        StartSeconds = StartSeconds,
        EndSeconds = EndSeconds > StartSeconds ? EndSeconds : StartSeconds,
        SourceTrack = SourceTrack,
        Epoch = Epoch,
        IsApproximate = !IsResolved,
    };
}

/// <summary>
/// A request to hear a moment. Produced now, honoured when playback exists.
///
/// <para>
/// <see cref="IsApproximate"/> is the important field: a request built from a stored timestamp
/// after its segment vanished should not be presented as an exact seek.
/// </para>
/// </summary>
public sealed record PlaybackRequest
{
    public required string SessionId { get; init; }

    public required double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    public string SourceTrack { get; init; } = string.Empty;

    public int? Epoch { get; init; }

    public bool IsApproximate { get; init; }
}
