using System.Text.Json;
using MCServerPanel.Yaml;

namespace MCServerPanel.Tests;

public class YamlEditorTests
{
    private const string Sample = """
# SnowyGems 主配置
# 这两行中文注释必须原样保留
settings:
  enabled: true        # 是否启用宝石系统
  max-gems: 64
  drop-rate: 0.25
  prefix: "&b[宝石] &r"  # 聊天前缀
  legacy: yes
  worlds:
    - world
    - world_nether
  messages:
    welcome: |
      欢迎来到服务器
      第二行
  nothing:
value: plain-text       # 普通字符串
""";

    private static YamlDocument Doc(string text) => YamlDocumentParser.Parse(text);

    private static YamlEditRequest Edit(object[] path, string type, object? value, string? expectedRaw = null) => new()
    {
        Path = path.Select(p => p is int i ? JsonSerializer.SerializeToElement(i) : JsonSerializer.SerializeToElement((string)p)).ToList(),
        Type = type,
        Value = JsonSerializer.SerializeToElement(value),
        ExpectedRaw = expectedRaw
    };

    private static YamlNode Node(YamlDocument doc, params object[] path) =>
        doc.Find(path) ?? throw new Xunit.Sdk.XunitException("找不到节点：" + string.Join("/", path));

    private static string Apply(string text, params YamlEditRequest[] edits)
    {
        var doc = Doc(text);
        Assert.Null(doc.Error);
        var result = new YamlEditor(doc, "\n").Apply(edits);
        Assert.Null(result.Error);
        Assert.NotNull(result.NewText);
        return result.NewText!;
    }

    [Fact]
    public void ParsesTypesAndComments()
    {
        var doc = Doc(Sample);
        Assert.Null(doc.Error);
        Assert.Equal(ScalarType.Bool, Node(doc, "settings", "enabled").Type);
        Assert.Equal(ScalarType.Int, Node(doc, "settings", "max-gems").Type);
        Assert.Equal(ScalarType.Float, Node(doc, "settings", "drop-rate").Type);
        Assert.Equal(ScalarType.String, Node(doc, "settings", "prefix").Type);
        Assert.Equal(ScalarType.Bool, Node(doc, "settings", "legacy").Type);
        Assert.Equal(ScalarType.Null, Node(doc, "settings", "nothing").Type);
        Assert.Equal("literal", Node(doc, "settings", "messages", "welcome").Style);
        Assert.Equal("# 是否启用宝石系统", Node(doc, "settings", "enabled").Comment);
        Assert.Equal("# 聊天前缀", Node(doc, "settings", "prefix").Comment);
        Assert.Equal("&b[宝石] &r", Node(doc, "settings", "prefix").DecodedValue);
        Assert.Equal("欢迎来到服务器\n第二行\n", Node(doc, "settings", "messages", "welcome").DecodedValue);
        Assert.True(doc.CollectCommentSignature().Count >= 5);
    }

    [Fact]
    public void EditsBoolAndKeepsInlineComment()
    {
        var doc = Doc(Sample);
        var outText = Apply(Sample, Edit(new object[] { "settings", "enabled" }, "bool", false,
            Node(doc, "settings", "enabled").RawValue));

        Assert.Contains("  enabled: false        # 是否启用宝石系统", outText);
        Assert.Contains("# 这两行中文注释必须原样保留", outText);
        var re = Doc(outText);
        Assert.Null(re.Error);
        Assert.Equal("false", Node(re, "settings", "enabled").DecodedValue);
        Assert.Equal(doc.CollectCommentSignature(), re.CollectCommentSignature());
    }

    [Fact]
    public void EditsIntAndFloat()
    {
        var doc = Doc(Sample);
        var outText = Apply(Sample,
            Edit(new object[] { "settings", "max-gems" }, "int", 128, Node(doc, "settings", "max-gems").RawValue),
            Edit(new object[] { "settings", "drop-rate" }, "float", 0.05, Node(doc, "settings", "drop-rate").RawValue));

        var re = Doc(outText);
        Assert.Equal(ScalarType.Int, Node(re, "settings", "max-gems").Type);
        Assert.Equal(128L, long.Parse(Node(re, "settings", "max-gems").DecodedValue!));
        Assert.Equal(0.05, double.Parse(Node(re, "settings", "drop-rate").DecodedValue!));
        Assert.Contains("  prefix: \"&b[宝石] &r\"  # 聊天前缀", outText);
    }

    [Fact]
    public void EditsQuotedStringAndEscapesWhenNeeded()
    {
        var doc = Doc(Sample);
        var outText = Apply(Sample, Edit(new object[] { "settings", "prefix" }, "string", "a: b # 不是注释",
            Node(doc, "settings", "prefix").RawValue));

        var re = Doc(outText);
        Assert.Equal("a: b # 不是注释", Node(re, "settings", "prefix").DecodedValue);
        Assert.Contains("# 聊天前缀", outText);
    }

