using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using MCServerPanel.Models;
using MCServerPanel.Security;
using MCServerPanel.Services;
using MCServerPanel.Services.Auth;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Windows 控制台默认代码页会把中文和一次性令牌打印成乱码
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 某些终端不支持 */ }

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// ---------- 基础组件 ----------
var settingsStore = new SettingsStore(ArgValue(args, "--data-dir"));
var authStore = new AuthStore(settingsStore.DataDir);
var sessions = new SessionStore();
var throttle = new LoginThrottle();
var audit = new AuditLog(settingsStore.DataDir);

// 忘记密码只能在本机命令行重置（不需要旧密码）
if (args.Contains("--reset-password"))
{
    var newPassword = Environment.GetEnvironmentVariable("MCSP_NEW_PASSWORD");
    if (string.IsNullOrEmpty(newPassword))
    {
        Console.Write("新的管理员密码（至少 10 位）: ");
        newPassword = ReadPasswordMasked();
    }
    authStore.ResetPassword(newPassword!);
    Console.WriteLine("密码已重置。请重新启动面板并登录。");
    return;
}

var lockedRoot = ArgValue(args, "--plugins-root") ?? Environment.GetEnvironmentVariable("MCSP_PLUGINS_ROOT");
var testMode = Environment.GetEnvironmentVariable("MCSP_TEST_MODE") == "1";

builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton(authStore);
builder.Services.AddSingleton(sessions);
builder.Services.AddSingleton(throttle);
builder.Services.AddSingleton(audit);
var catalog = new PluginCatalog(settingsStore, lockedRoot);
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<DirectoryBrowser>();
builder.Services.AddSingleton<EditSessionRegistry>();
var runtime = new RuntimeInfo();
builder.Services.AddSingleton(runtime);

// ---------- 监听地址：默认回环；开放到局域网/公网前必须已设置密码（fail closed） ----------
int settingsPort = settingsStore.Current.Port is > 0 and < 65536 ? settingsStore.Current.Port : SettingsStore.DefaultPort;
int preferredPort = int.TryParse(ArgValue(args, "--port"), out var cliPort) && cliPort is > 0 and < 65536
    ? cliPort
    : settingsPort;

bool lanRequested = settingsStore.Current.AllowLan;
bool lanAllowed = lanRequested && authStore.IsConfigured;
string host = lanAllowed ? "0.0.0.0" : "127.0.0.1";
int actualPort = FindFreePort(preferredPort, lanAllowed);

runtime.ActualPort = actualPort;
runtime.LanEnabled = lanAllowed;
runtime.Urls.Add($"http://localhost:{actualPort}");
if (lanAllowed)
{
    foreach (var ip in LocalIPv4())
        runtime.Urls.Add($"http://{ip}:{actualPort}");
}

builder.WebHost.UseUrls($"http://{host}:{actualPort}");

var app = builder.Build();

// ---------- 安全响应头 + 认证/CSRF ----------
app.Use(async (ctx, next) =>
{
    AddSecurityHeaders(ctx);
    await next();
});

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    var mutating = IsMutating(ctx.Request.Method);

    if (path.StartsWithSegments("/api"))
    {
        if (mutating && !HasCsrfHeader(ctx.Request))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "缺少 X-MCSP-CSRF 请求头（防跨站请求伪造）。" });
            return;
        }

        var isPublic = path.StartsWithSegments("/api/auth/login")
                       || path.StartsWithSegments("/api/auth/setup")
                       || path.StartsWithSegments("/api/auth/session")
                       || path.StartsWithSegments("/api/health");

        if (isPublic)
        {
            await next();
            return;
        }

        if (testMode && IsLoopback(ctx))
        {
            ctx.Items["user"] = "test-mode";
            await next();
            return;
        }

        if (sessions.TryValidate(ctx.Request.Cookies[SessionStore.CookieName], out var user))
        {
            ctx.Items["user"] = user;
            await next();
            return;
        }

        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = "未登录或会话已过期。",
            setupRequired = !authStore.IsConfigured
        });
        return;
    }

    // 单页应用入口保护（静态资源本身不含数据，放行）
    if (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
    {
        if (!authStore.IsConfigured && !(testMode && IsLoopback(ctx)))
        {
            ctx.Response.Redirect("/setup.html");
            return;
        }
        if (!(testMode && IsLoopback(ctx)) &&
            !sessions.TryValidate(ctx.Request.Cookies[SessionStore.CookieName], out _))
        {
            ctx.Response.Redirect("/login.html");
            return;
        }
    }

    await next();
});

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (PanelException ex)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.Clear();
            ctx.Response.StatusCode = ex.Status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }
    catch (BadHttpRequestException ex)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                error = "请求内容格式不正确（可能是前端版本与后端不一致，刷新页面重试）：" + ex.Message
            });
            await ctx.Response.WriteAsync(payload);
        }
    }
    catch (Exception ex)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.Clear();
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var detail = ex.Message;
            if (ex.InnerException is not null) detail += " | " + ex.InnerException.Message;
            await ctx.Response.WriteAsJsonAsync(new { error = "服务器内部错误：" + detail });
        }
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------- 认证 API ----------
app.MapGet("/api/auth/session", (HttpContext ctx) =>
{
    if (testMode && IsLoopback(ctx))
        return Results.Ok(new SessionDto
        {
            Authenticated = true,
            SetupRequired = !authStore.IsConfigured,
            Username = authStore.Record?.Username ?? "test-mode",
            TestMode = true
        });

    var authed = sessions.TryValidate(ctx.Request.Cookies[SessionStore.CookieName], out var user);
    return Results.Ok(new SessionDto
    {
        Authenticated = authed,
        SetupRequired = !authStore.IsConfigured,
        Username = authed ? user : null
    });
});

