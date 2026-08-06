using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Transcripts;
using EchoForge.Contracts.Workers;

namespace EchoForge.Core.Transcripts;

/// <summary>Why a session cannot be turned into a transcription request.</summary>
public sealed record RequestBuildFailure(string Code, string Detail);

/// <summary>The result of trying to describe a session to a worker.</summary>
public sealed record RequestBuildResult(TranscriptionRequest? Request, RequestBuildFailure? Failure)
{
    public bool Succeeded => Request is not null;
}

/// <summary>
/// Turns a recorded session into the request a worker can act on.
///
/// <para>
/// The interesting work here is time. Chunk offsets in the session snapshot are <b>relative to
/// their epoch</b>, because that is what the recorder can know without holding a session-wide
/// frame counter across a pause. The transcript, by contrast, lives on a single session timeline.
/// This is the one place that conversion happens, so no downstream component has to guess which
/// origin a number belongs to.
/// </para>
///
/// <para>
/// Epoch placement uses wall-clock deltas between epoch starts, but is then forced to be
/// monotonic: an epoch never begins before the previous one's audio ends. A clock that jumped
/// backwards across a suspend/resume would otherwise produce overlapping epochs, and every
/// downstream bound check would be meaningless.
/// </para>
///
/// <para>
/// Epoch length is taken from the audio, not from the wall clock. A wall-clock epoch can be
/// longer than the audio it produced — a device died halfway through — and a transcript must not
/// be allowed to place a segment in time where no audio exists.
/// </para>
/// </summary>
public static class TranscriptionRequestBuilder
{
    /// <summary>Only these tracks exist, and each may appear once.</summary>
    private static readonly SourceTrack[] TrackOrder = [SourceTrack.Microphone, SourceTrack.System];

    public static RequestBuildResult Build(
        SessionSnapshot snapshot,
        string sessionRoot,
        string outputPath,
        int transcriptRevision,
        DateTimeOffset createdAtUtc,
        RequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (transcriptRevision < 1)
        {
            return Fail(WorkerErrorCodes.InvalidRequest, "transcript revision must be at least 1");
        }

        if (!snapshot.HasAudio)
        {
            return Fail(WorkerErrorCodes.InputMissing, "the session has no finalized audio chunks");
        }

        List<SessionEpoch> epochs = [.. snapshot.Epochs.OrderBy(e => e.Index)];
        if (epochs.Count == 0)
        {
            return Fail(WorkerErrorCodes.InvalidRequest, "the session records no capture epochs");
        }

        // Every chunk must belong to an epoch the session admits to. A chunk pointing at an
        // epoch that is not in the snapshot has no origin, so it has no place on the timeline.
        HashSet<int> knownEpochs = [.. epochs.Select(e => e.Index)];
        foreach (SessionTrack track in snapshot.Tracks)
        {
            foreach (AudioChunkMetadata chunk in track.Chunks)
            {
                if (!knownEpochs.Contains(chunk.EpochIndex))
                {
                    return Fail(
                        WorkerErrorCodes.InvalidRequest,
                        FormattableString.Invariant(
                            $"chunk {chunk.Index} on {track.Track} names epoch {chunk.EpochIndex}, which the session does not record"));
                }

                if (chunk.SampleRate <= 0 || chunk.Channels <= 0)
                {
                    return Fail(
                        WorkerErrorCodes.InputInvalid,
                        FormattableString.Invariant($"chunk {chunk.Index} on {track.Track} has an unusable format"));
                }
            }
        }

        Dictionary<int, double> epochStart = [];
        Dictionary<int, double> epochEnd = [];
        DateTimeOffset origin = epochs[0].StartedUtc;
        double cursor = 0;

        foreach (SessionEpoch epoch in epochs)
        {
            double wallClockStart = (epoch.StartedUtc - origin).TotalSeconds;
            double start = Math.Max(cursor, Math.Max(0, wallClockStart));

            double contentEnd = 0;
            foreach (SessionTrack track in snapshot.Tracks)
            {
                foreach (AudioChunkMetadata chunk in track.Chunks.Where(c => c.EpochIndex == epoch.Index))
                {
                    contentEnd = Math.Max(contentEnd, ChunkEnd(chunk));
                }
            }

            epochStart[epoch.Index] = start;
            epochEnd[epoch.Index] = start + contentEnd;
            cursor = start + contentEnd;
        }

        List<RequestEpoch> requestEpochs =
        [
            .. epochs.Select(e => new RequestEpoch(e.Index, epochStart[e.Index], epochEnd[e.Index]))
        ];

        List<RequestTrack> tracks = [];
        foreach (SourceTrack track in TrackOrder)
        {
            SessionTrack? source = snapshot.Tracks.FirstOrDefault(t => t.Track == track);
            if (source is null)
            {
                continue;
            }

            List<RequestChunk> chunks =
            [
                .. source.Chunks
                    .OrderBy(c => c.EpochIndex)
                    .ThenBy(c => c.Index)
                    .Select(c => new RequestChunk
                    {
                        Index = c.Index,
                        Epoch = c.EpochIndex,
                        RelativePath = Normalize(c.RelativePath),
                        StartSeconds = epochStart[c.EpochIndex] + c.StartSeconds,
                        EndSeconds = epochStart[c.EpochIndex] + ChunkEnd(c),
                        SampleRate = c.SampleRate,
                        Channels = c.Channels,
                        Frames = c.SampleFrames,
                        Sha256 = string.IsNullOrWhiteSpace(c.Sha256) ? null : c.Sha256,
                    })
            ];

            tracks.Add(new RequestTrack
            {
                SourceTrack = TrackName(track),
                Chunks = chunks,
            });
        }

        double duration = requestEpochs.Count == 0 ? 0 : requestEpochs[^1].EndSeconds;

        return new RequestBuildResult(
            new TranscriptionRequest
            {
                SessionId = snapshot.SessionId,
                TranscriptRevision = transcriptRevision,
                CreatedAtUtc = createdAtUtc,
                SessionRoot = sessionRoot,
                OutputPath = outputPath,
                DurationSeconds = duration,
                Epochs = requestEpochs,
                Tracks = tracks,
                Options = options,
            },
            null);
    }

