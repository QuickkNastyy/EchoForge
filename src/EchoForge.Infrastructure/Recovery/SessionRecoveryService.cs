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
    int ChunksQuarantined,
    bool SnapshotRebuilt,
    bool JournalTruncated,
    IReadOnlyList<string> Notes)
{
    public bool ChangedAnything => ChunksRecovered > 0 || ChunksQuarantined > 0 || SnapshotRebuilt;
}

/// <summary>
/// Brings an interrupted session back to a known state on startup.
///
/// <para>
/// The rules are the plan's: completed chunks are never deleted or moved, the journal is the
/// recovery authority, a truncated final journal line is expected rather than fatal, an active
/// chunk is repaired in place or quarantined explicitly, and nothing is invented to fill a gap.
/// Running recovery twice on the same session does nothing the second time.
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

    /// <summary>Recovers every session that needs it. Sessions already settled are left alone.</summary>
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

        // Active chunks first, so a repaired chunk is part of the rebuilt snapshot.
        (int recovered, int quarantined) = RecoverActiveChunks(sessionId, paths, notes);

        // Re-read: recovery appends its own chunk_completed events, and a snapshot built from
        // the pre-recovery view would omit them and never converge across repeated runs.
        journal = _store.ReadJournal(sessionId);

        SessionSnapshot rebuilt = SessionSnapshotBuilder.FromJournal(sessionId, journal.Events, _time.GetUtcNow());
        SessionSnapshot? existing = _store.ReadSnapshot(sessionId);
        bool snapshotRebuilt = false;

        SessionState state = DetermineState(rebuilt, quarantined, journal);
        rebuilt = rebuilt with { State = state };

        if (existing is null)
        {
            notes.Add("session.json was missing or unreadable and was rebuilt from the journal");
            _store.WriteSnapshot(rebuilt);
            snapshotRebuilt = true;
        }
        else if (existing.State != state || existing.Tracks.Sum(t => t.Chunks.Count) != rebuilt.Tracks.Sum(t => t.Chunks.Count))
        {
            _store.WriteSnapshot(rebuilt);
            snapshotRebuilt = true;
        }

        if (recovered > 0 || quarantined > 0 || snapshotRebuilt)
        {
            _store.Append(sessionId, JournalEvent.Create(
                JournalEventTypes.SessionRecovered,
                _time.GetUtcNow(),
                ("chunks_recovered", recovered.ToString(CultureInfo.InvariantCulture)),
                ("chunks_quarantined", quarantined.ToString(CultureInfo.InvariantCulture)),
                ("state", state.ToString())));
        }

        return new RecoveryOutcome(sessionId, state, recovered, quarantined, snapshotRebuilt, truncatedTail, notes);
    }

    private static SessionState DetermineState(SessionSnapshot snapshot, int quarantined, JournalReadResult journal)
    {
        if (quarantined > 0)
        {
            return SessionState.NeedsAttention;
        }

        bool stopped = journal.Events.Any(e => e.Type == JournalEventTypes.SessionStopped);
        if (stopped)
        {
            return SessionState.Recorded;
        }

        // The session never recorded a stop: it was interrupted. Audio that survived is still
        // usable, so it is Recorded rather than Failed, unless nothing was captured at all.
        return snapshot.HasAudio ? SessionState.Recorded : SessionState.NeedsAttention;
    }

    /// <summary>
    /// Repairs or quarantines every abandoned active chunk. Idempotent: once an active directory
    /// has been drained there is nothing left to find on a second run.
    /// </summary>
    private (int Recovered, int Quarantined) RecoverActiveChunks(string sessionId, SessionPaths paths, List<string> notes)
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
        CaptureFormat? format = ReadSidecarFormat(partPath);

        if (format is null)
        {
            Quarantine(sessionId, partPath, "no sidecar; the recorded format is unknown and must not be guessed", notes);
            return false;
        }

        ChunkRepairOutcome outcome = _repairer.Repair(partPath, format);
        if (!outcome.Repaired || outcome.FrameCount <= 0)
        {
            Quarantine(sessionId, partPath, outcome.Problem ?? "repair produced no frames", notes);
            return false;
        }

        // Promote the repaired part to a finalized chunk so its audio is usable.
        int index = ParseIndex(name);
        string finalPath = Path.Combine(trackDirectory, "chunks", $"{index:D6}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        if (File.Exists(finalPath))
        {
            // A finalized chunk with this index already exists; never overwrite it.
            Quarantine(sessionId, partPath, $"chunk {index:D6} is already finalized", notes);
            return false;
        }

        File.Move(partPath, finalPath);
        DeleteSidecar(partPath);

        notes.Add($"recovered {name} as {index:D6}.wav ({outcome.FrameCount} frames, {outcome.TrimmedBytes} bytes trimmed)");

        _store.Append(sessionId, JournalEvent.Create(
            JournalEventTypes.ChunkCompleted,
            _time.GetUtcNow(),
            ("track", Path.GetFileName(trackDirectory)),
            ("index", index.ToString(CultureInfo.InvariantCulture)),
            ("frames", outcome.FrameCount.ToString(CultureInfo.InvariantCulture)),
            ("sha256", Sha256(finalPath)),
            ("recovered", "true"),
            ("trimmed_bytes", outcome.TrimmedBytes.ToString(CultureInfo.InvariantCulture))));

        return true;
    }

    private void Quarantine(string sessionId, string partPath, string reason, List<string> notes)
    {
        SessionPaths paths = _store.Resolve(sessionId);
        Directory.CreateDirectory(paths.QuarantineRoot);

        string name = Path.GetFileName(partPath);
        string destination = Path.Combine(paths.QuarantineRoot, name);
        int suffix = 1;
        while (File.Exists(destination))
        {
            destination = Path.Combine(paths.QuarantineRoot, $"{name}.{suffix++}");
        }

        File.Move(partPath, destination);
        DeleteSidecar(partPath);

        notes.Add($"quarantined {name}: {reason}");

        _store.Append(sessionId, JournalEvent.Create(
            JournalEventTypes.RecoveryQuarantine,
            _time.GetUtcNow(),
            ("file", name),
            ("reason", reason)));
    }

    private static void DeleteSidecar(string partPath)
    {
        string sidecar = SidecarPath(partPath);
        if (File.Exists(sidecar))
        {
            File.Delete(sidecar);
        }
    }

    private static string SidecarPath(string partPath) =>
        Path.ChangeExtension(partPath, null) + ".state.json";

    /// <summary>
    /// Reads the format the chunk was actually recorded in. Recovery never guesses a format:
    /// without the sidecar the file is quarantined instead.
    /// </summary>
    private static CaptureFormat? ReadSidecarFormat(string partPath)
    {
        string sidecar = SidecarPath(partPath);
        if (!File.Exists(sidecar))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(sidecar);
            using JsonDocument document = JsonDocument.Parse(stream);
            return new CaptureFormat(
                document.RootElement.GetProperty("sample_rate").GetInt32(),
                document.RootElement.GetProperty("channels").GetInt32(),
                16);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static int ParseIndex(string partFileName)
    {
        string stem = partFileName.Split('.', 2)[0];
        return int.TryParse(stem, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? index : 0;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
