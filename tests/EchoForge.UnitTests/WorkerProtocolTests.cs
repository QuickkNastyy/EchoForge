using System.Text.Json;
using EchoForge.Contracts.Workers;

namespace EchoForge.UnitTests;

/// <summary>
/// Framing and validation, without a process.
///
/// <para>
/// These are the failures most likely to be interesting and least likely to be reproducible
/// on demand from a real worker: a line that is half-written, a version from a future build,
/// a progress counter that claims more work was done than exists. They are decided by pure
/// code so a failure points at a line rather than at a flaky child.
/// </para>
/// </summary>
public sealed class WorkerProtocolTests
{
    private static string Line(string json) => json;

    // -- envelopes ---------------------------------------------------------------------

    [Fact]
    public void ABlankLineMeansNothingAndIsSkippedRatherThanRefused()
    {
        foreach (string blank in (string[])["", "   ", "\t"])
        {
            WorkerMessageParse parse = WorkerMessageCodec.Parse(blank);

            Assert.True(parse.IsIgnorable);
            Assert.Equal(WorkerParseFailure.Blank, parse.Failure);
        }
    }

    [Fact]
    public void InvalidJsonIsRefusedRatherThanGuessedAt()
    {
        WorkerMessageParse parse = WorkerMessageCodec.Parse("{\"protocol_version\":1, \"type\":");

        Assert.Equal(WorkerParseFailure.InvalidJson, parse.Failure);
        Assert.Null(parse.Message);
    }

    [Fact]
    public void ALineThatIsNotAnObjectIsNotAnEnvelope()
    {
        Assert.Equal(WorkerParseFailure.NotAnEnvelope, WorkerMessageCodec.Parse("[1,2,3]").Failure);
        Assert.Equal(WorkerParseFailure.NotAnEnvelope, WorkerMessageCodec.Parse("\"hello\"").Failure);
    }

    [Fact]
    public void AMissingVersionOrTypeIsNotAnEnvelope()
    {
        Assert.Equal(
            WorkerParseFailure.NotAnEnvelope,
            WorkerMessageCodec.Parse(Line("{\"type\":\"ready\"}")).Failure);

        Assert.Equal(
            WorkerParseFailure.NotAnEnvelope,
            WorkerMessageCodec.Parse(Line("{\"protocol_version\":1}")).Failure);
    }

    [Fact]
    public void AnUnknownVersionIsRefusedBeforeItsBodyIsInterpreted()
    {
        // The fields below are nonsense for version 1. That must not matter: the version is
        // checked first, so a future shape is never half-read into today's types.
        WorkerMessageParse parse = WorkerMessageCodec.Parse(
            Line("{\"protocol_version\":2,\"type\":\"result\",\"totally\":\"different\"}"));

        Assert.Equal(WorkerParseFailure.UnsupportedVersion, parse.Failure);
        Assert.Equal(2, parse.ProtocolVersion);
        Assert.Equal("result", parse.MessageType);
    }

    [Fact]
    public void AnUnknownTypeIsNamedInTheFailure()
    {
        WorkerMessageParse parse = WorkerMessageCodec.Parse(
            Line("{\"protocol_version\":1,\"type\":\"invented\"}"));

        Assert.Equal(WorkerParseFailure.UnknownType, parse.Failure);
        Assert.Equal("invented", parse.MessageType);
    }

    // -- shapes ------------------------------------------------------------------------

    [Fact]
    public void AWellFormedReadyIsAccepted()
    {
        WorkerMessageParse parse = WorkerMessageCodec.Parse(Line(
            "{\"protocol_version\":1,\"type\":\"ready\",\"worker_version\":\"0.1.0\"," +
            "\"supported_protocol_versions\":[1],\"backends\":[\"mock\"]}"));

        ReadyMessage ready = Assert.IsType<ReadyMessage>(parse.Message);
        Assert.Equal("0.1.0", ready.WorkerVersion);
        Assert.Contains(1, ready.SupportedProtocolVersions);
        Assert.Contains("mock", ready.Backends);
    }

