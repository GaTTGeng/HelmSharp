using System.Security.Cryptography;
using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// String manipulation helpers used by template functions.
/// </summary>
internal static class StringHelpers
{
    public static string Quote(object? value)
        => "\"" + EscapeQuotedString(TypeConverters.ToTemplateString(value)) + "\"";

    private static string EscapeQuotedString(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    public static string Unquote(string value)
        => value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"') ? value[1..^1] : value;

    public static string Indent(string value, int spaces, bool prependNewLine)
    {
        var prefix = new string(' ', spaces);
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = string.Join('\n', lines.Select(line => line.Length == 0 ? line : prefix + line));
        return prependNewLine ? "\n" + result : result;
    }

    public static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newValue + text[(index + oldValue.Length)..];
    }

    public static string Sha256Sum(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
