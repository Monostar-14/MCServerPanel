using System.Text;
using System.Text.Json;

namespace MCServerPanel.Yaml;

public sealed class YamlEditRequest
{
    public List<JsonElement> Path { get; set; } = new();
    public string Type { get; set; } = "string";
    public JsonElement Value { get; set; }
    public string? ExpectedRaw { get; set; }
}

public sealed class YamlApplyResult
{
    public string? NewText { get; set; }
    public string? Error { get; set; }
    public int? ErrorLine { get; set; }
    public List<string> Applied { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 把「改哪些值」翻译成对原文的定点字符替换，并在写回前做三重校验：
/// 1) 改后文件仍能被 YAML 解析器解析；
/// 2) 整行注释与行尾注释序列完全一致；
/// 3) 所有未改动的标量 token 与键结构逐一相同。
/// 任何一条不满足就放弃保存（调用方不会落盘）。
/// </summary>
public sealed class YamlEditor
{
    private readonly YamlDocument _doc;
    private readonly string _text;
    private readonly string _nl;

    public YamlEditor(YamlDocument doc, string newline)
    {
        _doc = doc;
        _text = doc.Text;
        _nl = newline;
    }

    private sealed record Replacement(int Start, int End, string Text, string PathKey, string Label,
        string? ExpectedToken, string? ExpectedBlock);

    public YamlApplyResult Apply(IReadOnlyList<YamlEditRequest> edits)
    {
        var result = new YamlApplyResult();
        if (_doc.Error is not null)
        {
            result.Error = _doc.Error;
            result.ErrorLine = _doc.ErrorLine;
            return result;
        }
        if (_doc.Root is null)
        {
            result.Error = "这个 YAML 文件是空的，没有可编辑的配置项。";
            return result;
        }

        var reps = new List<Replacement>();
        foreach (var edit in edits)
        {
            var segs = YamlPath.FromJson(edit.Path);
            var label = YamlPath.Display(segs);
            var node = _doc.Find(segs);
            if (node is null) { result.Error = $"找不到配置项：{label}"; return result; }
            if (node.Kind != YamlKind.Scalar) { result.Error = $"「{label}」不是单个值，无法直接修改。"; return result; }
            if (!node.Editable)
            {
                result.Error = $"「{label}」是 YAML 锚点引用（*别名），请修改它所引用的原始配置。";
                return result;
            }
            if (edit.ExpectedRaw is not null && !string.Equals(edit.ExpectedRaw, node.RawValue, StringComparison.Ordinal))
            {
                result.Error = $"「{label}」已在磁盘上被外部修改，请重新加载后再保存。";
                return result;
            }

            if (!TryBuild(node, edit, out var rep, out var err))
            {
                result.Error = err;
                return result;
            }
            reps.Add(rep);
        }

        reps.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < reps.Count; i++)
        {
            if (reps[i].Start < reps[i - 1].End)
            {
                result.Error = "多个改动落在同一处，已取消保存。";
                return result;
            }
        }

        var sb = new StringBuilder(_text);
        foreach (var rep in reps.OrderByDescending(r => r.Start))
            sb.Remove(rep.Start, rep.End - rep.Start).Insert(rep.Start, rep.Text);
        var newText = sb.ToString();

        // —— 校验 ——
        var newDoc = YamlDocumentParser.Parse(newText);
        if (newDoc.Error is not null)
        {
            result.Error = $"修改后的 YAML 无法解析，已取消保存：{newDoc.Error}";
            result.ErrorLine = newDoc.ErrorLine;
            return result;
        }

        var oldSig = _doc.CollectCommentSignature();
        var newSig = newDoc.CollectCommentSignature();
        if (!oldSig.SequenceEqual(newSig))
        {
            int at = -1;
            for (int i = 0; i < Math.Min(oldSig.Count, newSig.Count); i++)
                if (oldSig[i] != newSig[i]) { at = i; break; }
            result.Error = "校验未通过：这次保存会改动注释或格式，已取消。";
            if (at >= 0) result.Error += $"\n差异位置：{oldSig[at]}  →  {newSig[at]}";
            else result.Error += $"\n注释数量：改前 {oldSig.Count} 条，改后 {newSig.Count} 条。";
            return result;
        }

        var editedKeys = reps.Select(r => r.PathKey).ToHashSet(StringComparer.Ordinal);
        foreach (var edited in edits)
        {
            var segs = YamlPath.FromJson(edited.Path);
            var node2 = newDoc.Find(segs);
            if (node2 is null) { result.Error = $"校验失败：修改后找不到「{YamlPath.Display(segs)}」。"; return result; }
            var rep = reps.First(r => r.PathKey == YamlPath.Key(segs));
            if (rep.ExpectedToken is not null &&
                !string.Equals(node2.RawValue, rep.ExpectedToken, StringComparison.Ordinal))
            {
                result.Error = $"校验失败：「{rep.Label}」写回后的值不符合预期（{rep.ExpectedToken} → {node2.RawValue}）。";
                return result;
            }
            if (rep.ExpectedBlock is not null)
            {
                var actual = node2.DecodedValue ?? "";
                if (actual.TrimEnd('\n', '\r') != rep.ExpectedBlock.TrimEnd('\n', '\r'))
                {
                    result.Error = $"校验失败：多行文本「{rep.Label}」写回后内容不一致。";
                    return result;
                }
            }
            result.Applied.Add(rep.Label);
        }

        foreach (var (pathKey, node) in _doc.Index)
        {
            if (editedKeys.Contains(pathKey)) continue;
            var other = newDoc.Index.GetValueOrDefault(pathKey);
            if (other is null || other.Kind != node.Kind || other.RawValue != node.RawValue)
            {
                result.Error = $"校验失败：未修改的配置项「{YamlPath.Display(node.Path)}」在保存后发生了变化，已取消。";
                return result;
            }
        }

        if (newDoc.Warnings.Count > _doc.Warnings.Count)
        {
            result.Error = "校验失败：修改后出现了新的 YAML 结构告警，已取消。";
            return result;
        }

        result.NewText = newText;
        return result;
    }

    private bool TryBuild(YamlNode node, YamlEditRequest edit, out Replacement rep, out string error)
    {
        rep = null!;
        error = "";
        var type = (edit.Type ?? "string").ToLowerInvariant();
        bool isBlock = node.Style is "literal" or "folded";
        string label = NodeLabel(node.Path);

        switch (type)
        {
            case "bool":
            {
                if (edit.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = $"「{label}」需要一个布尔值。";
                    return false;
                }
                var token = ScalarCodec.EncodeBool(edit.Value.GetBoolean(), node.BoolFamily, node.BoolCase);
                rep = new Replacement(node.ValueStart, node.ValueEnd, Pad(node.ValueStart, node.ValueEnd, token),
                    node.PathKey, label, token, null);
                return true;
            }
            case "int":
            {
                if (!TryGetLong(edit.Value, out var l))
                {
                    error = $"「{label}」需要一个整数。";
                    return false;
                }
                var token = ScalarCodec.EncodeInt(l);
                rep = new Replacement(node.ValueStart, node.ValueEnd, Pad(node.ValueStart, node.ValueEnd, token),
                    node.PathKey, label, token, null);
                return true;
            }
            case "float":
            {
                if (!TryGetDouble(edit.Value, out var d) || double.IsNaN(d) || double.IsInfinity(d))
                {
                    error = $"「{label}」需要一个有效数字。";
                    return false;
                }
                var token = ScalarCodec.EncodeFloat(d);
                rep = new Replacement(node.ValueStart, node.ValueEnd, Pad(node.ValueStart, node.ValueEnd, token),
                    node.PathKey, label, token, null);
                return true;
            }
            case "block":
            case "string" when isBlock:
            {
                var value = edit.Value.ValueKind == JsonValueKind.Null ? "" : (edit.Value.GetString() ?? "");
                rep = BuildBlock(node, value, label);
                return true;
            }
            default:
            {
                var value = edit.Value.ValueKind switch
                {
                    JsonValueKind.Null => "",
                    JsonValueKind.String => edit.Value.GetString() ?? "",
                    _ => edit.Value.ToString()
                };
                var token = ScalarCodec.EncodeString(value, node.Style);
                rep = new Replacement(node.ValueStart, node.ValueEnd, Pad(node.ValueStart, node.ValueEnd, token),
                    node.PathKey, label, token, null);
                return true;
            }
        }
    }

    /// <summary>空值插入时补一个空格，避免把 key: 写成 key:true。</summary>
    private static string Pad(int start, int end, string token) =>
        start == end && token.Length > 0 ? " " + token : token;

    private Replacement BuildBlock(YamlNode node, string value, string label)
    {
        int headerStart = node.NodeStart;
        int headerEnd = LineEnd(headerStart);
        var header = _text[headerStart..headerEnd];

        // 折叠块在编辑时改为字面块（内容语义不变，且不会因为折行规则破坏文本）
        var newHeader = header;
        var gt = header.IndexOf('>');
        if (gt >= 0) newHeader = header[..gt] + "|" + header[(gt + 1)..];

        int contentStart = headerEnd;
        if (contentStart < _text.Length && _text[contentStart] == '\r') contentStart++;
        if (contentStart < _text.Length && _text[contentStart] == '\n') contentStart++;

        int indent = DetectIndent(contentStart, node.NodeEnd) ?? (Math.Max(node.KeyIndent, 0) + 2);
        var lines = NormalizeLines(value);

        var sb = new StringBuilder();
        sb.Append(newHeader).Append(_nl);
        foreach (var line in lines)
        {
            if (line.TrimEnd().Length == 0) sb.Append(_nl);
            else sb.Append(' ', indent).Append(line).Append(_nl);
        }

        var expected = string.Join("\n", lines).TrimEnd('\n');
        return new Replacement(headerStart, Math.Max(node.NodeEnd, headerEnd), sb.ToString(),
            node.PathKey, label, null, expected);
    }

    private static List<string> NormalizeLines(string value)
    {
        var v = value.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = v.Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private int? DetectIndent(int contentStart, int contentEnd)
    {
        int i = contentStart;
        int end = Math.Min(contentEnd, _text.Length);
        while (i < end)
        {
            int lineEnd = LineEnd(i);
            var line = _text[i..lineEnd];
            if (line.Trim().Length > 0)
            {
                int spaces = 0;
                while (spaces < line.Length && line[spaces] == ' ') spaces++;
                return spaces;
            }
            i = lineEnd + 1;
        }
        return null;
    }

    private int LineEnd(int index)
    {
        int i = Math.Max(index, 0);
        while (i < _text.Length && _text[i] != '\n' && _text[i] != '\r') i++;
        return i;
    }

    private static string NodeLabel(List<object> path) =>
        path.Count == 0 ? "根节点" : YamlPath.Display(path);

    private static bool TryGetLong(JsonElement el, out long value)
    {
        value = 0;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt64(out value);
        if (el.ValueKind == JsonValueKind.String) return long.TryParse(el.GetString(), out value);
        return false;
    }

    private static bool TryGetDouble(JsonElement el, out double value)
    {
        value = 0;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetDouble(out value);
        if (el.ValueKind == JsonValueKind.String)
            return double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        return false;
    }
}