    [Fact]
    public void AReadyThatDeclaresNoVersionsOrNoBackendsIsMalformed()
    {
        Assert.Equal(WorkerParseFailure.InvalidShape, WorkerMessageCodec.Parse(Line(
            "{\"protocol_version\":1,\"type\":\"ready\",\"worker_version\":\"x\"," +
            "\"supported_protocol_versions\":[],\"backends\":[\"mock\"]}")).Failure);

        Assert.Equal(WorkerParseFailure.InvalidShape, WorkerMessageCodec.Parse(Line(
            "{\"protocol_version\":1,\"type\":\"ready\",\"worker_version\":\"x\"," +
            "\"supported_protocol_versions\":[1],\"backends\":[]}")).Failure);
    }

    [Fact]
    public void ProgressThatClaimsMoreWorkThanExistsIsMalformed()
    {
        WorkerMessageParse parse = WorkerMessageCodec.Parse(Line(
            "{\"protocol_version\":1,\"type\":\"progress\",\"job_id\":\"j\"," +
            "\"stage\":\"transcribing_microphone\",\"completed_units\":99,\"total_units\":3}"));

        Assert.Equal(WorkerParseFailure.InvalidShape, parse.Failure);
    }

    [Fact]
    public void ProgressWithAnUnknownStageIsMalformed()
    {
        WorkerMessageParse parse = WorkerMessageCodec.Parse(Line(
            "{\"protocol_version\":1,\"type\":\"progress\",\"job_id\":\"j\"," +
            "\"stage\":\"daydreaming\",\"completed_units\":1,\"total_units\":3}"));

        Assert.Equal(WorkerParseFailure.InvalidShape, parse.Failure);
    }

    [Fact]
    public void ProgressWithAMissingFieldIsMalformed()
    {
        WorkerMessageParse parse = WorkerMessageCodec.Parse(Line(
            "{\"protocol_version\":1,\"type\":\"progress\",\"job_id\":\"j\",\"stage\":\"merging\"}"));

        Assert.Equal(WorkerParseFailure.InvalidShape, parse.Failure);
    }

    [Fact]
    public void AResultWithoutARealDigestIsMalformed()
    {
        WorkerMessageParse parse = WorkerMessageCodec.Parse(Line(
            "{\"protocol_version\":1,\"type\":\"result\",\"job_id\":\"j\",\"output_path\":\"t.json\"," +
            "\"sha256\":\"not-a-real-digest\",\"segment_count\":1,\"duration_seconds\":2.0}"));

        Assert.Equal(WorkerParseFailure.InvalidShape, parse.Failure);
    }

    [Fact]
    public void AResultDigestMustBeLowerCaseHex()
    {
        string upper = new('A', 64);
        WorkerMessageParse parse = WorkerMessageCodec.Parse(Line(
            $"{{\"protocol_version\":1,\"type\":\"result\",\"job_id\":\"j\",\"output_path\":\"t.json\"," +
            $"\"sha256\":\"{upper}\",\"segment_count\":1,\"duration_seconds\":2.0}}"));

        Assert.Equal(WorkerParseFailure.InvalidShape, parse.Failure);
    }

    [Fact]
    public void AWellFormedResultIsAccepted()
    {
        string digest = new('a', 64);
        WorkerMessageParse parse = WorkerMessageCodec.Parse(Line(
            $"{{\"protocol_version\":1,\"type\":\"result\",\"job_id\":\"j\",\"output_path\":\"t.json\"," +
            $"\"sha256\":\"{digest}\",\"segment_count\":4,\"duration_seconds\":2.5}}"));

        ResultMessage result = Assert.IsType<ResultMessage>(parse.Message);
        Assert.Equal(4, result.SegmentCount);
        Assert.Equal(2.5, result.DurationSeconds);
    }