app.MapPost("/api/auth/setup", async (SetupRequest req, HttpContext ctx) =>
{
    var ip = ClientIp(ctx);
    var key = "setup:" + ip;
    if (throttle.IsBlocked(key, out var wait))
        return Results.Json(new { error = $"尝试过于频繁，请 {wait} 秒后再试。", retryAfter = wait }, statusCode: 429);

    try
    {
        authStore.Setup(req.Token, req.Username, req.Password);
    }
    catch (PanelException)
    {
        throttle.RecordFailure(key);
        audit.Write("setup_failed", null, ip);
        throw;
    }

    throttle.Reset(key);
    var token = sessions.Create(authStore.Record!.Username);
    SetSessionCookie(ctx, token);
    audit.Write("setup", authStore.Record.Username, ip);
    return Results.Ok(new SessionDto { Authenticated = true, Username = authStore.Record.Username });
});

app.MapPost("/api/auth/login", async (LoginRequest req, HttpContext ctx) =>
{
    var ip = ClientIp(ctx);
    if (!authStore.IsConfigured)
        return Results.Json(new { error = "还没有初始化管理员账号。", setupRequired = true }, statusCode: 409);

    var ipKey = "ip:" + ip;
    var userKey = "user:" + (req.Username ?? "");
    if (throttle.IsBlocked(ipKey, out var wait) || throttle.IsBlocked(userKey, out wait))
    {
        audit.Write("login_blocked", req.Username, ip, new { retryAfter = wait });
        return Results.Json(new { error = $"尝试过于频繁，请 {wait} 秒后再试。", retryAfter = wait }, statusCode: 429);
    }

    if (!authStore.Verify(req.Username ?? "", req.Password ?? ""))
    {
        throttle.RecordFailure(ipKey);
        throttle.RecordFailure(userKey);
        audit.Write("login_failed", req.Username, ip);
        await Task.Delay(150);
        return Results.Json(new { error = "用户名或密码不正确。" }, statusCode: 401);
    }

    throttle.Reset(ipKey);
    throttle.Reset(userKey);
    var token = sessions.Create(authStore.Record!.Username);
    SetSessionCookie(ctx, token);
    audit.Write("login_ok", authStore.Record.Username, ip);
    return Results.Ok(new SessionDto { Authenticated = true, Username = authStore.Record.Username });
});

