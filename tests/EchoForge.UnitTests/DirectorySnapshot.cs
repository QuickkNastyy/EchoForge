using System.Security.Cryptography;

namespace EchoForge.UnitTests;

/// <summary>
/// A byte-for-byte fingerprint of a directory tree: every file's relative path, length, and
/// SHA-256. Used to prove that an operation changed nothing on disk.
/// </summary>
public sealed record DirectorySnapshot(IReadOnlyList<string> Entries)
{
    public static DirectorySnapshot Capture(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            return new DirectorySnapshot([]);
        }

        List<string> entries = [];
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            try
            {
                using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                entries.Add($"{relative}|{new FileInfo(file).Length}|{hash}");
            }
            catch (IOException)
            {
                // A session lease is held with FileShare.None and cannot be read at all. Its
                // presence is still worth recording; its contents are not the point.
                entries.Add($"{relative}|<exclusively held>");
            }
        }

        return new DirectorySnapshot(entries);
    }

    public string Describe() => string.Join(Environment.NewLine, Entries);

    public bool Matches(DirectorySnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Entries.SequenceEqual(other.Entries, StringComparer.Ordinal);
    }
}
