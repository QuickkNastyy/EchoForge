using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;

namespace EchoForge.UnitTests;

public sealed class PcmChunkWriterTests
{
    private static readonly CaptureFormat Mono48 = new(48_000, 1, 16);
    private static readonly TimeSpan OneSecondChunks = TimeSpan.FromSeconds(1);

    private static byte[] Frames(int count, short value = 1000)
    {
        byte[] bytes = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), value);
        }

        return bytes;
    }

    /// <summary>
    /// Builds a packet that claims to start at <paramref name="timelineFrame"/>. The QPC
    /// position is what the writer actually uses; the device position is carried along as the
    /// diagnostic it is, since a resampling endpoint reports it in its own rate.
    /// </summary>
    private static PacketHeader Header(long timelineFrame, int frames, AudioPacketConditions conditions = AudioPacketConditions.None) =>
        new(timelineFrame, timelineFrame * 10_000_000 / 48_000, frames, conditions);

    [Fact]
    public void ContiguousPacketsProduceNoSilence()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.Microphone, Mono48, 0, OneSecondChunks);

        for (int i = 0; i < 10; i++)
        {
            writer.Write(Header(i * 480, 480), Frames(480));
        }

        Assert.Equal(4_800, writer.SessionFrames);
        Assert.Equal(0, writer.SilenceFramesInserted);
        Assert.Empty(writer.PendingDiscontinuities);
    }

    [Fact]
    public void AGapInTheTimestampBecomesExplicitSilence()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.System, Mono48, 0, OneSecondChunks);

        writer.Write(Header(0, 480), Frames(480));
        // The endpoint went silent for 100 ms; the next packet's timestamp resumes at 4,800.
        writer.Write(Header(4_800, 480), Frames(480));

        Assert.Equal(4_800 + 480, writer.SessionFrames);
        Assert.Equal(4_320, writer.SilenceFramesInserted);

        CaptureDiscontinuity gap = Assert.Single(writer.PendingDiscontinuities);
        Assert.Equal(DiscontinuityKind.Silence, gap.Kind);
        Assert.Equal(4_320, gap.FrameCount);
    }

    [Fact]
    public void AdvanceToFillsATrackWhoseEndpointStoppedDelivering()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.Microphone, Mono48, 0, TimeSpan.FromSeconds(60));

        // Ten seconds of audio, then the endpoint stalls and never sends another packet.
        for (int i = 0; i < 1_000; i++)
        {
            writer.Write(Header(i * 480, 480), Frames(480));
        }

        Assert.Equal(480_000, writer.SessionFrames);

        // The session ran for thirty seconds regardless of what the endpoint did.
        writer.AdvanceTo(30 * 10_000_000L);

        Assert.Equal(1_440_000, writer.SessionFrames);
        Assert.Equal(960_000, writer.SilenceFramesInserted);
    }

    [Fact]
    public void AdvanceToIsANoOpWhenTheTimelineIsAlreadyCurrent()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.Microphone, Mono48, 0, TimeSpan.FromSeconds(60));

        writer.Write(Header(0, 48_000), Frames(48_000));
        writer.AdvanceTo(1 * 10_000_000L);

        Assert.Equal(48_000, writer.SessionFrames);
        Assert.Equal(0, writer.SilenceFramesInserted);
    }

    [Fact]
    public void JitterBelowTheGapThresholdDoesNotChurnSilence()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.Microphone, Mono48, 0, OneSecondChunks);

        // Packets a few frames early or late are ordinary QPC jitter, not missing audio.
        writer.Write(Header(0, 480), Frames(480));
        writer.Write(Header(490, 480), Frames(480));
        writer.Write(Header(955, 480), Frames(480));

        Assert.Equal(1_440, writer.SessionFrames);
        Assert.Equal(0, writer.SilenceFramesInserted);
        Assert.Empty(writer.PendingDiscontinuities);
    }

    [Fact]
    public void FrameCountTracksWallClockAcrossALongSilence()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.System, Mono48, 0, TimeSpan.FromSeconds(60));

        writer.Write(Header(0, 480), Frames(480));
        // Thirty seconds of silence: no packets arrive at all, then the clock has moved on.
        writer.Write(Header(480 + (48_000 * 30), 480), Frames(480));

        // The timeline must still be 30 seconds long, which is the whole point of
        // deriving silence from the packet timestamp rather than from arrival time.
        double seconds = writer.SessionFrames / 48_000.0;
        Assert.InRange(seconds, 30.01, 30.03);
    }

    [Fact]
    public void ChunksRotateAtTheConfiguredDurationAndAreIndependentlyValid()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.Microphone, Mono48, 0, OneSecondChunks);

        // Two and a half seconds of audio in 480-frame packets.
        for (int i = 0; i < 250; i++)
        {
            writer.Write(Header(i * 480, 480), Frames(480));
        }

        Assert.Equal(2, writer.CompletedChunks.Count);

        foreach (AudioChunkMetadata chunk in writer.CompletedChunks)
        {
            Assert.Equal(48_000, chunk.SampleFrames);
            Assert.Equal(64, chunk.Sha256.Length);

            string path = Path.Combine(temp.Path, "chunks", $"{chunk.Index:D6}.wav");
            WavValidation validation = WavPcm16Reader.Validate(path);
            Assert.True(validation.IsValid, validation.Problem);
            Assert.Equal(48_000, validation.FrameCount);
        }

        Assert.Equal(0.0, writer.CompletedChunks[0].StartSeconds);
        Assert.Equal(1.0, writer.CompletedChunks[1].StartSeconds);
    }

    [Fact]
    public void APacketSpanningAChunkBoundaryIsSplitWithoutLoss()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.Microphone, Mono48, 0, OneSecondChunks);

        // 47,800 frames, then a 400-frame packet that straddles the 48,000 boundary.
        writer.Write(Header(0, 47_800), Frames(47_800));
        writer.Write(Header(47_800, 400), Frames(400));
        writer.Complete();

        Assert.Equal(48_200, writer.SessionFrames);
        Assert.Equal(2, writer.CompletedChunks.Count);
        Assert.Equal(48_000, writer.CompletedChunks[0].SampleFrames);
        Assert.Equal(200, writer.CompletedChunks[1].SampleFrames);
    }

    [Fact]
    public void ATimestampBehindTheTimelineDropsOverlapRatherThanRewriting()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.System, Mono48, 0, OneSecondChunks);

        writer.Write(Header(0, 9_600), Frames(9_600));
        // A packet claiming 50 ms in, when 200 ms is already on the timeline.
        writer.Write(Header(2_400, 480), Frames(480));

        // Audio already written is never rewritten, so the whole overlapping packet is dropped.
        Assert.Equal(9_600, writer.SessionFrames);

        CaptureDiscontinuity error = Assert.Single(writer.PendingDiscontinuities);
        Assert.Equal(DiscontinuityKind.TimestampError, error.Kind);
        Assert.Equal(480, error.FrameCount);
    }

    [Fact]
    public void AnEngineDiscontinuityIsRecordedButDoesNotLoseAudio()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.System, Mono48, 0, OneSecondChunks);

        writer.Write(Header(0, 480), Frames(480));
        writer.Write(Header(480, 480, AudioPacketConditions.DataDiscontinuity), Frames(480));

        Assert.Equal(960, writer.SessionFrames);
        CaptureDiscontinuity flagged = Assert.Single(writer.PendingDiscontinuities);
        Assert.Equal(DiscontinuityKind.EngineDiscontinuity, flagged.Kind);
    }

    [Fact]
    public void ASilentPacketIsWrittenAsSilenceRatherThanItsPayload()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.System, Mono48, 0, OneSecondChunks);

        writer.Write(Header(0, 480, AudioPacketConditions.Silent), ReadOnlySpan<byte>.Empty);
        writer.Complete();

        Assert.Equal(480, writer.SessionFrames);
        AudioChunkMetadata chunk = Assert.Single(writer.CompletedChunks);

        string path = Path.Combine(temp.Path, "chunks", $"{chunk.Index:D6}.wav");
        byte[] bytes = File.ReadAllBytes(path);
        Assert.All(bytes[WavPcm16Writer.HeaderBytes..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void FinalizedChunksAreDurableBeforeTheNextChunkCompletes()
    {
        using TempDirectory temp = new();

        // A 200 ms flush cadence so the active chunk has durable bytes half a second in.
        PcmChunkWriter writer = new(
            temp.Path, SourceTrack.Microphone, Mono48, 0, OneSecondChunks, TimeSpan.FromMilliseconds(200));

        try
        {
            // 1.5 seconds: one chunk finalizes, the second is still active.
            for (int i = 0; i < 150; i++)
            {
                writer.Write(Header(i * 480, 480), Frames(480));
            }

            Assert.Single(writer.CompletedChunks);

            // The finalized chunk is complete and valid on disk right now, without waiting
            // for the session to end. This is what survives a kill.
            string finalized = Path.Combine(temp.Path, "chunks", "000001.wav");
            Assert.True(File.Exists(finalized));
            Assert.True(WavPcm16Reader.Validate(finalized).IsValid);

            // The active chunk exists and has flushed audio past its header, so a crash
            // leaves recoverable bytes rather than an empty stub.
            string active = Path.Combine(temp.Path, "active", "000002.part.wav");
            Assert.True(File.Exists(active));
            Assert.True(new FileInfo(active).Length > WavPcm16Writer.HeaderBytes);

            // The sidecar records how far the active chunk had got.
            Assert.True(File.Exists(Path.Combine(temp.Path, "active", "000002.part.state.json")));
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void AnAbandonedActiveChunkIsRepairableOnceItsHandleIsReleased()
    {
        using TempDirectory temp = new();
        string active;

        // Writing then releasing the handle without finalizing models what a killed
        // process leaves behind: flushed data and an unpatched header.
        PcmChunkWriter writer = new(
            temp.Path, SourceTrack.Microphone, Mono48, 0, OneSecondChunks, TimeSpan.FromMilliseconds(200));
        for (int i = 0; i < 150; i++)
        {
            writer.Write(Header(i * 480, 480), Frames(480));
        }

        active = Path.Combine(temp.Path, "active", "000002.part.wav");

        // The writer still holds the file open for writing, so share both accesses.
        byte[] abandoned;
        using (FileStream snapshot = new(active, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            abandoned = new byte[snapshot.Length];
            snapshot.ReadExactly(abandoned);
        }

        writer.Dispose();

        // Restore the file to its pre-finalization state and repair it as startup would.
        File.WriteAllBytes(active, abandoned);
        WavRepairResult repair = WavPcm16Reader.Repair(active, Mono48);

        Assert.True(repair.Repaired, repair.Problem);
        Assert.True(repair.FrameCount > 0);
        Assert.True(WavPcm16Reader.Validate(active).IsValid);
    }
}
