using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MCServerPanel.Services.Auth;

/// <summary>服务器端会话：滑动空闲过期 + 绝对过期，登出/改密可整体吊销。</summary>
public sealed class SessionStore
{
    public const string CookieName = "mcsp_session";

    private sealed record Session(string User, DateTime CreatedUtc, DateTime LastSeenUtc);

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _idle;
    private readonly TimeSpan _absolute;

    public SessionStore(TimeSpan? idle = null, TimeSpan? absolute = null)
    {
        _idle = idle ?? TimeSpan.FromMinutes(30);
        _absolute = absolute ?? TimeSpan.FromHours(12);
    }

    public string Create(string user)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = DateTime.UtcNow;
        _sessions[token] = new Session(user, now, now);
        return token;
    }

    public bool TryValidate(string? token, out string user)
    {
        user = "";
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!_sessions.TryGetValue(token, out var session)) return false;

        var now = DateTime.UtcNow;
        if (now - session.LastSeenUtc > _idle || now - session.CreatedUtc > _absolute)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        _sessions[token] = session with { LastSeenUtc = now };
        user = session.User;
        return true;
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) _sessions.TryRemove(token, out _);
    }

    public void RevokeAll()
    {
        foreach (var key in _sessions.Keys) _sessions.TryRemove(key, out _);
    }

    public int Count => _sessions.Count;
}
