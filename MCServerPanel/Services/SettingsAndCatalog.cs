using System.Text;
using System.Text.RegularExpressions;
using MCServerPanel.Models;
using MCServerPanel.Security;

namespace MCServerPanel.Services;

public sealed class PanelException : Exception
{
    public int Status { get; }
    public PanelException(string message, int status = 400) : base(message) => Status = status;
}

public sealed class PanelSettings
{
    public string? ServerPath { get; set; }
    public int Port { get; set; } = 8989;
    /// <summary>默认只监听回环；需要局域网/公网访问时必须显式打开，且必须已设置密码。</summary>
    public bool AllowLan { get; set; }
}

public sealed class SettingsStore
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string FilePath { get; }
    public string DataDir { get; }
    /// <summary>目录是怎么选出来的（含不可写兜底时的提示）。</summary>
    public DataDirChoice Location { get; }
    public PanelSettings Current { get; private set; } = new();

    public SettingsStore(string? dataDir = null)
    {
        // 默认放程序目录，避免用户去 %LOCALAPPDATA% 找；--data-dir 显式指定时以参数为准。
        Location = DataLocations.Resolve(dataDir);
        var dir = Location.Path;
        Directory.CreateDirectory(dir);
        DataDir = dir;
        FilePath = System.IO.Path.Combine(dir, "settings.json");
        Current = Load();
    }

    public static int DefaultPort => 8989;

    private PanelSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = System.Text.Json.JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s is not null) return s;
            }
        }
        catch { /* 配置损坏时用默认值，面板仍可启动 */ }
        return new PanelSettings();
    }

    public void Save(PanelSettings settings)
    {
        if (settings.Port is < 1 or > 65535) throw new PanelException("端口必须在 1–65535 之间。");
        Current = settings;
        File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(settings, JsonOpts), Encoding.UTF8);
    }
}

public sealed record Layout(string? ServerRoot, string? PluginsPath, string? Error, List<string> Hints);

public static class ServerLayout
{
    public static Layout Resolve(string? input)
    {
        var hints = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return new Layout(null, null, "还没有设置服务端目录。", hints);

        string full;
        try
        {
            var cleaned = input.Trim().Trim('"');
            full = Path.GetFullPath(cleaned);
        }
        catch (Exception ex)
        {
            return new Layout(null, null, $"路径无效：{ex.Message}", hints);
        }

        if (!Directory.Exists(full))
            return new Layout(null, null, $"目录不存在：{full}", hints);

        // 尽量给出「这是不是 MC 服务端」的线索，方便用户确认选对了目录
        if (File.Exists(Path.Combine(full, "server.properties")) ||
            File.Exists(Path.Combine(full, "bukkit.yml")) ||
            File.Exists(Path.Combine(full, "spigot.yml")) ||
            File.Exists(Path.Combine(full, "paper-global.yml")))
            hints.Add("已识别为 Minecraft 服务端根目录");

        var candidates = new List<string>();
        var selfName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(selfName, "plugins", StringComparison.OrdinalIgnoreCase) && HasPluginFiles(full))
            candidates.Add(full);

        var pluginsSub = Path.Combine(full, "plugins");
        if (Directory.Exists(pluginsSub)) candidates.Add(pluginsSub);

        if (candidates.Count == 0 && HasPluginFiles(full))
        {
            candidates.Add(full);
            hints.Add("所选目录直接被当作插件配置目录");
        }

        if (candidates.Count == 0)
            return new Layout(full, null, "这个目录里既没有 plugins 文件夹，也没有 .jar/.yml 插件文件。", hints);

        var pluginsPath = Path.GetFullPath(candidates[0]);
        var root = pluginsPath.Equals(Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(pluginsPath)
            : full;

        if (root is not null && File.Exists(Path.Combine(root, "server.properties")))
            hints.Add("服务端正在运行时，保存配置后需要重载插件或重启服务端才会生效");

        return new Layout(root, pluginsPath, null, hints);
    }

    private static bool HasPluginFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.jar").Any()
                   || Directory.EnumerateFiles(dir, "*.yml").Any()
                   || Directory.EnumerateFiles(dir, "*.yaml").Any();
        }
        catch
        {
            return false;
        }
    }
}

