using MCServerPanel.Services.Auth;

namespace MCServerPanel.Tests;

public class AuthTests
{
    [Fact]
    public void PasswordHashRoundtrip()
    {
        var (salt, hash, iterations) = PasswordHasher.Hash("correct horse battery staple");
        Assert.Equal(PasswordHasher.DefaultIterations, iterations);
        Assert.True(PasswordHasher.Verify("correct horse battery staple", salt, hash, iterations));
        Assert.False(PasswordHasher.Verify("wrong password", salt, hash, iterations));
        Assert.False(PasswordHasher.Verify("", salt, hash, iterations));
    }

    [Fact]
    public void PasswordHashesUseUniqueSalts()
    {
        var a = PasswordHasher.Hash("same-password-123");
        var b = PasswordHasher.Hash("same-password-123");
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.True(PasswordHasher.Verify("same-password-123", a.Salt, a.Hash, a.Iterations));
        Assert.True(PasswordHasher.Verify("same-password-123", b.Salt, b.Hash, b.Iterations));
    }

    [Fact]
    public void VerifyRejectsMalformedHash()
    {
        Assert.False(PasswordHasher.Verify("x", "not-base64", "not-base64", 1000));
        Assert.False(PasswordHasher.Verify("x", "", "", 0));
    }

    [Fact]
    public void SessionValidatesAndExpiresByIdle()
    {
        var store = new SessionStore(TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(10));
        var token = store.Create("admin");
        Assert.True(store.TryValidate(token, out var user));
        Assert.Equal("admin", user);

        Thread.Sleep(140);
        Assert.False(store.TryValidate(token, out _));
    }

    [Fact]
    public void SessionExpiresAbsolutelyEvenWhenActive()
    {
        var store = new SessionStore(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(150));
        var token = store.Create("admin");
        for (int i = 0; i < 3; i++)
        {
            Thread.Sleep(60);
            store.TryValidate(token, out _);
        }
        Thread.Sleep(120);
        Assert.False(store.TryValidate(token, out _));
    }

    [Fact]
    public void RevokeAllInvalidatesEverySession()
    {
        var store = new SessionStore();
        var a = store.Create("admin");
        var b = store.Create("admin");
        store.RevokeAll();
        Assert.False(store.TryValidate(a, out _));
        Assert.False(store.TryValidate(b, out _));
    }

    [Fact]
    public void TokenIsNotGuessedByEmptyValue()
    {
        var store = new SessionStore();
        Assert.False(store.TryValidate(null, out _));
        Assert.False(store.TryValidate("", out _));
        Assert.False(store.TryValidate("made-up-token", out _));
    }

    [Fact]
    public void ThrottleBlocksAfterRepeatedFailures()
    {
        var throttle = new LoginThrottle(maxFailures: 3, window: TimeSpan.FromSeconds(5), block: TimeSpan.FromMilliseconds(200));
        for (int i = 0; i < 3; i++)
        {
            Assert.False(throttle.IsBlocked("ip:1.2.3.4", out _));
            throttle.RecordFailure("ip:1.2.3.4");
        }

        Assert.True(throttle.IsBlocked("ip:1.2.3.4", out var retry));
        Assert.True(retry >= 1);

        Thread.Sleep(260);
        Assert.False(throttle.IsBlocked("ip:1.2.3.4", out _));
    }

    [Fact]
    public void ThrottleSuccessResetsCounter()
    {
        var throttle = new LoginThrottle(maxFailures: 2, window: TimeSpan.FromMinutes(5));
        throttle.RecordFailure("u:admin");
        throttle.RecordFailure("u:admin");
        Assert.True(throttle.IsBlocked("u:admin", out _));

        throttle.Reset("u:admin");
        Assert.False(throttle.IsBlocked("u:admin", out _));
    }

    [Fact]
    public void PasswordPolicyRejectsWeakInput()
    {
        Assert.Throws<MCServerPanel.Services.PanelException>(() => AuthStore.ValidatePassword("admin", "short"));
        Assert.Throws<MCServerPanel.Services.PanelException>(() => AuthStore.ValidatePassword("admin", "admin"));
        AuthStore.ValidatePassword("admin", "a-long-enough-password");
    }

    [Fact]
    public void SetupRequiresTheOneTimeToken()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcsp-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AuthStore(dir);
            Assert.False(store.IsConfigured);
            Assert.NotNull(store.SetupToken);

            Assert.Throws<MCServerPanel.Services.PanelException>(() =>
                store.Setup("wrong-token", "admin", "a-long-enough-password"));

            store.Setup(store.SetupToken!, "admin", "a-long-enough-password");
            Assert.True(store.IsConfigured);
            Assert.True(store.Verify("admin", "a-long-enough-password"));
            Assert.False(store.Verify("admin", "wrong"));

            var reloaded = new AuthStore(dir);
            Assert.True(reloaded.IsConfigured);
            Assert.True(reloaded.Verify("admin", "a-long-enough-password"));
            Assert.Null(reloaded.SetupToken);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
