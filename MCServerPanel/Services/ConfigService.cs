using System.Security.Cryptography;
using System.Text;
using MCServerPanel.Models;
using MCServerPanel.Services.Auth;
using MCServerPanel.Yaml;

namespace MCServerPanel.Services;

public static class TextFile
{
    public sealed record Loaded(string Text, Encoding Encoding, bool Bom);

    public static Loaded Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (encoding, bomLength) = Detect(bytes);
        return new Loaded(encoding.GetString(bytes, bomLength, bytes.Length - bomLength), encoding, bomLength > 0);
    }

    public static (Encoding Encoding, int BomLength) Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2);

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return (new UTF8Encoding(false), 0);
        }
        catch (DecoderFallbackException)
        {
            // 老配置常见 GBK 编码（中文注释），按 GB18030 读取并原样写回
            try { return (Encoding.GetEncoding("GB18030"), 0); }
            catch { return (Encoding.Latin1, 0); }
        }
    }

    public static void WriteAtomic(string path, string text, Encoding encoding, bool bom)
    {
        var preamble = bom ? encoding.GetPreamble() : Array.Empty<byte>();
        var body = encoding.GetBytes(text);
        var tmp = path + ".mcpanel.tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(preamble, 0, preamble.Length);
            fs.Write(body, 0, body.Length);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static string Hash(string path) => Hash(File.ReadAllBytes(path));

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class BackupStore
{
    /// <summary>备份跟着数据目录走（默认就是程序目录），不再单独写用户目录。</summary>
    public static string? Save(string sourcePath, byte[] bytes, string? pluginsRoot, string dataDir)
    {
        try
        {
            var dir = Path.Combine(dataDir, "backups", DateTime.Now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dir);

            var rel = pluginsRoot is not null
                ? Path.GetRelativePath(pluginsRoot, sourcePath)
                : Path.GetFileName(sourcePath);
            var safe = rel.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
            var target = Path.Combine(dir, $"{DateTime.Now:HHmmssfff}_{safe}.bak");
            File.WriteAllBytes(target, bytes);
            Prune(dir);
            return target;
        }
        catch
        {
            return null; // 备份失败不应阻断保存，但调用方会记录告警
        }
    }

    private static void Prune(string todayDir)
    {
        try
        {
            var root = Directory.GetParent(todayDir)?.FullName;
            if (root is null) return;
            foreach (var file in Directory.EnumerateFiles(root, "*.bak", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < DateTime.Now.AddDays(-30)) info.Delete();
            }
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                    Directory.Delete(dir);
            }
        }
        catch { }
    }
}

