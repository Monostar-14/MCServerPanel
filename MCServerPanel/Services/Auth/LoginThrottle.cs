using System.Collections.Concurrent;

namespace MCServerPanel.Services.Auth;

/// <summary>
/// 登录失败限速：按 IP 和用户名分别计数，窗口内失败过多则短暂封禁。
/// 纯内存实现，进程重启后清零（足够挡住在线爆破，不依赖外部存储）。
/// </summary>
public sealed class LoginThrottle
{
    private sealed class Entry
    {
        public readonly List<DateTime> Failures = new();
        public DateTime? BlockedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _block;

    public LoginThrottle(int maxFailures = 5, TimeSpan? window = null, TimeSpan? block = null)
    {
        _maxFailures = Math.Max(1, maxFailures);
        _window = window ?? TimeSpan.FromMinutes(5);
        _block = block ?? TimeSpan.FromMinutes(5);
    }

    public bool IsBlocked(string key, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!_entries.TryGetValue(key, out var entry)) return false;

        var now = DateTime.UtcNow;
        lock (entry)
        {
            if (entry.BlockedUntil is { } until && until > now)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
                return true;
            }

            if (entry.BlockedUntil is { } expired && expired <= now)
            {
                entry.BlockedUntil = null;
                entry.Failures.Clear();
            }
        }
        return false;
    }

    public void RecordFailure(string key)
    {
        var now = DateTime.UtcNow;
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            entry.Failures.RemoveAll(t => now - t > _window);
            entry.Failures.Add(now);
            if (entry.Failures.Count >= _maxFailures)
            {
                entry.BlockedUntil = now + _block;
                entry.Failures.Clear();
            }
        }
    }

    public void Reset(string key) => _entries.TryRemove(key, out _);

    public void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, entry) in _entries)
        {
            lock (entry)
            {
                entry.Failures.RemoveAll(t => now - t > _window);
                if (entry.Failures.Count == 0 && (entry.BlockedUntil is null || entry.BlockedUntil <= now))
                    _entries.TryRemove(key, out _);
            }
        }
    }
}
