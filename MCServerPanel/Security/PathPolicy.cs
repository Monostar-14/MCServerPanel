using MCServerPanel.Services;

namespace MCServerPanel.Security;

/// <summary>
/// 路径边界。所有"从网页传进来的相对路径"都必须先过这里：
/// 拒绝绝对路径、..、盘符/ADS、Windows 保留名，解析符号链接后再确认仍在根目录内。
/// </summary>
public static class PathPolicy
{
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsInside(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root);
        var trimmed = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) trimmed = rootFull; // 根目录（/ 或 C:\）

        if (string.Equals(Path.GetFullPath(candidate), trimmed, Comparison)) return true;
        var prefix = trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, Comparison);
    }

    /// <summary>逐段解析符号链接 / junction，得到真实路径（不存在的尾段原样保留）。</summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;

        var segments = full[root.Length..]
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            for (int hop = 0; hop < 40; hop++)
            {
                try
                {
                    FileSystemInfo info = Directory.Exists(current)
                        ? new DirectoryInfo(current)
                        : new FileInfo(current);
                    if (!info.Exists && !Directory.Exists(current)) break;
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null) break;
                    current = Path.GetFullPath(target.FullName);
                }
                catch
                {
                    break;
                }
            }
        }

        return Path.GetFullPath(current);
    }

    public static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new PanelException("缺少文件参数。");
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new PanelException("非法的文件路径。");

        foreach (var raw in relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Trim();
            if (segment is "." or "..") throw new PanelException("非法的文件路径。");
            if (OperatingSystem.IsWindows())
            {
                if (segment.EndsWith('.') || segment.EndsWith(' '))
                    throw new PanelException("非法的文件名。");
                if (segment.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0)
                    throw new PanelException("非法的文件名。");
                var stem = segment.Split('.')[0];
                if (ReservedNames.Contains(stem)) throw new PanelException("非法的文件名。");
            }
            if (segment.Any(char.IsControl)) throw new PanelException("非法的文件名。");
        }
    }

    /// <summary>把相对路径解析成绝对路径，并保证结果（含符号链接解析后）仍在 root 内。</summary>
    public static string ResolveFileInside(string root, string relative)
    {
        ValidateRelativePath(relative);
        var rootFull = Path.GetFullPath(root);
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(rootFull, normalized));

        if (!IsInside(rootFull, combined)) throw new PanelException("非法的文件路径。");

        var realRoot = RealPath(rootFull);
        var realFile = RealPath(combined);
        if (!IsInside(realRoot, realFile)) throw new PanelException("非法的文件路径（符号链接指向目录之外）。");

        return realFile;
    }
}
