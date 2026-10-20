using System.Security.Cryptography;

namespace MCServerPanel.Services.Auth;

/// <summary>PBKDF2-SHA256 密码哈希。只保存盐和哈希，不保存明文。</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static (string Salt, string Hash, int Iterations) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, KeySize);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(key), DefaultIterations);
    }

    public static bool Verify(string password, string saltBase64, string hashBase64, int iterations)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            if (salt.Length < 8 || expected.Length < 16) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string GenerateToken(int bytes = 24) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