    [Fact]
    public void KeepsYesNoFamilyWhenToggling()
    {
        var doc = Doc(Sample);
        var outText = Apply(Sample, Edit(new object[] { "settings", "legacy" }, "bool", false,
            Node(doc, "settings", "legacy").RawValue));

        Assert.Contains("  legacy: no", outText);
        Assert.DoesNotContain("legacy: false", outText);
        Assert.Equal("no", Node(Doc(outText), "settings", "legacy").DecodedValue);
    }

    [Fact]
    public void EditsSequenceItemByIndex()
    {
        var doc = Doc(Sample);
        var outText = Apply(Sample, Edit(new object[] { "settings", "worlds", 1 }, "string", "world_the_end",
            Node(doc, "settings", "worlds", 1).RawValue));

        Assert.Contains("    - world\n    - world_the_end", outText);
        Assert.Contains("    - world\n", outText);
        Assert.Equal("world_the_end", Node(Doc(outText), "settings", "worlds", 1).DecodedValue);
    }

    [Fact]
    public void EditsBlockScalarPreservingIndentAndOtherContent()
    {
        var doc = Doc(Sample);
        var outText = Apply(Sample, Edit(new object[] { "settings", "messages", "welcome" }, "block",
            "新的第一行\n新的第二行\n第三行", Node(doc, "settings", "messages", "welcome").RawValue));

        var re = Doc(outText);
        var block = Node(re, "settings", "messages", "welcome");
        Assert.Equal("新的第一行\n新的第二行\n第三行\n", block.DecodedValue);
        Assert.Contains("    welcome: |\n", outText);
        Assert.Contains("      新的第一行\n      新的第二行\n      第三行\n", outText);
        Assert.Contains("  nothing:\n", outText);
        Assert.Contains("# 普通字符串", outText);
    }

    [Fact]
    public void WritesIntoEmptyValue()
    {
        var doc = Doc(Sample);
        var outText = Apply(Sample, Edit(new object[] { "settings", "nothing" }, "string", "hello",
            Node(doc, "settings", "nothing").RawValue));

        Assert.Contains("  nothing: hello\n", outText);
        Assert.Equal("hello", Node(Doc(outText), "settings", "nothing").DecodedValue);
    }

    [Fact]
    public void OnlyTouchedBytesChange()
    {
        var doc = Doc(Sample);
        var outText = Apply(Sample, Edit(new object[] { "value" }, "string", "changed",
            Node(doc, "value").RawValue));

        var before = Sample.Split('\n');
        var after = outText.Split('\n');
        Assert.Equal(before.Length, after.Length);
        int changedLines = before.Zip(after).Count(p => p.First != p.Second);
        Assert.Equal(1, changedLines);
        Assert.Equal("value: changed       # 普通字符串", after[^1]);
    }

    [Fact]
    public void RejectsStaleExpectedRaw()
    {
        var doc = Doc(Sample);
        var result = new YamlEditor(doc, "\n").Apply(new[]
        {
            Edit(new object[] { "settings", "enabled" }, "bool", false, "true ")
        });
        Assert.NotNull(result.Error);
        Assert.Contains("外部修改", result.Error);
    }

    [Fact]
    public void RejectsUnknownPath()
    {
        var doc = Doc(Sample);
        var result = new YamlEditor(doc, "\n").Apply(new[]
        {
            Edit(new object[] { "settings", "nope" }, "bool", true)
        });
        Assert.NotNull(result.Error);
        Assert.Contains("找不到配置项", result.Error);
    }

    [Fact]
    public void EditsCrlfFileWithCrlfOutput()
    {
        var crlf = Sample.Replace("\n", "\r\n");
        var doc = Doc(crlf);
        Assert.Null(doc.Error);
        var result = new YamlEditor(doc, "\r\n").Apply(new[]
        {
            Edit(new object[] { "settings", "enabled" }, "bool", false, Node(doc, "settings", "enabled").RawValue)
        });
        Assert.Null(result.Error);
        Assert.Contains("  enabled: false        # 是否启用宝石系统\r\n", result.NewText);
        Assert.DoesNotContain("\n\n", result.NewText!.Replace("\r\n", "\n"));
        var re = Doc(result.NewText!);
        Assert.Null(re.Error);
        Assert.Equal("false", Node(re, "settings", "enabled").DecodedValue);
    }

    [Fact]
    public void HandlesAnchorsAndAliases()
    {
        const string yaml = """
defaults: &def
  a: 1
use: *def
tagged: !!str 123
""";
        var doc = Doc(yaml);
        Assert.Null(doc.Error);
        Assert.Equal(ScalarType.Alias, Node(doc, "use").Type);
        Assert.False(Node(doc, "use").Editable);
        Assert.Equal(ScalarType.String, Node(doc, "tagged").Type);
        Assert.Equal("&def", Node(doc, "defaults").Anchor is null ? null : "&" + Node(doc, "defaults").Anchor);
    }

    [Fact]
    public void CommentSignatureDetectsHashInsideBlock()
    {
        var doc = Doc("""
a: |
  # 这不是注释，是内容
b: 1  # 这是注释
""");
        var sig = doc.CollectCommentSignature();
        Assert.DoesNotContain("line:# 这不是注释，是内容", sig);
        Assert.Contains("at[b]:# 这是注释", sig);
    }
}
