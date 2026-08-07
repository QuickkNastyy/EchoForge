using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Infrastructure.Summaries;

/// <summary>One attempt at producing a summary.</summary>
public sealed record SummaryAttempt(
    string SessionId,
    string JobId,
    int Revision,
    string StagingPath,
    int TranscriptRevision,
    string TranscriptSha256);

/// <summary>The outcome of trying to activate. A refusal leaves the previous revision selected.</summary>
public sealed record SummaryActivation(SummaryRevisionRecord? Revision, string? Refusal)
{
    public bool Activated => Revision is not null;

    public static SummaryActivation Refuse(string reason) => new(null, reason);
}

/// <summary>
/// Summary revisions on disk, under the session they describe.
///
/// <para>
/// The same authority model as transcripts and chunks, for the same reason. The journal says
/// which revisions were activated; the files say which ones still exist; neither half alone is
/// sufficient. A summary file no activation vouches for is not a revision.
/// </para>
///
/// <para>
/// Summary prose never enters the journal. The journal is a recovery ledger read by diagnostics,
/// and a meeting's decisions are exactly the sort of content that must not end up there —
/// identities, digests and counts only.
/// </para>
/// </summary>
public sealed class FileSummaryStore(ISessionStore sessions)
{
    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly Lock _writeLock = new();

