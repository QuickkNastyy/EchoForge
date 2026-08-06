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
    private readonly BlockingCollection<(string SessionId, JournalEvent Event, long Sequence)> _pending = new();
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

    private long _enqueued;
    private long _settled;

    /// <summary>Events durably written so far.</summary>
    public long Written { get; private set; }

    /// <summary>Events that could not be written, with the reason surfaced through onError.</summary>
    public long Failed { get; private set; }

    /// <summary>Sequence number of the most recently accepted write.</summary>
    public long HighWaterMark => Interlocked.Read(ref _enqueued);

    public void Enqueue(string sessionId, JournalEvent journalEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(journalEvent);

        if (_pending.IsAddingCompleted)
        {
            return;
        }

        long sequence = Interlocked.Increment(ref _enqueued);

        try
        {
            _pending.Add((sessionId, journalEvent, sequence));
        }
        catch (InvalidOperationException)
        {
            // Completed concurrently. Settle it so a barrier cannot wait forever.
            Interlocked.Increment(ref _settled);
        }
    }

    /// <summary>
    /// Waits for an exact barrier: every write accepted before this call has either been fsynced
    /// or failed.
    ///
    /// <para>
    /// Queue depth is not a barrier — an item can be dequeued and still be mid-fsync, so a count
    /// of zero would return before the data is durable. Sequence numbers are compared instead.
    /// </para>
    /// </summary>
    /// <returns>False on timeout, meaning the journal may be behind the audio on disk.</returns>
    public bool Drain(TimeSpan timeout)
    {
        long target = Interlocked.Read(ref _enqueued);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (Interlocked.Read(ref _settled) < target)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(2);
        }

        return true;
    }

    private void Run()
    {
        try
        {
            foreach ((string sessionId, JournalEvent journalEvent, long _) in _pending.GetConsumingEnumerable())
            {
                try
                {
                    _store.Append(sessionId, journalEvent);
                    Written++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Failed++;

                    // Surfaced, never swallowed. The audio and its record are already durable;
                    // the ledger is what has fallen behind, so the session needs reconciling.
                    _onError?.Invoke($"journal write failed: {ex.GetType().Name}");
                }
                finally
                {
                    // Settled either way, so a barrier cannot hang on a failed write.
                    Interlocked.Increment(ref _settled);
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
