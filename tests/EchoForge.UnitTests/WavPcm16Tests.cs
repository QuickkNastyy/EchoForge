using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;

namespace EchoForge.UnitTests;

public sealed class WavPcm16Tests
{
    private static readonly CaptureFormat Mono48 = new(48_000, 1, 16);
    private static readonly CaptureFormat Stereo48 = new(48_000, 2, 16);

    [Fact]
    public void WrittenFileValidatesAndReportsItsFrameCount()
    {
        using TempDirectory temp = new();
        string path = temp.Combine("chunk.wav");

        using (WavPcm16Writer writer = new(path, Stereo48))
        {
            writer.WriteFrames(new byte[4 * 1000], 1000);
            writer.Close();
        }

        WavValidation validation = WavPcm16Reader.Validate(path);

        Assert.True(validation.IsValid, validation.Problem);
        Assert.Equal(1000, validation.FrameCount);
        Assert.Equal(48_000, validation.Format!.SampleRate);
        Assert.Equal(2, validation.Format.Channels);
    }

    [Fact]
    public void SilenceCountsTowardFramesAndIsWrittenAsZeroes()
    {
        using TempDirectory temp = new();
        string path = temp.Combine("silence.wav");

        using (WavPcm16Writer writer = new(path, Mono48))
        {
            writer.WriteSilence(4_800);
            writer.Close();
        }

        WavValidation validation = WavPcm16Reader.Validate(path);
        Assert.True(validation.IsValid, validation.Problem);
        Assert.Equal(4_800, validation.FrameCount);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.All(bytes[WavPcm16Writer.HeaderBytes..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void UnpatchedHeaderFailsValidation()
    {
        using TempDirectory temp = new();
        string path = temp.Combine("killed.part.wav");

        // Simulates a process kill: data was flushed but Close never patched the header.
        using (FileStream stream = new(path, FileMode.Create))
        {
            WavPcm16Writer.WriteHeader(stream, Mono48, dataBytes: 0);
            stream.Write(new byte[2 * 500]);
        }

        WavValidation validation = WavPcm16Reader.Validate(path);
        Assert.False(validation.IsValid);
        Assert.Contains("data chunk declares", validation.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void RepairPatchesHeaderAndKeepsEveryDurableFrame()
    {
        using TempDirectory temp = new();
        string path = temp.Combine("killed.part.wav");

        using (FileStream stream = new(path, FileMode.Create))
        {
            WavPcm16Writer.WriteHeader(stream, Mono48, dataBytes: 0);
            stream.Write(new byte[2 * 500]);
        }

        WavRepairResult repair = WavPcm16Reader.Repair(path, Mono48);

        Assert.True(repair.Repaired, repair.Problem);
        Assert.Equal(500, repair.FrameCount);
        Assert.Equal(0, repair.TrimmedBytes);
        Assert.True(WavPcm16Reader.Validate(path).IsValid);
    }

    [Fact]
    public void RepairTrimsAnIncompleteTrailingFrame()
    {
        using TempDirectory temp = new();
        string path = temp.Combine("torn.part.wav");

        using (FileStream stream = new(path, FileMode.Create))
        {
            WavPcm16Writer.WriteHeader(stream, Stereo48, dataBytes: 0);
            // 10 whole frames plus 3 stray bytes, as if the process died mid-write.
            stream.Write(new byte[(4 * 10) + 3]);
        }

        WavRepairResult repair = WavPcm16Reader.Repair(path, Stereo48);

        Assert.True(repair.Repaired, repair.Problem);
        Assert.Equal(10, repair.FrameCount);
        Assert.Equal(3, repair.TrimmedBytes);

        WavValidation validation = WavPcm16Reader.Validate(path);
        Assert.True(validation.IsValid, validation.Problem);
        Assert.Equal(10, validation.FrameCount);
    }

    [Fact]
    public void RepairRejectsAFileTooShortToHoldAHeader()
    {
        using TempDirectory temp = new();
        string path = temp.Combine("stub.part.wav");
        File.WriteAllBytes(path, new byte[10]);

        WavRepairResult repair = WavPcm16Reader.Repair(path, Mono48);

        Assert.False(repair.Repaired);
        Assert.NotNull(repair.Problem);
    }
}
