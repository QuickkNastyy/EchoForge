using System.Collections.Concurrent;
using EchoForge.Contracts.Sessions;

namespace EchoForge.Core.Recording;

/// <summary>
/// A single-threaded persistence path for journal events.
///
/// <para>
/// Chunk finalization happens on a writer thread that must keep draining the capture queue. An
/// fsync there would stall audio, so events are handed to this queue and written by a dedicated
/// thread instead. The audio itself is already durable before anything is enqueued — the chunk's
/// <c>.meta.json</c> record is written beside the WAV — so a crash with events still in flight
/// loses nothing that recovery cannot reconstruct.
/// </para>
/// </summary>
public sealed class JournalPersistenceQueue : IDisposable
{
    private readonly BlockingCollection<(string SessionId, JournalEvent Event)> _pending = new();
    private readonly ISessionStore _store;
    private readonly Thread _worker;
    private readonly Action<string>? _onError;
    private bool _disposed;

    public JournalPersistenceQueue(ISessionStore store, Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _onError = onError;

        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "EchoForge journal persistence",
        };

        _worker.Start();
    }

    /// <summary>Events written so far. Used by tests to wait for the queue to catch up.</summary>
    public long Written { get; private set; }

    /// <summary>Events that could not be written, with the reason surfaced through onError.</summary>
    public long Failed { get; private set; }

    public void Enqueue(string sessionId, JournalEvent journalEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(journalEvent);

        if (_pending.IsAddingCompleted)
        {
            return;
        }

        try
        {
            _pending.Add((sessionId, journalEvent));
        }
        catch (InvalidOperationException)
        {
            // Completed concurrently. The chunk record on disk still makes this recoverable.
        }
    }

    /// <summary>
    /// Blocks until everything queued so far has been written. Called at epoch and session
    /// boundaries, never from the UI thread and never from a capture thread.
    /// </summary>
    public void Drain(TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (_pending.Count > 0 && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }
    }

    private void Run()
    {
        try
        {
            foreach ((string sessionId, JournalEvent journalEvent) in _pending.GetConsumingEnumerable())
            {
                try
                {
                    _store.Append(sessionId, journalEvent);
                    Written++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Failed++;
                    _onError?.Invoke($"journal write failed: {ex.GetType().Name}");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(5));
        _pending.Dispose();
    }
}