    private static readonly JsonSerializerOptions ProjectionJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
        },
    };

    public static string SummaryRoot(SessionPaths paths) => Path.Combine(paths.Root, "summary");

    public static string RevisionPath(SessionPaths paths, int revision) =>
        Path.Combine(SummaryRoot(paths), $"summary.v{revision.ToString(CultureInfo.InvariantCulture)}.json");

    public static string StagingPath(SessionPaths paths, int revision) => RevisionPath(paths, revision) + ".staging";

    private static string ProjectionPath(SessionPaths paths) => Path.Combine(paths.Root, "summaries.json");

    public string PathFor(string sessionId, int revision) => RevisionPath(_sessions.Resolve(sessionId), revision);

    // -- reconstruction ------------------------------------------------------------------------

    public SummaryState Read(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        SessionPaths paths = _sessions.Resolve(sessionId);
        IReadOnlyList<JournalEvent> events = _sessions.ReadJournal(sessionId).Events;

        Dictionary<int, SummaryRevisionRecord> revisions = [];
        SummaryJobRecord? job = null;
        int? explicitlySelected = null;
        int highest = 0;

        foreach (JournalEvent entry in events)
        {
            switch (entry.Type)
            {
                case JournalEventTypes.SummaryQueued:
                {
                    if (entry.Field("job_id") is not { } jobId || entry.IntField("revision") is not { } revision)
                    {
                        continue;
                    }

                    highest = Math.Max(highest, revision);
                    job = new SummaryJobRecord
                    {
                        JobId = jobId,
                        Revision = revision,
                        State = ProcessingStageState.Queued,
                        TranscriptRevision = entry.IntField("transcript_revision") ?? 0,
                        QueuedUtc = entry.TimestampUtc,
                    };
                    break;
                }

                case JournalEventTypes.SummaryStarted when job is not null && job.JobId == entry.Field("job_id"):
                    job = job with { State = ProcessingStageState.Running, StartedUtc = entry.TimestampUtc };
                    break;

                case JournalEventTypes.SummaryActivated:
                {
                    SummaryRevisionRecord? record = ReadRevision(entry);
                    if (record is null)
                    {
                        continue;
                    }

                    highest = Math.Max(highest, record.Revision);
                    revisions[record.Revision] = record;

                    if (job?.JobId == record.JobId)
                    {
                        job = job with
                        {
                            State = ProcessingStageState.Succeeded,
                            CompletedUtc = entry.TimestampUtc,
                            FailureCode = null,
                            FailureSummary = null,
                        };
                    }

                    break;
                }

                case JournalEventTypes.SummaryFailed when job is not null && job.JobId == entry.Field("job_id"):
                    job = job with
                    {
                        State = ProcessingStageState.Failed,
                        CompletedUtc = entry.TimestampUtc,
                        FailureCode = entry.Field("code"),
                        FailureSummary = entry.Field("summary"),
                    };
                    break;

                case JournalEventTypes.SummaryCancelled when job is not null && job.JobId == entry.Field("job_id"):
                    job = job with
                    {
                        State = ProcessingStageState.Cancelled,
                        CompletedUtc = entry.TimestampUtc,
                        FailureCode = null,
                        FailureSummary = null,
                    };
                    break;

                case JournalEventTypes.SummaryRevisionSelected:
                    if (entry.IntField("revision") is { } selected)
                    {
                        explicitlySelected = selected;
                    }

                    break;
            }
        }

        List<SummaryRevisionRecord> present =
        [
            .. revisions.Values
                .OrderBy(r => r.Revision)
                .Select(r => r with { FileExists = File.Exists(RevisionPath(paths, r.Revision)) })
        ];

        int? selectedRevision =
            explicitlySelected is { } chosen && present.Any(r => r.Revision == chosen && r.FileExists)
                ? chosen
                : present.LastOrDefault(r => r.FileExists)?.Revision;

        // An attempt the journal shows as still running is one whose process died. Saying
        // "running" after a restart would be a claim about a process that does not exist.
        if (job is { State: ProcessingStageState.Queued or ProcessingStageState.Running })
        {
            job = job with
            {
                State = ProcessingStageState.Failed,
                FailureCode = "interrupted",
                FailureSummary = "Summarising was interrupted before it finished. Nothing was changed and you can try again.",
            };
        }

        job = MergeLiveProgress(paths, job);

        return new SummaryState
        {
            Stage = job?.State ?? (present.Count > 0 ? ProcessingStageState.Succeeded : ProcessingStageState.NotRequested),
            SelectedRevision = selectedRevision,
            Revisions = present,
            CurrentJob = job,
            HighestAllocatedRevision = Math.Max(highest, present.Count == 0 ? 0 : present[^1].Revision),
        };
    }

    // -- the attempt lifecycle -------------------------------------------------------------------

    public SummaryAttempt BeginAttempt(
        string sessionId,
        string jobId,
        int transcriptRevision,
        string transcriptSha256,
        SummaryOptions options,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);

        lock (_writeLock)
        {
            SessionPaths paths = _sessions.Resolve(sessionId);
            Directory.CreateDirectory(SummaryRoot(paths));

            SummaryState state = Read(sessionId);
            int revision = Math.Max(state.HighestAllocatedRevision, HighestOnDisk(paths)) + 1;

            _sessions.Append(sessionId, JournalEvent.Create(
                JournalEventTypes.SummaryQueued,
                now,
                ("job_id", jobId),
                ("revision", Invariant(revision)),
                ("transcript_revision", Invariant(transcriptRevision)),
                ("transcript_sha256", transcriptSha256),
                ("prompt_version", options.PromptVersion),
                ("backend", options.Backend)));

            WriteProjection(paths, Read(sessionId) with
            {
                Stage = ProcessingStageState.Queued,
                CurrentJob = new SummaryJobRecord
                {
                    JobId = jobId,
                    Revision = revision,
                    State = ProcessingStageState.Queued,
                    TranscriptRevision = transcriptRevision,
                    QueuedUtc = now,
                },
            });

            return new SummaryAttempt(
                sessionId, jobId, revision, StagingPath(paths, revision), transcriptRevision, transcriptSha256);
        }
    }

    public void MarkStarted(SummaryAttempt attempt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        lock (_writeLock)
        {
            _sessions.Append(attempt.SessionId, JournalEvent.Create(
                JournalEventTypes.SummaryStarted,
                now,
                ("job_id", attempt.JobId),
                ("revision", Invariant(attempt.Revision))));

            UpdateJob(attempt, job => job with { State = ProcessingStageState.Running, StartedUtc = now });
        }
    }

    public void RecordProgress(SummaryAttempt attempt, string stage, int completed, int total)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        lock (_writeLock)
        {
            UpdateJob(attempt, job => job with
            {
                Stage = stage,
                CompletedUnits = Math.Max(0, completed),
                TotalUnits = Math.Max(0, total),
            });
        }
    }

    /// <summary>
    /// Verifies the staged bytes, moves them into place, and only then journals the activation.
    ///
    /// <para>
    /// A crash between the rename and the append leaves a file nothing vouches for, which startup
    /// discards. A crash the other way round would leave the journal claiming a summary that does
    /// not exist.
    /// </para>
    /// </summary>
    public SummaryActivation Activate(
        SummaryAttempt attempt,
        string expectedSha256,
        SummaryDocument summary,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(summary);

        lock (_writeLock)
        {
            SessionPaths paths = _sessions.Resolve(attempt.SessionId);

            if (!File.Exists(attempt.StagingPath))
            {
                return SummaryActivation.Refuse("the staged summary is not there");
            }

            string actual;
            long length;
            try
            {
                using FileStream stream = File.OpenRead(attempt.StagingPath);
                length = stream.Length;
                actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SummaryActivation.Refuse($"the staged summary could not be read ({ex.GetType().Name})");
            }

            if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            {
                return SummaryActivation.Refuse("the staged summary does not match the digest reported for it");
            }

            if (length == 0)
            {
                return SummaryActivation.Refuse("the staged summary is empty");
            }

            string destination = RevisionPath(paths, attempt.Revision);
            if (File.Exists(destination))
            {
                return SummaryActivation.Refuse("that revision already exists");
            }

            try
            {
                using (FileStream flush = new(attempt.StagingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    flush.Flush(flushToDisk: true);
                }

                File.Move(attempt.StagingPath, destination, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SummaryActivation.Refuse($"the summary could not be activated ({ex.GetType().Name})");
            }

            SummaryRevisionRecord record = new()
            {
                Revision = attempt.Revision,
                JobId = attempt.JobId,
                CreatedUtc = now,
                RelativePath = $"summary/summary.v{Invariant(attempt.Revision)}.json",
                SummarySha256 = actual,
                TranscriptRevision = summary.TranscriptRevision,
                TranscriptSha256 = summary.TranscriptSha256,
                PromptVersion = summary.PromptVersion,
                Backend = summary.Model.Backend,
                ModelId = summary.Model.ModelId,
                WorkerVersion = summary.Model.WorkerVersion,
                ProducesSummaries = summary.Model.ProducesSummaries,
                DecisionCount = summary.Decisions.Count,
                ActionCount = summary.ActionItems.Count,
                EvidenceValidated = true,
            };

            // Identities, digests and counts. No prose: the journal is a recovery ledger, and a
            // meeting's decisions do not belong in one.
            _sessions.Append(attempt.SessionId, JournalEvent.Create(
                JournalEventTypes.SummaryActivated,
                now,
                ("job_id", record.JobId),
                ("revision", Invariant(record.Revision)),
                ("relative_path", record.RelativePath),
                ("summary_sha256", record.SummarySha256),
                ("transcript_revision", Invariant(record.TranscriptRevision)),
                ("transcript_sha256", record.TranscriptSha256),
                ("prompt_version", record.PromptVersion),
                ("backend", record.Backend),
                ("model_id", record.ModelId),
                ("worker_version", record.WorkerVersion),
                ("produces_summaries", record.ProducesSummaries ? "true" : "false"),
                ("decisions", Invariant(record.DecisionCount)),
                ("actions", Invariant(record.ActionCount)),
                ("evidence_validated", "true")));

            WriteProjection(paths, Read(attempt.SessionId));
            return new SummaryActivation(record, null);
        }
    }

    public void MarkFailed(SummaryAttempt attempt, string failureCode, string failureSummary, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        lock (_writeLock)
        {
            DiscardStaging(attempt.StagingPath);

            _sessions.Append(attempt.SessionId, JournalEvent.Create(
                JournalEventTypes.SummaryFailed,
                now,
                ("job_id", attempt.JobId),
                ("revision", Invariant(attempt.Revision)),
                ("code", failureCode),
                ("summary", failureSummary)));

            UpdateJob(attempt, job => job with
            {
                State = ProcessingStageState.Failed,
                CompletedUtc = now,
                FailureCode = failureCode,
                FailureSummary = failureSummary,
            });
        }
    }

    public void MarkCancelled(SummaryAttempt attempt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        lock (_writeLock)
        {
            DiscardStaging(attempt.StagingPath);

            _sessions.Append(attempt.SessionId, JournalEvent.Create(
                JournalEventTypes.SummaryCancelled,
                now,
                ("job_id", attempt.JobId),
                ("revision", Invariant(attempt.Revision))));

            UpdateJob(attempt, job => job with
            {
                State = ProcessingStageState.Cancelled,
                CompletedUtc = now,
                FailureCode = null,
                FailureSummary = null,
            });
        }
    }

    public bool SelectRevision(string sessionId, int revision, DateTimeOffset now)
    {
        lock (_writeLock)
        {
            SummaryState state = Read(sessionId);
            if (!state.Revisions.Any(r => r.Revision == revision && r.FileExists))
            {
                return false;
            }

            _sessions.Append(sessionId, JournalEvent.Create(
                JournalEventTypes.SummaryRevisionSelected, now, ("revision", Invariant(revision))));

            WriteProjection(_sessions.Resolve(sessionId), Read(sessionId));
            return true;
        }
    }

    /// <summary>Removes staged summaries no activation vouches for. Called at startup.</summary>
    public int DiscardOrphanStaging(string sessionId)
    {
        lock (_writeLock)
        {
            string root = SummaryRoot(_sessions.Resolve(sessionId));
            if (!Directory.Exists(root))
            {
                return 0;
            }

            int discarded = 0;
            foreach (string staged in Directory.EnumerateFiles(root, "*.staging")
                         .Concat(Directory.EnumerateFiles(root, "*.partial")))
            {
                try
                {
                    File.Delete(staged);
                    discarded++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }

            return discarded;
        }
    }

    /// <summary>Reads an activated revision, verified against the digest it was activated under.</summary>
    public SummaryDocument? ReadSummary(string sessionId, int revision)
    {
        SummaryState state = Read(sessionId);
        SummaryRevisionRecord? record = state.Revisions.FirstOrDefault(r => r.Revision == revision);
        if (record is null || !record.FileExists)
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(RevisionPath(_sessions.Resolve(sessionId), revision));

            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), record.SummarySha256, StringComparison.Ordinal))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SummaryDocument>(bytes, SummaryDocument.Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // -- plumbing -----------------------------------------------------------------------------------

    private static void DiscardStaging(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Startup collects it. An undeleted staging file is never mistaken for a revision.
        }
    }

    private void UpdateJob(SummaryAttempt attempt, Func<SummaryJobRecord, SummaryJobRecord> update)
    {
        SessionPaths paths = _sessions.Resolve(attempt.SessionId);
        SummaryState state = ReadProjection(paths) ?? Read(attempt.SessionId);

        SummaryJobRecord job = state.CurrentJob is { } existing && existing.JobId == attempt.JobId
            ? existing
            : new SummaryJobRecord
            {
                JobId = attempt.JobId,
                Revision = attempt.Revision,
                State = ProcessingStageState.Queued,
                TranscriptRevision = attempt.TranscriptRevision,
            };

        SummaryJobRecord updated = update(job);
        WriteProjection(paths, state with { CurrentJob = updated, Stage = updated.State });
    }

    private static SummaryJobRecord? MergeLiveProgress(SessionPaths paths, SummaryJobRecord? job)
    {
        if (job is null)
        {
            return null;
        }

        SummaryState? projection = ReadProjection(paths);
        return projection?.CurrentJob is { } live && live.JobId == job.JobId
            ? job with { Stage = live.Stage, CompletedUnits = live.CompletedUnits, TotalUnits = live.TotalUnits }
            : job;
    }

    private static int HighestOnDisk(SessionPaths paths)
    {
        string root = SummaryRoot(paths);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        int highest = 0;
        foreach (string file in Directory.EnumerateFiles(root, "summary.v*.json*"))
        {
            string name = Path.GetFileName(file);
            int start = "summary.v".Length;
            int end = name.IndexOf('.', start);
            if (end > start && int.TryParse(name.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                highest = Math.Max(highest, value);
            }
        }

        return highest;
    }

    private static SummaryRevisionRecord? ReadRevision(JournalEvent entry)
    {
        if (entry.Field("job_id") is not { } jobId ||
            entry.IntField("revision") is not { } revision ||
            entry.Field("summary_sha256") is not { } digest)
        {
            return null;
        }

        return new SummaryRevisionRecord
        {
            Revision = revision,
            JobId = jobId,
            CreatedUtc = entry.TimestampUtc,
            RelativePath = entry.Field("relative_path") ?? $"summary/summary.v{Invariant(revision)}.json",
            SummarySha256 = digest,
            TranscriptRevision = entry.IntField("transcript_revision") ?? 0,
            TranscriptSha256 = entry.Field("transcript_sha256") ?? string.Empty,
            PromptVersion = entry.Field("prompt_version") ?? string.Empty,
            Backend = entry.Field("backend") ?? string.Empty,
            ModelId = entry.Field("model_id") ?? string.Empty,
            WorkerVersion = entry.Field("worker_version") ?? string.Empty,
            ProducesSummaries = entry.Field("produces_summaries") == "true",
            DecisionCount = entry.IntField("decisions") ?? 0,
            ActionCount = entry.IntField("actions") ?? 0,
            EvidenceValidated = entry.Field("evidence_validated") == "true",
        };
    }

    private static SummaryState? ReadProjection(SessionPaths paths)
    {
        string path = ProjectionPath(paths);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<SummaryState>(stream, ProjectionJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static void WriteProjection(SessionPaths paths, SummaryState state)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            string path = ProjectionPath(paths);
            string temporary = path + ".tmp";

            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(state, ProjectionJson));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The projection is a convenience. Failing to write it must not fail a summary
            // whose journal entry already succeeded.
        }
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
