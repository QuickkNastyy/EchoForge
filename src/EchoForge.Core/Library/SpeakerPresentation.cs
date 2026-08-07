using EchoForge.Contracts.Library;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Core.Library;

/// <summary>
/// Decides what name a reader sees, without ever changing what was recorded.
///
/// <para>
/// Renaming a remote speaker is a display preference. The transcript revision is immutable and
/// stays byte-identical to the digest it was activated under; the alias lives beside it, is
/// applied on the way to a screen or an export, and can be removed with nothing to undo.
/// </para>
///
/// <para>
/// <b>You is not renameable, and this is where that is enforced.</b> Microphone attribution is
/// the one speaker fact EchoForge derives rather than infers — it follows from which device the
/// audio came from. Allowing it to be aliased would put the only certain label on the same
/// footing as the uncertain ones, and would let "You" quietly become somebody else's name in an
/// exported document.
/// </para>
/// </summary>
public static class SpeakerPresentation
{
    /// <summary>True when this speaker may be given a different display name.</summary>
    public static bool IsAliasable(string speakerId) =>
        !string.Equals(speakerId, TranscriptSpeakers.YouId, StringComparison.Ordinal);

    /// <summary>
    /// Accepts only the aliases that are allowed to exist.
    ///
    /// <para>
    /// Filtering on the way in rather than on the way out means a stored file that somehow
    /// contains an alias for You cannot express it, however it got there.
    /// </para>
    /// </summary>
    public static SpeakerAliases Sanitize(SpeakerAliases? aliases)
    {
        if (aliases is null || aliases.IsEmpty)
        {
            return SpeakerAliases.None;
        }

        Dictionary<string, string> kept = new(StringComparer.Ordinal);

        foreach ((string speakerId, string alias) in aliases.BySpeakerId)
        {
            if (string.IsNullOrWhiteSpace(speakerId) || !IsAliasable(speakerId))
            {
                continue;
            }

            string trimmed = alias?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                // An empty alias is a removal, not a speaker with no name.
                continue;
            }

            kept[speakerId] = trimmed;
        }

        return kept.Count == 0 ? SpeakerAliases.None : new SpeakerAliases { BySpeakerId = kept };
    }

    /// <summary>Sets or clears one alias. Clearing is what makes renaming reversible.</summary>
    public static SpeakerAliases With(SpeakerAliases? existing, string speakerId, string? alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);

        Dictionary<string, string> updated = new(
            existing?.BySpeakerId ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(alias))
        {
            updated.Remove(speakerId);
        }
        else if (IsAliasable(speakerId))
        {
            updated[speakerId] = alias.Trim();
        }

        return Sanitize(new SpeakerAliases { BySpeakerId = updated });
    }

    /// <summary>The name to show for one segment.</summary>
    public static string Present(TranscriptSegment segment, SpeakerAliases? aliases)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return (aliases ?? SpeakerAliases.None).Present(segment.SpeakerId, segment.SpeakerName);
    }

    /// <summary>The name to show for one declared speaker.</summary>
    public static string Present(TranscriptSpeaker speaker, SpeakerAliases? aliases)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        return (aliases ?? SpeakerAliases.None).Present(speaker.Id, speaker.Name);
    }

    /// <summary>
    /// The speakers a user may rename, with what they are currently shown as.
    ///
    /// <para>
    /// Derived from the transcript rather than from the alias file, so a speaker who never spoke
    /// is not offered and a speaker who did is always offered even with no alias set.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string SpeakerId, string OriginalName, string DisplayName)> Renameable(
        TranscriptDocument transcript,
        SpeakerAliases? aliases)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        SpeakerAliases overlay = aliases ?? SpeakerAliases.None;

        return
        [
            .. transcript.Speakers
                .Where(s => IsAliasable(s.Id))
                .Select(s => (s.Id, s.Name, overlay.Present(s.Id, s.Name)))
        ];
    }
}
