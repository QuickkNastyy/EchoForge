using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;

namespace EchoForge.Infrastructure.Recovery;

/// <summary>What recovery did to one session. Reported, never silently applied.</summary>
public sealed record RecoveryOutcome(
    string SessionId,
    SessionState State,
    int ChunksRecovered,
    int ChunksReconciled,
    int ChunksQuarantined,
    bool SnapshotRebuilt,
    bool JournalTruncated,
    IReadOnlyList<string> Notes)
{
    public bool ChangedAnything =>
        ChunksRecovered > 0 || ChunksReconciled > 0 || ChunksQuarantined > 0 || SnapshotRebuilt;
}

/// <summary>
/// Brings an interrupted session back to a known state on startup.
///
/// <para>
/// Completed chunks are never deleted or renumbered, the journal is the recovery authority, a
/// truncated final journal line is expected rather than fatal, an active chunk is repaired in
/// place or quarantined explicitly, and nothing is invented to fill a gap. Running recovery twice
/// on the same session does nothing the second time.
/// </para>
///
/// <para>
/// <b>Reconciliation.</b> A chunk becomes complete on disk before its journal line is written, so
/// a crash in that window leaves a valid WAV the journal knows nothing about. Recovery therefore
/// walks the chunk directories and adds a record for every finalized file the journal is missing,
/// using the <c>.meta.json</c> the writer emitted beside it.
/// </para>
/// </summary>
public sealed class SessionRecoveryService
{
    private readonly ISessionStore _store;
    private readonly IActiveChunkRepairer _repairer;
    private readonly TimeProvider _time;