    /// <summary>
    /// The identity of the audio a transcript was produced from.
    ///
    /// <para>
    /// The worker computes the same digest from the same request, byte for byte, which is what
    /// lets the host recognise its own transcript later and lets a test prove the two
    /// implementations agree. The canonical form is one line per chunk, in request order:
    /// <c>track|epoch|index|relative_path|frames|sha256</c>, UTF-8, newline-terminated.
    /// </para>
    /// </summary>
    public static string SourceManifestSha256(TranscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        StringBuilder builder = new();
        foreach (RequestTrack track in request.Tracks)
        {
            foreach (RequestChunk chunk in track.Chunks)
            {
                builder.Append(CultureInfo.InvariantCulture, $"{track.SourceTrack}|{chunk.Epoch}|{chunk.Index}|");
                builder.Append(CultureInfo.InvariantCulture, $"{chunk.RelativePath}|{chunk.Frames}|{chunk.Sha256 ?? string.Empty}\n");
            }
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>The wire name of a track. One mapping, used everywhere.</summary>
    public static string TrackName(SourceTrack track) => track switch
    {
        SourceTrack.Microphone => TranscriptSpeakers.MicrophoneTrack,
        SourceTrack.System => TranscriptSpeakers.SystemTrack,
        _ => throw new ArgumentOutOfRangeException(nameof(track), track, "unknown source track"),
    };

    /// <summary>
    /// Chunk end derived from the audio rather than read from the recorded end time. The frame
    /// count is what was actually written; a recorded end time is a description of it.
    /// </summary>
    private static double ChunkEnd(AudioChunkMetadata chunk) =>
        chunk.StartSeconds + ((double)chunk.SampleFrames / chunk.SampleRate);

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');

    private static RequestBuildResult Fail(string code, string detail) =>
        new(null, new RequestBuildFailure(code, detail));
}
