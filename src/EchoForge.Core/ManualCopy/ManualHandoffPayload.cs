namespace EchoForge.Core.ManualCopy;

/// <summary>
/// The exact text of a manual handoff, with the facts a preview needs to describe it honestly.
///
/// <para>
/// <see cref="Text"/> is everything that will go to the clipboard or the file — there is no hidden
/// attachment and nothing is added later. The counts are computed from that same string, so what a
/// preview shows and what is copied cannot disagree.
/// </para>
/// </summary>
public sealed record ManualHandoffPayload
{
    public required string Text { get; init; }

    /// <summary>The prompt template version baked into <see cref="Text"/>.</summary>
    public required string TemplateVersion { get; init; }

    /// <summary>The session the transcript belongs to. An identifier, never a file path.</summary>
    public required string SessionId { get; init; }

    /// <summary>The exact transcript revision this handoff was built from.</summary>
    public required int TranscriptRevision { get; init; }

    /// <summary>How many segments were included.</summary>
    public required int IncludedSegmentCount { get; init; }

    /// <summary>How many segments the revision has in total.</summary>
    public required int TotalSegmentCount { get; init; }

    /// <summary>True when fewer than all of the revision's segments were included.</summary>
    public bool IsSubset => IncludedSegmentCount < TotalSegmentCount;

    /// <summary>Whether a local-summary reference block was appended.</summary>
    public required bool IncludesSummaryReference { get; init; }

    /// <summary>Length of <see cref="Text"/> in UTF-16 characters.</summary>
    public int CharacterCount => Text.Length;

    /// <summary>
    /// A deliberately rough token estimate: one token per four characters, rounded up. It is not a
    /// tokeniser and is not tied to any provider; it exists so a preview can say "about this many"
    /// without pretending to a precision it does not have.
    /// </summary>
    public int ApproximateTokenCount => ApproximateTokens(Text);

    /// <summary>
    /// The same rough estimate, over arbitrary text, so a preview the user has edited is counted the
    /// same way as the composed payload.
    /// </summary>
    public static int ApproximateTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text.Length + 3) / 4;
    }
}

/// <summary>
/// The outcome of composing a handoff. A refusal — an empty selection, no transcript — carries a
/// reason and no payload, and never produces text to copy.
/// </summary>
public sealed record ManualHandoffResult(bool Succeeded, string? Code, string Message, ManualHandoffPayload? Payload)
{
    public static ManualHandoffResult Ok(ManualHandoffPayload payload) =>
        new(true, null, "Ready to copy.", payload);

    public static ManualHandoffResult Fail(string code, string message) =>
        new(false, code, message, null);
}
