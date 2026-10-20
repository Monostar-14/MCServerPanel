using System.Globalization;
using MCServerPanel.Models;

namespace MCServerPanel.Yaml;

public static class NodeDtoFactory
{
    public static NodeDto Build(YamlNode node)
    {
        var dto = new NodeDto
        {
            Path = node.Path,
            PathKey = node.PathKey,
            Key = node.Key,
            Index = node.Index,
            Comment = node.Comment,
            DocComment = node.DocComment,
            Line = node.KeyLine > 0 ? node.KeyLine : node.Line,
            Style = node.Style,
            Raw = node.RawValue,
            Editable = node.Editable
        };

        switch (node.Kind)
        {
            case YamlKind.Mapping:
                dto.Kind = "map";
                dto.Type = "map";
                dto.Children = node.Children.Select(Build).ToList();
                break;
            case YamlKind.Sequence:
                dto.Kind = "seq";
                dto.Type = "seq";
                dto.Children = node.Children.Select(Build).ToList();
                break;
            default:
                dto.Kind = "scalar";
                ApplyScalar(dto, node);
                break;
        }

        return dto;
    }

    private static void ApplyScalar(NodeDto dto, YamlNode node)
    {
        var decoded = node.DecodedValue ?? node.RawValue;
        switch (node.Type)
        {
            case ScalarType.Bool:
                dto.Type = "bool";
                dto.Value = ScalarCodec.TryBoolToken(decoded, out var b, out _) ? b : false;
                break;
            case ScalarType.Int:
                dto.Type = "int";
                dto.Value = long.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                    ? l : decoded;
                dto.Step = 1;
                break;
            case ScalarType.Float:
                dto.Type = "float";
                dto.Value = double.TryParse(decoded, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d : decoded;
                dto.Step = 0.1;
                break;
            case ScalarType.Null:
                dto.Type = "null";
                dto.Value = null;
                break;
            case ScalarType.Alias:
                dto.Type = "alias";
                dto.Value = node.RawValue;
                dto.Editable = false;
                break;
            default:
                dto.Type = "string";
                dto.Value = decoded;
                dto.Multiline = node.Style is "literal" or "folded";
                break;
        }

        if (node.Anchor is not null) dto.Comment = dto.Comment is null ? $"锚点 &{node.Anchor}" : dto.Comment;
    }
}
