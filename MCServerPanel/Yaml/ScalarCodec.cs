using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MCServerPanel.Yaml;

/// <summary>
/// 类型识别与写回编码。原则：只重写用户真正改过的那一个标量，
/// 并尽量保留原来的书写风格（引号、yes/no、大小写）。
/// </summary>
public static class ScalarCodec
{
    private static readonly Regex IntRegex = new(@"^[+-]?[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex FloatRegex = new(
        @"^[+-]?(?:[0-9]+\.[0-9]*|\.[0-9]+|[0-9]+[eE][+-]?[0-9]+|[0-9]+\.[0-9]*[eE][+-]?[0-9]+)$",
        RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(
        @"^[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}([Tt ].*)?$", RegexOptions.Compiled);

    public static ScalarType Classify(string value, string style, string? tag)
    {
        if (style is "literal" or "folded") return ScalarType.String;
        if (tag is not null)
        {
            if (tag.EndsWith(":str", StringComparison.Ordinal)) return ScalarType.String;
            if (tag.EndsWith(":int", StringComparison.Ordinal)) return ScalarType.Int;
            if (tag.EndsWith(":float", StringComparison.Ordinal)) return ScalarType.Float;
            if (tag.EndsWith(":bool", StringComparison.Ordinal)) return ScalarType.Bool;
            if (tag.EndsWith(":null", StringComparison.Ordinal)) return ScalarType.Null;
        }
        if (style != "plain") return ScalarType.String;

        if (value.Length == 0) return ScalarType.Null;
        if (value == "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase)) return ScalarType.Null;
        if (TryBoolToken(value, out _, out _)) return ScalarType.Bool;
        if (IntRegex.IsMatch(value)) return ScalarType.Int;
        if (FloatRegex.IsMatch(value)) return ScalarType.Float;
        return ScalarType.String;
    }

    public static bool TryBoolToken(string raw, out bool value, out string family)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true": value = true; family = "true/false"; return true;
            case "false": value = false; family = "true/false"; return true;
            case "yes": value = true; family = "yes/no"; return true;
            case "no": value = false; family = "yes/no"; return true;
            case "on": value = true; family = "on/off"; return true;
            case "off": value = false; family = "on/off"; return true;
            default: value = false; family = ""; return false;
        }
    }

    /// <summary>记录原始布尔词的风格（yes/no 家族 + 大小写），写回时沿用。</summary>
    public static void DescribeBoolToken(string raw, out string? family, out string? casing)
    {
        family = null;
        casing = null;
        var token = raw.Trim();
        if (!TryBoolToken(token, out _, out var fam)) return;
        family = fam;
        casing = token == token.ToLowerInvariant() ? "lower"
            : token == token.ToUpperInvariant() ? "upper"
            : "title";
    }

    public static string EncodeBool(bool value, string? family, string? casing)
    {
        var (t, f) = family switch
        {
            "yes/no" => ("yes", "no"),
            "on/off" => ("on", "off"),
            _ => ("true", "false")
        };
        var token = value ? t : f;
        return casing switch
        {
            "upper" => token.ToUpperInvariant(),
            "title" => char.ToUpperInvariant(token[0]) + token[1..],
            _ => token
        };
    }

    public static string EncodeInt(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string EncodeFloat(double value)
    {
        var s = value.ToString("0.0##############", CultureInfo.InvariantCulture);
        if (s == "-0.0") s = "0.0";
        return s;
    }

    public static string EncodeString(string value, string style)
    {
        return style switch
        {
            "single" => "'" + value.Replace("'", "''") + "'",
            "double" => "\"" + EscapeDouble(value) + "\"",
            _ => NeedsQuotes(value) ? "\"" + EscapeDouble(value) + "\"" : value
        };
    }

    public static string EscapeDouble(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                case '\0': sb.Append(@"\0"); break;
                default:
                    if (c < 0x20 || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("X2"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>决定字符串是否可以不加引号地写成 plain 标量。</summary>
    public static bool NeedsQuotes(string value)
    {
        if (value.Length == 0) return true;
        if (value.Trim().Length != value.Length) return true;
        if (value.Any(c => c is '\n' or '\r' or '\t')) return true;

        char c0 = value[0];
        if ("#&*!|>%@`'\"[]{}".Contains(c0)) return true;
        if (c0 is '-' or '?' or ':' && (value.Length == 1 || value[1] == ' ')) return true;
        if (value.Contains(": ")) return true;
        if (value.EndsWith(':')) return true;
        if (value.Contains(" #")) return true;
        if (value == "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
        if (TryBoolToken(value, out _, out _)) return true;
        if (IntRegex.IsMatch(value) || FloatRegex.IsMatch(value)) return true;
        if (DateRegex.IsMatch(value)) return true;
        if (value.StartsWith(".inf", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
