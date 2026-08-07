using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EchoForge.Contracts.Evaluation;

namespace EchoForge.Core.Evaluation;

/// <summary>
/// One completed meeting/model pair, and everything that would make it stale.
///
/// <para>
/// The identity is the point. A resumed evaluation that reuses a result produced under a different
/// prompt, a different model revision or an edited gold fact is not a resumed evaluation — it is
/// two half-experiments reported as one, and the seam is invisible in the output. So a checkpoint
/// is keyed by the full input identity and a mismatch re-runs rather than reusing.
/// </para>
/// </summary>
public sealed record EvaluationCheckpoint
{
    [JsonPropertyName("meeting_id")]
    public required string MeetingId { get; init; }

    [JsonPropertyName("backend")]
    public required string Backend { get; init; }

    /// <summary>Covers the corpus gold, the model revision, the prompts, and the run settings.</summary>
    [JsonPropertyName("input_fingerprint")]
    public required string InputFingerprint { get; init; }

    [JsonPropertyName("completed_utc")]
    public DateTimeOffset CompletedUtc { get; init; }

    [JsonPropertyName("score")]
    public required MeetingScore Score { get; init; }
}

/// <summary>A resumable evaluation's accumulated results.</summary>
public sealed record EvaluationJournal
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("corpus_id")]
    public string CorpusId { get; init; } = string.Empty;

    [JsonPropertyName("entries")]
    public IReadOnlyList<EvaluationCheckpoint> Entries { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

/// <summary>Reads and writes the resume journal, and decides what may be reused.</summary>
public static class EvaluationCheckpoints
{
    /// <summary>
    /// Everything a result depends on, in one digest.
    ///
    /// <para>
    /// Model revision and prompt versions are in here for the same reason the chunk fingerprint
    /// carries them: a result produced by a different model, or by the same model asked a
    /// different question, is a different result however similar the meeting was.
    /// </para>
    /// </summary>
    public static string Fingerprint(
        string corpusFingerprint,
        string meetingId,
        string backend,
        string modelRevision,
        IEnumerable<string> promptVersions,
        string settings)
    {
        StringBuilder builder = new();
        builder.Append(corpusFingerprint).Append('');
        builder.Append(meetingId).Append('');
        builder.Append(backend).Append('');
        builder.Append(modelRevision).Append('');
        builder.AppendJoin(',', promptVersions.OrderBy(v => v, StringComparer.Ordinal));
        builder.Append('');
        builder.Append(settings);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static EvaluationJournal Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new EvaluationJournal();
            }

            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<EvaluationJournal>(stream, EvaluationJournal.Json) ?? new EvaluationJournal();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable journal costs time, never correctness: everything is simply re-run.
            return new EvaluationJournal();
        }
    }

    /// <summary>
    /// Adds one result, replacing any earlier entry for the same pair.
    ///
    /// <para>
    /// Written whole to a neighbour and swapped in, so a failure part-way through never leaves a
    /// truncated journal that would discard results already paid for.
    /// </para>
    /// </summary>
    public static EvaluationJournal Append(string path, EvaluationJournal journal, EvaluationCheckpoint entry)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(entry);

        List<EvaluationCheckpoint> entries =
        [
            .. journal.Entries.Where(e =>
                !string.Equals(e.MeetingId, entry.MeetingId, StringComparison.Ordinal) ||
                !string.Equals(e.Backend, entry.Backend, StringComparison.Ordinal)),
            entry,
        ];

        EvaluationJournal updated = journal with { Entries = entries };

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string staging = path + ".writing";
            File.WriteAllText(staging, JsonSerializer.Serialize(updated, EvaluationJournal.Json));
            File.Move(staging, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the journal costs a re-run, not a wrong answer.
        }

        return updated;
    }

    /// <summary>The reusable result for this pair, or null when anything it depended on changed.</summary>
    public static MeetingScore? Reusable(EvaluationJournal journal, string meetingId, string backend, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(journal);

        EvaluationCheckpoint? entry = journal.Entries.FirstOrDefault(e =>
            string.Equals(e.MeetingId, meetingId, StringComparison.Ordinal) &&
            string.Equals(e.Backend, backend, StringComparison.Ordinal));

        return entry is not null && string.Equals(entry.InputFingerprint, fingerprint, StringComparison.Ordinal)
            ? entry.Score
            : null;
    }
}
