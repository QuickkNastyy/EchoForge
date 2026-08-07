using System.Security.Cryptography;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Sessions;

namespace EchoForge.UnitTests;

/// <summary>
/// One source chunk to write.
///
/// <para>
/// <see cref="Level"/> is a constant sample value rather than a tone, and that is what makes the
/// alignment tests readable: a band-limited resampler passes a constant through unchanged, so
/// "session second 71 holds level 3000" is a direct statement about which chunk the audio at that
/// moment came from. A gap is zero, and nothing else is.
/// </para>
/// </summary>
public sealed record PlaybackChunkSpec(
    SourceTrack Track,
    int Epoch,
    double Seconds,
    short Level,
    int SampleRate = 48000,
    int Channels = 1,
    /// <summary>Extra silence before this chunk, inside its epoch. Makes a mid-epoch gap.</summary>
    double GapBefore = 0);

/// <summary>
/// Builds real two-track sessions on disk for the playback tests.
///
/// <para>
/// Real files on purpose: the claim being tested is that the aligned derivative can be rebuilt
/// from immutable chunks and that its frames mean exact session moments. A fixture that handed the
/// builder samples in memory would test the arithmetic and skip everything the arithmetic is for.
/// </para>
/// </summary>
public sealed class PlaybackFixture : IDisposable
{
    /// <summary>Seconds between the end of one epoch's audio and the start of the next.</summary>
    public const double EpochGapSeconds = 5;

    private readonly TempDirectory _temp = new();

    public PlaybackFixture(string sessionId = "01JPLAYBACK")
    {
        SessionId = sessionId;
        Sessions = new FileSessionStore(_temp.Path);
        Sessions.Create(sessionId);
    }

    public string SessionId { get; }

    public FileSessionStore Sessions { get; }

    public string Root => _temp.Path;

    public SessionPaths Paths => Sessions.Resolve(SessionId);

    public static readonly DateTimeOffset Origin = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Writes the chunks, the snapshot, and returns the request playback is built from.</summary>
    public TranscriptionRequest Build(params PlaybackChunkSpec[] specs)
    {
        ArgumentNullException.ThrowIfNull(specs);

        SessionPaths paths = Paths;
        Dictionary<SourceTrack, List<AudioChunkMetadata>> byTrack = [];
        Dictionary<(SourceTrack, int), double> cursor = [];
        Dictionary<int, double> epochLength = [];
        Dictionary<SourceTrack, CaptureFormat> formats = [];
        int index = 0;

        foreach (PlaybackChunkSpec spec in specs)
        {
            index++;
            string folder = spec.Track.ToString().ToLowerInvariant();
            string relative = $"tracks/{folder}/chunks/{index:D6}.wav";
            string path = Path.Combine(paths.Root, relative.Replace('/', Path.DirectorySeparatorChar));

            long frames = WriteWav(path, spec);

            double start = cursor.GetValueOrDefault((spec.Track, spec.Epoch)) + spec.GapBefore;
            double length = (double)frames / spec.SampleRate;

            using (FileStream stream = File.OpenRead(path))
            {
                string digest = Convert.ToHexStringLower(SHA256.HashData(stream));

                if (!byTrack.TryGetValue(spec.Track, out List<AudioChunkMetadata>? list))
                {
                    list = [];
                    byTrack[spec.Track] = list;
                    formats[spec.Track] = new CaptureFormat(spec.SampleRate, spec.Channels, 16);
                }

                list.Add(new AudioChunkMetadata(
                    index, relative, spec.Track, start, start + length,
                    spec.SampleRate, spec.Channels, frames, digest, [], spec.Epoch));
            }

            cursor[(spec.Track, spec.Epoch)] = start + length;
            epochLength[spec.Epoch] = Math.Max(epochLength.GetValueOrDefault(spec.Epoch), start + length);
        }

        List<SessionEpoch> epochs = [];
        double wall = 0;
        foreach (int epoch in epochLength.Keys.Order())
        {
            epochs.Add(new SessionEpoch(
                epoch,
                Origin.AddSeconds(wall),
                Origin.AddSeconds(wall + epochLength[epoch]),
                0,
                1,
                EpochEndReason.Paused));

            wall += epochLength[epoch] + EpochGapSeconds;
        }

        SessionSnapshot snapshot = new(
            SessionId,
            SessionState.Recorded,
            Origin,
            Origin,
            Origin.AddSeconds(wall),
            epochs,
            [
                .. byTrack.Keys.Order().Select(track => new SessionTrack(
                    track,
                    track.ToString().ToLowerInvariant(),
                    track.ToString(),
                    formats[track],
                    byTrack[track]))
            ]);

        Sessions.WriteSnapshot(snapshot);

        RequestBuildResult built = TranscriptionRequestBuilder.Build(
            snapshot,
            paths.Root,
            Path.Combine(paths.Root, "out.json"),
            1,
            Origin,
            new RequestOptions { Backend = "playback" });

        Assert.True(built.Succeeded, built.Failure?.Detail);
        return built.Request!;
    }

    /// <summary>Session-relative second an epoch's audio begins at, given the fixture's gaps.</summary>
    public static double EpochStart(TranscriptionRequest request, int epoch) =>
        request.Epochs.First(e => e.Index == epoch).StartSeconds;

    /// <summary>The digest of every source chunk, so a test can prove nothing rewrote one.</summary>
    public IReadOnlyDictionary<string, string> SourceDigests()
    {
        Dictionary<string, string> digests = new(StringComparer.Ordinal);
        string root = Path.Combine(Paths.Root, "tracks");

        if (!Directory.Exists(root))
        {
            return digests;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*.wav", SearchOption.AllDirectories).Order())
        {
            using FileStream stream = File.OpenRead(file);
            digests[Path.GetRelativePath(Paths.Root, file).Replace('\\', '/')] =
                Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        return digests;
    }

    private static long WriteWav(string path, PlaybackChunkSpec spec)
    {
        long frames = (long)Math.Round(spec.Seconds * spec.SampleRate);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] data = new byte[frames * spec.Channels * 2];
        for (long frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < spec.Channels; channel++)
            {
                BitConverter.TryWriteBytes(
                    data.AsSpan((int)(((frame * spec.Channels) + channel) * 2), 2), spec.Level);
            }
        }

        byte[] header = new byte[44];
        long dataBytes = data.Length;
        "RIFF"u8.CopyTo(header.AsSpan(0));
        BitConverter.TryWriteBytes(header.AsSpan(4), (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BitConverter.TryWriteBytes(header.AsSpan(16), 16u);
        BitConverter.TryWriteBytes(header.AsSpan(20), (ushort)1);
        BitConverter.TryWriteBytes(header.AsSpan(22), (ushort)spec.Channels);
        BitConverter.TryWriteBytes(header.AsSpan(24), (uint)spec.SampleRate);
        BitConverter.TryWriteBytes(header.AsSpan(28), (uint)(spec.SampleRate * spec.Channels * 2));
        BitConverter.TryWriteBytes(header.AsSpan(32), (ushort)(spec.Channels * 2));
        BitConverter.TryWriteBytes(header.AsSpan(34), (ushort)16);
        "data"u8.CopyTo(header.AsSpan(36));
        BitConverter.TryWriteBytes(header.AsSpan(40), (uint)dataBytes);

        using FileStream stream = File.Create(path);
        stream.Write(header);
        stream.Write(data);
        return frames;
    }

    public void Dispose() => _temp.Dispose();
}
