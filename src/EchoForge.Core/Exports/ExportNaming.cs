using System.Text;

namespace EchoForge.Core.Exports;

/// <summary>
/// Turning meeting text into a file name, and getting bytes onto disk without lying about it.
///
/// <para>
/// Meeting titles are user text. They contain colons, slashes, emoji, right-to-left marks, and
/// occasionally the name of a device. Windows additionally reserves a handful of names that look
/// perfectly ordinary — <c>CON</c>, <c>NUL</c>, <c>COM1</c> — and refuses trailing dots and
/// spaces. A sanitizer that only strips <see cref="Path.GetInvalidFileNameChars"/> passes all of
/// those straight through and produces a file that cannot be created, or worse, one that opens a
/// device.
/// </para>
/// </summary>
public static class ExportNaming
{
    /// <summary>
    /// Names Windows will not let a file have, whatever extension follows.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Long enough to stay recognisable, short enough that the whole path stays under the limit
    /// once a user's own folder is in front of it.
    /// </summary>
    private const int MaximumLength = 120;

    /// <summary>
    /// Makes a file name that is safe, non-empty, and still recognisable.
    ///
    /// <para>
    /// Unicode is <b>kept</b>, not stripped. A meeting titled in Japanese should export to a file
    /// a Japanese speaker can find; reducing it to ASCII would leave a row of underscores. Only
    /// what the filesystem genuinely refuses is replaced.
    /// </para>
    /// </summary>
    public static string Sanitize(string? candidate, string fallback = "meeting")
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return fallback;
        }

        StringBuilder safe = new(candidate.Length);
        char[] invalid = Path.GetInvalidFileNameChars();

        foreach (char character in candidate)
        {
            // Control characters are legal in some filesystems and useful in none.
            if (char.IsControl(character) || Array.IndexOf(invalid, character) >= 0)
            {
                safe.Append('-');
                continue;
            }

            safe.Append(character);
        }

        // Collapse the runs of dashes that replacing characters tends to produce.
        string collapsed = string.Join('-', safe.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));

        // Windows silently drops trailing dots and spaces, so a name ending in one resolves to a
        // different file than the one that was asked for.
        collapsed = collapsed.Trim().TrimEnd('.', ' ');

        if (collapsed.Length > MaximumLength)
        {
            collapsed = collapsed[..MaximumLength].TrimEnd('.', ' ', '-');
        }

        if (collapsed.Length == 0)
        {
            return fallback;
        }

        // A reserved name is reserved with or without an extension, so it is prefixed rather
        // than replaced: the user's title stays readable.
        string stem = Path.GetFileNameWithoutExtension(collapsed);
        return Reserved.Contains(stem) ? "_" + collapsed : collapsed;
    }

    /// <summary>
    /// Writes bytes through a temporary neighbour, so an interrupted write cannot leave a
    /// truncated file wearing the name of a complete one.
    /// </summary>
    public static ExportResult WriteAtomically(string destinationPath, byte[] payload, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(payload);

        string temporary = destinationPath + ".tmp";

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destinationPath, overwrite);
            return ExportResult.Ok(destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The canonical revision was only ever read, so a failure here costs the export and
            // nothing else.
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Leaving a .tmp behind is untidy, not harmful.
            }

            return ExportResult.Fail("write_failed", $"The file could not be written ({ex.GetType().Name}).");
        }
    }
}
