using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Transcripts;

namespace EchoForge.UnitTests;

/// <summary>
/// The two pieces of the transcript contract that live on the host: turning a recorded
/// session into a request, and refusing a transcript that could not be true.
/// </summary>
public sealed class TranscriptContractTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    // -- building a request --------------------------------------------------------------

    private static SessionSnapshot Snapshot(
        IEnumerable<SessionEpoch> epochs,
        IEnumerable<SessionTrack> tracks) =>
        new("01JSESSION", SessionState.Recorded, Origin, Origin, null, [.. epochs], [.. tracks]);

    private static SessionEpoch Epoch(int index, double startsAfterSeconds, double? lengthSeconds) =>
        new(
            index,
            Origin.AddSeconds(startsAfterSeconds),
            lengthSeconds is null ? null : Origin.AddSeconds(startsAfterSeconds + lengthSeconds.Value),
            0,
            lengthSeconds is null ? null : 1,
            lengthSeconds is null ? EpochEndReason.Running : EpochEndReason.Paused);

    private static SessionTrack Track(SourceTrack track, params AudioChunkMetadata[] chunks) =>
        new(track, "device", "device", new CaptureFormat(48000, 1, 16), chunks);

    private static AudioChunkMetadata Chunk(
        int index,
        int epoch,
        double startSeconds,
        long frames,
        SourceTrack track,
        int sampleRate = 48000) =>
        new(
            index,
            $"tracks/{(track == SourceTrack.Microphone ? "microphone" : "system")}/chunks/{index:D6}.wav",
            track,
            startSeconds,
            startSeconds + ((double)frames / sampleRate),
            sampleRate,
            1,
            frames,
            new string('a', 64),
            [],
            epoch);

    private static RequestOptions Options => new() { Backend = WorkerProtocol.MockBackend };

    private static TranscriptionRequest Build(SessionSnapshot snapshot)
    {
        RequestBuildResult result = TranscriptionRequestBuilder.Build(
            snapshot, @"C:\sessions\01JSESSION", @"C:\sessions\01JSESSION\transcript\transcript.v1.json",
            1, Origin, Options);

        Assert.True(result.Succeeded, result.Failure?.Detail);
        return result.Request!;
    }

    [Fact]
    public void ASingleEpochSessionStartsAtZeroAndLastsAsLongAsItsAudio()
    {
        SessionSnapshot snapshot = Snapshot(
            [Epoch(1, 0, 60)],
            [Track(SourceTrack.Microphone, Chunk(1, 1, 0, 48000 * 45, SourceTrack.Microphone))]);

        TranscriptionRequest request = Build(snapshot);

        RequestEpoch epoch = Assert.Single(request.Epochs);
        Assert.Equal(0, epoch.StartSeconds);

        // The wall clock says sixty seconds; the audio says forty-five. The audio wins,
        // because a transcript must not be able to name a moment that was never captured.
        Assert.Equal(45, epoch.EndSeconds, 6);
        Assert.Equal(45, request.DurationSeconds, 6);
    }

    [Fact]
    public void EpochRelativeChunkOffsetsBecomeSessionRelativeOnes()
    {
        SessionSnapshot snapshot = Snapshot(
            [Epoch(1, 0, 60), Epoch(2, 120, 60)],
            [
                Track(
                    SourceTrack.Microphone,
                    Chunk(1, 1, 0, 48000 * 60, SourceTrack.Microphone),
                    // Second epoch: its own clock starts again at zero.
                    Chunk(2, 2, 0, 48000 * 30, SourceTrack.Microphone)),
            ]);

        TranscriptionRequest request = Build(snapshot);
        RequestChunk second = request.Tracks[0].Chunks[1];

        Assert.Equal(120, second.StartSeconds, 6);
        Assert.Equal(150, second.EndSeconds, 6);
        Assert.Equal(120, request.Epochs[1].StartSeconds, 6);
    }

    [Fact]
    public void AClockThatJumpedBackwardsCannotProduceOverlappingEpochs()
    {
        // The second epoch's wall clock claims it began before the first one's audio ended,
        // which a suspend/resume across a time change can genuinely produce.
        SessionSnapshot snapshot = Snapshot(
            [Epoch(1, 0, 60), Epoch(2, 10, 60)],
            [
                Track(
                    SourceTrack.Microphone,
                    Chunk(1, 1, 0, 48000 * 60, SourceTrack.Microphone),
                    Chunk(2, 2, 0, 48000 * 30, SourceTrack.Microphone)),
            ]);

        TranscriptionRequest request = Build(snapshot);

        Assert.Equal(60, request.Epochs[0].EndSeconds, 6);
        Assert.True(request.Epochs[1].StartSeconds >= request.Epochs[0].EndSeconds);
    }

    [Fact]
    public void EpochLengthComesFromTheLongestTrackInIt()
    {
        SessionSnapshot snapshot = Snapshot(
            [Epoch(1, 0, 60)],
            [
                Track(SourceTrack.Microphone, Chunk(1, 1, 0, 48000 * 20, SourceTrack.Microphone)),
                Track(SourceTrack.System, Chunk(1, 1, 0, 48000 * 35, SourceTrack.System)),
            ]);

        TranscriptionRequest request = Build(snapshot);

        Assert.Equal(35, request.Epochs[0].EndSeconds, 6);
    }

    [Fact]
    public void ChunkEndComesFromTheFrameCountRatherThanTheRecordedEndTime()
    {
        // A chunk recorded at 16 kHz measured as though it were 48 kHz reads a third of its
        // real length. Deriving from frames and rate is what stops that being possible.
        SessionSnapshot snapshot = Snapshot(
            [Epoch(1, 0, 60)],
            [Track(SourceTrack.Microphone, Chunk(1, 1, 0, 16000 * 10, SourceTrack.Microphone, sampleRate: 16000))]);

        TranscriptionRequest request = Build(snapshot);

        Assert.Equal(10, request.Tracks[0].Chunks[0].EndSeconds, 6);
    }

    [Fact]
    public void TracksAreAlwaysOrderedMicrophoneThenSystem()
    {
        SessionSnapshot snapshot = Snapshot(
            [Epoch(1, 0, 60)],
            [
                Track(SourceTrack.System, Chunk(1, 1, 0, 48000, SourceTrack.System)),
                Track(SourceTrack.Microphone, Chunk(1, 1, 0, 48000, SourceTrack.Microphone)),
            ]);

        TranscriptionRequest request = Build(snapshot);

        Assert.Equal(TranscriptSpeakers.MicrophoneTrack, request.Tracks[0].SourceTrack);
        Assert.Equal(TranscriptSpeakers.SystemTrack, request.Tracks[1].SourceTrack);
    }

    [Fact]
    public void PathsAreNormalisedSoTheDigestDoesNotDependOnASeparator()
    {
        AudioChunkMetadata windowsStyle = Chunk(1, 1, 0, 48000, SourceTrack.Microphone) with
        {
            RelativePath = @"tracks\microphone\chunks\000001.wav",
        };

        TranscriptionRequest request = Build(Snapshot([Epoch(1, 0, 60)], [Track(SourceTrack.Microphone, windowsStyle)]));

        Assert.Equal("tracks/microphone/chunks/000001.wav", request.Tracks[0].Chunks[0].RelativePath);
    }

    [Fact]
    public void ASessionWithNoAudioIsRefusedRatherThanTranscribed()
    {
        RequestBuildResult result = TranscriptionRequestBuilder.Build(
            Snapshot([Epoch(1, 0, 60)], [Track(SourceTrack.Microphone)]),
            @"C:\sessions\x", @"C:\sessions\x\t.json", 1, Origin, Options);

        Assert.False(result.Succeeded);
        Assert.Equal(WorkerErrorCodes.InputMissing, result.Failure!.Code);
    }

    [Fact]
    public void AChunkNamingAnEpochTheSessionDoesNotRecordIsRefused()
    {
        RequestBuildResult result = TranscriptionRequestBuilder.Build(
            Snapshot(
                [Epoch(1, 0, 60)],
                [Track(SourceTrack.Microphone, Chunk(1, 4, 0, 48000, SourceTrack.Microphone))]),
            @"C:\sessions\x", @"C:\sessions\x\t.json", 1, Origin, Options);

        Assert.False(result.Succeeded);
        Assert.Equal(WorkerErrorCodes.InvalidRequest, result.Failure!.Code);
    }

    [Fact]
    public void TheSourceManifestDigestChangesWhenTheAudioIdentityDoes()
    {
        TranscriptionRequest first = Build(Snapshot(
            [Epoch(1, 0, 60)],
            [Track(SourceTrack.Microphone, Chunk(1, 1, 0, 48000, SourceTrack.Microphone))]));

        AudioChunkMetadata rehashed = Chunk(1, 1, 0, 48000, SourceTrack.Microphone) with
        {
            Sha256 = new string('b', 64),
        };
        TranscriptionRequest second = Build(Snapshot(
            [Epoch(1, 0, 60)],
            [Track(SourceTrack.Microphone, rehashed)]));

        Assert.NotEqual(
            TranscriptionRequestBuilder.SourceManifestSha256(first),
            TranscriptionRequestBuilder.SourceManifestSha256(second));
    }

    [Fact]
    public void TheSourceManifestDigestIsStableForTheSameAudio()
    {
        SessionSnapshot snapshot = Snapshot(
            [Epoch(1, 0, 60)],
            [Track(SourceTrack.Microphone, Chunk(1, 1, 0, 48000, SourceTrack.Microphone))]);

        Assert.Equal(
            TranscriptionRequestBuilder.SourceManifestSha256(Build(snapshot)),
            TranscriptionRequestBuilder.SourceManifestSha256(Build(snapshot)));
    }

    // -- validating a transcript ----------------------------------------------------------

    private static TranscriptSegment Segment(
        string id,
        double start,
        double end,
        string track = TranscriptSpeakers.MicrophoneTrack,
        int epoch = 1)
    {
        (string speakerId, string speakerName) = TranscriptSpeakers.For(track);
        return new TranscriptSegment
        {
            Id = id,
            Epoch = epoch,
            StartSeconds = start,
            EndSeconds = end,
            SpeakerId = speakerId,
            SpeakerName = speakerName,
            SourceTrack = track,
            Text = "text",
            Confidence = null,
            Language = TranscriptSpeakers.UndeterminedLanguage,
            Words = [new TranscriptWord("text", start, end, null)],
        };
    }

    private static TranscriptDocument Document(params TranscriptSegment[] segments) => new()
    {
        SessionId = "01JSESSION",
        TranscriptRevision = 1,
        CreatedAtUtc = Origin,
        DurationSeconds = 60,
        Model = new TranscriptModel("echoforge-mock", "mock", "mock-v1", "mock-v1", "none", false, "0.1.0"),
        Epochs = [new TranscriptEpoch(1, 0, 60)],
        Speakers =
        [
            new TranscriptSpeaker(TranscriptSpeakers.YouId, TranscriptSpeakers.YouName, TranscriptSpeakers.MicrophoneTrack),
            new TranscriptSpeaker(TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName, TranscriptSpeakers.SystemTrack),
        ],
        Languages = [new TranscriptLanguage(TranscriptSpeakers.MicrophoneTrack, "und", null)],
        Segments = segments,
    };

    [Fact]
    public void AWellFormedTranscriptIsAccepted()
    {
        TranscriptVerdict verdict = TranscriptValidator.Validate(
            Document(Segment("segment-000001", 0, 3), Segment("segment-000002", 3, 6)));

        Assert.True(verdict.IsValid, string.Join("; ", verdict.Problems));
    }

    [Fact]
    public void ASegmentOutsideItsEpochIsRefused()
    {
        TranscriptVerdict verdict = TranscriptValidator.Validate(Document(Segment("segment-000001", 58, 75)));

        Assert.Contains(verdict.Problems, p => p.Contains("outside epoch", StringComparison.Ordinal));
    }

    [Fact]
    public void ASegmentNamingAnEpochTheTranscriptDoesNotCarryIsRefused()
    {
        TranscriptVerdict verdict = TranscriptValidator.Validate(
            Document(Segment("segment-000001", 0, 3, epoch: 9)));

        Assert.Contains(verdict.Problems, p => p.Contains("not in this transcript", StringComparison.Ordinal));
    }

    [Fact]
    public void MicrophoneContentAttributedToAnyoneButYouIsRefused()
    {
        TranscriptSegment mislabelled = Segment("segment-000001", 0, 3) with { SpeakerName = "Alex" };

        TranscriptVerdict verdict = TranscriptValidator.Validate(Document(mislabelled));

        Assert.Contains(verdict.Problems, p => p.Contains("must be attributed to You", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemContentAttributedToAnyoneButRemoteIsRefused()
    {
        TranscriptSegment mislabelled = Segment("segment-000001", 0, 3, TranscriptSpeakers.SystemTrack) with
        {
            SpeakerId = TranscriptSpeakers.YouId,
            SpeakerName = TranscriptSpeakers.YouName,
        };

        TranscriptVerdict verdict = TranscriptValidator.Validate(Document(mislabelled));

        Assert.Contains(verdict.Problems, p => p.Contains("must be attributed to Remote", StringComparison.Ordinal));
    }

    [Fact]
    public void OutOfOrderSegmentsAreRefused()
    {
        TranscriptVerdict verdict = TranscriptValidator.Validate(
            Document(Segment("segment-000001", 10, 12), Segment("segment-000002", 2, 4)));

        Assert.Contains(verdict.Problems, p => p.Contains("out of order", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateSegmentIdsAreRefused()
    {
        TranscriptVerdict verdict = TranscriptValidator.Validate(
            Document(Segment("segment-000001", 0, 3), Segment("segment-000001", 3, 6)));

        Assert.Contains(verdict.Problems, p => p.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void AWordOutsideItsSegmentIsRefused()
    {
        TranscriptSegment segment = Segment("segment-000001", 1, 3) with
        {
            Words = [new TranscriptWord("late", 2, 9, null)],
        };

        TranscriptVerdict verdict = TranscriptValidator.Validate(Document(segment));

        Assert.Contains(verdict.Problems, p => p.Contains("outside its segment", StringComparison.Ordinal));
    }

    [Fact]
    public void WordsOutOfTimestampOrderAreRefused()
    {
        TranscriptSegment segment = Segment("segment-000001", 0, 4) with
        {
            Words =
            [
                new TranscriptWord("second", 3, 4, null),
                new TranscriptWord("first", 0, 1, null),
            ],
        };

        TranscriptVerdict verdict = TranscriptValidator.Validate(Document(segment));

        Assert.Contains(verdict.Problems, p => p.Contains("not in timestamp order", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOverlapCitingASegmentOnTheSameTrackIsRefused()
    {
        TranscriptSegment first = Segment("segment-000001", 0, 3) with
        {
            OverlapsSegmentIds = ["segment-000002"],
        };

        TranscriptVerdict verdict = TranscriptValidator.Validate(
            Document(first, Segment("segment-000002", 1, 4)));

        Assert.Contains(verdict.Problems, p => p.Contains("same-track overlap", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOverlapCitingAnUnknownSegmentIsRefused()
    {
        TranscriptSegment segment = Segment("segment-000001", 0, 3) with
        {
            OverlapsSegmentIds = ["segment-000404"],
        };

        TranscriptVerdict verdict = TranscriptValidator.Validate(Document(segment));

        Assert.Contains(verdict.Problems, p => p.Contains("unknown overlap", StringComparison.Ordinal));
    }

    [Fact]
    public void ACrossTrackOverlapIsAccepted()
    {
        TranscriptSegment mine = Segment("segment-000001", 0, 3) with
        {
            OverlapsSegmentIds = ["segment-000002"],
        };
        TranscriptSegment theirs = Segment("segment-000002", 1, 4, TranscriptSpeakers.SystemTrack) with
        {
            OverlapsSegmentIds = ["segment-000001"],
        };

        TranscriptVerdict verdict = TranscriptValidator.Validate(Document(mine, theirs));

        Assert.True(verdict.IsValid, string.Join("; ", verdict.Problems));
    }

    [Fact]
    public void ASegmentThatOutlivesTheSessionIsRefused()
    {
        TranscriptDocument document = Document(Segment("segment-000001", 0, 3)) with
        {
            DurationSeconds = 1,
        };

        TranscriptVerdict verdict = TranscriptValidator.Validate(document);

        Assert.Contains(verdict.Problems, p => p.Contains("after the session", StringComparison.Ordinal));
    }

    [Fact]
    public void OverlappingEpochsAreRefused()
    {
        TranscriptDocument document = Document(Segment("segment-000001", 0, 3)) with
        {
            Epochs = [new TranscriptEpoch(1, 0, 30), new TranscriptEpoch(2, 20, 50)],
        };

        TranscriptVerdict verdict = TranscriptValidator.Validate(document);

        Assert.Contains(verdict.Problems, p => p.Contains("before the previous epoch ends", StringComparison.Ordinal));
    }

    // -- the stage state ------------------------------------------------------------------

    [Fact]
    public void TranscriptionStateIsSeparateFromTheRecordingState()
    {
        // A session can be Recorded while transcription has never been asked for, is
        // running, has failed, and is running again. None of that is a recording state.
        TranscriptionStage stage = TranscriptionStage.NotRequested;

        Assert.Equal(ProcessingStageState.NotRequested, stage.State);
        Assert.False(stage.IsActive);

        stage = stage.Queue();
        Assert.True(stage.IsActive);

        stage = stage.Run(Origin, "digest");
        stage = stage.Succeed(1, Origin.AddMinutes(2));
        Assert.Equal(1, stage.Revision);

        // A later failure must not retract the revision that already succeeded.
        stage = stage.Run(Origin.AddMinutes(3), "digest").Fail("backend_failed", Origin.AddMinutes(4));

        Assert.Equal(ProcessingStageState.Failed, stage.State);
        Assert.Equal(1, stage.Revision);
        Assert.Equal("backend_failed", stage.FailureCode);
    }

    [Fact]
    public void CancellingAStageClearsTheFailureRatherThanKeepingAStaleOne()
    {
        TranscriptionStage stage = TranscriptionStage.NotRequested
            .Run(Origin, null)
            .Fail("backend_failed", Origin)
            .Run(Origin, null)
            .Cancel(Origin);

        Assert.Equal(ProcessingStageState.Cancelled, stage.State);
        Assert.Null(stage.FailureCode);
    }
}
