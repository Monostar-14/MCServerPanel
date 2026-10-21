using MCServerPanel.Services;

namespace MCServerPanel.Tests;

/// <summary>
/// 数据目录规则的回归测试：默认放程序目录（不再写 %LOCALAPPDATA%），
/// 不可写时回退并说明原因，旧版数据只导入一次且不覆盖。
/// </summary>
public class DataLocationsTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcsp-data-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    [Fact]
    public void ExplicitDataDirWinsOverProgramDirectory()
    {
        using var explicitDir = new TempDir();
        using var exeDir = new TempDir();
        using var fallback = new TempDir();

        var choice = DataLocations.Resolve(explicitDir.Path, exeDir: exeDir.Path, fallbackDir: fallback.Path);

        Assert.Equal(System.IO.Path.GetFullPath(explicitDir.Path), choice.Path);
        Assert.False(choice.UsedFallback);
        Assert.Null(choice.Warning);
    }

    [Fact]
    public void DefaultsToProgramDirectoryInsteadOfUserProfile()
    {
        using var exeDir = new TempDir();
        using var fallback = new TempDir();

        var choice = DataLocations.Resolve(null, exeDir: exeDir.Path, fallbackDir: fallback.Path);

        Assert.Equal(System.IO.Path.GetFullPath(exeDir.Path), choice.Path);
        Assert.False(choice.UsedFallback);
        Assert.Null(choice.Warning);
        // 关键回归：默认值绝不能是 %LOCALAPPDATA%\MCServerPanel
        Assert.NotEqual(DataLocations.LegacyDirectory, choice.Path);
    }

    [Fact]
    public void TrailingSeparatorIsTrimmed()
    {
        using var exeDir = new TempDir();
        using var fallback = new TempDir();

        var choice = DataLocations.Resolve(null,
            exeDir: exeDir.Path + System.IO.Path.DirectorySeparatorChar,
            fallbackDir: fallback.Path);

        Assert.Equal(System.IO.Path.GetFullPath(exeDir.Path), choice.Path);
        Assert.False(choice.Path.EndsWith('\\') || choice.Path.EndsWith('/'));
    }

    [Fact]
    public void FallsBackWithExplanationWhenProgramDirectoryIsUnwritable()
    {
        using var holder = new TempDir();
        // 拿一个「文件」当程序目录：CreateDirectory/写探针都会失败
        var blocked = holder.Combine("not-a-directory");
        File.WriteAllText(blocked, "x");
        using var fallback = new TempDir();

        var choice = DataLocations.Resolve(null, exeDir: blocked, fallbackDir: fallback.Path);

        Assert.True(choice.UsedFallback);
        Assert.Equal(System.IO.Path.GetFullPath(fallback.Path), choice.Path);
        Assert.False(string.IsNullOrWhiteSpace(choice.Warning));
        Assert.True(Directory.Exists(choice.Path));
    }

    [Fact]
    public void ImportsLegacyDataOnceAndNeverOverwrites()
    {
        using var legacy = new TempDir();
        using var target = new TempDir();

        File.WriteAllText(legacy.Combine("auth.json"), "{\"username\":\"admin\"}");
        File.WriteAllText(legacy.Combine("settings.json"), "{\"port\":8989}");
        Directory.CreateDirectory(legacy.Combine("logs"));
        File.WriteAllText(legacy.Combine("logs", "audit-202610.jsonl"), "{}\n");
        Directory.CreateDirectory(legacy.Combine("backups", "20261021"));
        File.WriteAllText(legacy.Combine("backups", "20261021", "120000000_DemoPlugin_config.yml.bak"), "old");

        var result = DataLocations.TryImportLegacy(target.Path, legacy.Path);

        Assert.True(result.Copied);
        Assert.Equal("{\"username\":\"admin\"}", File.ReadAllText(target.Combine("auth.json")));
        Assert.True(File.Exists(target.Combine("settings.json")));
        Assert.True(File.Exists(target.Combine("logs", "audit-202610.jsonl")));
        Assert.True(File.Exists(target.Combine("backups", "20261021", "120000000_DemoPlugin_config.yml.bak")));
        Assert.NotNull(result.Message);
        // 复制而不是移动：旧目录原样保留
        Assert.True(File.Exists(legacy.Combine("auth.json")));

        File.WriteAllText(target.Combine("auth.json"), "{\"username\":\"second-run\"}");
        var again = DataLocations.TryImportLegacy(target.Path, legacy.Path);

        Assert.False(again.Copied);
        Assert.Equal("{\"username\":\"second-run\"}", File.ReadAllText(target.Combine("auth.json")));
    }

    [Fact]
    public void SilentWhenThereIsNoLegacyDirectory()
    {
        using var target = new TempDir();
        using var holder = new TempDir();
        var missing = holder.Combine("不存在");

        var result = DataLocations.TryImportLegacy(target.Path, missing);

        Assert.False(result.Copied);
        Assert.Null(result.Message);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void BackupStoreWritesUnderDataDirectory()
    {
        using var dataDir = new TempDir();
        using var pluginsRoot = new TempDir();
        var source = pluginsRoot.Combine("DemoPlugin", "config.yml");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "Debug: false\n");
        var bytes = File.ReadAllBytes(source);

        var saved = BackupStore.Save(source, bytes, pluginsRoot.Path, dataDir.Path);

        Assert.NotNull(saved);
        Assert.StartsWith(System.IO.Path.Combine(dataDir.Path, "backups"), saved!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(saved!));
        Assert.Equal(bytes, File.ReadAllBytes(saved!));
        // 备份不再散落到旧版固定位置（%LOCALAPPDATA%\MCServerPanel）
        Assert.False(saved!.StartsWith(DataLocations.LegacyDirectory, StringComparison.OrdinalIgnoreCase));
    }
}
