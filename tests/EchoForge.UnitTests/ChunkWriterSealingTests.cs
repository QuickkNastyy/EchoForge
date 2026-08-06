using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;

namespace EchoForge.UnitTests;

/// <summary>
/// Regression tests for the reviewed defect: a pipeline disposed after an explicit stop could
/// append silence and finalize a fresh chunk, mutating a session that had already been
/// validated and written to the manifest.
/// </summary>
public sealed class ChunkWriterSealingTests
{
    private static readonly CaptureFormat Mono48 = new(48_000, 1, 16);
    private static readonly TimeSpan OneSecondChunks = TimeSpan.FromSeconds(1);

    private static byte[] Frames(int count)
    {
        byte[] bytes = new byte[count * 2];
        Array.Fill(bytes, (byte)7);
        return bytes;
    }

    private static PacketHeader Header(long timelineFrame, int frames) =>
        new(timelineFrame, timelineFrame * 10_000_000 / 48_000, frames, AudioPacketConditions.None);

    private static PcmChunkWriter WriteTwoSeconds(TempDirectory temp)
    {
        PcmChunkWriter writer = new(temp.Path, SourceTrack.Microphone, Mono48, 0, OneSecondChunks);
        for (int i = 0; i < 200; i++)
        {
            writer.Write(Header(i * 480, 480), Frames(480));
        }

        return writer;
    }

    [Fact]
    public void CompleteSealsTheWriter()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = WriteTwoSeconds(temp);

        Assert.False(writer.IsSealed);
        writer.Complete();
        Assert.True(writer.IsSealed);
    }

    [Fact]
    public void AdvancingAfterCompleteLeavesTheTrackByteForByteUnchanged()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = WriteTwoSeconds(temp);

        writer.Complete();

        long framesAfterStop = writer.SessionFrames;
        int chunksAfterStop = writer.CompletedChunks.Count;
        List<string> hashesAfterStop = [.. writer.CompletedChunks.Select(c => c.Sha256)];
        DirectorySnapshot before = DirectorySnapshot.Capture(temp.Path);

        // This is exactly what Dispose used to do after an explicit Stop: pad the timeline to
        // "now", which manufactured silence and a whole new chunk.
        writer.AdvanceTo(600 * 10_000_000L, exact: true);
        writer.Write(Header(96_000, 480), Frames(480));
        writer.RecordOverflow(1_000, "should be ignored");
        writer.Complete();

        DirectorySnapshot after = DirectorySnapshot.Capture(temp.Path);

        Assert.Equal(framesAfterStop, writer.SessionFrames);
        Assert.Equal(chunksAfterStop, writer.CompletedChunks.Count);
        Assert.Equal(hashesAfterStop, writer.CompletedChunks.Select(c => c.Sha256).ToList());
        Assert.True(after.Matches(before), $"disk changed after seal:{Environment.NewLine}{after.Describe()}");
    }

    [Fact]
    public void DisposeAfterCompleteLeavesTheTrackByteForByteUnchanged()
    {
        using TempDirectory temp = new();
        PcmChunkWriter writer = WriteTwoSeconds(temp);

        writer.Complete();
        DirectorySnapshot before = DirectorySnapshot.Capture(temp.Path);
        long frames = writer.SessionFrames;
        int chunks = writer.CompletedChunks.Count;

        writer.Dispose();

        DirectorySnapshot after = DirectorySnapshot.Capture(temp.Path);
        Assert.Equal(frames, writer.SessionFrames);
        Assert.Equal(chunks, writer.CompletedChunks.Count);
        Assert.True(after.Matches(before), $"disk changed after dispose:{Environment.NewLine}{after.Describe()}");
    }

    [Fact]
    public void CompleteIsIdempotent()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = WriteTwoSeconds(temp);

        writer.Complete();
        DirectorySnapshot before = DirectorySnapshot.Capture(temp.Path);
        int chunks = writer.CompletedChunks.Count;

        writer.Complete();
        writer.Complete();

        Assert.Equal(chunks, writer.CompletedChunks.Count);
        Assert.True(DirectorySnapshot.Capture(temp.Path).Matches(before));
    }

    [Fact]
    public void SealingAnEmptyWriterProducesNoFiles()
    {
        using TempDirectory temp = new();
        using PcmChunkWriter writer = new(temp.Path, SourceTrack.System, Mono48, 0, OneSecondChunks);

        // A track whose endpoint never started must not be padded into existence.
        writer.Complete();
        writer.AdvanceTo(600 * 10_000_000L, exact: true);
        writer.Complete();

        Assert.Empty(writer.CompletedChunks);
        Assert.Equal(0, writer.SessionFrames);
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path, "chunks")));
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path, "active")));
    }
}
