using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace MCServerPanel.Yaml;

/// <summary>
/// 用 YamlDotNet 的事件流建树，同时记录每个标量在原文中的精确字符区间。
/// 不做任何文本重排，因此可以把「改值」做成一次外科手术式的字符串替换。
/// </summary>
public static class YamlDocumentParser
{
    public static YamlDocument Parse(string text)
    {
        var doc = new YamlDocument { Text = text };
        var events = new List<ParsingEvent>();
        try
        {
            var parser = new Parser(new StringReader(text));
            while (parser.MoveNext()) events.Add(parser.Current!);
        }
        catch (YamlException ex)
        {
            doc.Error = $"YAML 语法错误：{ex.Message}";
            doc.ErrorLine = (int)ex.Start.Line;
            doc.ErrorColumn = (int)ex.Start.Column;
            return doc;
        }

        try
        {
            doc.Root = new Builder(events, text, doc).ParseDocument();
        }
        catch (YamlException ex)
        {
            doc.Error = $"YAML 结构错误：{ex.Message}";
            doc.ErrorLine = (int)ex.Start.Line;
            doc.ErrorColumn = (int)ex.Start.Column;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            doc.Error = $"YAML 解析失败：{ex.Message}";
        }

        doc.BuildIndex();
        return doc;
    }

    private sealed record KeyInfo(string Key, string KeyRaw, int KeyLine, int KeyIndent, string? Comment);

    private sealed class Builder
    {
        private const int MaxDepth = 200;
        private const int MaxNodes = 200_000;

        private readonly List<ParsingEvent> _events;
        private readonly string _text;
        private readonly YamlDocument _doc;
        private readonly int[] _lineStarts;
        private int _i;
        private int _nodes;

        public Builder(List<ParsingEvent> events, string text, YamlDocument doc)
        {
            _events = events;
            _text = text;
            _doc = doc;
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            _lineStarts = starts.ToArray();
        }

        private string? LineText(int line1Based)
        {
            int idx = line1Based - 1;
            if (idx < 0 || idx >= _lineStarts.Length) return null;
            int start = _lineStarts[idx];
            int end = idx + 1 < _lineStarts.Length ? _lineStarts[idx + 1] - 1 : _text.Length;
            if (end > start && end <= _text.Length && _text[end - 1] == '\r') end--;
            return _text[start..Math.Max(start, end)];
        }

