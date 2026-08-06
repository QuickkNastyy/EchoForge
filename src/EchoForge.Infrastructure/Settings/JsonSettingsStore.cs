using System.Text.Json;
using EchoForge.Contracts.Settings;

namespace EchoForge.Infrastructure.Settings;

/// <summary>
/// Settings as a single JSON file under <c>%LOCALAPPDATA%\EchoForge\config</c>, replaced
/// atomically so a crash mid-save cannot leave an unreadable file.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _path;

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EchoForge", "config", "settings.json");

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            using FileStream stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<AppSettings>(stream, Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Unreadable settings must not stop the app from recording.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string temporary = _path + ".tmp";
        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
    }
}