app.MapPost("/api/auth/logout", (HttpContext ctx) =>
{
    sessions.Revoke(ctx.Request.Cookies[SessionStore.CookieName]);
    ctx.Response.Cookies.Delete(SessionStore.CookieName, new CookieOptions { Path = "/" });
    audit.Write("logout", ctx.Items["user"] as string, ClientIp(ctx));
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/auth/change-password", (ChangePasswordRequest req, HttpContext ctx) =>
{
    authStore.ChangePassword(req.CurrentPassword, req.NewPassword);
    sessions.RevokeAll();
    var token = sessions.Create(authStore.Record!.Username);
    SetSessionCookie(ctx, token);
    audit.Write("password_changed", authStore.Record.Username, ClientIp(ctx));
    return Results.Ok(new { ok = true });
});

// ---------- 健康检查（未登录只返回最小信息） ----------
app.MapGet("/api/health", (HttpContext ctx) =>
{
    var authed = (testMode && IsLoopback(ctx)) ||
                 sessions.TryValidate(ctx.Request.Cookies[SessionStore.CookieName], out _);
    if (!authed)
        return Results.Ok(new { ok = true, authRequired = true, setupRequired = !authStore.IsConfigured });

    return Results.Ok(new
    {
        ok = true,
        port = runtime.ActualPort,
        urls = runtime.Urls,
        version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0",
        settingsFile = settingsStore.FilePath,
        authConfigured = authStore.IsConfigured,
        pluginsRootLocked = catalog.RootLocked
    });
});

// ---------- 设置 ----------
app.MapGet("/api/settings", (SettingsStore store) =>
{
    var layout = catalog.CurrentLayout;
    return Results.Ok(new SettingsDto
    {
        ServerPath = store.Current.ServerPath,
        PluginsPath = layout.PluginsPath,
        Error = layout.Error,
        Hints = layout.Hints,
        Port = store.Current.Port,
        ActualPort = runtime.ActualPort,
        AllowLan = store.Current.AllowLan,
        Urls = runtime.Urls,
        SettingsFile = store.FilePath,
        AuthConfigured = authStore.IsConfigured,
        PluginsRootLocked = catalog.RootLocked,
        RestartRequired = store.Current.Port != runtime.ActualPort || store.Current.AllowLan != runtime.LanEnabled
    });
});

app.MapPost("/api/settings", (SettingsRequest req, SettingsStore store, HttpContext ctx) =>
{
    if (catalog.RootLocked && !string.IsNullOrWhiteSpace(req.ServerPath) &&
        !string.Equals(Path.GetFullPath(req.ServerPath.Trim().Trim('"')), catalog.LockedRoot, PathPolicy.Comparison))
        throw new PanelException("插件根目录已由启动参数固定，网页中不能修改。", 400);

    if (req.AllowLan && !authStore.IsConfigured)
        throw new PanelException("开放局域网/公网访问前必须先设置管理员密码。", 400);

    var layout = req.ServerPath is null ? null : ServerLayout.Resolve(req.ServerPath);
    if (!string.IsNullOrWhiteSpace(req.ServerPath) && (layout is null || layout.PluginsPath is null))
        throw new PanelException(layout?.Error ?? "无法在这个目录下找到插件配置。");

    store.Save(new PanelSettings
    {
        ServerPath = string.IsNullOrWhiteSpace(req.ServerPath) ? null : Path.GetFullPath(req.ServerPath.Trim().Trim('"')),
        Port = req.Port,
        AllowLan = req.AllowLan
    });

    audit.Write("settings_changed", ctx.Items["user"] as string, ClientIp(ctx),
        new { port = req.Port, allowLan = req.AllowLan, serverPath = store.Current.ServerPath });

    var saved = catalog.CurrentLayout;
    return Results.Ok(new SettingsDto
    {
        ServerPath = store.Current.ServerPath,
        PluginsPath = saved.PluginsPath,
        Error = saved.Error,
        Hints = saved.Hints,
        Port = store.Current.Port,
        ActualPort = runtime.ActualPort,
        AllowLan = store.Current.AllowLan,
        Urls = runtime.Urls,
        SettingsFile = store.FilePath,
        AuthConfigured = authStore.IsConfigured,
        PluginsRootLocked = catalog.RootLocked,
        RestartRequired = store.Current.Port != runtime.ActualPort || store.Current.AllowLan != runtime.LanEnabled
    });
});

app.MapPost("/api/settings/restart", (HttpContext ctx) =>
{
    var exe = Environment.ProcessPath;
    if (string.IsNullOrEmpty(exe)) throw new PanelException("无法确定程序路径，请手动重启面板。");

    audit.Write("restart", ctx.Items["user"] as string, ClientIp(ctx));

    var args = Environment.GetCommandLineArgs();
    var isDotnetHost = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    var psi = new ProcessStartInfo
    {
        FileName = exe,
        WorkingDirectory = AppContext.BaseDirectory,
        UseShellExecute = true
    };
    if (isDotnetHost) psi.ArgumentList.Add(args[0]);   // dotnet <dll> ...
    foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);

    Process.Start(psi);
    _ = Task.Run(async () =>
    {
        await Task.Delay(400);
        Environment.Exit(0);
    });
    return Results.Ok(new { ok = true });
});

// ---------- 目录与配置 ----------
app.MapGet("/api/browse", (string? path, DirectoryBrowser browser) =>
{
    if (catalog.RootLocked)
        throw new PanelException("插件根目录已固定，目录浏览已关闭。", 403);
    return Results.Ok(browser.Browse(path));
});

app.MapGet("/api/catalog", (PluginCatalog plugins) => Results.Ok(plugins.Build()));

app.MapGet("/api/config", (string file, ConfigService configs) => Results.Ok(configs.Load(file)));

app.MapPost("/api/config/save", (SaveRequest req, ConfigService configs, HttpContext ctx) =>
    Results.Ok(configs.Save(req, ctx.Items["user"] as string, ClientIp(ctx))));

// —— 多人协作：编辑占用 + 磁盘哈希检测 ——
app.MapGet("/api/config/watch", (string file, string? clientId, string? clientName, string? since, ConfigService configs) =>
    Results.Ok(configs.Watch(file, clientId, clientName, since)));

