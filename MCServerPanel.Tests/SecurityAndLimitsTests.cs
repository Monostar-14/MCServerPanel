using System.Text;
using MCServerPanel.Security;
using MCServerPanel.Services;
using MCServerPanel.Yaml;

namespace MCServerPanel.Tests;

public class SecurityAndLimitsTests
{
    [Theory]
    [InlineData("Plugin/config.yml")]
    [InlineData("Plugin/sub/dir/config.yml")]
    [InlineData("config.yml")]
    public void AcceptsNormalRelativePaths(string relative)
    {
        PathPolicy.ValidateRelativePath(relative);
    }

    [Theory]
    [InlineData("../secret.yml")]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("Plugin/../../outside.yml")]
    public void RejectsTraversalAndAbsolutePaths(string relative)
    {
        Assert.Throws<PanelException>(() => PathPolicy.ValidateRelativePath(relative));
    }

    [Fact]
    public void RejectsAlternateDataStreamsOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<PanelException>(() => PathPolicy.ValidateRelativePath("config.yml:hidden"));
        Assert.Throws<PanelException>(() => PathPolicy.ValidateRelativePath("CON.yml"));
    }

    [Fact]
    public void ResolveFileInsideKeepsFilesUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsp-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Plugin"));
        var file = Path.Combine(root, "Plugin", "config.yml");
        File.WriteAllText(file, "a: 1\n");
        try
        {
            var resolved = PathPolicy.ResolveFileInside(root, "Plugin/config.yml");
            Assert.True(PathPolicy.IsInside(root, resolved));
            Assert.Throws<PanelException>(() => PathPolicy.ResolveFileInside(root, "../outside.yml"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void ResolveFileInsideRejectsSymlinkEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsp-root-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "mcsp-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.yml");
        File.WriteAllText(secret, "password: hunter2\n");
        var link = Path.Combine(root, "link.yml");

        try
        {
            try { File.CreateSymbolicLink(link, secret); }
            catch (Exception) { return; } // Windows 未开开发者模式时跳过

            Assert.Throws<PanelException>(() => PathPolicy.ResolveFileInside(root, "link.yml"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void RejectsTooDeeplyNestedYaml()
    {
        var sb = new StringBuilder("root: ");
        for (int i = 0; i < 260; i++) sb.Append("{a: ");
        sb.Append('1');
        for (int i = 0; i < 260; i++) sb.Append('}');

        var doc = YamlDocumentParser.Parse(sb.ToString());
        Assert.NotNull(doc.Error);
        Assert.Contains("嵌套", doc.Error);
    }

    [Fact]
    public void DuplicateKeysFollowYamlLastWinsAndWarn()
    {
        var doc = YamlDocumentParser.Parse("key: first\nother: 1\nkey: second\n");
        Assert.Null(doc.Error);
        var node = doc.Find(new object[] { "key" });
        Assert.NotNull(node);
        Assert.Equal("second", node!.DecodedValue);
        Assert.Contains(doc.Warnings, w => w.Contains("重复键"));
        Assert.Single(doc.Root!.Children.Where(c => c.Key == "key"));
    }

    [Fact]
    public void DeepButReasonableYamlStillParses()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++) sb.Append(new string(' ', i * 2)).Append("k").Append(i).Append(":\n");
        sb.Append(new string(' ', 50 * 2)).Append("leaf: 1\n");
        var doc = YamlDocumentParser.Parse(sb.ToString());
        Assert.Null(doc.Error);
    }
}
