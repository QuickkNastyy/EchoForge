using System.Text;
using EchoForge.Contracts.Library;

namespace EchoForge.Infrastructure.Library;

/// <summary>
/// Recovers match positions from FTS5's marked-up text.
///
/// <para>
/// FTS5 will wrap matches in delimiters of our choosing but will not report offsets. Rather than
/// re-finding the terms in .NET — which would disagree with SQLite's tokenizer about diacritics,
/// case folding and word boundaries, and would highlight the wrong span for anything non-English
/// — the markers are asked for, located, and removed. The tokenizer stays the single authority on
/// what matched.
/// </para>
///
/// <para>
/// The delimiters are STX and ETX, two control characters. Any printable choice could appear in a
/// transcript: somebody says "asterisk", a summary quotes a bracket, and the highlighting starts
/// landing on the wrong words.
/// </para>
/// </summary>
public static class Highlighting
{
    public const char Open = '';
    public const char Close = '';

    /// <summary>Splits marked-up text into the clean text and the ranges that matched.</summary>
    public static (string Text, IReadOnlyList<HighlightRange> Highlights) Extract(string? marked)
    {
        if (string.IsNullOrEmpty(marked))
        {
            return (string.Empty, []);
        }

        StringBuilder text = new(marked.Length);
        List<HighlightRange> ranges = [];
        int start = -1;

        foreach (char character in marked)
        {
            if (character == Open)
            {
                // A nested or repeated open marker cannot happen from FTS5, but treating the
                // latest one as authoritative keeps this total rather than throwing on input
                // that came from a database.
                start = text.Length;
                continue;
            }

            if (character == Close)
            {
                if (start >= 0 && text.Length > start)
                {
                    ranges.Add(new HighlightRange(start, text.Length - start));
                }

                start = -1;
                continue;
            }

            text.Append(character);
        }

        return (text.ToString(), ranges);
    }
}
