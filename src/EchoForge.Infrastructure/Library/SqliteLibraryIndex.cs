using System.Globalization;
using System.Text;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Library;
using Microsoft.Data.Sqlite;

namespace EchoForge.Infrastructure.Library;

/// <summary>How an index came to be in the state it is in.</summary>
public sealed record IndexHealth(bool Usable, bool Rebuilt, string? Detail)
{
    public static readonly IndexHealth Ready = new(true, false, null);
}

/// <summary>
/// A disposable full-text index over the meeting library.
///
/// <para>
/// <b>SQLite is never the source of truth, and the design makes that structural rather than
/// aspirational.</b> Everything in this database was computed by <see cref="LibraryProjection"/>
/// from the session folders, and the only thing stored that is not already in a file is the
/// inverted index itself. Delete the file, corrupt it, ship a schema change, or find it written
/// by an older build — in every case the answer is the same: throw it away and rebuild from disk.
/// Nothing is lost because nothing was only here.
/// </para>
///
/// <para>
/// That is also why a corrupt database is not an error the user has to understand. It is a cache
/// miss with a long recovery time, and the application keeps working while it recovers.
/// </para>
/// </summary>
public sealed class SqliteLibraryIndex : IDisposable
{
    /// <summary>
    /// Bumped whenever the stored shape changes. There is deliberately no migration path: a
    /// projection that can be rebuilt in seconds does not need one, and hand-written migrations
    /// are how a cache quietly becomes a thing you are afraid to delete.
    ///
    /// <para>
    /// Version 2 added <c>created_utc_ticks</c>. Date filtering had been comparing round-trip
    /// timestamp <i>strings</i>, which orders correctly only while every row happens to carry the
    /// same UTC offset — true today and not a thing to build a date filter on. An integer is
    /// unambiguous, and bumping the version costs a rebuild, which is exactly what a cache is for.
    /// </para>
    ///
    /// <para>
    /// Version 3 refreshes presentation data cached in <c>meetings</c>: default recording titles
    /// now use the user's local 12-hour clock, and a presentation-only title sidecar can override
    /// them. The SQL columns did not change, but old cached titles still need one rebuild on upgrade.
    /// </para>
    /// </summary>
    public const int SchemaVersion = 3;

    private readonly string _databasePath;
    private readonly LibraryProjection _projection;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    static SqliteLibraryIndex()
    {
        // Idempotent, and cheap. The bundle usually self-initialises through a module
        // initializer; calling it explicitly means the index does not depend on that continuing.
        SQLitePCL.Batteries_V2.Init();
    }

    public SqliteLibraryIndex(string databasePath, LibraryProjection projection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
    }