    public SessionRecoveryService(ISessionStore store, IActiveChunkRepairer repairer, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(repairer);
        _store = store;
        _repairer = repairer;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<RecoveryOutcome> ScanAll()
    {
        List<RecoveryOutcome> outcomes = [];
        foreach (string sessionId in _store.EnumerateSessions())
        {
            RecoveryOutcome outcome = Recover(sessionId);
            if (outcome.ChangedAnything || outcome.State is SessionState.NeedsAttention)
            {
                outcomes.Add(outcome);
            }
        }

        return outcomes;
    }

    public RecoveryOutcome Recover(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        SessionPaths paths = _store.Resolve(sessionId);
        List<string> notes = [];

        JournalReadResult journal = _store.ReadJournal(sessionId);
        bool truncatedTail = journal.TruncatedFinalLine;

        if (truncatedTail)
        {
            notes.Add("final journal line was truncated and was discarded");
        }

        if (journal.SkippedLines > 0)
        {
            notes.Add($"{journal.SkippedLines} malformed journal line(s) skipped");
        }

        // A start that never opened its endpoints is not an interrupted recording.
        if (journal.Events.Any(e => e.Type == JournalEventTypes.SessionStartFailed))
        {
            SessionSnapshot failed = SessionSnapshotBuilder.FromJournal(sessionId, journal.Events, _time.GetUtcNow());
            bool wrote = ReplaceSnapshotIfDifferent(sessionId, failed with { State = SessionState.Failed });
            notes.Add("this session failed to start and contains no audio");
            return new RecoveryOutcome(sessionId, SessionState.Failed, 0, 0, 0, wrote, truncatedTail, notes);
        }

        (int recovered, int quarantined) = RecoverActiveChunks(sessionId, paths, notes);
        int reconciled = ReconcileFinalizedChunks(sessionId, paths, journal.Events, notes);

        // Re-read: recovery appends its own chunk events, and a snapshot built from the
        // pre-recovery view would omit them and never converge across repeated runs.
        journal = _store.ReadJournal(sessionId);
        SessionSnapshot rebuilt = SessionSnapshotBuilder.FromJournal(sessionId, journal.Events, _time.GetUtcNow());

        SessionState state = DetermineState(rebuilt, quarantined, journal, notes);
        rebuilt = rebuilt with { State = state };

        bool snapshotRebuilt = ReplaceSnapshotIfDifferent(sessionId, rebuilt);

        if (recovered > 0 || quarantined > 0 || reconciled > 0 || snapshotRebuilt)
        {
            _store.Append(sessionId, JournalEvent.Create(
                JournalEventTypes.SessionRecovered,
                _time.GetUtcNow(),
                ("chunks_recovered", Text(recovered)),
                ("chunks_reconciled", Text(reconciled)),
                ("chunks_quarantined", Text(quarantined)),
                ("state", state.ToString())));
        }

        return new RecoveryOutcome(
            sessionId, state, recovered, reconciled, quarantined, snapshotRebuilt, truncatedTail, notes);
    }

    private static SessionState DetermineState(
        SessionSnapshot snapshot, int quarantined, JournalReadResult journal, List<string> notes)
    {
        if (quarantined > 0)
        {
            return SessionState.NeedsAttention;
        }

        if (notes.Any(n => n.Contains("cannot be placed", StringComparison.Ordinal)))
        {
            return SessionState.NeedsAttention;
        }

        bool stopped = journal.Events.Any(e => e.Type == JournalEventTypes.SessionStopped);
        if (stopped)
        {
            return SessionState.Recorded;
        }

        return snapshot.HasAudio ? SessionState.Recorded : SessionState.NeedsAttention;
    }

    /// <summary>
    /// Writes the snapshot only when the full canonical state differs. Chunk count alone is not
    /// enough: indices, hashes, frames, epochs, start times, device metadata, formats, and the end
    /// timestamp all have to agree, or the on-disk snapshot is stale in a way that matters.
    /// </summary>
    private bool ReplaceSnapshotIfDifferent(string sessionId, SessionSnapshot rebuilt)
    {
        SessionSnapshot? existing = _store.ReadSnapshot(sessionId);
        if (existing is not null && CanonicalEquals(existing, rebuilt))
        {
            return false;
        }

        _store.WriteSnapshot(rebuilt);
        return true;
    }

    /// <summary>
    /// Compares everything the journal is authoritative for. <c>Title</c> is excluded: it is user
    /// metadata that the journal never carries.
    /// </summary>
    internal static bool CanonicalEquals(SessionSnapshot left, SessionSnapshot right)
    {
        if (left.SessionId != right.SessionId ||
            left.State != right.State ||
            left.StartedUtc != right.StartedUtc ||
            left.EndedUtc != right.EndedUtc ||
            left.Epochs.Count != right.Epochs.Count ||
            left.Tracks.Count != right.Tracks.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Epochs.Count; i++)
        {
            if (left.Epochs[i] != right.Epochs[i])
            {
                return false;
            }
        }

        for (int i = 0; i < left.Tracks.Count; i++)
        {
            SessionTrack a = left.Tracks[i];
            SessionTrack b = right.Tracks[i];

            if (a.Track != b.Track ||
                a.DeviceId != b.DeviceId ||
                a.DeviceName != b.DeviceName ||
                a.Format != b.Format ||
                a.Chunks.Count != b.Chunks.Count)
            {
                return false;
            }

            for (int c = 0; c < a.Chunks.Count; c++)
            {
                AudioChunkMetadata x = a.Chunks[c];
                AudioChunkMetadata y = b.Chunks[c];

                if (x.Index != y.Index ||
                    x.EpochIndex != y.EpochIndex ||
                    x.SampleFrames != y.SampleFrames ||
                    x.SampleRate != y.SampleRate ||
                    x.Channels != y.Channels ||
                    x.Sha256 != y.Sha256 ||
                    Math.Abs(x.StartSeconds - y.StartSeconds) > 0.0005)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Adds a journal record for every finalized WAV the journal does not already know about.
    /// This is what makes a crash between finalization and journal append survivable.
    /// </summary>
    private int ReconcileFinalizedChunks(
        string sessionId, SessionPaths paths, IReadOnlyList<JournalEvent> events, List<string> notes)
    {
        if (!Directory.Exists(paths.TracksRoot))
        {
            return 0;
        }

        HashSet<(string Track, int Index)> known = [];
        foreach (JournalEvent journalEvent in events.Where(e => e.Type == JournalEventTypes.ChunkCompleted))
        {
            string? track = journalEvent.Field("track");
            int? index = journalEvent.IntField("index");
            if (track is not null && index is not null)
            {
                known.Add((track.ToLowerInvariant(), index.Value));
            }
        }

        int reconciled = 0;

        foreach (string trackDirectory in Directory.GetDirectories(paths.TracksRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            string trackName = Path.GetFileName(trackDirectory);
            string chunks = Path.Combine(trackDirectory, "chunks");
            if (!Directory.Exists(chunks))
            {
                continue;
            }

            foreach (string wav in Directory.GetFiles(chunks, "*.wav").OrderBy(f => f, StringComparer.Ordinal))
            {
                if (!TryParseIndex(Path.GetFileNameWithoutExtension(wav), out int index))
                {
                    notes.Add($"{Path.GetFileName(wav)} has an unrecognised name and cannot be placed on the timeline");
                    continue;
                }

                if (known.Contains((trackName, index)))
                {
                    continue;
                }

                ChunkRecord? record = ReadChunkRecord(Path.Combine(chunks, $"{index:D6}.meta.json"));
                if (record is null)
                {
                    // The audio is kept, but without its record we cannot say which epoch it
                    // belongs to or where it starts. Say so rather than defaulting to zero.
                    ChunkValidation validation = _repairer.Validate(wav);
                    notes.Add(validation.IsValid
                        ? $"{trackName}/{index:D6}.wav has no metadata record and cannot be placed on the timeline; the audio is retained"
                        : $"{trackName}/{index:D6}.wav has no metadata record and did not validate: {validation.Problem}");
                    continue;
                }

                _store.Append(sessionId, JournalEvent.Create(
                    JournalEventTypes.ChunkCompleted, _time.GetUtcNow(),
                    ("track", record.Track),
                    ("index", Text(record.Index)),
                    ("epoch", Text(record.Epoch)),
                    ("frames", Text(record.Frames)),
                    ("start_seconds", record.StartSeconds.ToString("0.####", CultureInfo.InvariantCulture)),
                    ("sample_rate", Text(record.SampleRate)),
                    ("channels", Text(record.Channels)),
                    ("sha256", record.Sha256 ?? Sha256(wav)),
                    ("reconciled", "true")));

                notes.Add($"reconciled {trackName}/{index:D6}.wav from its metadata record");
                reconciled++;
            }
        }

        return reconciled;
    }

    private (int Recovered, int Quarantined) RecoverActiveChunks(
        string sessionId, SessionPaths paths, List<string> notes)
    {
        if (!Directory.Exists(paths.TracksRoot))
        {
            return (0, 0);
        }

        int recovered = 0;
        int quarantined = 0;

        foreach (string trackDirectory in Directory.GetDirectories(paths.TracksRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            string activeDirectory = Path.Combine(trackDirectory, "active");
            if (!Directory.Exists(activeDirectory))
            {
                continue;
            }

            foreach (string part in Directory.GetFiles(activeDirectory, "*.part.wav").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (TryRecoverOne(sessionId, trackDirectory, part, notes))
                {
                    recovered++;
                }
                else
                {
                    quarantined++;
                }
            }
        }

        return (recovered, quarantined);
    }

    private bool TryRecoverOne(string sessionId, string trackDirectory, string partPath, List<string> notes)
    {
        string name = Path.GetFileName(partPath);
        ChunkRecord? record = ReadChunkRecord(SidecarPath(partPath));

        if (record is null)
        {
            Quarantine(sessionId, partPath, "no metadata record; the format, epoch, and start time are unknown and must not be guessed", notes);
            return false;
        }

        if (!TryParseIndex(name.Split('.', 2)[0], out int index) || index != record.Index)
        {
            Quarantine(sessionId, partPath, $"file name does not match its record (index {record.Index})", notes);
            return false;
        }

        ChunkRepairOutcome outcome = _repairer.Repair(partPath, record.Format());
        if (!outcome.Repaired || outcome.FrameCount <= 0)
        {
            Quarantine(sessionId, partPath, outcome.Problem ?? "repair produced no frames", notes);
            return false;
        }

        string finalPath = Path.Combine(trackDirectory, "chunks", $"{index:D6}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        if (File.Exists(finalPath))
        {
            Quarantine(sessionId, partPath, $"chunk {index:D6} is already finalized", notes);
            return false;
        }

        File.Move(partPath, finalPath);
        string sha = Sha256(finalPath);

        // Promote the record alongside the audio so reconciliation sees a finalized chunk.
        WriteChunkRecord(
            Path.Combine(trackDirectory, "chunks", $"{index:D6}.meta.json"),
            record with { Frames = outcome.FrameCount, Sha256 = sha, Finalized = true });

        DeleteIfPresent(SidecarPath(partPath));

        notes.Add($"recovered {name} as {index:D6}.wav ({outcome.FrameCount} frames, {outcome.TrimmedBytes} bytes trimmed)");

        _store.Append(sessionId, JournalEvent.Create(
            JournalEventTypes.ChunkCompleted,
            _time.GetUtcNow(),
            ("track", record.Track),
            ("index", Text(index)),
            ("epoch", Text(record.Epoch)),
            ("frames", Text(outcome.FrameCount)),
            ("start_seconds", record.StartSeconds.ToString("0.####", CultureInfo.InvariantCulture)),
            ("sample_rate", Text(record.SampleRate)),
            ("channels", Text(record.Channels)),
            ("sha256", sha),
            ("recovered", "true"),
            ("trimmed_bytes", Text(outcome.TrimmedBytes))));

        return true;
    }

    /// <summary>
    /// Moves an unusable active chunk to quarantine <b>with its sidecar</b>, preserving both
    /// original names. The sidecar is diagnostic evidence about why recovery failed; deleting it
    /// would throw away the only clue.
    /// </summary>
    private void Quarantine(string sessionId, string partPath, string reason, List<string> notes)
    {
        SessionPaths paths = _store.Resolve(sessionId);
        Directory.CreateDirectory(paths.QuarantineRoot);

        string name = Path.GetFileName(partPath);
        string destination = UniquePath(Path.Combine(paths.QuarantineRoot, name));
        File.Move(partPath, destination);

        string sidecar = SidecarPath(partPath);
        if (File.Exists(sidecar))
        {
            File.Move(sidecar, UniquePath(Path.Combine(paths.QuarantineRoot, Path.GetFileName(sidecar))));
        }

        File.WriteAllText(destination + ".reason.txt", reason);
        notes.Add($"quarantined {name}: {reason}");

        _store.Append(sessionId, JournalEvent.Create(
            JournalEventTypes.RecoveryQuarantine,
            _time.GetUtcNow(),
            ("file", name),
            ("reason", reason)));
    }

    private static string UniquePath(string path)
    {
        string candidate = path;
        int suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = $"{path}.{suffix++}";
        }

        return candidate;
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string SidecarPath(string partPath) =>
        Path.ChangeExtension(partPath, null) + ".state.json";

    private static ChunkRecord? ReadChunkRecord(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            ChunkRecord? record = JsonSerializer.Deserialize<ChunkRecord>(stream, ChunkRecord.Json);
            return record is null || record.SampleRate <= 0 || record.Channels <= 0 ? null : record;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static void WriteChunkRecord(string path, ChunkRecord record)
    {
        string temporary = path + ".tmp";
        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record, ChunkRecord.Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Parses a six-digit chunk index. An unrecognised name must never silently become chunk 0,
    /// which would collide with real numbering and misplace the audio.
    /// </summary>
    private static bool TryParseIndex(string stem, out int index)
    {
        index = 0;
        return stem.Length > 0
            && stem.All(char.IsAsciiDigit)
            && int.TryParse(stem, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
            && index > 0;
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
