using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Processing;

namespace EchoForge.UnitTests;

/// <summary>
/// Dividing a prepared session into transcription work.
///
/// <para>
/// The planner is a pure function, so these build requests and derivative records directly rather
/// than producing real audio: a twenty-minute session takes no longer to plan than a one-second
/// one, and what is under test is the arithmetic and the rules, not the resampler.
/// </para>
/// </summary>
public sealed class WindowPlannerTests
{
    private const string SessionId = "01JWINDOW";

    private static RequestChunk Chunk(int index, int epoch, double start, double seconds, int rate = 16000) => new()
    {
        Index = index,
        Epoch = epoch,
        RelativePath = $"tracks/microphone/chunks/{index:D6}.wav",
        StartSeconds = start,
        EndSeconds = start + seconds,
        SampleRate = rate,
        Channels = 1,
        Frames = (long)(seconds * rate),
        Sha256 = new string('a', 64),
    };

    /// <summary>A session made of sixty-second chunks, which is what the recorder produces.</summary>
    private static TranscriptionRequest Request(params (int Epoch, double Start, double Seconds)[] stretches)
    {
        List<RequestChunk> chunks = [];
        List<RequestEpoch> epochs = [];
        int index = 1;

        foreach ((int epoch, double start, double seconds) in stretches)
        {
            double cursor = start;
            double end = start + seconds;

            while (cursor < end - 1e-9)
            {
                double length = Math.Min(60, end - cursor);
                chunks.Add(Chunk(index++, epoch, cursor, length));
                cursor += length;
            }

            epochs.Add(new RequestEpoch(epoch, start, end));
        }

        return new TranscriptionRequest
        {
            SessionId = SessionId,
            TranscriptRevision = 1,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
            SessionRoot = @"C:\sessions\01JWINDOW",
            OutputPath = @"C:\sessions\01JWINDOW\out.json",
            DurationSeconds = epochs.Count == 0 ? 0 : epochs[^1].EndSeconds,
            Epochs = epochs,
            Tracks = [new RequestTrack { SourceTrack = "microphone", Chunks = chunks }],
            Options = new RequestOptions { Backend = "mock" },
        };
    }

    private static DerivativeSet Derivatives(double totalSeconds, string sha = "b") => new(
    [
        new DerivativeRecord
        {
            SourceTrack = "microphone",
            RelativePath = "derived/audio/derivative-v1/microphone.wav",
            TimingMapRelativePath = "derived/audio/derivative-v1/microphone.timing.json",
            Sha256 = new string(sha[0], 64),
            TimingMapSha256 = new string('c', 64),
            SizeBytes = 44 + ((long)(totalSeconds * 16000) * 2),
            SampleRate = 16000,
            Channels = 1,
            TotalFrames = (long)(totalSeconds * 16000),
            SourceManifestSha256 = new string('d', 64),
            ProcessingVersion = "derivative-v1",
            CreatedUtc = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
        },
    ]);

    private static WindowPlan Plan(
        TranscriptionRequest request,
        DerivativeSet derivatives,
        WindowPlanOptions? options = null,
        IReadOnlyList<WindowCheckpoint>? existing = null) =>
        TranscriptionWindowPlanner.Plan(request, derivatives, ProcessingProfile.CpuInt8, options, existing);

    // -- shape ------------------------------------------------------------------------------

    [Fact]
    public void ASingleShortEpochBecomesOneWindow()
    {
        WindowPlan plan = Plan(Request((1, 0, 90)), Derivatives(90));

        TranscriptionWindow window = Assert.Single(plan.Windows);

        Assert.Equal(0, window.SessionStartSeconds);
        Assert.Equal(90, window.SessionEndSeconds);
        Assert.Equal(0, window.OverlapBeforeSeconds);
        Assert.Equal(0, window.OverlapAfterSeconds);
        Assert.Equal(1, window.Epoch);
        Assert.Equal(16000 * 90, window.EndFrame);
    }