app.MapPost("/api/config/claim", (ClaimRequest req, ConfigService configs, HttpContext ctx) =>
{
    var res = configs.Claim(req.File, req.ClientId, req.ClientName, req.Takeover);
    if (req.Takeover)
        audit.Write("claim_takeover", ctx.Items["user"] as string, ClientIp(ctx),
            new { file = req.File, previous = res.Previous?.ClientName });
    return Results.Ok(res);
});

app.MapPost("/api/config/release", (ReleaseRequest req, ConfigService configs) =>
{
    configs.Release(req.ClientId);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/locks", (string? clientId, EditSessionRegistry registry) =>
    Results.Ok(registry.Snapshot(clientId)));

app.MapGet("/api/raw", (string file, PluginCatalog plugins) =>
{
    var full = plugins.ResolveConfigPath(file);
    var loaded = TextFile.Read(full);
    return Results.Text(loaded.Text, "text/plain; charset=utf-8");
});

app.MapFallbackToFile("index.html");

app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = $"http://localhost:{runtime.ActualPort}";
    Console.WriteLine();
    Console.WriteLine("  MCServerPanel 已启动");
    Console.WriteLine($"  本机访问：{url}");
    foreach (var extra in runtime.Urls.Skip(1)) Console.WriteLine($"  局域网访问：{extra}");
    Console.WriteLine($"  设置文件：{settingsStore.FilePath}");
    Console.WriteLine($"  插件根：{(catalog.RootLocked ? catalog.LockedRoot : settingsStore.Current.ServerPath ?? "(未设置)")}");

    if (!authStore.IsConfigured && authStore.SetupToken is { } token)
    {
        Console.WriteLine();
        Console.WriteLine("  ⚠ 还没有管理员密码。请打开 /setup.html，用下面的一次性令牌初始化：");
        Console.WriteLine($"    初始化令牌：{token}");
        Console.WriteLine("    （令牌只在本次进程内有效，重启会重新生成）");
    }
    if (lanRequested && !lanAllowed)
    {
        Console.WriteLine();
        Console.WriteLine("  ⚠ 设置里要求开放局域网，但还没有设置管理员密码：已强制只监听 127.0.0.1。");
        Console.WriteLine("    请先完成初始化并登录，再在设置里打开局域网访问。");
    }
    if (testMode)
        Console.WriteLine("  ⚠ MCSP_TEST_MODE=1：仅限自动化测试，回环请求会跳过登录校验。请勿在生产使用。");
    Console.WriteLine("  按 Ctrl+C 停止。");
    Console.WriteLine();

    if (!Debugger.IsAttached && !args.Contains("--no-browser") && OperatingSystem.IsWindows())
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 打不开浏览器不影响使用 */ }
    }
});

app.Run();

// ---------- 本地函数 ----------
static string? ArgValue(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

static bool IsMutating(string method) =>
    HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
    HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

static bool HasCsrfHeader(HttpRequest request) =>
    request.Headers.TryGetValue("X-MCSP-CSRF", out var v) && v == "1";

static bool IsLoopback(HttpContext ctx)
{
    var ip = ctx.Connection.RemoteIpAddress;
    return ip is null || IPAddress.IsLoopback(ip);
}

static string ClientIp(HttpContext ctx)
{
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is not null && IPAddress.IsLoopback(remote) &&
        ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var fwd))
    {
        var first = fwd.ToString().Split(',')[0].Trim();
        if (IPAddress.TryParse(first, out var parsed)) return parsed.ToString();
    }
    return remote?.ToString() ?? "unknown";
}

static void SetSessionCookie(HttpContext ctx, string token)
{
    var secure = ctx.Request.IsHttps ||
                 (IsLoopback(ctx) && ctx.Request.Headers["X-Forwarded-Proto"] == "https");
    ctx.Response.Cookies.Append(SessionStore.CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true
    });
}

static void AddSecurityHeaders(HttpContext ctx)
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; connect-src 'self'; font-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
}

static string ReadPasswordMasked()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var sb = new StringBuilder();
    ConsoleKeyInfo key;
    while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0) sb.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

static int FindFreePort(int preferred, bool lan)
{
    var address = lan ? IPAddress.Any : IPAddress.Loopback;
    for (int port = preferred; port < Math.Min(preferred + 40, 65535); port++)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return port;
        }
        catch (SocketException) { }
    }
    return preferred;
}

static IEnumerable<string> LocalIPv4()
{
    var result = new List<string>();
    try
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = addr.Address.ToString();
                if (ip.StartsWith("169.254.")) continue;
                result.Add(ip);
            }
        }
    }
    catch { }
    return result.Distinct();
}