        /// <summary>收集键所在行往上连续的一段注释，作为该配置项的说明文字。</summary>
        private string? FindDocComment(int keyLine)
        {
            var lines = new List<string>();
            for (int line = keyLine - 1; line >= 1; line--)
            {
                var text = LineText(line);
                if (text is null) break;
                var trimmed = text.Trim();
                if (trimmed.Length == 0 || !trimmed.StartsWith('#')) break;
                // 纯装饰性的分隔注释（#### / ==== / ----）不作为说明
                if (trimmed.TrimStart('#').Trim(' ', '=', '-', '_', '*', '~', '#').Length > 0)
                    lines.Insert(0, trimmed);
                if (lines.Count >= 40) break;
            }
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        public YamlNode? ParseDocument()
        {
            while (_i < _events.Count && _events[_i] is not DocumentStart) _i++;
            if (_i >= _events.Count) return null;
            _i++; // DocumentStart
            if (_i < _events.Count && _events[_i] is DocumentEnd) return null;

            var root = ParseNode(null, new List<object>(), null);

            while (_i < _events.Count)
            {
                if (_events[_i] is DocumentStart)
                {
                    _doc.MultipleDocuments = true;
                    _doc.Warnings.Add("文件包含多个 YAML 文档，仅加载了第一个。");
                    break;
                }
                _i++;
            }
            return root;
        }

        private YamlNode ParseNode(KeyInfo? key, List<object> parentPath, object? segment, int depth = 0)
        {
            if (depth > MaxDepth)
                throw new InvalidOperationException($"YAML 嵌套层数超过 {MaxDepth} 层，已拒绝解析（防止恶意文件耗尽资源）。");
            if (++_nodes > MaxNodes)
                throw new InvalidOperationException($"YAML 节点数量超过 {MaxNodes} 个，已拒绝解析（防止恶意文件耗尽资源）。");

            if (_i >= _events.Count) throw new InvalidOperationException("YAML 事件流意外结束。");
            var evt = _events[_i];

            return evt switch
            {
                MappingStart ms => ParseMapping(ms, key, parentPath, segment, depth),
                SequenceStart ss => ParseSequence(ss, key, parentPath, segment, depth),
                Scalar sc => ParseScalar(sc, key, parentPath, segment),
                AnchorAlias aa => ParseAlias(aa, key, parentPath, segment),
                _ => throw new InvalidOperationException($"无法解析的节点类型：{evt.GetType().Name}")
            };
        }

        private YamlNode NewNode(YamlKind kind, KeyInfo? key, List<object> parentPath, object? segment)
        {
            var node = new YamlNode { Kind = kind };
            node.Path = new List<object>(parentPath);
            if (segment is not null) node.Path.Add(segment);
            if (key is not null)
            {
                node.Key = key.Key;
                node.KeyRaw = key.KeyRaw;
                node.KeyLine = key.KeyLine;
                node.KeyIndent = key.KeyIndent;
                node.Comment = key.Comment;
                node.DocComment = FindDocComment(key.KeyLine);
            }
            if (segment is int idx) node.Index = idx;
            return node;
        }

        private YamlNode ParseMapping(MappingStart start, KeyInfo? key, List<object> parentPath, object? segment, int depth)
        {
            var node = NewNode(YamlKind.Mapping, key, parentPath, segment);
            node.Line = (int)start.Start.Line;
            node.NodeStart = (int)start.Start.Index;
            node.Anchor = start.Anchor.IsEmpty ? null : start.Anchor.Value;
            node.Tag = start.Tag.IsEmpty ? null : start.Tag.Value;
            _i++;

            while (_i < _events.Count && _events[_i] is not MappingEnd)
            {
                if (_events[_i] is not Scalar keyScalar)
                {
                    // 复杂键（? {...}）在插件配置里极罕见：跳过这一对
                    _doc.Warnings.Add($"第 {_events[_i].Start.Line} 行存在复杂键，已跳过。");
                    ParseNode(null, node.Path, null, depth + 1);
                    if (_i < _events.Count && _events[_i] is not MappingEnd) ParseNode(null, node.Path, null, depth + 1);
                    continue;
                }

                _i++;
                var keyRaw = Slice((int)keyScalar.Start.Index, (int)keyScalar.End.Index);
                var keyComment = FindComment((int)keyScalar.End.Index, (int)keyScalar.Start.Index);
                var info = new KeyInfo(keyScalar.Value, keyRaw, (int)keyScalar.Start.Line, (int)keyScalar.Start.Column - 1, keyComment);

                var child = ParseNode(info, node.Path, keyScalar.Value, depth + 1);
                var existing = node.Children.FindIndex(c => c.Key == child.Key);
                if (existing >= 0)
                {
                    // YAML 规范下重复键以最后一处为准（SnakeYAML 亦然），这里保持同样语义并给出提示
                    _doc.Warnings.Add($"第 {(int)keyScalar.Start.Line} 行重复键「{keyScalar.Value}」，按 YAML 规范以最后一处为准。");
                    node.Children[existing] = child;
                }
                else node.Children.Add(child);
            }

            if (_i < _events.Count && _events[_i] is MappingEnd me)
            {
                node.NodeEnd = (int)me.Start.Index;
                _i++;
            }
            else node.NodeEnd = _text.Length;

            return node;
        }

        private YamlNode ParseSequence(SequenceStart start, KeyInfo? key, List<object> parentPath, object? segment, int depth)
        {
            var node = NewNode(YamlKind.Sequence, key, parentPath, segment);
            node.Line = (int)start.Start.Line;
            node.NodeStart = (int)start.Start.Index;
            node.Anchor = start.Anchor.IsEmpty ? null : start.Anchor.Value;
            node.Tag = start.Tag.IsEmpty ? null : start.Tag.Value;
            _i++;

            int index = 0;
            while (_i < _events.Count && _events[_i] is not SequenceEnd)
            {
                var child = ParseNode(null, node.Path, index, depth + 1);
                if (child.Key is null) child.Index = index;
                node.Children.Add(child);
                index++;
            }

            if (_i < _events.Count && _events[_i] is SequenceEnd se)
            {
                node.NodeEnd = (int)se.Start.Index;
                _i++;
            }
            else node.NodeEnd = _text.Length;

            return node;
        }

        private YamlNode ParseScalar(Scalar s, KeyInfo? key, List<object> parentPath, object? segment)
        {
            var node = NewNode(YamlKind.Scalar, key, parentPath, segment);
            node.Line = (int)s.Start.Line;
            node.NodeStart = (int)s.Start.Index;
            node.NodeEnd = (int)s.End.Index;
            node.Style = s.Style switch
            {
                ScalarStyle.SingleQuoted => "single",
                ScalarStyle.DoubleQuoted => "double",
                ScalarStyle.Literal => "literal",
                ScalarStyle.Folded => "folded",
                _ => "plain"
            };
            node.Anchor = s.Anchor.IsEmpty ? null : s.Anchor.Value;
            node.Tag = s.Tag.IsEmpty ? null : s.Tag.Value;

            // 事件区间可能包含锚点/标签前缀（&a 42 / !!str 123），值区间要跳过它们
            int vs = node.NodeStart;
            int ve = node.NodeEnd;
            int p = vs;
            while (p < ve && (_text[p] == '&' || _text[p] == '!'))
            {
                while (p < ve && !char.IsWhiteSpace(_text[p])) p++;
                while (p < ve && _text[p] == ' ') p++;
            }
            node.ValueStart = p;
            node.ValueEnd = ve;
            node.RawValue = p <= ve ? _text[p..ve] : "";
            node.DecodedValue = s.Value;
            node.Type = ScalarCodec.Classify(s.Value, node.Style, node.Tag);

            if (node.Type == ScalarType.Bool)
            {
                ScalarCodec.DescribeBoolToken(node.RawValue, out var family, out var casing);
                node.BoolFamily = family;
                node.BoolCase = casing;
            }

            _i++;

            node.Comment = node.Style is "literal" or "folded"
                ? FindComment(node.NodeStart, node.NodeStart)
                : FindComment(ve, vs);

            return node;
        }

        private YamlNode ParseAlias(AnchorAlias aa, KeyInfo? key, List<object> parentPath, object? segment)
        {
            var node = NewNode(YamlKind.Scalar, key, parentPath, segment);
            node.Type = ScalarType.Alias;
            node.Style = "alias";
            node.Line = (int)aa.Start.Line;
            node.NodeStart = (int)aa.Start.Index;
            node.NodeEnd = (int)aa.End.Index;
            node.ValueStart = node.NodeStart;
            node.ValueEnd = node.NodeEnd;
            node.RawValue = Slice(node.NodeStart, node.NodeEnd);
            node.Comment = FindComment(node.NodeEnd, node.NodeStart);
            _i++;
            return node;
        }

        private string Slice(int start, int end) =>
            start >= 0 && end <= _text.Length && end >= start ? _text[start..end] : "";

        /// <summary>从 from 开始扫描到本行行尾，返回第一个 # 之后的注释文本（含 #）。</summary>
        private string? FindComment(int from, int floor)
        {
            int i = Math.Max(from, 0);
            if (i < floor) i = floor;
            while (i < _text.Length && _text[i] != '\n' && _text[i] != '\r')
            {
                if (_text[i] == '#') return _text[i..LineEnd(i)].TrimEnd();
                i++;
            }
            return null;
        }

        private int LineEnd(int index)
        {
            int i = index;
            while (i < _text.Length && _text[i] != '\n' && _text[i] != '\r') i++;
            return i;
        }
    }
}
