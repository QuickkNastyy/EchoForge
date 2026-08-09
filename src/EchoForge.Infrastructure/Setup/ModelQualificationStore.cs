using System.Text.Json;
using EchoForge.Contracts.Setup;

namespace EchoForge.Infrastructure.Setup;

/// <summary>
/// Remembers that a model was proved to work here, and forgets it when that stops being true.
///
/// <para>
/// A cache of a fact, never the fact itself. The question "is this model ready" is answered by
/// running it; this only saves somebody from having to answer it again every time the settings
/// page is opened. Every read is checked against the revision currently installed, so a record
/// that outlived what it described is discarded rather than believed.
/// </para>
/// </summary>
public sealed class ModelQualificationStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _root;

    public ModelQualificationStore(string modelsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        _root = Path.Combine(modelsRoot, "qualification");
    }

    /// <summary>
    /// What is known about this model, or null.
    ///
    /// <para>
    /// Null both when nothing was ever recorded and when what was recorded describes a different
    /// revision. The caller cannot tell those apart, and should not: neither is evidence that the
    /// model on disk works.
    /// </para>
    /// </summary>
    public ModelQualification? Read(string modelId, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        string path = PathFor(modelId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            ModelQualification? record = JsonSerializer.Deserialize<ModelQualification>(
                File.ReadAllText(path), Json);

            return record is not null && record.Describes(revision) ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable record is the same as no record. It is a cache.
            return null;
        }
    }

    public void Write(ModelQualification record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            Directory.CreateDirectory(_root);
            string path = PathFor(record.ModelId);
            string temporary = path + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the record costs one re-qualification, which is a far smaller problem than
            // failing an install that otherwise succeeded.
        }
    }

    public void Forget(string modelId)
    {
        try
        {
            File.Delete(PathFor(modelId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string PathFor(string modelId) =>
        Path.Combine(_root, Sanitize(modelId) + ".json");

    private static string Sanitize(string modelId)
    {
        Span<char> buffer = stackalloc char[modelId.Length];
        for (int i = 0; i < modelId.Length; i++)
        {
            char c = modelId[i];
            buffer[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-';
        }

        return new string(buffer);
    }
}
