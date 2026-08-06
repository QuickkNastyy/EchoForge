using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Transcripts;

namespace EchoForge.Infrastructure.Processing;

/// <summary>
/// Transcript revisions on the local filesystem, under the session that produced them.
///
/// <para>
/// The authority model matches recording's exactly, and for the same reason. The journal says
/// which revisions were activated; the files say which ones still exist. A transcript file no
/// activation vouches for is not a revision, and a revision whose file has gone is not
/// selectable. Trusting either half alone is how a half-written file ends up presented as
/// canonical.
/// </para>
///
/// <para>
/// <c>processing.json</c> is a projection in the same sense <c>session.json</c> is: convenient,
/// rebuildable, and never consulted about whether something was activated. It exists so the UI
/// can read progress without replaying a journal, and so a restart can say that an attempt was
/// interrupted.
/// </para>
/// </summary>
public sealed class FileTranscriptionStore(ISessionStore sessions) : ITranscriptionStore
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

    public string RevisionPath(string sessionId, int revision) =>
        _sessions.Resolve(sessionId).TranscriptRevisionPath(revision);

    // -- reconstruction --------------------------------------------------------------------

    public TranscriptionState Read(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        SessionPaths paths = _sessions.Resolve(sessionId);
        IReadOnlyList<JournalEvent> events = _sessions.ReadJournal(sessionId).Events;

        Dictionary<int, TranscriptRevisionRecord> revisions = [];
        TranscriptionJobRecord? job = null;
        int? explicitlySelected = null;
        int highestAllocated = 0;

        foreach (JournalEvent entry in events)
        {
            switch (entry.Type)
            {
                case JournalEventTypes.TranscriptionQueued:
                {
                    if (ReadAttempt(entry) is not { } queued)
                    {
                        continue;
                    }

                    highestAllocated = Math.Max(highestAllocated, queued.Revision);
                    job = new TranscriptionJobRecord
                    {
                        JobId = queued.JobId,
                        Revision = queued.Revision,
                        State = ProcessingStageState.Queued,
                        QueuedUtc = entry.TimestampUtc,
                        SourceManifestSha256 = entry.Field("source_sha256"),
                        Backend = entry.Field("backend"),
                        Profile = entry.Field("profile"),
                    };
                    break;
                }

                case JournalEventTypes.TranscriptionStarted:
                {
                    if (ReadAttempt(entry) is not { } started || job?.JobId != started.JobId)
                    {
                        continue;
                    }

                    job = job with { State = ProcessingStageState.Running, StartedUtc = entry.TimestampUtc };
                    break;
                }

                case JournalEventTypes.TranscriptionActivated:
                {
                    TranscriptRevisionRecord? record = ReadRevision(entry);
                    if (record is null)
                    {
                        continue;
                    }

                    highestAllocated = Math.Max(highestAllocated, record.Revision);
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

                case JournalEventTypes.TranscriptionFailed:
                {
                    if (ReadAttempt(entry) is not { } failed || job?.JobId != failed.JobId)
                    {
                        continue;
                    }

                    job = job with
                    {
                        State = ProcessingStageState.Failed,
                        CompletedUtc = entry.TimestampUtc,
                        FailureCode = entry.Field("code"),
                        FailureSummary = entry.Field("summary"),
                    };
                    break;
                }

                case JournalEventTypes.TranscriptionCancelled:
                {
                    if (ReadAttempt(entry) is not { } cancelled || job?.JobId != cancelled.JobId)
                    {
                        continue;
                    }

                    job = job with
                    {
                        State = ProcessingStageState.Cancelled,
                        CompletedUtc = entry.TimestampUtc,
                        FailureCode = null,
                        FailureSummary = null,
                    };
                    break;
                }

                case JournalEventTypes.TranscriptionRevisionSelected:
                {
                    if (entry.IntField("revision") is { } selected)
                    {
                        explicitlySelected = selected;
                    }

                    break;
                }
            }
        }

        // A revision the journal vouches for but whose file has gone cannot be opened, exported,
        // or cited. It stays listed so the history is honest, and it is not selectable.
        List<TranscriptRevisionRecord> present = [];
        foreach (TranscriptRevisionRecord record in revisions.Values.OrderBy(r => r.Revision))
        {
            bool exists = File.Exists(paths.TranscriptRevisionPath(record.Revision));
            present.Add(record with { FileExists = exists });
        }

        int? selectedRevision = ChooseSelected(present, explicitlySelected);

        // An attempt that the journal shows as still running is an attempt whose process died.
        // Saying "running" after a restart would be a claim about a process that does not exist.
        if (job is { State: ProcessingStageState.Queued or ProcessingStageState.Running })
        {
            job = job with
            {
                State = ProcessingStageState.Failed,
                FailureCode = "interrupted",
                FailureSummary = "Transcription was interrupted before it finished. Your recording is unchanged and you can try again.",
            };
        }

        job = MergeLiveProgress(paths, job);

        return new TranscriptionState
        {
            Stage = job?.State ?? (present.Count > 0 ? ProcessingStageState.Succeeded : ProcessingStageState.NotRequested),
            SelectedRevision = selectedRevision,
            Revisions = present,
            CurrentJob = job,
            HighestAllocatedRevision = Math.Max(highestAllocated, present.Count == 0 ? 0 : present[^1].Revision),
        };
    }

    /// <summary>
    /// The selected revision: whichever the user last chose, provided its file is still there,
    /// otherwise the newest one that is.
    /// </summary>
    private static int? ChooseSelected(List<TranscriptRevisionRecord> present, int? explicitlySelected)
    {
        if (explicitlySelected is { } chosen &&
            present.Any(r => r.Revision == chosen && r.FileExists))
        {
            return chosen;
        }

        return present.LastOrDefault(r => r.FileExists)?.Revision;
    }

    /// <summary>
    /// Takes live progress from the projection when it describes the same attempt the journal
    /// knows about. The projection never contributes a state; only counters and a stage name.
    /// </summary>
    private static TranscriptionJobRecord? MergeLiveProgress(SessionPaths paths, TranscriptionJobRecord? job)
    {
        if (job is null)
        {
            return null;
        }

        TranscriptionState? projection = ReadProjection(paths);
        return projection?.CurrentJob is { } live && live.JobId == job.JobId
            ? job with
            {
                Stage = live.Stage,
                CompletedUnits = live.CompletedUnits,
                TotalUnits = live.TotalUnits,
            }
            : job;
    }

    // -- the attempt lifecycle -------------------------------------------------------------

    public TranscriptionAttempt BeginAttempt(
        string sessionId,
        string jobId,
        string sourceManifestSha256,
        TranscriptionOptions options,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(options);

        lock (_writeLock)
        {
            SessionPaths paths = _sessions.Resolve(sessionId);
            Directory.CreateDirectory(paths.TranscriptRoot);

            TranscriptionState state = Read(sessionId);

            // Never reuse a number. A staging file from an attempt that died still carries the
            // revision it was writing, and reusing it would make the two indistinguishable.
            int revision = Math.Max(state.HighestAllocatedRevision, HighestOnDisk(paths)) + 1;

            TranscriptionAttempt attempt = new(
                sessionId,
                jobId,
                revision,
                paths.TranscriptStagingPath(revision),
                sourceManifestSha256);

            _sessions.Append(sessionId, JournalEvent.Create(
                JournalEventTypes.TranscriptionQueued,
                now,
                ("job_id", jobId),
                ("revision", Invariant(revision)),
                ("source_sha256", sourceManifestSha256),
                ("backend", options.Backend),
                ("profile", options.Profile ?? string.Empty)));

            WriteProjection(paths, Read(sessionId) with
            {
                CurrentJob = new TranscriptionJobRecord
                {
                    JobId = jobId,
                    Revision = revision,
                    State = ProcessingStageState.Queued,
                    QueuedUtc = now,
                    SourceManifestSha256 = sourceManifestSha256,
                    Backend = options.Backend,
                    Profile = options.Profile,
                },
                Stage = ProcessingStageState.Queued,
            });

            return attempt;
        }
    }

    public void MarkStarted(TranscriptionAttempt attempt, TranscriptionOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(options);

        lock (_writeLock)
        {
            _sessions.Append(attempt.SessionId, JournalEvent.Create(
                JournalEventTypes.TranscriptionStarted,
                now,
                ("job_id", attempt.JobId),
                ("revision", Invariant(attempt.Revision)),
                ("backend", options.Backend)));

            UpdateJob(attempt, job => job with
            {
                State = ProcessingStageState.Running,
                StartedUtc = now,
            });
        }
    }

    public void RecordProgress(TranscriptionAttempt attempt, string stage, int completedUnits, int totalUnits)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        lock (_writeLock)
        {
            UpdateJob(attempt, job => job with
            {
                Stage = stage,
                CompletedUnits = Math.Max(0, completedUnits),
                TotalUnits = Math.Max(0, totalUnits),
            });
        }
    }

    public ActivationOutcome Activate(ActivationRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_writeLock)
        {
            TranscriptionAttempt attempt = request.Attempt;
            SessionPaths paths = _sessions.Resolve(attempt.SessionId);

            if (!File.Exists(attempt.StagingPath))
            {
                return ActivationOutcome.Refuse("the staged transcript is not there");
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
                return ActivationOutcome.Refuse($"the staged transcript could not be read ({ex.GetType().Name})");
            }

            // The digest is checked here as well as in the supervisor. Activation is the moment
            // a file becomes canonical, and it is the last place a mismatch can still be caught.
            if (!string.Equals(actual, request.ExpectedSha256, StringComparison.Ordinal))
            {
                return ActivationOutcome.Refuse("the staged transcript does not match the digest reported for it");
            }

            if (length == 0)
            {
                return ActivationOutcome.Refuse("the staged transcript is empty");
            }

            string destination = paths.TranscriptRevisionPath(attempt.Revision);
            if (File.Exists(destination))
            {
                // Revisions are immutable. Overwriting one would silently change what an existing
                // summary was written from.
                return ActivationOutcome.Refuse("that revision already exists");
            }

            try
            {
                Directory.CreateDirectory(paths.TranscriptRoot);

                // Force the staged bytes to durable storage before the rename. A rename that
                // beats the data to disk leaves a named-but-empty revision after a power loss.
                using (FileStream flush = new(attempt.StagingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    flush.Flush(flushToDisk: true);
                }

                File.Move(attempt.StagingPath, destination, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ActivationOutcome.Refuse($"the transcript could not be activated ({ex.GetType().Name})");
            }

            TranscriptDocument transcript = request.Transcript;
            TranscriptRevisionRecord record = new()
            {
                Revision = attempt.Revision,
                JobId = attempt.JobId,
                CreatedUtc = now,
                RelativePath = $"transcript/transcript.v{Invariant(attempt.Revision)}.json",
                TranscriptSha256 = actual,
                SourceManifestSha256 = attempt.SourceManifestSha256,
                SegmentCount = transcript.Segments.Count,
                DurationSeconds = transcript.DurationSeconds,
                Backend = transcript.Model.Backend,
                ModelId = transcript.Model.ModelId,
                Profile = request.Profile,
                WorkerVersion = transcript.Model.WorkerVersion,
                ProtocolVersion = request.ProtocolVersion,
                RecognizesSpeech = transcript.Model.RecognizesSpeech,
            };

            // Journalled only after the bytes are in place. A crash between the rename and this
            // append leaves an unvouched-for file, which startup discards; a crash the other way
            // round would leave the journal claiming a revision that does not exist.
            _sessions.Append(attempt.SessionId, JournalEvent.Create(
                JournalEventTypes.TranscriptionActivated,
                now,
                ("job_id", record.JobId),
                ("revision", Invariant(record.Revision)),
                ("relative_path", record.RelativePath),
                ("transcript_sha256", record.TranscriptSha256),
                ("source_sha256", record.SourceManifestSha256),
                ("segments", Invariant(record.SegmentCount)),
                ("duration_seconds", record.DurationSeconds.ToString("R", CultureInfo.InvariantCulture)),
                ("backend", record.Backend),
                ("model_id", record.ModelId),
                ("profile", record.Profile ?? string.Empty),
                ("worker_version", record.WorkerVersion),
                ("protocol_version", Invariant(record.ProtocolVersion)),
                ("recognizes_speech", record.RecognizesSpeech ? "true" : "false")));

            WriteProjection(paths, Read(attempt.SessionId));
            return new ActivationOutcome(record, null);
        }
    }

    public void MarkFailed(TranscriptionAttempt attempt, string failureCode, string failureSummary, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        lock (_writeLock)
        {
            DiscardStaging(attempt);

            _sessions.Append(attempt.SessionId, JournalEvent.Create(
                JournalEventTypes.TranscriptionFailed,
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

    public void MarkCancelled(TranscriptionAttempt attempt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        lock (_writeLock)
        {
            DiscardStaging(attempt);

            _sessions.Append(attempt.SessionId, JournalEvent.Create(
                JournalEventTypes.TranscriptionCancelled,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_writeLock)
        {
            TranscriptionState state = Read(sessionId);
            if (!state.Revisions.Any(r => r.Revision == revision && r.FileExists))
            {
                return false;
            }

            _sessions.Append(sessionId, JournalEvent.Create(
                JournalEventTypes.TranscriptionRevisionSelected,
                now,
                ("revision", Invariant(revision))));

            WriteProjection(_sessions.Resolve(sessionId), Read(sessionId));
            return true;
        }
    }

    public int DiscardOrphanStaging(string sessionId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_writeLock)
        {
            SessionPaths paths = _sessions.Resolve(sessionId);
            if (!Directory.Exists(paths.TranscriptRoot))
            {
                return 0;
            }

            int discarded = 0;
            foreach (string staged in Directory.EnumerateFiles(paths.TranscriptRoot, "*.staging"))
            {
                try
                {
                    File.Delete(staged);
                    discarded++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked by something else. It is not a revision either way, so leaving it
                    // is safe; it will be discarded on a later pass.
                    continue;
                }
            }

            // The worker's own temporary file, from a crash between its write and its rename.
            foreach (string partial in Directory.EnumerateFiles(paths.TranscriptRoot, "*.partial"))
            {
                try
                {
                    File.Delete(partial);
                    discarded++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }

            if (discarded > 0)
            {
                _sessions.Append(sessionId, JournalEvent.Create(
                    JournalEventTypes.TranscriptionStagingDiscarded,
                    now,
                    ("files", Invariant(discarded))));
            }

            return discarded;
        }
    }

    public TranscriptDocument? ReadTranscript(string sessionId, int revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        TranscriptionState state = Read(sessionId);
        TranscriptRevisionRecord? record = state.Revisions.FirstOrDefault(r => r.Revision == revision);
        if (record is null || !record.FileExists)
        {
            return null;
        }

        string path = _sessions.Resolve(sessionId).TranscriptRevisionPath(revision);

        try
        {
            byte[] bytes = File.ReadAllBytes(path);

            // The digest recorded at activation is the identity of this revision. A file that no
            // longer matches it is not the revision, whatever its name says.
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), record.TranscriptSha256, StringComparison.Ordinal))
            {
                return null;
            }

            return JsonSerializer.Deserialize<TranscriptDocument>(bytes, TranscriptDocument.Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // -- plumbing ----------------------------------------------------------------------------

    private static void DiscardStaging(TranscriptionAttempt attempt)
    {
        try
        {
            if (File.Exists(attempt.StagingPath))
            {
                File.Delete(attempt.StagingPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Startup will collect it. An undeleted staging file is never mistaken for a
            // revision, so this is untidy rather than dangerous.
        }
    }

    private void UpdateJob(TranscriptionAttempt attempt, Func<TranscriptionJobRecord, TranscriptionJobRecord> update)
    {
        SessionPaths paths = _sessions.Resolve(attempt.SessionId);
        TranscriptionState state = ReadProjection(paths) ?? Read(attempt.SessionId);

        TranscriptionJobRecord job = state.CurrentJob is { } existing && existing.JobId == attempt.JobId
            ? existing
            : new TranscriptionJobRecord
            {
                JobId = attempt.JobId,
                Revision = attempt.Revision,
                State = ProcessingStageState.Queued,
                SourceManifestSha256 = attempt.SourceManifestSha256,
            };

        TranscriptionJobRecord updated = update(job);
        WriteProjection(paths, state with { CurrentJob = updated, Stage = updated.State });
    }

    private static int HighestOnDisk(SessionPaths paths)
    {
        if (!Directory.Exists(paths.TranscriptRoot))
        {
            return 0;
        }

        int highest = 0;
        foreach (string file in Directory.EnumerateFiles(paths.TranscriptRoot, "transcript.v*.json*"))
        {
            string name = Path.GetFileName(file);
            int start = "transcript.v".Length;
            int end = name.IndexOf('.', start);
            if (end > start &&
                int.TryParse(name.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                highest = Math.Max(highest, value);
            }
        }

        return highest;
    }

    private static TranscriptionState? ReadProjection(SessionPaths paths)
    {
        if (!File.Exists(paths.ProcessingPath))
        {
            return null;
        }

        try
        {
            using FileStream stream = new(paths.ProcessingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<TranscriptionState>(stream, ProjectionJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged projection costs nothing: the journal can rebuild all of it.
            return null;
        }
    }

    private static void WriteProjection(SessionPaths paths, TranscriptionState state)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            string temporary = paths.ProcessingPath + ".tmp";
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(state, ProjectionJson);

            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, paths.ProcessingPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The projection is a convenience. Failing to write it must not fail a transcription
            // whose journal entry already succeeded.
        }
    }

    private static (string JobId, int Revision)? ReadAttempt(JournalEvent entry)
    {
        string? jobId = entry.Field("job_id");
        int? revision = entry.IntField("revision");
        return jobId is null || revision is null ? null : (jobId, revision.Value);
    }

    private static TranscriptRevisionRecord? ReadRevision(JournalEvent entry)
    {
        if (ReadAttempt(entry) is not { } attempt)
        {
            return null;
        }

        string? transcriptSha = entry.Field("transcript_sha256");
        string? sourceSha = entry.Field("source_sha256");
        if (transcriptSha is null || sourceSha is null)
        {
            return null;
        }

        return new TranscriptRevisionRecord
        {
            Revision = attempt.Revision,
            JobId = attempt.JobId,
            CreatedUtc = entry.TimestampUtc,
            RelativePath = entry.Field("relative_path") ?? $"transcript/transcript.v{Invariant(attempt.Revision)}.json",
            TranscriptSha256 = transcriptSha,
            SourceManifestSha256 = sourceSha,
            SegmentCount = entry.IntField("segments") ?? 0,
            DurationSeconds = double.TryParse(entry.Field("duration_seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) ? duration : 0,
            Backend = entry.Field("backend") ?? string.Empty,
            ModelId = entry.Field("model_id") ?? string.Empty,
            Profile = string.IsNullOrEmpty(entry.Field("profile")) ? null : entry.Field("profile"),
            WorkerVersion = entry.Field("worker_version") ?? string.Empty,
            ProtocolVersion = entry.IntField("protocol_version") ?? 0,
            RecognizesSpeech = entry.Field("recognizes_speech") == "true",
        };
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
