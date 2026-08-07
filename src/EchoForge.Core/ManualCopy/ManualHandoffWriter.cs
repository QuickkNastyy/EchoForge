using System.Text;
using EchoForge.Core.Exports;

namespace EchoForge.Core.ManualCopy;

/// <summary>
/// Saves a composed handoff to a file the user chose, so the feature still works when the clipboard
/// does not.
///
/// <para>
/// Same discipline as every other export here: UTF-8 without a byte-order mark, a fixed
/// <c>\n</c> line ending already baked into the payload, an existing file never replaced unless the
/// caller says so, and the write staged through a temporary neighbour and moved into place, so an
/// interrupted save cannot leave a truncated file wearing the real name. It writes only the payload
/// text it was given; it reads and changes nothing canonical.
/// </para>
/// </summary>
public static class ManualHandoffWriter
{
    /// <summary>
    /// Saves the handoff text — which may be the composed payload or a version the user edited in the
    /// preview, since edits belong to the copy, never to the canonical transcript.
    /// </summary>
    public static ExportResult Save(string text, string destinationPath, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (File.Exists(destinationPath) && !overwrite)
        {
            return ExportResult.Fail(
                "exists",
                "A file of that name is already there. Choose another name, or confirm replacing it.");
        }

        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
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
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destinationPath, overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            TryDelete(temporary);
            return ExportResult.Fail("write_failed", $"The handoff could not be written ({ex.GetType().Name}).");
        }

        return ExportResult.Ok(destinationPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp is untidy, not dangerous, and it never wears the real name.
        }
    }
}
