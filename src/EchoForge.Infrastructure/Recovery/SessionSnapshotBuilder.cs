using System.Globalization;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;

namespace EchoForge.Infrastructure.Recovery;

/// <summary>
/// Rebuilds a session snapshot from the journal.
///
/// <para>
/// The journal is the recovery authority; <c>session.json</c> is a convenience derived from it.
/// This is what lets a missing, truncated, or contradictory snapshot be replaced rather than
/// mourned.
/// </para>
/// </summary>
public static class SessionSnapshotBuilder
{
    public static SessionSnapshot FromJournal(
        string sessionId,
        IReadOnlyList<JournalEvent> events,
        DateTimeOffset fallbackCreatedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(events);

        DateTimeOffset created = fallbackCreatedUtc;
        DateTimeOffset? started = null;
        DateTimeOffset? ended = null;

        Dictionary<int, EpochBuilder> epochs = [];
        Dictionary<SourceTrack, TrackBuilder> tracks = [];

        foreach (JournalEvent journalEvent in events)
        {
            switch (journalEvent.Type)
            {
                case JournalEventTypes.SessionCreated:
                    created = journalEvent.TimestampUtc;
                    break;

                case JournalEventTypes.EpochStarted:
                {
                    int index = journalEvent.IntField("epoch") ?? 1;
                    epochs[index] = new EpochBuilder
                    {
                        Index = index,
                        StartedUtc = journalEvent.TimestampUtc,
                        StartQpc = journalEvent.LongField("start_qpc") ?? 0,
                    };

                    started ??= journalEvent.TimestampUtc;
                    break;
                }

                case JournalEventTypes.EpochEnded:
                {
                    int index = journalEvent.IntField("epoch") ?? 1;
                    if (epochs.TryGetValue(index, out EpochBuilder? epoch))
                    {
                        epoch.EndedUtc = journalEvent.TimestampUtc;
                        epoch.EndQpc = journalEvent.LongField("end_qpc");
                        epoch.EndReason = Enum.TryParse(journalEvent.Field("reason"), out EpochEndReason reason)
                            ? reason
                            : EpochEndReason.Stopped;
                    }

                    break;
                }

                case JournalEventTypes.TrackOpened:
                {
                    if (!TryParseTrack(journalEvent.Field("track"), out SourceTrack track))
                    {
                        break;
                    }

                    tracks[track] = new TrackBuilder
                    {
                        Track = track,
                        DeviceId = journalEvent.Field("device_id") ?? string.Empty,
                        DeviceName = journalEvent.Field("device_name") ?? string.Empty,
                        Format = new CaptureFormat(
                            journalEvent.IntField("sample_rate") ?? 48_000,
                            journalEvent.IntField("channels") ?? 2,
                            16),
                    };

                    break;
                }

                case JournalEventTypes.ChunkCompleted:
                {
                    if (!TryParseTrack(journalEvent.Field("track"), out SourceTrack track))
                    {
                        break;
                    }

                    if (!tracks.TryGetValue(track, out TrackBuilder? builder))
                    {
                        // A chunk for a track whose open event was lost. Keep the audio.
                        builder = new TrackBuilder
                        {
                            Track = track,
                            DeviceId = string.Empty,
                            DeviceName = string.Empty,
                            Format = new CaptureFormat(
                                journalEvent.IntField("sample_rate") ?? 48_000,
                                journalEvent.IntField("channels") ?? 2,
                                16),
                        };

                        tracks[track] = builder;
                    }

                    int index = journalEvent.IntField("index") ?? 0;
                    if (builder.Chunks.Any(c => c.Index == index))
                    {
                        // Recovery may re-journal a chunk it promoted. Never double count.
                        break;
                    }

                    long frames = journalEvent.LongField("frames") ?? 0;
                    double start = ParseDouble(journalEvent.Field("start_seconds"));

                    builder.Chunks.Add(new AudioChunkMetadata(
                        index,
                        $"tracks/{track.ToString().ToLowerInvariant()}/chunks/{index:D6}.wav",
                        track,
                        start,
                        start + (builder.Format.SampleRate == 0 ? 0 : (double)frames / builder.Format.SampleRate),
                        builder.Format.SampleRate,
                        builder.Format.Channels,
                        frames,
                        journalEvent.Field("sha256") ?? string.Empty,
                        [],
                        journalEvent.IntField("epoch") ?? 1));

                    break;
                }

                case JournalEventTypes.SessionStopped:
                    ended = journalEvent.TimestampUtc;
                    break;

                default:
                    break;
            }
        }

        List<SessionEpoch> epochList = [.. epochs.Values
            .OrderBy(e => e.Index)
            .Select(e => new SessionEpoch(e.Index, e.StartedUtc, e.EndedUtc, e.StartQpc, e.EndQpc, e.EndReason))];

        List<SessionTrack> trackList = [.. tracks.Values
            .OrderBy(t => t.Track)
            .Select(t => new SessionTrack(
                t.Track,
                t.DeviceId,
                t.DeviceName,
                t.Format,
                [.. t.Chunks.OrderBy(c => c.Index)]))];

        return new SessionSnapshot(
            sessionId,
            SessionState.Recovering,
            created,
            started,
            ended,
            epochList,
            trackList);
    }

    private static bool TryParseTrack(string? value, out SourceTrack track) =>
        Enum.TryParse(value, ignoreCase: true, out track);

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;

    private sealed class EpochBuilder
    {
        public int Index { get; init; }

        public DateTimeOffset StartedUtc { get; init; }

        public DateTimeOffset? EndedUtc { get; set; }

        public long StartQpc { get; init; }

        public long? EndQpc { get; set; }

        public EpochEndReason EndReason { get; set; } = EpochEndReason.Running;
    }

    private sealed class TrackBuilder
    {
        public SourceTrack Track { get; init; }

        public required string DeviceId { get; init; }

        public required string DeviceName { get; init; }

        public required CaptureFormat Format { get; init; }

        public List<AudioChunkMetadata> Chunks { get; } = [];
    }
}