    public string DatabasePath => _databasePath;

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // Shared cache off, WAL on: many readers, one writer, which is exactly the access
        // pattern of a UI that searches while a rebuild is running.
        Pooling = false,
    }.ToString();

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(ConnectionString);

        try
        {
            connection.Open();

            // The first real read of the file happens here, so this is where "not a database"
            // surfaces. If it throws, the connection has to be closed on the way out: a leaked
            // handle keeps the corrupt file locked, and the rebuild that follows then cannot
            // delete the very file it was called to replace.
            using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // -- lifecycle ---------------------------------------------------------------------------------

    /// <summary>
    /// Makes the index usable, rebuilding it if that is what usable requires.
    ///
    /// <para>
    /// Returns rather than throws. A library that refuses to open because its cache is damaged
    /// would be worse than one with no cache at all.
    /// </para>
    /// </summary>
    public async Task<IndexHealth> EnsureReadyAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            using SqliteConnection connection = Open();

            if (IsCurrent(connection))
            {
                return IndexHealth.Ready;
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Unreadable, locked by something that died, or not a database at all. All of them
            // mean the same thing here.
            return await RebuildAsync(progress, $"the search index could not be opened ({ex.GetType().Name})", cancellationToken)
                .ConfigureAwait(false);
        }

        return await RebuildAsync(progress, "the search index was missing or out of date", cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsCurrent(SqliteConnection connection)
    {
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM index_meta WHERE id = 1;";
            object? value = command.ExecuteScalar();

            if (value is null || Convert.ToInt32(value, CultureInfo.InvariantCulture) != SchemaVersion)
            {
                return false;
            }

            // The table existing is not the same as it working. A truncated file can present a
            // valid header and fail on the first real read, and finding that out here is far
            // better than finding it out mid-search.
            using SqliteCommand probe = connection.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM meetings; SELECT count(*) FROM search;";
            probe.ExecuteScalar();

            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Throws the database away and builds a new one from the session folders.</summary>
    public async Task<IndexHealth> RebuildAsync(
        IProgress<string>? progress = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Discard();

            using SqliteConnection connection = Open();
            CreateSchema(connection);

            IReadOnlyList<string> sessionIds = _projection.SessionIds();
            int indexed = 0;

            foreach (string sessionId in sessionIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LibraryDocument? document = _projection.Build(sessionId);
                if (document is null)
                {
                    continue;
                }

                using SqliteTransaction transaction = connection.BeginTransaction();
                Write(connection, transaction, document);
                transaction.Commit();

                indexed++;
                progress?.Report($"Indexed {indexed.ToString(CultureInfo.CurrentCulture)} of {sessionIds.Count.ToString(CultureInfo.CurrentCulture)}");
            }

            return new IndexHealth(true, true, reason);
        }
        catch (OperationCanceledException)
        {
            // A cancelled rebuild leaves a partial index. It is discarded rather than left to
            // answer searches with a subset of the library and no way to tell.
            Discard();
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return new IndexHealth(false, false, $"the search index could not be built ({ex.GetType().Name})");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Re-indexes one meeting, leaving the rest alone.</summary>
    public async Task<bool> UpdateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using SqliteConnection connection = Open();
            CreateSchema(connection);

            LibraryDocument? document = _projection.Build(sessionId);

            using SqliteTransaction transaction = connection.BeginTransaction();

            if (document is null)
            {
                Delete(connection, transaction, sessionId);
            }
            else
            {
                Write(connection, transaction, document);
            }

            transaction.Commit();
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Removes the database. Always safe: it holds nothing that is not also on disk.</summary>
    public void Discard()
    {
        foreach (string path in (string[])[_databasePath, _databasePath + "-wal", _databasePath + "-shm"])
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
                // A locked file will be overwritten by the rebuild that follows.
            }
        }
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS index_meta (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                version INTEGER NOT NULL,
                built_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS meetings (
                session_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                created_utc_ticks INTEGER NOT NULL,
                started_utc TEXT,
                duration_seconds REAL NOT NULL,
                state TEXT NOT NULL,
                has_audio INTEGER NOT NULL,
                has_transcript INTEGER NOT NULL,
                selected_transcript_revision INTEGER,
                transcript_revisions TEXT NOT NULL,
                transcript_backend TEXT NOT NULL,
                transcript_model_id TEXT NOT NULL,
                transcript_profile TEXT,
                segment_count INTEGER NOT NULL,
                has_summary INTEGER NOT NULL,
                selected_summary_revision INTEGER,
                summary_revisions TEXT NOT NULL,
                summary_backend TEXT NOT NULL,
                summary_model_id TEXT NOT NULL,
                summary_profile TEXT,
                summary_source_transcript_revision INTEGER,
                summary_produces_summaries INTEGER NOT NULL,
                needs_attention INTEGER NOT NULL,
                attention_reason TEXT
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts5(
                text,
                session_id UNINDEXED,
                kind UNINDEXED,
                transcript_revision UNINDEXED,
                segment_id UNINDEXED,
                summary_revision UNINDEXED,
                start_seconds UNINDEXED,
                source_track UNINDEXED,
                speaker_name UNINDEXED,
                hit_id UNINDEXED,
                ordinal UNINDEXED,
                tokenize = "unicode61 remove_diacritics 2"
            );

            CREATE INDEX IF NOT EXISTS meetings_created ON meetings (created_utc_ticks DESC);
            """;
        command.ExecuteNonQuery();

        using SqliteCommand meta = connection.CreateCommand();
        meta.CommandText = "INSERT INTO index_meta (id, version, built_utc) VALUES (1, $v, $t) " +
                           "ON CONFLICT(id) DO UPDATE SET version = $v, built_utc = $t;";
        meta.Parameters.AddWithValue("$v", SchemaVersion);
        meta.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        meta.ExecuteNonQuery();
    }

    private static void Delete(SqliteConnection connection, SqliteTransaction transaction, string sessionId)
    {
        foreach (string sql in (string[])["DELETE FROM meetings WHERE session_id = $s;", "DELETE FROM search WHERE session_id = $s;"])
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$s", sessionId);
            command.ExecuteNonQuery();
        }
    }

    private static void Write(SqliteConnection connection, SqliteTransaction transaction, LibraryDocument document)
    {
        LibraryEntry entry = document.Entry;
        Delete(connection, transaction, entry.SessionId);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO meetings (
                    session_id, title, created_utc, created_utc_ticks, started_utc, duration_seconds, state, has_audio,
                    has_transcript, selected_transcript_revision, transcript_revisions,
                    transcript_backend, transcript_model_id, transcript_profile, segment_count,
                    has_summary, selected_summary_revision, summary_revisions, summary_backend,
                    summary_model_id, summary_profile, summary_source_transcript_revision,
                    summary_produces_summaries, needs_attention, attention_reason
                ) VALUES (
                    $session_id, $title, $created_utc, $created_utc_ticks, $started_utc, $duration_seconds, $state, $has_audio,
                    $has_transcript, $selected_transcript_revision, $transcript_revisions,
                    $transcript_backend, $transcript_model_id, $transcript_profile, $segment_count,
                    $has_summary, $selected_summary_revision, $summary_revisions, $summary_backend,
                    $summary_model_id, $summary_profile, $summary_source_transcript_revision,
                    $summary_produces_summaries, $needs_attention, $attention_reason
                );
                """;

            command.Parameters.AddWithValue("$session_id", entry.SessionId);
            command.Parameters.AddWithValue("$title", entry.Title);
            command.Parameters.AddWithValue("$created_utc", entry.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$created_utc_ticks", entry.CreatedUtc.UtcTicks);
            command.Parameters.AddWithValue("$started_utc", (object?)entry.StartedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            command.Parameters.AddWithValue("$duration_seconds", entry.Duration.TotalSeconds);
            command.Parameters.AddWithValue("$state", entry.State.ToString());
            command.Parameters.AddWithValue("$has_audio", entry.HasAudio ? 1 : 0);
            command.Parameters.AddWithValue("$has_transcript", entry.HasTranscript ? 1 : 0);
            command.Parameters.AddWithValue("$selected_transcript_revision", (object?)entry.SelectedTranscriptRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$transcript_revisions", string.Join(',', entry.TranscriptRevisions));
            command.Parameters.AddWithValue("$transcript_backend", entry.TranscriptBackend);
            command.Parameters.AddWithValue("$transcript_model_id", entry.TranscriptModelId);
            command.Parameters.AddWithValue("$transcript_profile", (object?)entry.TranscriptProfile ?? DBNull.Value);
            command.Parameters.AddWithValue("$segment_count", entry.SegmentCount);
            command.Parameters.AddWithValue("$has_summary", entry.HasSummary ? 1 : 0);
            command.Parameters.AddWithValue("$selected_summary_revision", (object?)entry.SelectedSummaryRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$summary_revisions", string.Join(',', entry.SummaryRevisions));
            command.Parameters.AddWithValue("$summary_backend", entry.SummaryBackend);
            command.Parameters.AddWithValue("$summary_model_id", entry.SummaryModelId);
            command.Parameters.AddWithValue("$summary_profile", (object?)entry.SummaryProfile ?? DBNull.Value);
            command.Parameters.AddWithValue("$summary_source_transcript_revision", (object?)entry.SummarySourceTranscriptRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$summary_produces_summaries", entry.SummaryProducesSummaries ? 1 : 0);
            command.Parameters.AddWithValue("$needs_attention", entry.NeedsAttention ? 1 : 0);
            command.Parameters.AddWithValue("$attention_reason", (object?)entry.AttentionReason ?? DBNull.Value);

            command.ExecuteNonQuery();
        }

        int ordinal = 0;

        if (document.Transcript is { } transcript)
        {
            foreach (TranscriptSegment segment in transcript.Segments)
            {
                if (string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                // The alias is applied on the way into the index so search results read the way
                // the transcript reads. Evidence identity still comes from the segment ID, which
                // no alias can touch.
                Index(
                    connection, transaction, entry.SessionId, SearchHitKind.TranscriptSegment, segment.Text,
                    transcriptRevision: transcript.TranscriptRevision,
                    segmentId: segment.Id,
                    summaryRevision: null,
                    startSeconds: segment.StartSeconds,
                    sourceTrack: segment.SourceTrack,
                    speakerName: SpeakerPresentation.Present(segment, entry.Aliases),
                    ordinal: ordinal++);
            }
        }

        if (document.Summary is { } summary)
        {
            void Items(IReadOnlyList<SummaryItem> items, SearchHitKind kind)
            {
                foreach (SummaryItem item in items)
                {
                    Index(
                        connection, transaction, entry.SessionId, kind, item.Text,
                        transcriptRevision: summary.TranscriptRevision,
                        segmentId: item.Evidence.Count > 0 ? item.Evidence[0].SegmentId : null,
                        summaryRevision: summary.SummaryRevision,
                        startSeconds: item.Evidence.Count > 0 ? item.Evidence[0].StartSeconds : null,
                        sourceTrack: item.Evidence.Count > 0 ? item.Evidence[0].SourceTrack : null,
                        speakerName: null,
                        ordinal: ordinal++);
                }
            }

            if (!string.IsNullOrWhiteSpace(summary.Overview))
            {
                Index(
                    connection, transaction, entry.SessionId, SearchHitKind.SummaryOverview, summary.Overview,
                    summary.TranscriptRevision, null, summary.SummaryRevision, null, null, null, ordinal++);
            }

            Items(summary.KeyPoints, SearchHitKind.SummaryKeyPoint);
            Items(summary.Decisions, SearchHitKind.SummaryDecision);
            Items(summary.OpenQuestions, SearchHitKind.SummaryQuestion);
            Items(summary.Risks, SearchHitKind.SummaryRisk);
            Items(summary.Blockers, SearchHitKind.SummaryBlocker);

            foreach (SummaryAction action in summary.ActionItems)
            {
                Index(
                    connection, transaction, entry.SessionId, SearchHitKind.SummaryAction, action.Task,
                    transcriptRevision: summary.TranscriptRevision,
                    segmentId: action.Evidence.Count > 0 ? action.Evidence[0].SegmentId : null,
                    summaryRevision: summary.SummaryRevision,
                    startSeconds: action.Evidence.Count > 0 ? action.Evidence[0].StartSeconds : null,
                    sourceTrack: action.Evidence.Count > 0 ? action.Evidence[0].SourceTrack : null,
                    speakerName: null,
                    ordinal: ordinal++);
            }
        }
    }

    private static void Index(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        SearchHitKind kind,
        string text,
        int? transcriptRevision,
        string? segmentId,
        int? summaryRevision,
        double? startSeconds,
        string? sourceTrack,
        string? speakerName,
        int ordinal)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search (
                text, session_id, kind, transcript_revision, segment_id, summary_revision,
                start_seconds, source_track, speaker_name, hit_id, ordinal
            ) VALUES ($text, $session_id, $kind, $tr, $seg, $sr, $start, $track, $speaker, $hit, $ordinal);
            """;

        // Stable across rebuilds: the same content produces the same identity, so a UI can key
        // rows on it and a test can assert ordering without depending on rowids.
        string hitId = string.Create(
            CultureInfo.InvariantCulture,
            $"{sessionId}:{kind}:{transcriptRevision?.ToString(CultureInfo.InvariantCulture) ?? "-"}:{segmentId ?? "-"}:{summaryRevision?.ToString(CultureInfo.InvariantCulture) ?? "-"}:{ordinal}");

        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$tr", (object?)transcriptRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$seg", (object?)segmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sr", (object?)summaryRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$start", (object?)startSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$track", (object?)sourceTrack ?? DBNull.Value);
        command.Parameters.AddWithValue("$speaker", (object?)speakerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$hit", hitId);
        command.Parameters.AddWithValue("$ordinal", ordinal);

        command.ExecuteNonQuery();
    }

    // -- reading ------------------------------------------------------------------------------------

    /// <summary>
    /// Every meeting the filter admits, newest first.
    ///
    /// <para>
    /// A date range is a <c>WHERE</c> clause, never a reason to rebuild. The index already knows
    /// when every meeting was; throwing it away to ask a narrower question would turn a keystroke
    /// into a full re-read of every transcript on disk.
    /// </para>
    /// </summary>
    public IReadOnlyList<LibraryEntry> Meetings(LibraryFilter? filter = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();

            StringBuilder sql = new("SELECT * FROM meetings");
            AppendRange(sql, command, filter, "WHERE", "created_utc_ticks");
            sql.Append(" ORDER BY created_utc_ticks DESC, session_id ASC;");

            command.CommandText = sql.ToString();

            using SqliteDataReader reader = command.ExecuteReader();
            List<LibraryEntry> entries = [];

            while (reader.Read())
            {
                entries.Add(ReadEntry(reader));
            }

            return entries;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            return [];
        }
    }

    private static LibraryEntry ReadEntry(SqliteDataReader reader) => new()
    {
        SessionId = reader.GetString(reader.GetOrdinal("session_id")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        CreatedUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_utc")), CultureInfo.InvariantCulture),
        StartedUtc = Nullable(reader, "started_utc") is { } started
            ? DateTimeOffset.Parse(started, CultureInfo.InvariantCulture)
            : null,
        Duration = TimeSpan.FromSeconds(reader.GetDouble(reader.GetOrdinal("duration_seconds"))),
        State = Enum.TryParse(reader.GetString(reader.GetOrdinal("state")), out Contracts.Sessions.SessionState state)
            ? state
            : Contracts.Sessions.SessionState.New,
        HasAudio = reader.GetInt32(reader.GetOrdinal("has_audio")) != 0,
        HasTranscript = reader.GetInt32(reader.GetOrdinal("has_transcript")) != 0,
        SelectedTranscriptRevision = NullableInt(reader, "selected_transcript_revision"),
        TranscriptRevisions = Revisions(reader, "transcript_revisions"),
        TranscriptBackend = reader.GetString(reader.GetOrdinal("transcript_backend")),
        TranscriptModelId = reader.GetString(reader.GetOrdinal("transcript_model_id")),
        TranscriptProfile = Nullable(reader, "transcript_profile"),
        SegmentCount = reader.GetInt32(reader.GetOrdinal("segment_count")),
        HasSummary = reader.GetInt32(reader.GetOrdinal("has_summary")) != 0,
        SelectedSummaryRevision = NullableInt(reader, "selected_summary_revision"),
        SummaryRevisions = Revisions(reader, "summary_revisions"),
        SummaryBackend = reader.GetString(reader.GetOrdinal("summary_backend")),
        SummaryModelId = reader.GetString(reader.GetOrdinal("summary_model_id")),
        SummaryProfile = Nullable(reader, "summary_profile"),
        SummarySourceTranscriptRevision = NullableInt(reader, "summary_source_transcript_revision"),
        SummaryProducesSummaries = reader.GetInt32(reader.GetOrdinal("summary_produces_summaries")) != 0,
        NeedsAttention = reader.GetInt32(reader.GetOrdinal("needs_attention")) != 0,
        AttentionReason = Nullable(reader, "attention_reason"),
    };

    /// <summary>
    /// Adds the date bounds to a query, comparing integers rather than formatted timestamps.
    ///
    /// <para>
    /// Both ends inclusive, and both converted through <c>UtcTicks</c> so a bound written in one
    /// offset and a meeting recorded in another are still compared as the instants they are.
    /// </para>
    /// </summary>
    private static void AppendRange(
        StringBuilder sql, SqliteCommand command, LibraryFilter? filter, string joiner, string column)
    {
        if (filter is null || filter.IsEmpty)
        {
            return;
        }

        string next = joiner;

        if (filter.Since is { } since)
        {
            sql.Append(CultureInfo.InvariantCulture, $" {next} {column} >= $since");
            command.Parameters.AddWithValue("$since", since.UtcTicks);
            next = "AND";
        }

        if (filter.Until is { } until)
        {
            sql.Append(CultureInfo.InvariantCulture, $" {next} {column} <= $until");
            command.Parameters.AddWithValue("$until", until.UtcTicks);
        }

        sql.Append(' ');
    }

    private static string? Nullable(SqliteDataReader reader, string column)
    {
        int index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    private static int? NullableInt(SqliteDataReader reader, string column)
    {
        int index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : reader.GetInt32(index);
    }

    private static IReadOnlyList<int> Revisions(SqliteDataReader reader, string column)
    {
        string raw = reader.GetString(reader.GetOrdinal(column));
        return raw.Length == 0
            ? []
            : [.. raw.Split(',').Select(part => int.Parse(part, CultureInfo.InvariantCulture))];
    }

    /// <summary>Full-text search across transcripts and summaries.</summary>
    public SearchResults Search(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? expression = FtsQuery.Build(query.Text);
        if (expression is null)
        {
            return SearchResults.Empty;
        }

        try
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();

            StringBuilder sql = new();
            sql.Append("SELECT highlight(search, 0, char(2), char(3)) AS marked, text, session_id, kind, ");
            sql.Append("transcript_revision, segment_id, summary_revision, start_seconds, source_track, ");
            sql.Append("speaker_name, hit_id, ordinal, bm25(search) AS rank ");
            sql.Append("FROM search JOIN meetings USING (session_id) WHERE search MATCH $q ");

            if (query.SessionId is { } sessionId)
            {
                sql.Append("AND search.session_id = $session ");
                command.Parameters.AddWithValue("$session", sessionId);
            }

            AppendRange(
                sql,
                command,
                new LibraryFilter { Since = query.Since, Until = query.Until },
                "AND",
                "meetings.created_utc_ticks");

            if (query.Kinds.Count > 0)
            {
                sql.Append("AND kind IN (");
                for (int i = 0; i < query.Kinds.Count; i++)
                {
                    sql.Append(i == 0 ? "$k" : ", $k").Append(i.ToString(CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("$k" + i.ToString(CultureInfo.InvariantCulture), query.Kinds[i].ToString());
                }

                sql.Append(") ");
            }

            // Relevance first, then a total order. bm25 ties are common - two segments of the
            // same length containing the same word score identically - and without the tie-break
            // the same search would return the same rows in a different order on a rebuild.
            sql.Append("ORDER BY rank ASC, meetings.created_utc_ticks DESC, hit_id ASC LIMIT $limit;");

            command.CommandText = sql.ToString();
            command.Parameters.AddWithValue("$q", expression);
            command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));

            using SqliteDataReader reader = command.ExecuteReader();
            List<SearchHit> hits = [];

            while (reader.Read())
            {
                hits.Add(ReadHit(reader));
            }

            // unicode61 splits on whitespace and punctuation, which is every script that writes
            // spaces and none of the ones that do not. A Japanese sentence arrives as a single
            // enormous token, so searching for a word inside it matches nothing at all. Falling
            // back to an exact substring scan is what makes those languages searchable; it stays
            // conservative because a substring is a stricter test than a token match, not a
            // looser one.
            if (hits.Count == 0 && NeedsSubstringFallback(query.Text))
            {
                return new SearchResults { Hits = SubstringSearch(connection, query) };
            }

            return new SearchResults { Hits = hits };
        }
        catch (SqliteException ex)
        {
            // A malformed query is the user's typing, not a broken index; an unreadable database
            // is the opposite. Both are reported rather than shown as "no results", because
            // emptiness and inability to answer are very different statements.
            bool malformed = ex.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase);

            return new SearchResults
            {
                IndexUnavailable = !malformed,
                Notice = malformed
                    ? "That search could not be understood. Try plain words, or a phrase in quotes."
                    : "The search index is not available. It can be rebuilt from your recordings.",
            };
        }
    }

    /// <summary>
    /// True when the query is written in a script the tokenizer cannot split into words.
    ///
    /// <para>
    /// Deliberately narrow. Falling back for every empty result would turn a search box that
    /// finds words into one that finds fragments, and "cat" would start matching "certificate".
    /// </para>
    /// </summary>
    private static bool NeedsSubstringFallback(string text) =>
        text.Any(character => character is >= '぀' and <= 'ヿ'    // kana
            or >= '㐀' and <= '䶿'                               // CJK extension A
            or >= '一' and <= '鿿'                               // CJK unified
            or >= '가' and <= '힯'                               // hangul syllables
            or >= '豈' and <= '﫿');                             // CJK compatibility

    /// <summary>Exact substring matching, for text the tokenizer cannot break into words.</summary>
    private static List<SearchHit> SubstringSearch(SqliteConnection connection, SearchQuery query)
    {
        string needle = query.Text.Trim().Trim('"');
        if (needle.Length == 0)
        {
            return [];
        }

        using SqliteCommand command = connection.CreateCommand();
        StringBuilder sql = new();
        sql.Append("SELECT text, session_id, kind, transcript_revision, segment_id, summary_revision, ");
        sql.Append("start_seconds, source_track, speaker_name, hit_id, ordinal ");
        sql.Append("FROM search JOIN meetings USING (session_id) WHERE instr(text, $needle) > 0 ");

        if (query.SessionId is { } sessionId)
        {
            sql.Append("AND search.session_id = $session ");
            command.Parameters.AddWithValue("$session", sessionId);
        }

        // The fallback is a different way of matching, not a different library: a date range
        // still means the same thing when the query happens to be in a script the tokenizer
        // cannot split.
        AppendRange(
            sql,
            command,
            new LibraryFilter { Since = query.Since, Until = query.Until },
            "AND",
            "meetings.created_utc_ticks");

        // No relevance to rank by, so the order is simply stable.
        sql.Append("ORDER BY meetings.created_utc_ticks DESC, hit_id ASC LIMIT $limit;");

        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$needle", needle);
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));

        using SqliteDataReader reader = command.ExecuteReader();
        List<SearchHit> hits = [];

        while (reader.Read())
        {
            string text = reader.GetString(reader.GetOrdinal("text"));

            hits.Add(new SearchHit
            {
                SessionId = reader.GetString(reader.GetOrdinal("session_id")),
                Kind = Enum.TryParse(reader.GetString(reader.GetOrdinal("kind")), out SearchHitKind kind)
                    ? kind
                    : SearchHitKind.TranscriptSegment,
                Text = text,
                Highlights = Occurrences(text, needle),
                TranscriptRevision = NullableInt(reader, "transcript_revision"),
                SegmentId = Nullable(reader, "segment_id"),
                SummaryRevision = NullableInt(reader, "summary_revision"),
                StartSeconds = reader.IsDBNull(reader.GetOrdinal("start_seconds"))
                    ? null
                    : reader.GetDouble(reader.GetOrdinal("start_seconds")),
                SourceTrack = Nullable(reader, "source_track"),
                SpeakerName = Nullable(reader, "speaker_name"),
                HitId = reader.GetString(reader.GetOrdinal("hit_id")),
                Rank = 0,
            });
        }

        return hits;
    }

    private static List<HighlightRange> Occurrences(string text, string needle)
    {
        List<HighlightRange> ranges = [];
        int at = text.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            ranges.Add(new HighlightRange(at, needle.Length));
            at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return ranges;
    }

    private static SearchHit ReadHit(SqliteDataReader reader)
    {
        string marked = reader.GetString(reader.GetOrdinal("marked"));
        (string text, IReadOnlyList<HighlightRange> highlights) = Highlighting.Extract(marked);

        return new SearchHit
        {
            SessionId = reader.GetString(reader.GetOrdinal("session_id")),
            Kind = Enum.TryParse(reader.GetString(reader.GetOrdinal("kind")), out SearchHitKind kind)
                ? kind
                : SearchHitKind.TranscriptSegment,
            Text = text,
            Highlights = highlights,
            TranscriptRevision = NullableInt(reader, "transcript_revision"),
            SegmentId = Nullable(reader, "segment_id"),
            SummaryRevision = NullableInt(reader, "summary_revision"),
            StartSeconds = reader.IsDBNull(reader.GetOrdinal("start_seconds"))
                ? null
                : reader.GetDouble(reader.GetOrdinal("start_seconds")),
            SourceTrack = Nullable(reader, "source_track"),
            SpeakerName = Nullable(reader, "speaker_name"),
            HitId = reader.GetString(reader.GetOrdinal("hit_id")),
            Rank = reader.GetDouble(reader.GetOrdinal("rank")),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeGate.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
