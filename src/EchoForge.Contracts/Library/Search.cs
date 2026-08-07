namespace EchoForge.Contracts.Library;

/// <summary>Where a search hit came from. Kept explicit so a result can say what it is.</summary>
public enum SearchHitKind
{
    TranscriptSegment,
    SummaryOverview,
    SummaryKeyPoint,
    SummaryDecision,
    SummaryAction,
    SummaryQuestion,
    SummaryRisk,
    SummaryBlocker,
}

/// <summary>A range within the hit's text that matched, for highlighting.</summary>
/// <remarks>
/// Offsets are into the returned <see cref="SearchHit.Text"/> in UTF-16 code units, which is what
/// a .NET string is indexed by, so a caller can slice without re-searching and without a
/// surrogate pair being cut in half.
/// </remarks>
public readonly record struct HighlightRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// One search result.
///
/// <para>
/// A hit carries the revision it was found in, not just the session. A transcript hit that named
/// only its session would be unopenable once a second revision existed — there would be no way to
/// know which text the offsets belonged to.
/// </para>
/// </summary>
public sealed record SearchHit
{
    public required string SessionId { get; init; }

    public required SearchHitKind Kind { get; init; }

    /// <summary>The matched text, already presented with any speaker alias applied.</summary>
    public required string Text { get; init; }

    public IReadOnlyList<HighlightRange> Highlights { get; init; } = [];

    /// <summary>Set for a transcript hit. The revision the offsets and segment ID belong to.</summary>
    public int? TranscriptRevision { get; init; }

    public string? SegmentId { get; init; }

    public double? StartSeconds { get; init; }

    public string? SourceTrack { get; init; }

    /// <summary>The name to show, after aliases. Never the alias source of truth.</summary>
    public string? SpeakerName { get; init; }

    /// <summary>Set for a summary hit. The summary revision the text came from.</summary>
    public int? SummaryRevision { get; init; }

    /// <summary>Stable across runs for the same content, so a UI can key rows on it.</summary>
    public required string HitId { get; init; }

    /// <summary>Lower sorts first. Ties are broken deterministically by the search itself.</summary>
    public double Rank { get; init; }
}

/// <summary>What to look for, and where.</summary>
public sealed record SearchQuery
{
    public required string Text { get; init; }

    /// <summary>Null searches every meeting. A value restricts to one.</summary>
    public string? SessionId { get; init; }

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }

    /// <summary>Empty means every kind.</summary>
    public IReadOnlyList<SearchHitKind> Kinds { get; init; } = [];

    public int Limit { get; init; } = 200;

    /// <summary>
    /// True when the query is a quoted phrase, which must match adjacent words in order.
    ///
    /// <para>
    /// Detected from the text rather than set by the caller, so a user typing quotes gets what
    /// quotes mean everywhere else.
    /// </para>
    /// </summary>
    public bool IsPhrase => Text.Trim().StartsWith('"') && Text.Trim().EndsWith('"') && Text.Trim().Length > 2;
}

/// <summary>What a search returned, and whether the index was in a position to answer.</summary>
public sealed record SearchResults
{
    public static readonly SearchResults Empty = new() { Hits = [] };

    public IReadOnlyList<SearchHit> Hits { get; init; } = [];

    /// <summary>
    /// True when the index could not answer and the caller should offer a rebuild rather than
    /// present emptiness as "no matches". They are very different statements.
    /// </summary>
    public bool IndexUnavailable { get; init; }

    public string? Notice { get; init; }
}
