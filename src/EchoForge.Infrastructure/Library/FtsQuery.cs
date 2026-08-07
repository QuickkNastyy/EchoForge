using System.Text;

namespace EchoForge.Infrastructure.Library;

/// <summary>
/// Turns what a person typed into an FTS5 expression.
///
/// <para>
/// FTS5's query language has its own operators — <c>AND</c>, <c>OR</c>, <c>NOT</c>, <c>NEAR</c>,
/// <c>*</c>, <c>^</c>, <c>-</c>, parentheses, colons. A meeting search box is not a query console,
/// and a user typing <c>Q3 (revised)</c> or <c>budget: 40k</c> means those characters literally.
/// Passing raw input through would give them a syntax error for punctuation, or silently change
/// what they searched for.
/// </para>
///
/// <para>
/// So every term is quoted, which makes it a literal in FTS5, and embedded quotes are doubled.
/// The one piece of syntax deliberately preserved is the user's own: wrapping the whole thing in
/// quotes means a phrase, because that is what quotes mean everywhere else.
/// </para>
/// </summary>
public static class FtsQuery
{
    /// <summary>The expression to hand FTS5, or null when there is nothing to search for.</summary>
    public static string? Build(string? input)
    {
        string trimmed = (input ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        // An explicit phrase: everything inside the quotes must appear together, in order.
        if (trimmed.Length > 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
        {
            string phrase = trimmed[1..^1];
            return phrase.Trim().Length == 0 ? null : Quote(phrase);
        }

        List<string> terms = [];

        foreach (string word in trimmed.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Strip punctuation that is only ever noise at a word boundary, but keep what is
            // inside a word: "don't" and "40k" are words, and "COVID-19" is one term.
            string cleaned = word.Trim('.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'');

            if (cleaned.Length == 0)
            {
                continue;
            }

            terms.Add(Quote(cleaned));
        }

        if (terms.Count == 0)
        {
            return null;
        }

        // Every word must appear. A search that returned rows containing any one of them would
        // bury the meeting somebody was looking for under everything that mentioned "the".
        return string.Join(" AND ", terms);
    }

    /// <summary>Quotes a string as an FTS5 literal, doubling any quote inside it.</summary>
    private static string Quote(string value)
    {
        StringBuilder quoted = new(value.Length + 4);
        quoted.Append('"');

        foreach (char character in value)
        {
            if (character == '"')
            {
                quoted.Append('"');
            }

            quoted.Append(character);
        }

        quoted.Append('"');
        return quoted.ToString();
    }
}