    [Fact]
    public void ALongEpochIsCutIntoTenMinuteWindowsThatOverlapByFiveSeconds()
    {
        // Twenty-five minutes: enough for three windows and a remainder.
        WindowPlan plan = Plan(Request((1, 0, 1500)), Derivatives(1500));

        Assert.True(plan.Windows.Count >= 3);

        for (int i = 0; i < plan.Windows.Count; i++)
        {
            TranscriptionWindow window = plan.Windows[i];

            Assert.True(window.DurationSeconds <= 600 + 1e-9, "a window is longer than ten minutes");
            Assert.True(window.DurationSeconds > 0);

            if (i == 0)
            {
                Assert.Equal(0, window.OverlapBeforeSeconds);
                continue;
            }

            TranscriptionWindow previous = plan.Windows[i - 1];

            // Adjacent windows share exactly five seconds of audio, so a sentence spoken across
            // the boundary is heard whole by at least one of them.
            Assert.Equal(5, previous.SessionEndSeconds - window.SessionStartSeconds, 6);
            Assert.Equal(5, window.OverlapBeforeSeconds);
            Assert.Equal(5, previous.OverlapAfterSeconds);
        }

        Assert.Equal(0, plan.Windows[^1].OverlapAfterSeconds);
        Assert.Equal(1500, plan.Windows[^1].SessionEndSeconds, 6);
    }

    [Fact]
    public void TheWholeEpochIsCoveredWithNoHoleBetweenWindows()
    {
        WindowPlan plan = Plan(Request((1, 0, 2000)), Derivatives(2000));

        double reached = 0;
        foreach (TranscriptionWindow window in plan.Windows)
        {
            Assert.True(window.SessionStartSeconds <= reached + 1e-9, "a stretch of audio is in no window");
            reached = Math.Max(reached, window.SessionEndSeconds);
        }

        Assert.Equal(2000, reached, 6);
    }

    [Fact]
    public void AShortFinalWindowIsAllowed()
    {
        // 610 seconds: a full window and ten seconds left, which is longer than the overlap.
        WindowPlan plan = Plan(Request((1, 0, 610)), Derivatives(610));

        Assert.Equal(2, plan.Windows.Count);
        Assert.Equal(600, plan.Windows[0].DurationSeconds, 6);
        Assert.Equal(15, plan.Windows[1].DurationSeconds, 6);
        Assert.Equal(610, plan.Windows[1].SessionEndSeconds, 6);
    }

    [Fact]
    public void AFinalWindowIsNeverShorterThanTheOverlap()
    {
        // 600.1 seconds: only a tenth of a second falls past the first window. The second window
        // still starts an overlap back, so it carries context rather than being a sliver - and
        // that is a property of the algorithm, not a special case bolted onto it.
        WindowPlan plan = Plan(Request((1, 0, 600.1)), Derivatives(600.1));

        Assert.Equal(2, plan.Windows.Count);
        Assert.Equal(600.1, plan.Windows[^1].SessionEndSeconds, 6);
        Assert.True(plan.Windows[^1].DurationSeconds >= 5);

        foreach (double length in (double[])[600.1, 601, 700, 1195, 1200.5, 2400])
        {
            WindowPlan other = Plan(Request((1, 0, length)), Derivatives(length));
            Assert.True(
                other.Windows[^1].DurationSeconds >= 5 - 1e-9,
                $"a {length} second epoch produced a final window of {other.Windows[^1].DurationSeconds} seconds");
        }
    }

    [Fact]
    public void WindowsNeverStraddleAnEpochBoundary()
    {
        // Two epochs with a gap between them, each long enough to need several windows.
        WindowPlan plan = Plan(Request((1, 0, 900), (2, 960, 900)), Derivatives(1860));

        Assert.Contains(plan.Windows, w => w.Epoch == 1);
        Assert.Contains(plan.Windows, w => w.Epoch == 2);

        foreach (TranscriptionWindow window in plan.Windows)
        {
            if (window.Epoch == 1)
            {
                Assert.True(window.SessionEndSeconds <= 900 + 1e-9);
            }
            else
            {
                Assert.True(window.SessionStartSeconds >= 960 - 1e-9);
            }
        }

        // The first window of the second epoch starts fresh: there is nothing before it to
        // overlap with, because recording genuinely stopped.
        TranscriptionWindow firstOfSecond = plan.Windows.First(w => w.Epoch == 2);
        Assert.Equal(0, firstOfSecond.OverlapBeforeSeconds);
        Assert.Equal(960, firstOfSecond.SessionStartSeconds, 6);
    }

    [Fact]
    public void ChunkBoundariesAreIgnoredWhenChoosingWhereToCut()
    {
        // The source is sixty-second chunks; none of the boundaries land on a multiple of sixty
        // except by coincidence, because storage boundaries are not speech boundaries.
        WindowPlan plan = Plan(Request((1, 0, 1500)), Derivatives(1500));

        Assert.True(plan.Windows.Count > 2);
        Assert.Contains(plan.Windows.Skip(1), w => Math.Abs(w.SessionStartSeconds % 60) > 1e-6);
    }

    // -- empty and silent ---------------------------------------------------------------------------

