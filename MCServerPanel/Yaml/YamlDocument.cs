namespace MCServerPanel.Yaml;

public sealed class YamlDocument
{
    public string Text { get; set; } = "";
    public YamlNode? Root { get; set; }
    public string? Error { get; set; }
    public int ErrorLine { get; set; }
    public int ErrorColumn { get; set; }
    public bool MultipleDocuments { get; set; }
    public List<string> Warnings { get; } = new();
    public Dictionary<string, YamlNode> Index { get; } = new(StringComparer.Ordinal);

    public void BuildIndex()
    {
        Index.Clear();
        if (Root is null) return;
        Walk(Root);
    }

    private void Walk(YamlNode node)
    {
        if (node.Kind == YamlKind.Scalar)
        {
            Index[node.PathKey] = node;
        }
        foreach (var child in node.Children) Walk(child);
    }

    public YamlNode? Find(IEnumerable<object> path)
    {
        var segments = path.ToList();
        var node = Root;
        if (node is null) return null;
        if (segments.Count == 0) return node;
        foreach (var seg in segments)
        {
            if (node.Kind == YamlKind.Mapping && seg is string key)
            {
                node = node.Children.FirstOrDefault(c => c.Key == key);
            }
            else if (node.Kind == YamlKind.Sequence && seg is int idx)
            {
                node = node.Children.FirstOrDefault(c => c.Index == idx);
            }
            else return null;
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>按文档顺序收集所有注释（整行注释 + 行尾注释），用于保存前后的不变性校验。</summary>
    public List<string> CollectCommentSignature()
    {
        var result = new List<string>();
        var blockSpans = new List<(int Start, int End)>();
        if (Root is not null) CollectBlockSpans(Root, blockSpans);

        var text = Text;
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            var line = text.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r');

            bool insideBlock = blockSpans.Any(s => lineStart >= s.Start && lineStart < s.End);
            if (!insideBlock)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#')) result.Add("line:" + trimmed);
            }
            lineStart = lineEnd + 1;
        }

        if (Root is not null) CollectInline(Root, result);
        return result;
    }

    private static void CollectInline(YamlNode node, List<string> result)
    {
        if (node.Comment is not null) result.Add($"at[{YamlPath.Display(node.Path)}]:{node.Comment}");
        foreach (var child in node.Children) CollectInline(child, result);
    }

    private static void CollectBlockSpans(YamlNode node, List<(int, int)> spans)
    {
        if (node.Kind == YamlKind.Scalar && (node.Style == "literal" || node.Style == "folded"))
            spans.Add((node.NodeStart, Math.Max(node.NodeEnd, node.NodeStart + 1)));
        foreach (var child in node.Children) CollectBlockSpans(child, spans);
    }
}
