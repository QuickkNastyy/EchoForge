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
///
/// <para>
/// A track is opened once per epoch, so a paused-and-resumed session carries several
/// <c>track_opened</c> events for the same track. Reconstruction therefore <b>accumulates</b>:
/// a later open updates device metadata and records that epoch's format, and never discards
/// chunks already rebuilt. Each chunk keeps the format it was recorded with rather than
/// inheriting whatever the last epoch happened to negotiate.
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
        bool startFailed = false;

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

                    CaptureFormat format = new(
                        journalEvent.IntField("sample_rate") ?? 48_000,
                        journalEvent.IntField("channels") ?? 2,
                        16);

                    // Accumulate. Re-opening a track in a later epoch must never erase history.
                    TrackBuilder builder = Ensure(tracks, track, format);
                    builder.DeviceId = journalEvent.Field("device_id") ?? builder.DeviceId;
                    builder.DeviceName = journalEvent.Field("device_name") ?? builder.DeviceName;
                    builder.FormatByEpoch[journalEvent.IntField("epoch") ?? 1] = format;
                    builder.LatestFormat = format;
                    break;
                }

                case JournalEventTypes.ChunkCompleted:
                {
                    if (!TryParseTrack(journalEvent.Field("track"), out SourceTrack track))
                    {
                        break;
                    }

                    int epoch = journalEvent.IntField("epoch") ?? 1;
                    TrackBuilder builder = Ensure(tracks, track, null);

                    // The chunk's own recorded format wins, then that epoch's, then the track's.
                    CaptureFormat format = new(
                        journalEvent.IntField("sample_rate")
                            ?? (builder.FormatByEpoch.TryGetValue(epoch, out CaptureFormat? byEpoch) ? byEpoch.SampleRate : builder.LatestFormat.SampleRate),
                        journalEvent.IntField("channels")
                            ?? (builder.FormatByEpoch.TryGetValue(epoch, out CaptureFormat? byEpoch2) ? byEpoch2.Channels : builder.LatestFormat.Channels),
                        16);

                    int index = journalEvent.IntField("index") ?? 0;
                    long frames = journalEvent.LongField("frames") ?? 0;
                    double start = ParseDouble(journalEvent.Field("start_seconds"));
                    string sha = journalEvent.Field("sha256") ?? string.Empty;

                    AudioChunkMetadata chunk = new(
                        index,
                        $"tracks/{track.ToString().ToLowerInvariant()}/chunks/{index:D6}.wav",
                        track,
                        start,
                        start + (format.SampleRate == 0 ? 0 : (double)frames / format.SampleRate),
                        format.SampleRate,
                        format.Channels,
                        frames,
                        sha,
                        [],
                        epoch);

                    // A chunk may be journalled twice — once by the writer, once by recovery
                    // promoting a repaired part. Keep the richer record, never two.
                    if (builder.Chunks.TryGetValue(index, out AudioChunkMetadata? existing))
                    {
                        if (existing.SampleFrames == 0 && frames > 0)
                        {
                            builder.Chunks[index] = chunk;
                        }
                    }
                    else
                    {
                        builder.Chunks[index] = chunk;
                    }

                    break;
                }

                case JournalEventTypes.SessionStopped:
                    ended = journalEvent.TimestampUtc;
                    break;

                case JournalEventTypes.SessionStartFailed:
                    startFailed = true;
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
                t.LatestFormat,
                [.. t.Chunks.Values.OrderBy(c => c.Index)]))];

        return new SessionSnapshot(
            sessionId,
            startFailed ? SessionState.Failed : SessionState.Recovering,
            created,
            started,
            ended,
            epochList,
            trackList);
    }

    private static TrackBuilder Ensure(
        Dictionary<SourceTrack, TrackBuilder> tracks,
        SourceTrack track,
        CaptureFormat? format)
    {
        if (tracks.TryGetValue(track, out TrackBuilder? existing))
        {
            return existing;
        }

        TrackBuilder builder = new()
        {
            Track = track,
            DeviceId = string.Empty,
            DeviceName = string.Empty,
            LatestFormat = format ?? new CaptureFormat(48_000, 2, 16),
        };

        tracks[track] = builder;
        return builder;
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

        public required string DeviceId { get; set; }

        public required string DeviceName { get; set; }

        public required CaptureFormat LatestFormat { get; set; }

        /// <summary>Format negotiated for each epoch, so a change between epochs stays visible.</summary>
        public Dictionary<int, CaptureFormat> FormatByEpoch { get; } = [];

        /// <summary>Keyed by chunk index so a re-journalled chunk cannot be counted twice.</summary>
        public Dictionary<int, AudioChunkMetadata> Chunks { get; } = [];
    }
}