    [Fact]
    public void AnErrorWithAnUnknownCodeIsMalformed()
    {
        WorkerMessageParse parse = WorkerMessageCodec.Parse(Line(
            "{\"protocol_version\":1,\"type\":\"error\",\"code\":\"gremlins\",\"stage\":\"merging\"}"));

        Assert.Equal(WorkerParseFailure.InvalidShape, parse.Failure);
    }

    [Fact]
    public void TimeoutIsNotAnErrorAWorkerMayClaim()
    {
        // A timeout is the host's verdict on a silent child. A worker asserting one would be
        // describing something it cannot observe.
        Assert.DoesNotContain("timeout", WorkerErrorCodes.All);
    }

    [Fact]
    public void EveryErrorCodeTheWorkerKnowsIsAcceptedByTheCodec()
    {
        foreach (string code in WorkerErrorCodes.All)
        {
            WorkerMessageParse parse = WorkerMessageCodec.Parse(Line(
                $"{{\"protocol_version\":1,\"type\":\"error\",\"code\":\"{code}\",\"stage\":\"preparing\"}}"));

            Assert.True(parse.IsMessage, code);
        }
    }

    // -- round trips -------------------------------------------------------------------

    [Fact]
    public void EveryMessageSerialisesToExactlyOneLine()
    {
        WorkerMessage[] messages =
        [
            new HelloMessage { HostVersion = "EchoForge/test" },
            new StartJobMessage
            {
                JobId = "job-1",
                Request = SmallRequest(),
            },
            new CancelMessage { JobId = "job-1", Reason = "user" },
        ];

        foreach (WorkerMessage message in messages)
        {
            string line = WorkerMessageCodec.Serialize(message);

            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.Equal(message.Type, document.RootElement.GetProperty("type").GetString());
            Assert.Equal(WorkerProtocol.Version, document.RootElement.GetProperty("protocol_version").GetInt32());
        }
    }

    [Fact]
    public void AHostOnlyMessageStillRoundTripsSoTheSupervisorCanRecogniseIt()
    {
        // The supervisor must be able to tell that a worker echoed a host-only message back,
        // which means the codec has to parse one rather than call it unknown.
        string line = WorkerMessageCodec.Serialize(new HelloMessage { HostVersion = "x" });

        Assert.IsType<HelloMessage>(WorkerMessageCodec.Parse(line).Message);
    }

    [Fact]
    public void ProgressFractionIsClampedRatherThanDividingByZero()
    {
        ProgressMessage empty = new()
        {
            JobId = "j",
            Stage = "preparing",
            CompletedUnits = 0,
            TotalUnits = 0,
        };

        Assert.Equal(0, empty.Fraction);
    }

    // -- stage names -------------------------------------------------------------------

    [Fact]
    public void EveryStageHasAWireNameThatParsesBack()
    {
        foreach (WorkerStage stage in Enum.GetValues<WorkerStage>())
        {
            string wire = WorkerStages.ToWire(stage);

            Assert.True(WorkerStages.TryParse(wire, out WorkerStage parsed));
            Assert.Equal(stage, parsed);
        }
    }

    [Fact]
    public void AnUnknownStageNameDoesNotParse()
    {
        Assert.False(WorkerStages.TryParse("daydreaming", out _));
        Assert.False(WorkerStages.TryParse(null, out _));
    }

    internal static TranscriptionRequest SmallRequest() => new()
    {
        SessionId = "01JTEST",
        TranscriptRevision = 1,
        CreatedAtUtc = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
        SessionRoot = @"C:\sessions\01JTEST",
        OutputPath = @"C:\sessions\01JTEST\transcript\transcript.v1.json",
        DurationSeconds = 60,
        Epochs = [new RequestEpoch(1, 0, 60)],
        Tracks = [],
        Options = new RequestOptions { Backend = WorkerProtocol.MockBackend },
    };
}
