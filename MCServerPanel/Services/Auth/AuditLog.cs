using System.Text;
using System.Text.Json;

namespace MCServerPanel.Services.Auth;

/// <summary>
/// 追加式审计日志（JSONL，按月切分）。只记录"谁在什么时候对哪个文件做了什么"，
/// 不记录配置值本身，避免把数据库密码等敏感内容写进日志。
/// </summary>
public sealed class AuditLog
{
    private readonly string _dir;
    private readonly object _lock = new();

    public AuditLog(string dataDir)
    {
        _dir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(_dir);
    }

    public void Write(string evt, string? user, string? ip, object? detail = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("O"),
                evt,
                user,
                ip,
                detail
            });
            var path = Path.Combine(_dir, $"audit-{DateTime.UtcNow:yyyyMM}.jsonl");
            lock (_lock)
            {
                var created = !File.Exists(path);
                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    writer.WriteLine(line);
                }
                if (created && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // 审计日志写不进去不能影响主流程
        }
    }
}