    [Fact]
    public void ATrackWithNoAudioAtAllProducesNoWindows()
    {
        TranscriptionRequest request = Request((1, 0, 600)) with
        {
            Tracks = [new RequestTrack { SourceTrack = "microphone", Chunks = [] }],
        };

        WindowPlan plan = Plan(request, Derivatives(600));

        Assert.Empty(plan.Windows);
        Assert.Empty(plan.Checkpoints);
        Assert.True(plan.IsComplete);
    }

    [Fact]
    public void AnEpochThisTrackWasNotRecordingInProducesNoWindowsForIt()
    {
        // Two epochs, but the microphone only captured during the first.
        TranscriptionRequest request = Request((1, 0, 300), (2, 400, 300));
        request = request with
        {
            Tracks =
            [
                new RequestTrack
                {
                    SourceTrack = "microphone",
                    Chunks = [.. request.Tracks[0].Chunks.Where(c => c.Epoch == 1)],
                },
            ],
        };

        WindowPlan plan = Plan(request, Derivatives(700));

        Assert.NotEmpty(plan.Windows);
        Assert.All(plan.Windows, w => Assert.Equal(1, w.Epoch));
    }

    [Fact]
    public void ATrackWithNoDerivativeIsSkippedRatherThanPlannedAgainstNothing()
    {
        WindowPlan plan = Plan(Request((1, 0, 600)), new DerivativeSet([]));

        Assert.Empty(plan.Windows);
    }

    [Fact]
    public void BothTracksArePlannedSeparatelyAndNeverCombined()
    {
        TranscriptionRequest request = Request((1, 0, 300));
        request = request with
        {
            Tracks =
            [
                request.Tracks[0],
                new RequestTrack { SourceTrack = "system", Chunks = request.Tracks[0].Chunks },
            ],
        };

        DerivativeSet derivatives = new(
        [
            Derivatives(300).Derivatives[0],
            Derivatives(300).Derivatives[0] with { SourceTrack = "system", RelativePath = "derived/audio/derivative-v1/system.wav" },
        ]);

        WindowPlan plan = Plan(request, derivatives);

        Assert.NotEmpty(plan.For("microphone"));
        Assert.NotEmpty(plan.For("system"));
        Assert.All(plan.Windows, w => Assert.Contains(w.SourceTrack, (string[])["microphone", "system"]));

        // No window belongs to both, which is what keeps You and Remote deterministic.
        Assert.Equal(plan.Windows.Count, plan.Windows.Select(w => w.Id).Distinct().Count());
    }

    // -- determinism and identity ------------------------------------------------------------------------

    [Fact]
    public void PlanningTheSameSessionTwiceGivesTheSamePlan()
    {
        TranscriptionRequest request = Request((1, 0, 1500));

        WindowPlan first = Plan(request, Derivatives(1500));
        WindowPlan second = Plan(request, Derivatives(1500));

        Assert.Equal(
            first.Windows.Select(w => (w.Id, w.StartFrame, w.EndFrame, w.InputFingerprint)),
            second.Windows.Select(w => (w.Id, w.StartFrame, w.EndFrame, w.InputFingerprint)));
    }

    [Fact]
    public void WindowIdentifiersAreStableAndReadable()
    {
        WindowPlan plan = Plan(Request((1, 0, 1500)), Derivatives(1500));

        Assert.Equal("w-microphone-e001-0000", plan.Windows[0].Id);
        Assert.Equal("w-microphone-e001-0001", plan.Windows[1].Id);
    }

    [Fact]
    public void WindowFramesLineUpWithTheDerivativeTheyIndex()
    {
        WindowPlan plan = Plan(Request((1, 0, 1500)), Derivatives(1500));

        foreach (TranscriptionWindow window in plan.Windows)
        {
            Assert.Equal((long)Math.Round(window.SessionStartSeconds * 16000), window.StartFrame);
            Assert.Equal((long)Math.Round(window.SessionEndSeconds * 16000), window.EndFrame);
            Assert.True(window.EndFrame <= 1500 * 16000);
            Assert.True(window.Frames > 0);
        }
    }

    // -- checkpoints ---------------------------------------------------------------------------------------

    [Fact]
    public void EveryWindowStartsPending()
    {
        WindowPlan plan = Plan(Request((1, 0, 1500)), Derivatives(1500));

        Assert.Equal(plan.Windows.Count, plan.Checkpoints.Count);
        Assert.All(plan.Checkpoints, c => Assert.Equal(WindowCheckpointState.Pending, c.State));
        Assert.Equal(plan.Windows.Count, plan.Outstanding.Count);
        Assert.False(plan.IsComplete);
    }

