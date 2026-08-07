namespace EchoForge.Core.ManualCopy;

/// <summary>
/// What a user chose to put in a manual handoff. The defaults are the safe ones: the whole selected
/// transcript, and nothing else.
/// </summary>
public sealed record ManualHandoffOptions
{
    /// <summary>
    /// The segment IDs to include, or null for the whole transcript.
    ///
    /// <para>
    /// A subset is a deliberate narrowing. Only IDs that actually belong to the transcript being
    /// exported are honoured; an ID that is not in this revision is ignored rather than invented,
    /// because the one thing a handoff must never do is carry a segment from a different revision.
    /// </para>
    /// </summary>
    public IReadOnlySet<string>? IncludedSegmentIds { get; init; }

    /// <summary>
    /// Whether to append EchoForge's own local summary as clearly-labelled reference context.
    ///
    /// <para>
    /// Off by default. The point of the handoff is to have another assistant read the transcript; a
    /// local summary is added only when the user asks for it, and when added it is unmistakably
    /// marked as EchoForge's existing summary rather than part of the transcript.
    /// </para>
    /// </summary>
    public bool IncludeSummaryReference { get; init; }

    public static ManualHandoffOptions WholeTranscript { get; } = new();
}
