using System.Text.Json;
using EchoForge.Contracts.Playback;
using EchoForge.Infrastructure.Processing;

namespace EchoForge.Infrastructure.Playback;

/// <summary>Builds and caches the bounded waveform used before a transcript exists.</summary>
internal static class PlaybackEnergyBuilder
{
    private const int WavHeaderBytes = 44;
    private const int ReadFrames = 8192;

    public static string PathFor(string directory) => Path.Combine(directory, "playback.energy.json");

    public static PlaybackEnergyEnvelope? TryRead(string directory, PlaybackDerivativeRecord record)
    {
        string path = PathFor(directory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            PlaybackEnergyEnvelope? envelope = JsonSerializer.Deserialize<PlaybackEnergyEnvelope>(
                File.ReadAllBytes(path), PlaybackEnergyEnvelope.Json);
            return envelope is { } && envelope.Matches(record) ? envelope : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void TryWrite(string directory, PlaybackEnergyEnvelope envelope)
    {
        try
        {
            DerivativeBuilder.WriteAtomically(
                PathFor(directory),
                JsonSerializer.SerializeToUtf8Bytes(envelope, PlaybackEnergyEnvelope.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The aligned WAV remains fully usable. A missing waveform cache is rebuilt next time.
        }
    }

    public static async Task<PlaybackEnergyEnvelope> FromWavAsync(
        string audioPath,
        PlaybackDerivativeRecord record,
        CancellationToken cancellationToken)
    {
        PlaybackEnergyAccumulator energy = new(record.TotalFrames);
        byte[] bytes = new byte[ReadFrames * record.Channels * sizeof(short)];
        short[] samples = new short[ReadFrames * record.Channels];

        await using FileStream stream = new(
            audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        stream.Position = WavHeaderBytes;

        long cursor = 0;
        while (cursor < record.TotalFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frames = (int)Math.Min(ReadFrames, record.TotalFrames - cursor);
            int byteCount = frames * record.Channels * sizeof(short);
            await stream.ReadExactlyAsync(bytes.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
            Buffer.BlockCopy(bytes, 0, samples, 0, byteCount);
            energy.AddInterleaved(cursor, samples, frames, record.Channels);
            cursor += frames;
        }

        return energy.Build(record);
    }
}

/// <summary>Accumulates RMS energy into a fixed number of session-time buckets.</summary>
internal sealed class PlaybackEnergyAccumulator
{
    private readonly long _totalFrames;
    private readonly int _buckets;
    private readonly double[] _youSquares;
    private readonly double[] _remoteSquares;
    private readonly long[] _counts;

    public PlaybackEnergyAccumulator(long totalFrames, int buckets = PlaybackEnergyEnvelope.DefaultBuckets)
    {
        _totalFrames = Math.Max(0, totalFrames);
        _buckets = Math.Max(1, buckets);
        _youSquares = new double[_buckets];
        _remoteSquares = new double[_buckets];
        _counts = new long[_buckets];
    }

    public void AddSeparate(long startFrame, ReadOnlySpan<short> you, ReadOnlySpan<short> remote, int frames)
    {
        if (_totalFrames <= 0 || frames <= 0)
        {
            return;
        }

        int offset = 0;
        while (offset < frames)
        {
            (int bucket, int take) = Slice(startFrame, offset, frames);
            double youSum = 0;
            double remoteSum = 0;

            for (int i = 0; i < take; i++)
            {
                double y = you[offset + i];
                double r = remote[offset + i];
                youSum += y * y;
                remoteSum += r * r;
            }

            Commit(bucket, take, youSum, remoteSum);
            offset += take;
        }
    }

    public void AddInterleaved(long startFrame, short[] interleaved, int frames, int channels)
    {
        if (_totalFrames <= 0 || frames <= 0)
        {
            return;
        }

        int offset = 0;
        while (offset < frames)
        {
            (int bucket, int take) = Slice(startFrame, offset, frames);
            double youSum = 0;
            double remoteSum = 0;

            for (int i = 0; i < take; i++)
            {
                int sample = (offset + i) * channels;
                double y = interleaved[sample + PlaybackChannels.You];
                double r = interleaved[sample + PlaybackChannels.Remote];
                youSum += y * y;
                remoteSum += r * r;
            }

            Commit(bucket, take, youSum, remoteSum);
            offset += take;
        }
    }

    private (int Bucket, int Take) Slice(long startFrame, int offset, int frames)
    {
        long absolute = startFrame + offset;
        int bucket = (int)Math.Min(_buckets - 1, (absolute * _buckets) / _totalFrames);
        long bucketEnd = Math.Min(
            _totalFrames,
            (((long)bucket + 1) * _totalFrames + _buckets - 1) / _buckets);
        int take = (int)Math.Min(frames - offset, Math.Max(1, bucketEnd - absolute));
        return (bucket, take);
    }

    private void Commit(int bucket, int frames, double youSquares, double remoteSquares)
    {
        _youSquares[bucket] += youSquares;
        _remoteSquares[bucket] += remoteSquares;
        _counts[bucket] += frames;
    }

    public PlaybackEnergyEnvelope Build(PlaybackDerivativeRecord record) => new()
    {
        DerivativeSha256 = record.Sha256,
        ProcessingVersion = record.ProcessingVersion,
        Buckets = _buckets,
        You = Normalize(_youSquares, _counts),
        Remote = Normalize(_remoteSquares, _counts),
    };

    private static float[] Normalize(double[] squares, long[] counts)
    {
        double[] rms = new double[squares.Length];
        double maximum = 0;

        for (int i = 0; i < squares.Length; i++)
        {
            rms[i] = counts[i] <= 0 ? 0 : Math.Sqrt(squares[i] / counts[i]);
            maximum = Math.Max(maximum, rms[i]);
        }

        float[] result = new float[squares.Length];
        if (maximum <= 0)
        {
            return result;
        }

        for (int i = 0; i < rms.Length; i++)
        {
            result[i] = (float)Math.Clamp(rms[i] / maximum, 0, 1);
        }

        return result;
    }
}
