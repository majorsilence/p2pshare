namespace P2PShare.Core.Util;

/// <summary>
/// A thread-safe "have I handled this key recently?" set with both a time-to-live and a hard size
/// cap. The DHT hands us an effectively unbounded stream of info-hashes, so any plain
/// <see cref="HashSet{T}"/> used for de-duplication would grow until the process runs out of
/// memory; this one forgets entries after <c>ttl</c> (which also lets a hash that timed out earlier
/// be retried later, when peers may be reachable) and sheds the oldest ones if it hits capacity.
/// </summary>
public sealed class ExpiringSet
{
    private readonly Dictionary<string, DateTime> _entries;
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();

    public ExpiringSet(int capacity, TimeSpan ttl, Func<DateTime>? clock = null, StringComparer? comparer = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ttl = ttl;
        _clock = clock ?? (() => DateTime.UtcNow);
        _entries = new Dictionary<string, DateTime>(comparer ?? StringComparer.OrdinalIgnoreCase);
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>
    /// Records <paramref name="key"/> as handled now. Returns true if it was absent or its previous
    /// entry had expired (the caller should proceed), false if it is still fresh (skip it).
    /// </summary>
    public bool TryAdd(string key)
    {
        var now = _clock();
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var added) && now - added < _ttl)
                return false;

            _entries[key] = now;
            if (_entries.Count > _capacity) Evict(now);
            return true;
        }
    }

    private void Evict(DateTime now)
    {
        // Drop everything that has already expired first; only if that is not enough shed the
        // oldest entries, down to 90% of capacity so the next eviction is not immediately due.
        var expired = _entries.Where(kv => now - kv.Value >= _ttl).Select(kv => kv.Key).ToList();
        foreach (var key in expired) _entries.Remove(key);
        if (_entries.Count <= _capacity) return;

        var target = (int)(_capacity * 0.9);
        var oldest = _entries.OrderBy(kv => kv.Value).Take(_entries.Count - target).Select(kv => kv.Key).ToList();
        foreach (var key in oldest) _entries.Remove(key);
    }
}
