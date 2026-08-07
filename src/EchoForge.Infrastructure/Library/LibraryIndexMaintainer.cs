using System.Collections.Concurrent;

namespace EchoForge.Infrastructure.Library;

/// <summary>What one automatic index update did, for a surface that wants to react to it.</summary>
public sealed class IndexMaintenanceEventArgs(string sessionId, bool succeeded) : EventArgs
{
    public string SessionId { get; } = sessionId;

    public bool Succeeded { get; } = succeeded;
}

/// <summary>
/// Keeps the index in step with the session folders, without ever being able to affect them.
///
/// <para>
/// <b>The one rule that shapes everything here: a failed index update must never undo a canonical
/// operation.</b> A transcript that activated, activated. If re-indexing it then fails because the
/// database file is locked, on a full disk, or held open by something else, the transcript is still
/// the transcript — the only consequence is that search is briefly out of date and a rebuild will
/// fix it. So updates are fire-and-forget, every failure is swallowed and recorded rather than
/// thrown, and nothing that changes a session ever waits on this class or checks what it returned.
/// </para>
///
/// <para>
/// Requests for the same meeting <b>coalesce</b>. Activating a transcript, selecting it, and
/// renaming a speaker in quick succession are three notifications about one session, and running
/// three overlapping re-reads of the same folder would be both wasteful and a race. One update runs
/// at a time per session; anything asked for while it runs is collapsed into exactly one more pass
/// afterwards, which re-reads the final state and is therefore correct however many requests were
/// folded into it.
/// </para>
/// </summary>
public sealed class LibraryIndexMaintainer(SqliteLibraryIndex index) : IDisposable
{
    private readonly SqliteLibraryIndex _index = index ?? throw new ArgumentNullException(nameof(index));
    private readonly ConcurrentDictionary<string, SessionUpdate> _updates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Raised after each attempt, on whatever thread finished it.</summary>
    public event EventHandler<IndexMaintenanceEventArgs>? Updated;

    /// <summary>
    /// Sessions whose last update did not stick, so a surface can offer a rebuild rather than
    /// letting search quietly disagree with the files.
    /// </summary>
    public IReadOnlyCollection<string> NeedingRetry => [.. _failed.Keys];

    public bool IsBusy => _updates.Values.Any(update => update.IsRunning);

    /// <summary>
    /// Notes that a session's canonical state changed and returns immediately.
    ///
    /// <para>
    /// Deliberately returns nothing. A caller that could see whether indexing worked would
    /// eventually be written to care, and caring is how a cache starts being able to fail a
    /// transcript activation.
    /// </para>
    /// </summary>
    public void Invalidate(string sessionId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        SessionUpdate update = _updates.GetOrAdd(sessionId, _ => new SessionUpdate());

        lock (update.Sync)
        {
            update.Dirty = true;

            if (update.Running is not null)
            {
                // Folded into the pass already under way. That pass will loop once more and
                // re-read whatever state the session has settled into by then.
                return;
            }

            update.Running = Task.Run(() => DrainAsync(sessionId, update));
        }
    }

    /// <summary>Invalidates and waits, for a caller that genuinely needs the index caught up.</summary>
    public async Task<bool> UpdateNowAsync(string sessionId)
    {
        Invalidate(sessionId);
        await WaitAsync(sessionId).ConfigureAwait(false);
        return !_failed.ContainsKey(sessionId);
    }

    /// <summary>Waits for one session's outstanding work, or all of it.</summary>
    public async Task WaitAsync(string? sessionId = null)
    {
        while (true)
        {
            Task[] running =
            [
                .. _updates
                    .Where(pair => sessionId is null || string.Equals(pair.Key, sessionId, StringComparison.Ordinal))
                    .Select(pair => pair.Value.Running)
                    .Where(task => task is not null)
                    .Select(task => task!)
            ];

            if (running.Length == 0)
            {
                return;
            }

            await Task.WhenAll(running).ConfigureAwait(false);

            // A pass can queue itself again while we were waiting; loop until it is genuinely idle.
            if (!_updates.Values.Any(update => update.IsRunning))
            {
                return;
            }
        }
    }

    private async Task DrainAsync(string sessionId, SessionUpdate update)
    {
        while (true)
        {
            lock (update.Sync)
            {
                if (!update.Dirty || _disposed)
                {
                    update.Running = null;
                    return;
                }

                update.Dirty = false;
            }

            bool ok;
            try
            {
                ok = await _index.UpdateAsync(sessionId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException
                or InvalidOperationException)
            {
                // Whatever went wrong here, the session folder is unaffected. Saying so out loud:
                // this catch is the boundary that keeps a cache failure from becoming a data
                // failure, and it is why nothing upstream awaits this task.
                ok = false;
            }

            if (ok)
            {
                _failed.TryRemove(sessionId, out _);
            }
            else
            {
                _failed[sessionId] = 0;
            }

            Updated?.Invoke(this, new IndexMaintenanceEventArgs(sessionId, ok));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _updates.Clear();
        _failed.Clear();
    }

    private sealed class SessionUpdate
    {
        public Lock Sync { get; } = new();

        public bool Dirty { get; set; }

        public Task? Running { get; set; }

        public bool IsRunning
        {
            get { lock (Sync) { return Running is not null; } }
        }
    }
}
