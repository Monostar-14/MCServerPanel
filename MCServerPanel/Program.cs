using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using MCServerPanel.Models;
using MCServerPanel.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var settingsStore = new SettingsStore();
builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton<PluginCatalog>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<DirectoryBrowser>();
var runtime = new RuntimeInfo();
builder.Services.AddSingleton(runtime);

// 端口来自设置文件（默认 8989）。若被占用则自动向后寻找可用端口。
int preferredPort = settingsStore.Current.Port is > 0 and < 65536 ? settingsStore.Current.Port : SettingsStore.DefaultPort;
string host = settingsStore.Current.AllowLan ? "0.0.0.0" : "127.0.0.1";
int actualPort = FindFreePort(preferredPort, settingsStore.Current.AllowLan);

runtime.ActualPort = actualPort;
runtime.LanEnabled = settingsStore.Current.AllowLan;
runtime.Urls.Add($"http://localhost:{actualPort}");
if (settingsStore.Current.AllowLan)
{
    foreach (var ip in LocalIPv4())
        runtime.Urls.Add($"http://{ip}:{actualPort}");
}

builder.WebHost.UseUrls($"http://{host}:{actualPort}");

var app = builder.Build();

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

app.MapGet("/api/health", () => Results.Ok(new
{
    ok = true,
    port = runtime.ActualPort,
    urls = runtime.Urls,
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0",
    settingsFile = settingsStore.FilePath
}));

app.MapGet("/api/settings", (SettingsStore store, PluginCatalog catalog) =>
{
    var layout = ServerLayout.Resolve(store.Current.ServerPath);
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
        RestartRequired = store.Current.Port != runtime.ActualPort || store.Current.AllowLan != runtime.LanEnabled
    });
});

app.MapPost("/api/settings", (SettingsRequest req, SettingsStore store, PluginCatalog catalog) =>
{
    var layout = req.ServerPath is null ? null : ServerLayout.Resolve(req.ServerPath);
    if (!string.IsNullOrWhiteSpace(req.ServerPath) && (layout is null || layout.PluginsPath is null))
        throw new PanelException(layout?.Error ?? "无法在这个目录下找到插件配置。");

    store.Save(new PanelSettings
    {
        ServerPath = string.IsNullOrWhiteSpace(req.ServerPath) ? null : Path.GetFullPath(req.ServerPath.Trim().Trim('"')),
        Port = req.Port,
        AllowLan = req.AllowLan
    });

    var saved = ServerLayout.Resolve(store.Current.ServerPath);
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
        RestartRequired = store.Current.Port != runtime.ActualPort || store.Current.AllowLan != runtime.LanEnabled
    });
});

app.MapPost("/api/settings/restart", () =>
{
    var exe = Environment.ProcessPath;
    if (string.IsNullOrEmpty(exe)) throw new PanelException("无法确定程序路径，请手动重启面板。");

    var args = Environment.GetCommandLineArgs();
    var isDotnetHost = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    var psi = new ProcessStartInfo
    {
        FileName = exe,
        WorkingDirectory = AppContext.BaseDirectory,
        UseShellExecute = true
    };
    if (isDotnetHost)
    {
        // dotnet run 场景：重新用同一个 dll 启动
        foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
    }

    Process.Start(psi);
    _ = Task.Run(async () =>
    {
        await Task.Delay(400);
        Environment.Exit(0);
    });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/browse", (string? path, DirectoryBrowser browser) => Results.Ok(browser.Browse(path)));

app.MapGet("/api/catalog", (PluginCatalog catalog) => Results.Ok(catalog.Build()));

app.MapGet("/api/config", (string file, ConfigService configs) => Results.Ok(configs.Load(file)));

app.MapPost("/api/config/save", (SaveRequest req, ConfigService configs) => Results.Ok(configs.Save(req)));

app.MapGet("/api/raw", (string file, PluginCatalog catalog) =>
{
    var full = catalog.ResolveConfigPath(file);
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
    Console.WriteLine("  按 Ctrl+C 停止。");
    Console.WriteLine();

    if (!Debugger.IsAttached && !args.Contains("--no-browser"))
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 打不开浏览器不影响使用 */ }
    }
});

app.Run();

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
