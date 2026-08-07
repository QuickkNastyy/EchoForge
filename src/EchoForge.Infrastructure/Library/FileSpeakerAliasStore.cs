using System.Text.Json;
using System.Text.Json.Serialization;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Sessions;
using EchoForge.Core.Library;

namespace EchoForge.Infrastructure.Library;

/// <summary>What a speaker should be shown as. Presentation only; never part of a transcript.</summary>
internal sealed record SpeakerAliasFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("aliases")]
    public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.Ordinal);
}

[JsonSerializable(typeof(SpeakerAliasFile))]
internal sealed partial class SpeakerAliasContext : JsonSerializerContext;

/// <summary>
/// Stores remote speaker names beside a session, without touching its transcripts.
///
/// <para>
/// A separate file is the entire design. Writing an alias into the transcript would rewrite an
/// immutable revision, break the digest it was activated under, and make every citation into it
/// unverifiable — to change a display name. Keeping it outside means renaming is reversible by
/// construction: there is nothing to restore, because nothing was overwritten.
/// </para>
///
/// <para>
/// The file is a preference, not an authority. Losing it costs the names a user chose and nothing
/// else, so it is written plainly and read defensively.
/// </para>
/// </summary>
public sealed class FileSpeakerAliasStore(ISessionStore sessions)
{
    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly Lock _writeLock = new();

    /// <summary>Beside the session's own files, not inside <c>transcript/</c>.</summary>
    public string PathFor(string sessionId) =>
        Path.Combine(_sessions.Resolve(sessionId).Root, "speaker-aliases.json");

    public SpeakerAliases Read(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        try
        {
            string path = PathFor(sessionId);
            if (!File.Exists(path))
            {
                return SpeakerAliases.None;
            }

            using FileStream stream = File.OpenRead(path);
            SpeakerAliasFile? file = JsonSerializer.Deserialize(stream, SpeakerAliasContext.Default.SpeakerAliasFile);

            if (file is null)
            {
                return SpeakerAliases.None;
            }

            // Sanitised on the way out as well as in. A file that somehow contains an alias for
            // You cannot express one, however it came to be on disk.
            return SpeakerPresentation.Sanitize(new SpeakerAliases { BySpeakerId = file.Aliases });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged preference file means the original names, not a broken session.
            return SpeakerAliases.None;
        }
    }

    /// <summary>Replaces the whole overlay. An empty one removes the file.</summary>
    public bool Write(string sessionId, SpeakerAliases aliases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        SpeakerAliases safe = SpeakerPresentation.Sanitize(aliases);
        string path = PathFor(sessionId);

        lock (_writeLock)
        {
            try
            {
                if (safe.IsEmpty)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                SpeakerAliasFile file = new()
                {
                    Aliases = new Dictionary<string, string>(safe.BySpeakerId, StringComparer.Ordinal),
                };

                string staging = path + ".writing";
                using (FileStream stream = File.Create(staging))
                {
                    JsonSerializer.Serialize(stream, file, SpeakerAliasContext.Default.SpeakerAliasFile);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(staging, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Sets or clears one name. Clearing restores the transcript's own.</summary>
    public bool Rename(string sessionId, string speakerId, string? alias) =>
        Write(sessionId, SpeakerPresentation.With(Read(sessionId), speakerId, alias));
}
