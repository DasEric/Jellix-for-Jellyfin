using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Jellix.Services;

public sealed class OperationRateLimiter
{
    private const int MaxBuckets = 10_000;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _entries = new(StringComparer.Ordinal);
    private int _calls;

    public bool TryConsume(string identity, string operation, int limit, TimeSpan window)
    {
        var key = identity + "\n" + operation;
        if (_entries.Count >= MaxBuckets && !_entries.ContainsKey(key))
        {
            SweepExpired();
            if (_entries.Count >= MaxBuckets) return false;
        }

        var queue = _entries.GetOrAdd(key, static _ => new Queue<DateTime>());
        lock (queue)
        {
            var now = DateTime.UtcNow;
            while (queue.Count > 0 && now - queue.Peek() >= window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limit)
            {
                return false;
            }

            queue.Enqueue(now);
        }

        if (Interlocked.Increment(ref _calls) % 1024 == 0) SweepExpired();
        return true;
    }

    private void SweepExpired()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var entry in _entries)
        {
            lock (entry.Value)
            {
                while (entry.Value.Count > 0 && entry.Value.Peek() < cutoff) entry.Value.Dequeue();
                if (entry.Value.Count == 0) _entries.TryRemove(entry.Key, out _);
            }
        }
    }
}
