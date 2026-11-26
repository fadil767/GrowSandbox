using System.Collections.Concurrent;

namespace GrowSandbox.Server.Security;

/// <summary>
/// Rate limiter sliding-window sederhana per koneksi/IP untuk mencegah brute-force login/register
/// (maksimal 5 percobaan gagal dalam jendela 30 detik).
/// </summary>
public sealed class AuthRateLimiter
{
    public const int DefaultMaxFailedAttempts = 5;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(30);

    private readonly int _maxFailedAttempts;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _utcNowProvider;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _failureBuckets =
        new(StringComparer.OrdinalIgnoreCase);

    public AuthRateLimiter(
        int maxFailedAttempts = DefaultMaxFailedAttempts,
        TimeSpan? window = null,
        Func<DateTime>? utcNowProvider = null)
    {
        if (maxFailedAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFailedAttempts));
        }

        _maxFailedAttempts = maxFailedAttempts;
        _window = window ?? DefaultWindow;
        _utcNowProvider = utcNowProvider ?? (static () => DateTime.UtcNow);
    }

    public bool IsRateLimited(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_failureBuckets.TryGetValue(key, out Queue<DateTime>? bucket))
        {
            return false;
        }

        DateTime cutoff = _utcNowProvider() - _window;
        lock (bucket)
        {
            PruneOldEntries(bucket, cutoff);
            return bucket.Count >= _maxFailedAttempts;
        }
    }

    public void RecordFailure(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        DateTime now = _utcNowProvider();
        DateTime cutoff = now - _window;
        Queue<DateTime> bucket = _failureBuckets.GetOrAdd(key, static _ => new Queue<DateTime>());

        lock (bucket)
        {
            PruneOldEntries(bucket, cutoff);
            bucket.Enqueue(now);
        }
    }

    public void Reset(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _failureBuckets.TryRemove(key, out _);
    }

    private static void PruneOldEntries(Queue<DateTime> bucket, DateTime cutoff)
    {
        while (bucket.Count > 0 && bucket.Peek() <= cutoff)
        {
            bucket.Dequeue();
        }
    }
}
