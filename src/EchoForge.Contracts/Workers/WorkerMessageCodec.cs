using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Workers;

/// <summary>Why a line could not become a message.</summary>
public enum WorkerParseFailure
{
    /// <summary>Nothing went wrong.</summary>
    None,

    /// <summary>A blank or whitespace-only line. It carries no meaning and is skipped.</summary>
    Blank,

    /// <summary>The line is not a JSON object at all.</summary>
    InvalidJson,

    /// <summary>Valid JSON, but not a protocol envelope: no version, or no type.</summary>
    NotAnEnvelope,

    /// <summary>A version this host does not speak. Never parsed further, on principle.</summary>
    UnsupportedVersion,

    /// <summary>A type this host has never heard of.</summary>
    UnknownType,

    /// <summary>The right type, but the wrong shape: a missing or mistyped required field.</summary>
    InvalidShape,
}

/// <summary>The outcome of reading one line.</summary>
public readonly record struct WorkerMessageParse(
    WorkerMessage? Message,
    WorkerParseFailure Failure,
    string? Detail,
    int? ProtocolVersion,
    string? MessageType)
{
    public bool IsMessage => Message is not null;

    /// <summary>True for a line that means nothing and should simply be skipped.</summary>
    public bool IsIgnorable => Failure == WorkerParseFailure.Blank;

    public static WorkerMessageParse Ok(WorkerMessage message) =>
        new(message, WorkerParseFailure.None, null, message.ProtocolVersion, message.Type);

    public static WorkerMessageParse Bad(
        WorkerParseFailure failure,
        string detail,
        int? version = null,
        string? type = null) =>
        new(null, failure, detail, version, type);
}

/// <summary>
/// Turns protocol lines into messages and back.
///
/// <para>
/// This is pure: no streams, no process, no timing. Framing and validation are the part of the
/// protocol most likely to be wrong in an interesting way, so they are testable without launching
/// anything.
/// </para>
/// </summary>
public static class WorkerMessageCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// One message, one line. The result never contains a newline, so a writer can append the
    /// line terminator without re-checking the payload.
    /// </summary>
    public static string Serialize(WorkerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, message.GetType(), Options);
    }

    /// <summary>
    /// Reads one line. Unknown versions are refused before the body is looked at: parsing the
    /// fields of a version this host does not speak is exactly how a protocol mismatch turns into
    /// a silent misinterpretation.
    /// </summary>
    public static WorkerMessageParse Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return WorkerMessageParse.Bad(WorkerParseFailure.Blank, "blank line");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return WorkerMessageParse.Bad(WorkerParseFailure.InvalidJson, ex.Message);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return WorkerMessageParse.Bad(WorkerParseFailure.NotAnEnvelope, "line is not a JSON object");
            }

            if (!root.TryGetProperty("protocol_version", out JsonElement versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out int version))
            {
                return WorkerMessageParse.Bad(WorkerParseFailure.NotAnEnvelope, "protocol_version is missing or not an integer");
            }

            if (!root.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return WorkerMessageParse.Bad(WorkerParseFailure.NotAnEnvelope, "type is missing or not a string", version);
            }

            string type = typeElement.GetString()!;

            if (!WorkerProtocol.IsSupported(version))
            {
                return WorkerMessageParse.Bad(
                    WorkerParseFailure.UnsupportedVersion,
                    $"protocol version {version} is not supported by this host",
                    version,
                    type);
            }

            return Deserialize(line, version, type);
        }
    }

    private static WorkerMessageParse Deserialize(string line, int version, string type)
    {
        try
        {
            WorkerMessage? message = type switch
            {
                WorkerProtocol.Types.Hello => JsonSerializer.Deserialize<HelloMessage>(line, Options),
                WorkerProtocol.Types.StartJob => JsonSerializer.Deserialize<StartJobMessage>(line, Options),
                WorkerProtocol.Types.Cancel => JsonSerializer.Deserialize<CancelMessage>(line, Options),
                WorkerProtocol.Types.Ready => JsonSerializer.Deserialize<ReadyMessage>(line, Options),
                WorkerProtocol.Types.Started => JsonSerializer.Deserialize<StartedMessage>(line, Options),
                WorkerProtocol.Types.Progress => JsonSerializer.Deserialize<ProgressMessage>(line, Options),
                WorkerProtocol.Types.Warning => JsonSerializer.Deserialize<WarningMessage>(line, Options),
                WorkerProtocol.Types.Result => JsonSerializer.Deserialize<ResultMessage>(line, Options),
                WorkerProtocol.Types.Error => JsonSerializer.Deserialize<ErrorMessage>(line, Options),
                WorkerProtocol.Types.Cancelled => JsonSerializer.Deserialize<CancelledMessage>(line, Options),
                _ => null,
            };

            if (message is null)
            {
                return WorkerMessageParse.Bad(
                    WorkerParseFailure.UnknownType,
                    $"unknown message type '{type}'",
                    version,
                    type);
            }

            string? shapeProblem = Validate(message);
            return shapeProblem is null
                ? WorkerMessageParse.Ok(message)
                : WorkerMessageParse.Bad(WorkerParseFailure.InvalidShape, shapeProblem, version, type);
        }
        catch (JsonException ex)
        {
            return WorkerMessageParse.Bad(WorkerParseFailure.InvalidShape, ex.Message, version, type);
        }
    }

    /// <summary>
    /// The invariants the type system cannot state. A progress message whose stage is unknown or
    /// whose counters are impossible is malformed even though it deserialized cleanly.
    /// </summary>
    private static string? Validate(WorkerMessage message) => message switch
    {
        ProgressMessage progress when !WorkerStages.TryParse(progress.Stage, out _) =>
            $"unknown progress stage '{progress.Stage}'",
        ProgressMessage progress when progress.CompletedUnits < 0 || progress.TotalUnits < 0 =>
            "progress counters cannot be negative",
        ProgressMessage progress when progress.CompletedUnits > progress.TotalUnits =>
            "progress completed_units exceeds total_units",

        ResultMessage result when !IsSha256(result.Sha256) =>
            "result sha256 is not 64 lower-case hex characters",
        ResultMessage result when result.SegmentCount < 0 =>
            "result segment_count is negative",
        ResultMessage result when result.DurationSeconds < 0 =>
            "result duration_seconds is negative",
        ResultMessage result when string.IsNullOrWhiteSpace(result.OutputPath) =>
            "result output_path is empty",

        ErrorMessage error when !WorkerErrorCodes.IsKnown(error.Code) =>
            $"unknown error code '{error.Code}'",
        ErrorMessage error when !WorkerStages.TryParse(error.Stage, out _) =>
            $"unknown error stage '{error.Stage}'",

        CancelledMessage cancelled when !WorkerStages.TryParse(cancelled.Stage, out _) =>
            $"unknown cancelled stage '{cancelled.Stage}'",

        ReadyMessage ready when ready.SupportedProtocolVersions.Count == 0 =>
            "ready declares no supported protocol versions",
        ReadyMessage ready when ready.Backends.Count == 0 =>
            "ready declares no backends",

        // Exactly one request, matching the job kind. A start_job carrying neither, or both,
        // describes no job the worker could run.
        StartJobMessage job when job.JobKind == WorkerProtocol.TranscribeJobKind && job.Request is null =>
            "a transcribe job carries no transcription request",
        StartJobMessage job when job.JobKind == WorkerProtocol.SummarizeJobKind && job.SummaryRequest is null =>
            "a summarize job carries no summary request",
        StartJobMessage job when job.Request is not null && job.SummaryRequest is not null =>
            "a job carries two requests",

        _ => null,
    };

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }
}
