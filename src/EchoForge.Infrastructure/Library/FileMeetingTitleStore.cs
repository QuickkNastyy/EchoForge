using System.Text.Json;
using System.Text.Json.Serialization;
using EchoForge.Contracts.Sessions;

namespace EchoForge.Infrastructure.Library;

/// <summary>Presentation-only recording title stored beside the session.</summary>
internal sealed record MeetingTitleFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("title")]
    public required string Title { get; init; }
}

[JsonSerializable(typeof(MeetingTitleFile))]
internal sealed partial class MeetingTitleContext : JsonSerializerContext;

/// <summary>
/// Stores a user-chosen recording name without rewriting the recovery snapshot or any canonical
/// transcript/summary revision. Recovery is free to rebuild <c>session.json</c>; this overlay remains
/// independent, exactly like speaker aliases.
/// </summary>
public sealed class FileMeetingTitleStore(ISessionStore sessions)
{
    private const int MaxTitleLength = 160;
    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly Lock _writeLock = new();

    public string PathFor(string sessionId) =>
        Path.Combine(_sessions.Resolve(sessionId).Root, "meeting-title.json");

    public string? Read(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        try
        {
            string path = PathFor(sessionId);
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            MeetingTitleFile? file = JsonSerializer.Deserialize(stream, MeetingTitleContext.Default.MeetingTitleFile);
            return Clean(file?.Title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Sets a custom title. Blank removes the overlay and restores the date/time title.</summary>
    public bool Rename(string sessionId, string? title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string? clean = Clean(title);
        string path = PathFor(sessionId);

        lock (_writeLock)
        {
            try
            {
                if (clean is null)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string staging = path + ".writing";

                using (FileStream stream = File.Create(staging))
                {
                    JsonSerializer.Serialize(
                        stream,
                        new MeetingTitleFile { Title = clean },
                        MeetingTitleContext.Default.MeetingTitleFile);
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

    private static string? Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string clean = string.Join(" ", title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= MaxTitleLength ? clean : clean[..MaxTitleLength].TrimEnd();
    }
}