public sealed class PluginCatalog
{
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "backup", "backups", "old", "old_configs", "trash", ".git", "logs", "lib", "cache"
    };

    private static readonly Regex VersionRegex = new(@"-(\d[0-9A-Za-z.\-+]*)\.jar$", RegexOptions.Compiled);

    private readonly SettingsStore _settings;
    private readonly string? _lockedRoot;

    public PluginCatalog(SettingsStore settings, string? lockedPluginsRoot = null)
    {
        _settings = settings;
        _lockedRoot = string.IsNullOrWhiteSpace(lockedPluginsRoot) ? null : Path.GetFullPath(lockedPluginsRoot);
    }

    /// <summary>是否由启动参数/环境变量固定了插件根（固定后网页不能再改）。</summary>
    public bool RootLocked => _lockedRoot is not null;
    public string? LockedRoot => _lockedRoot;

    /// <summary>运行时数据目录（备份跟着它走，不另外散落在别处）。</summary>
    public string DataDir => _settings.DataDir;

    public Layout CurrentLayout
    {
        get
        {
            if (_lockedRoot is not null)
            {
                var hints = new List<string> { "插件根由启动配置固定，网页中不可更改" };
                return new Layout(Path.GetDirectoryName(_lockedRoot), _lockedRoot, null, hints);
            }
            return ServerLayout.Resolve(_settings.Current.ServerPath);
        }
    }

    public CatalogDto Build()
    {
        var layout = CurrentLayout;
        if (layout.PluginsPath is null)
            throw new PanelException(layout.Error ?? "无法定位 plugins 目录。", 409);

        var root = layout.PluginsPath;
        var result = new CatalogDto
        {
            ServerPath = layout.ServerRoot,
            PluginsPath = root,
            Hints = layout.Hints
        };

        var jars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var jar in Directory.EnumerateFiles(root, "*.jar"))
                jars[Path.GetFileNameWithoutExtension(jar)] = jar;
        }
        catch { /* 权限不足时忽略 */ }

        var groups = new Dictionary<string, PluginGroupDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateConfigFiles(root))
        {
            var rel = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            var segments = rel.Split('/');
            var groupName = segments.Length == 1 ? "(服务端通用)" : segments[0];
            var dirName = segments.Length == 1 ? "" : segments[0];

            if (!groups.TryGetValue(groupName, out var group))
            {
                var jar = FindJar(jars, groupName, out var version);
                group = new PluginGroupDto { Name = groupName, Jar = jar, Version = version };
                groups[groupName] = group;
            }

            group.Files.Add(new ConfigFileDto
            {
                RelativePath = rel,
                Name = file.Name,
                DirectoryName = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "",
                Size = file.Length,
                LastWrite = file.LastWriteTime,
                IsMain = IsMainConfig(file.Name)
            });
        }

        foreach (var group in groups.Values)
        {
            group.Files = group.Files
                .OrderBy(f => f.IsMain ? 0 : 1)
                .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.Plugins.Add(group);
        }

        result.Plugins = result.Plugins
            .OrderBy(g => g.Name == "(服务端通用)" ? 0 : 1)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.FileCount = result.Plugins.Sum(p => p.Files.Count);
        return result;
    }

    private static string? FindJar(Dictionary<string, string?> jars, string group, out string? version)
    {
        version = null;
        foreach (var (name, path) in jars)
        {
            if (!name.StartsWith(group, StringComparison.OrdinalIgnoreCase)) continue;
            var m = VersionRegex.Match(name + ".jar");
            if (m.Success) version = m.Groups[1].Value;
            return path is null ? null : Path.GetFileName(path);
        }
        return null;
    }

    private static bool IsMainConfig(string name) =>
        name.Equals("config.yml", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("config.yaml", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<FileInfo> EnumerateConfigFiles(string root)
    {
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root).EnumerateFiles("*.*", SearchOption.AllDirectories)
                .Where(f => IsYaml(f.Extension));
        }
        catch
        {
            yield break;
        }

        int scanned = 0;
        foreach (var file in files)
        {
            if (++scanned > 5000) yield break;
            if (scanned > 2000) yield break; // 防止超大 plugins 目录拖垮页面
            var rel = Path.GetRelativePath(root, file.FullName);
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Length - 1 > 3) continue;
            if (segments.Take(segments.Length - 1).Any(s => SkipFolders.Contains(s) || s.StartsWith('.'))) continue;
            if (file.Length > 4 * 1024 * 1024) continue;
            try
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch { continue; }
            yield return file;
        }
    }

    private static bool IsYaml(string ext) =>
        ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>把网页传来的相对路径解析成绝对路径，并确保它没有跳出 plugins 目录（含符号链接）。</summary>
    public string ResolveConfigPath(string relativePath)
    {
        var layout = CurrentLayout;
        if (layout.PluginsPath is null) throw new PanelException(layout.Error ?? "无法定位 plugins 目录。", 409);

        var full = PathPolicy.ResolveFileInside(layout.PluginsPath, relativePath);
        if (!IsYaml(Path.GetExtension(full))) throw new PanelException("只能编辑 .yml / .yaml 文件。");
        if (!File.Exists(full)) throw new PanelException("文件不存在：" + relativePath, 404);
        return full;
    }
}
