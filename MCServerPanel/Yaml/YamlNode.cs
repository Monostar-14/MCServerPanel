using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace MCServerPanel.Yaml;

public enum YamlKind { Mapping, Sequence, Scalar }

public enum ScalarType { Bool, Int, Float, String, Null, Alias }

/// <summary>
/// 一个 YAML 节点，携带它在源文件中的精确字符区间。
/// 所有写入操作只替换标量的 ValueStart..ValueEnd 区间（块标量替换其内容区），
/// 因此注释、空行、缩进、键顺序、未修改的引号风格全部保持原样。
/// </summary>
public sealed class YamlNode
{
    public YamlKind Kind { get; set; }
    public ScalarType Type { get; set; }

    public string? Key { get; set; }
    public string? KeyRaw { get; set; }
    public int KeyLine { get; set; }
    public int KeyIndent { get; set; }
    public int? Index { get; set; }

    public string? Anchor { get; set; }
    public string? Tag { get; set; }
    public string? Comment { get; set; }
    /// <summary>紧挨在键上方的一段注释（用于网页里给配置项做说明）。</summary>
    public string? DocComment { get; set; }

    public int Line { get; set; }
    public int NodeStart { get; set; }
    public int NodeEnd { get; set; }
    public int ValueStart { get; set; }
    public int ValueEnd { get; set; }

    public string RawValue { get; set; } = "";
    public string? DecodedValue { get; set; }
    public string Style { get; set; } = "plain";
    public string? BoolFamily { get; set; }
    public string? BoolCase { get; set; }

    public List<object> Path { get; set; } = new();
    public List<YamlNode> Children { get; } = new();

    public bool Editable => Kind == YamlKind.Scalar && Type != ScalarType.Alias;

    public string PathKey => YamlPath.Key(Path);

    public string Label => Key ?? (Index.HasValue ? $"#{Index.Value + 1}" : "根");
}

public static class YamlPath
{
    public static string Key(IEnumerable<object> segments) =>
        string.Join("\u001f", segments.Select(s => s is int i ? "#" + i.ToString() : (string)s));

    public static List<object> FromJson(IEnumerable<System.Text.Json.JsonElement> segments)
    {
        var list = new List<object>();
        foreach (var s in segments)
        {
            if (s.ValueKind == System.Text.Json.JsonValueKind.Number) list.Add(s.GetInt32());
            else list.Add(s.ToString());
        }
        return list;
    }

    public static string Display(IEnumerable<object> segments) =>
        string.Join(" › ", segments.Select(s => s is int i ? $"[{i}]" : (string)s));
}
