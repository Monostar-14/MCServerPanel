using System.Collections.Concurrent;
using MCServerPanel.Models;

namespace MCServerPanel.Services;

/// <summary>
/// 编辑占用登记（advisory lock）。
/// 浏览器打开一份配置时登记「谁在编辑」，每 5 秒心跳续期；
/// 同一文件以「最近有操作的人」为当前占用者；别人保存会被 423 拒绝（可接管 / 强制覆盖）。
/// 配合保存前的 SHA-256 校验，避免几个人同时改同一份文件时互相覆盖。
/// </summary>
public sealed class EditSessionRegistry
{
    private sealed record Session(string ClientId, string ClientName, string File, DateTime LastSeen);

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(90);
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public EditSessionRegistry() : this(null) { }

    public EditSessionRegistry(TimeSpan? ttl) => _ttl = ttl ?? DefaultTtl;

    public int Count => _sessions.Count;

    /// <summary>某个文件当前的占用者（最近心跳的那个会话）。</summary>
    public LockDto? HolderOf(string file)
    {
        Prune();
        return _sessions.Values
            .Where(s => s.File.Equals(file, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.LastSeen)
            .Select(s => ToDto(s))
            .FirstOrDefault();
    }

    /// <summary>登记 / 续期。若已被别人占用且不允许接管，则返回 false 和占用者。</summary>
    public (bool Ok, LockDto? Holder, LockDto? Previous) Claim(string file, string clientId, string clientName, bool takeover)
    {
        Prune();
        var holder = HolderOf(file);
        var heldByOther = holder is not null && !holder.ClientId.Equals(clientId, StringComparison.Ordinal);
        if (heldByOther && !takeover)
            return (false, holder, null);

        _sessions[clientId] = new Session(clientId,
            string.IsNullOrWhiteSpace(clientName) ? "未命名用户" : clientName.Trim(), file, DateTime.UtcNow);

        return (true, null, heldByOther ? holder : null);
    }

    public void Renew(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;
        if (_sessions.TryGetValue(clientId, out var s))
            _sessions[clientId] = s with { LastSeen = DateTime.UtcNow };
    }

    public void Release(string clientId)
    {
        if (!string.IsNullOrWhiteSpace(clientId)) _sessions.TryRemove(clientId, out _);
    }

    /// <summary>只有当前占用者（或没人占用时）才能写盘。</summary>
    public bool CanSave(string file, string? clientId)
    {
        var holder = HolderOf(file);
        return holder is null || holder.ClientId.Equals(clientId ?? "", StringComparison.Ordinal);
    }

    /// <summary>每个文件只返回一个当前占用者，便于前端在侧栏打标记。</summary>
    public List<LockDto> Snapshot(string? clientId)
    {
        Prune();
        return _sessions.Values
            .GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(s => s.LastSeen).First())
            .OrderByDescending(s => s.LastSeen)
            .Select(s => ToDto(s, clientId))
            .ToList();
    }

    private static LockDto ToDto(Session s, string? forClientId = null) => new()
    {
        File = s.File,
        ClientId = s.ClientId,
        ClientName = s.ClientName,
        AgeSeconds = Math.Max(0, (int)(DateTime.UtcNow - s.LastSeen).TotalSeconds),
        Mine = forClientId is not null && s.ClientId.Equals(forClientId, StringComparison.Ordinal)
    };

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, session) in _sessions)
            if (now - session.LastSeen > _ttl) _sessions.TryRemove(key, out _);
    }
}