    [Fact]
    public void ASucceededWindowIsReusedWhenNothingItDependsOnChanged()
    {
        TranscriptionRequest request = Request((1, 0, 1500));
        WindowPlan first = Plan(request, Derivatives(1500));

        List<WindowCheckpoint> done =
        [
            first.Checkpoints[0] with
            {
                State = WindowCheckpointState.Succeeded,
                CompletedUtc = new DateTimeOffset(2026, 8, 6, 12, 30, 0, TimeSpan.Zero),
            },
        ];

        WindowPlan second = Plan(request, Derivatives(1500), existing: done);

        Assert.Equal(WindowCheckpointState.Succeeded, second.CheckpointFor(first.Windows[0].Id)!.State);
        Assert.DoesNotContain(second.Outstanding, w => w.Id == first.Windows[0].Id);
        Assert.Equal(first.Windows.Count - 1, second.Outstanding.Count);
    }

    [Fact]
    public void ChangedAudioInvalidatesASucceededWindow()
    {
        TranscriptionRequest request = Request((1, 0, 1500));
        WindowPlan first = Plan(request, Derivatives(1500));

        List<WindowCheckpoint> done =
        [
            first.Checkpoints[0] with { State = WindowCheckpointState.Succeeded },
        ];

        // A different derivative digest: the audio behind this window is not what produced it.
        WindowPlan second = Plan(request, Derivatives(1500, sha: "e"), existing: done);

        Assert.Equal(WindowCheckpointState.Pending, second.CheckpointFor(first.Windows[0].Id)!.State);
        Assert.Equal(second.Windows.Count, second.Outstanding.Count);
    }

    [Fact]
    public void ChangedPlanningRulesInvalidateASucceededWindow()
    {
        TranscriptionRequest request = Request((1, 0, 1500));
        WindowPlan first = Plan(request, Derivatives(1500));
        List<WindowCheckpoint> done = [first.Checkpoints[0] with { State = WindowCheckpointState.Succeeded }];

        WindowPlan second = Plan(
            request, Derivatives(1500), new WindowPlanOptions { PlanningVersion = "windows-v2" }, done);

        Assert.Equal(WindowCheckpointState.Pending, second.CheckpointFor(first.Windows[0].Id)!.State);
    }

    [Fact]
    public void AFailedWindowDoesNotDisturbTheOnesThatSucceeded()
    {
        TranscriptionRequest request = Request((1, 0, 1500));
        WindowPlan first = Plan(request, Derivatives(1500));

        List<WindowCheckpoint> mixed =
        [
            first.Checkpoints[0] with { State = WindowCheckpointState.Succeeded },
            first.Checkpoints[1] with { State = WindowCheckpointState.Failed, FailureCode = "worker_crashed" },
            first.Checkpoints[2] with { State = WindowCheckpointState.Cancelled },
        ];

        WindowPlan second = Plan(request, Derivatives(1500), existing: mixed);

        Assert.Equal(WindowCheckpointState.Succeeded, second.CheckpointFor(first.Windows[0].Id)!.State);

        // A failure says nothing about whether the work can succeed now, so it goes back to
        // pending - and the window that already finished is untouched.
        Assert.Equal(WindowCheckpointState.Pending, second.CheckpointFor(first.Windows[1].Id)!.State);
        Assert.Equal(WindowCheckpointState.Pending, second.CheckpointFor(first.Windows[2].Id)!.State);
        Assert.DoesNotContain(second.Outstanding, w => w.Id == first.Windows[0].Id);
    }

    [Fact]
    public void APlanWithEveryWindowSucceededIsComplete()
    {
        TranscriptionRequest request = Request((1, 0, 700));
        WindowPlan first = Plan(request, Derivatives(700));

        List<WindowCheckpoint> done =
            [.. first.Checkpoints.Select(c => c with { State = WindowCheckpointState.Succeeded })];

        WindowPlan second = Plan(request, Derivatives(700), existing: done);

        Assert.True(second.IsComplete);
        Assert.Empty(second.Outstanding);
    }

    // -- refusals ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnOverlapAsLongAsTheWindowIsRefused()
    {
        // Windows that overlapped by their whole length would never advance.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Plan(Request((1, 0, 600)), Derivatives(600), new WindowPlanOptions { WindowSeconds = 60, OverlapSeconds = 60 }));
    }

    [Fact]
    public void AWindowWithNoLengthIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Plan(Request((1, 0, 600)), Derivatives(600), new WindowPlanOptions { WindowSeconds = 0 }));
    }
}
