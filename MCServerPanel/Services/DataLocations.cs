namespace MCServerPanel.Services;

/// <summary>数据目录的最终选择结果。</summary>
public sealed record DataDirChoice(string Path, bool UsedFallback, string? Warning);

/// <summary>旧数据的一次性导入结果。</summary>
public sealed record LegacyImport(bool Copied, string? Message, IReadOnlyList<string> Items);

/// <summary>
/// 决定运行时数据（settings.json / auth.json / logs / backups）放在哪。
/// 优先级：
///   1) 启动参数 --data-dir（显式指定，永远优先）；
///   2) 程序所在目录 —— 便携式布局，用户不用去 AppData 里翻文件；
///   3) 程序目录不可写（例如装进 Program Files 且非管理员）时，回退到本机用户数据目录，并在控制台说明原因。
/// </summary>
public static class DataLocations
{
    public const string FolderName = "MCServerPanel";

    private const string ProbePrefix = ".mcpanel-write-probe-";

    /// <summary>程序所在目录（发布成单文件 exe 时就是 exe 所在目录）。</summary>
    public static string ExeDirectory => TrimSeparator(AppContext.BaseDirectory);

    /// <summary>兜底位置；同时也是 1.0.0 及更早版本固定使用的位置。</summary>
    public static string LegacyDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>纯函数版本，便于单测：显式目录 > exeDir > 可写性兜底。</summary>
    public static DataDirChoice Resolve(string? explicitDir, string? exeDir = null, string? fallbackDir = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir))
            return new DataDirChoice(Path.GetFullPath(explicitDir.Trim()), false, null);

        var preferred = TrimSeparator(string.IsNullOrWhiteSpace(exeDir) ? ExeDirectory : exeDir);
        var (writable, why) = TryEnsureWritable(preferred);
        if (writable) return new DataDirChoice(preferred, false, null);

        var fallback = TrimSeparator(string.IsNullOrWhiteSpace(fallbackDir) ? LegacyDirectory : fallbackDir);
        Directory.CreateDirectory(fallback);
        return new DataDirChoice(
            fallback,
            true,
            $"程序目录不可写（{why}），数据改存 {fallback}。可用 --data-dir 指定其它位置。");
    }

    /// <summary>
    /// 把 1.0.0 及更早版本留在 %LOCALAPPDATA%\MCServerPanel 的数据复制到新目录。
    /// 只在「新目录还没有账号/设置」时执行，复制而非移动，旧文件原样保留，失败不阻断启动。
    /// </summary>
    public static LegacyImport TryImportLegacy(string dataDir, string? legacyDir = null)
    {
        try
        {
            var legacy = TrimSeparator(legacyDir ?? LegacyDirectory);
            var target = TrimSeparator(dataDir);

            if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return new LegacyImport(false, null, Array.Empty<string>());
            if (!Directory.Exists(legacy))
                return new LegacyImport(false, null, Array.Empty<string>());
            if (File.Exists(Path.Combine(target, "auth.json")) || File.Exists(Path.Combine(target, "settings.json")))
                return new LegacyImport(false, null, Array.Empty<string>());

            Directory.CreateDirectory(target);
            var items = new List<string>();

            foreach (var name in new[] { "auth.json", "settings.json" })
            {
                var src = Path.Combine(legacy, name);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(target, name), overwrite: false);
                items.Add(name);
            }
            foreach (var sub in new[] { "logs", "backups" })
            {
                var src = Path.Combine(legacy, sub);
                if (!Directory.Exists(src)) continue;
                CopyDirectory(src, Path.Combine(target, sub));
                items.Add(sub + "/");
            }

            if (items.Count == 0)
                return new LegacyImport(false, null, Array.Empty<string>());

            var message = $"已从旧数据目录导入：{string.Join("、", items)}\n" +
                          $"    旧目录仍保留在 {legacy}，确认新目录一切正常后可自行删除。";
            return new LegacyImport(true, message, items);
        }
        catch (Exception ex)
        {
            return new LegacyImport(false, $"旧数据导入失败（不影响启动）：{ex.GetType().Name}: {ex.Message}", Array.Empty<string>());
        }
    }

    private static (bool Ok, string? Why) TryEnsureWritable(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return (false, "程序目录未知");
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ProbePrefix + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static string TrimSeparator(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return string.Empty;
        var trimmed = dir.Trim();
        var root = Path.GetPathRoot(trimmed) ?? string.Empty;
        if (trimmed.Length <= root.Length) return trimmed;      // "C:\" 这类根路径保持原样
        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
