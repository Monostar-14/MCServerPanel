using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCServerPanel.Services.Auth;

/// <summary>
/// 管理员账号存储（auth.json）。首次运行没有账号时生成一次性 setup token，
/// 只能用它完成初始化；之后密码只以 PBKDF2 哈希形式存在。
/// </summary>
public sealed class AuthStore
{
    public sealed class AuthRecord
    {
        public string Username { get; set; } = "admin";
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Iterations { get; set; } = PasswordHasher.DefaultIterations;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _lock = new();

    public AuthRecord? Record { get; private set; }
    public string? SetupToken { get; private set; }
    public bool IsConfigured => Record is not null;
    public string FilePath => _path;

    public AuthStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "auth.json");
        Load();
        if (!IsConfigured) SetupToken = PasswordHasher.GenerateToken();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var record = JsonSerializer.Deserialize<AuthRecord>(File.ReadAllText(_path), JsonOpts);
            if (record is not null && !string.IsNullOrWhiteSpace(record.Hash) && !string.IsNullOrWhiteSpace(record.Salt))
                Record = record;
        }
        catch
        {
            // 文件损坏时视为未配置：仍然需要 setup token 才能重建
            Record = null;
        }
    }

    public static void ValidatePassword(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new PanelException("密码不能为空。");
        if (password.Length < 10) throw new PanelException("密码至少 10 个字符（建议 12 位以上，或使用密码管理器生成）。");
        if (password.Length > 200) throw new PanelException("密码过长（最多 200 个字符）。");
        if (!string.IsNullOrWhiteSpace(username) && password.Equals(username, StringComparison.OrdinalIgnoreCase))
            throw new PanelException("密码不能和用户名相同。");
    }

    public bool Verify(string username, string password)
    {
        lock (_lock)
        {
            var record = Record;
            if (record is null) return false;
            if (!string.Equals(record.Username, username, StringComparison.Ordinal)) return false;
            return PasswordHasher.Verify(password, record.Salt, record.Hash, record.Iterations);
        }
    }

    public bool VerifySetupToken(string? token)
    {
        lock (_lock)
        {
            if (SetupToken is null || string.IsNullOrEmpty(token)) return false;
            var a = Encoding.UTF8.GetBytes(SetupToken);
            var b = Encoding.UTF8.GetBytes(token);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
    }

    public void Setup(string token, string username, string password)
    {
        if (IsConfigured) throw new PanelException("管理员账号已存在，不能重复初始化。", 409);
        if (!VerifySetupToken(token))
            throw new PanelException("初始化令牌不正确。请查看服务器控制台（或 systemd 日志）里打印的一次性令牌。", 403);

        username = string.IsNullOrWhiteSpace(username) ? "admin" : username.Trim();
        if (username.Length > 64) throw new PanelException("用户名过长（最多 64 个字符）。");
        ValidatePassword(username, password);

        var (salt, hash, iterations) = PasswordHasher.Hash(password);
        Record = new AuthRecord { Username = username, Salt = salt, Hash = hash, Iterations = iterations, UpdatedAtUtc = DateTime.UtcNow };
        SetupToken = null;
        Save();
    }

    public void ChangePassword(string currentPassword, string newPassword)
    {
        lock (_lock)
        {
            var record = Record ?? throw new PanelException("还没有初始化管理员账号。", 409);
            if (!PasswordHasher.Verify(currentPassword, record.Salt, record.Hash, record.Iterations))
                throw new PanelException("当前密码不正确。", 403);
            ValidatePassword(record.Username, newPassword);

            var (salt, hash, iterations) = PasswordHasher.Hash(newPassword);
            Record = new AuthRecord
            {
                Username = record.Username,
                Salt = salt,
                Hash = hash,
                Iterations = iterations,
                UpdatedAtUtc = DateTime.UtcNow
            };
            Save();
        }
    }

    /// <summary>忘记密码时由本机命令行调用，不需要旧密码。</summary>
    public void ResetPassword(string newPassword)
    {
        lock (_lock)
        {
            var username = Record?.Username ?? "admin";
            ValidatePassword(username, newPassword);
            var (salt, hash, iterations) = PasswordHasher.Hash(newPassword);
            Record = new AuthRecord { Username = username, Salt = salt, Hash = hash, Iterations = iterations, UpdatedAtUtc = DateTime.UtcNow };
            SetupToken = null;
            Save();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(Record, JsonOpts);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _path, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
