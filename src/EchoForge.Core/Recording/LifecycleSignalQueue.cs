using System.Collections.Concurrent;

namespace EchoForge.Core.Recording;

/// <summary>
/// A single serialized thread for lifecycle work triggered by the operating system.
///
/// <para>
/// Endpoint notifications arrive on a COM callback and power notifications on a system thread.
/// Both give the handler very little time and neither tolerates blocking: joining capture
/// threads, hashing chunks, fsyncing the journal, or writing a snapshot inside one risks stalling
/// the audio stack or being cut off mid-write. The callbacks therefore only post a signal here
/// and return immediately, and every signal is processed in order on this thread.
/// </para>
/// </summary>
public sealed class LifecycleSignalQueue : IDisposable
{
    private readonly BlockingCollection<(long Sequence, Action Work)> _signals = new();
    private readonly Thread _worker;
    private readonly Action<string>? _onError;

    private long _posted;
    private long _processed;
    private bool _disposed;

    public LifecycleSignalQueue(Action<string>? onError = null)
    {
        _onError = onError;
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "EchoForge lifecycle signals",
        };

        _worker.Start();
    }

    /// <summary>Accepts a signal and returns at once. Never runs the work on the caller's thread.</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_signals.IsAddingCompleted)
        {
            return;
        }

        long sequence = Interlocked.Increment(ref _posted);

        try
        {
            _signals.Add((sequence, work));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Increment(ref _processed);
        }
    }

    /// <summary>
    /// Waits until every signal posted before this call has been processed. Used at boundaries
    /// and by tests; never called from a callback.
    /// </summary>
    public bool Drain(TimeSpan timeout)
    {
        long target = Interlocked.Read(ref _posted);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (Interlocked.Read(ref _processed) < target)
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
            foreach ((long _, Action work) in _signals.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    // A faulty signal handler must not kill the queue; later signals still run.
                    _onError?.Invoke($"lifecycle signal failed: {ex.GetType().Name}");
                }
                finally
                {
                    Interlocked.Increment(ref _processed);
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
        _signals.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(5));
        _signals.Dispose();
    }
}