public sealed class ConfigService
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Guards =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly PluginCatalog _catalog;
    private readonly EditSessionRegistry _sessions;
    private readonly AuditLog _audit;

    public ConfigService(PluginCatalog catalog, EditSessionRegistry sessions, AuditLog audit)
    {
        _catalog = catalog;
        _sessions = sessions;
        _audit = audit;
    }

    public ConfigResponse Load(string relativePath)
    {
        var full = _catalog.ResolveConfigPath(relativePath);
        var loaded = TextFile.Read(full);
        var doc = YamlDocumentParser.Parse(loaded.Text);
        return Build(relativePath, full, loaded.Text, doc, TextFile.Hash(File.ReadAllBytes(full)));
    }

    /// <summary>轮询用：返回当前磁盘哈希、是否有外部改动、编辑占用情况。</summary>
    public WatchDto Watch(string relativePath, string? clientId, string? clientName, string? since)
    {
        var full = _catalog.ResolveConfigPath(relativePath);
        var info = new FileInfo(full);
        var hash = TextFile.Hash(full);

        var busy = false;
        LockDto? holder = null;
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            var claim = _sessions.Claim(relativePath, clientId!, clientName ?? "未命名用户", takeover: false);
            if (claim.Ok) _sessions.Renew(clientId!);
            else { busy = true; holder = claim.Holder; }
        }

        return new WatchDto
        {
            Hash = hash,
            Size = info.Length,
            LastWrite = info.LastWriteTime,
            Changed = since is not null && !string.Equals(since, hash, StringComparison.OrdinalIgnoreCase),
            LockBusy = busy,
            Holder = holder,
            Locks = _sessions.Snapshot(clientId)
        };
    }

    public ClaimResultDto Claim(string relativePath, string clientId, string? clientName, bool takeover)
    {
        _catalog.ResolveConfigPath(relativePath);
        var result = _sessions.Claim(relativePath, clientId, clientName ?? "未命名用户", takeover);
        return new ClaimResultDto { Ok = result.Ok, Holder = result.Holder, Previous = result.Previous };
    }

    public void Release(string clientId) => _sessions.Release(clientId);

    public SaveResponse Save(SaveRequest request, string? actor = null, string? ip = null)
    {
        if (request.Edits.Count == 0) throw new PanelException("没有需要保存的改动。");
        var full = _catalog.ResolveConfigPath(request.File);
        var gate = Guards.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var bytes = File.ReadAllBytes(full);
            var hash = TextFile.Hash(bytes);
            if (!request.Force)
            {
                var holder = _sessions.HolderOf(request.File);
                if (holder is not null && !holder.ClientId.Equals(request.ClientId ?? "", StringComparison.Ordinal))
                    throw new PanelException(
                        $"「{holder.ClientName}」正在编辑这个文件（{holder.AgeSeconds} 秒前还有操作），已阻止同时写入。\n" +
                        "请先点「接管编辑」，或等对方保存完再改。", 423);
            }
            if (!request.Force && request.BaseHash is not null && !string.Equals(hash, request.BaseHash, StringComparison.OrdinalIgnoreCase))
                throw new PanelException("这个文件在磁盘上已被修改。请重新加载，确认后再保存。", 409);

            var (encoding, bomLength) = TextFile.Detect(bytes);
            var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var doc = YamlDocumentParser.Parse(text);
            if (doc.Error is not null)
                throw new PanelException($"文件当前有 YAML 错误，无法安全修改：{doc.Error}", 422);

            var editor = new YamlEditor(doc, newline);
            var applied = editor.Apply(request.Edits);
            if (applied.Error is not null)
                throw new PanelException(applied.Error, 422);
            if (applied.NewText is null) throw new PanelException("没有生成新的文件内容。", 500);

            var warnings = new List<string>();
            var backup = BackupStore.Save(full, bytes, _catalog.CurrentLayout.PluginsPath, _catalog.DataDir);
            if (backup is null) warnings.Add("备份旧文件失败，但已写入新配置。");

            TextFile.WriteAtomic(full, applied.NewText, encoding, bomLength > 0);
            if (!string.IsNullOrWhiteSpace(request.ClientId)) _sessions.Renew(request.ClientId!);

            var newBytes = File.ReadAllBytes(full);
            var newHash = TextFile.Hash(newBytes);
            var (enc2, bom2) = TextFile.Detect(newBytes);
            var newText = enc2.GetString(newBytes, bom2, newBytes.Length - bom2);
            var newDoc = YamlDocumentParser.Parse(newText);

            // 落盘后再次校验：解析正常 + 注释一条不少
            if (newDoc.Error is not null)
                throw new PanelException($"写入后文件无法解析，请用备份恢复：{backup}", 500);
            var before = doc.CollectCommentSignature();
            var after = newDoc.CollectCommentSignature();
            if (!before.SequenceEqual(after))
                throw new PanelException($"写入后注释校验失败，请用备份恢复：{backup}", 500);

            warnings.AddRange(applied.Warnings);
            _audit.Write("save", actor, ip, new
            {
                file = request.File,
                keys = request.Edits.Select(e => YamlPath.Display(YamlPath.FromJson(e.Path))).ToArray(),
                oldHash = hash[..12],
                newHash = newHash[..12],
                forced = request.Force,
                backup
            });
            return new SaveResponse
            {
                Config = Build(request.File, full, newText, newDoc, newHash),
                Applied = applied.Applied,
                Warnings = warnings
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private static ConfigResponse Build(string relativePath, string full, string text, YamlDocument doc, string hash)
    {
        var info = new FileInfo(full);
        var response = new ConfigResponse
        {
            File = relativePath.Replace('\\', '/'),
            Name = info.Name,
            Hash = hash,
            Text = text,
            LineCount = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1,
            Size = info.Length,
            LastWrite = info.LastWriteTime,
            Warnings = doc.Warnings.ToList(),
            ParseError = doc.Error,
            ErrorLine = doc.ErrorLine > 0 ? doc.ErrorLine : null
        };

        if (doc.Root is not null) response.Root = NodeDtoFactory.Build(doc.Root);
        return response;
    }
}

public sealed class DirectoryBrowser
{
    public BrowseDto Browse(string? path)
    {
        var dto = new BrowseDto();
        try
        {
            dto.Drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();

            if (string.IsNullOrWhiteSpace(path)) return dto; // 只返回盘符，由前端展示

            string full;
            try { full = Path.GetFullPath(path.Trim().Trim('"')); }
            catch (Exception ex) { dto.Error = "路径无效：" + ex.Message; return dto; }

            if (!Directory.Exists(full)) { dto.Error = "目录不存在：" + full; return dto; }

            dto.Path = full;
            dto.Parent = Directory.GetParent(full)?.FullName;

            foreach (var dir in Directory.EnumerateDirectories(full).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || name.StartsWith('$')) continue;
                FileAttributes attrs;
                try { attrs = File.GetAttributes(dir); } catch { continue; }
                if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)) continue;

                var plugins = Path.Combine(dir, "plugins");
                var hasPlugins = Directory.Exists(plugins);
                dto.Directories.Add(new BrowseEntryDto
                {
                    Name = name,
                    Path = dir,
                    HasPlugins = hasPlugins,
                    IsServerRoot = File.Exists(Path.Combine(dir, "server.properties")),
                    ConfigCount = hasPlugins ? CountYaml(plugins) : 0
                });
            }
        }
        catch (Exception ex)
        {
            dto.Error = ex.Message;
        }
        return dto;
    }

    private static int CountYaml(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Take(500)
                .Count(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }
}

/// <summary>运行期信息：实际监听端口与可访问地址。</summary>
public sealed class RuntimeInfo
{
    public int ActualPort { get; set; }
    public bool LanEnabled { get; set; }
    public List<string> Urls { get; } = new();
}
