using System.Security.Cryptography;
using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// String manipulation helpers used by template functions.
/// </summary>
internal static class StringHelpers
{
    /// <summary>Sprig <c>quote</c>: wraps the value in double quotes with backslash escaping.</summary>
    public static string Quote(object? value)
        => "\"" + EscapeQuotedString(TypeConverters.ToTemplateString(value)) + "\"";

    /// <summary>Escapes backslashes and control characters for a double-quoted Go string literal.</summary>
    private static string EscapeQuotedString(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    /// <summary>
    /// Sprig <c>unquote</c>: strips one layer of surrounding double quotes and
    /// unescapes the content. Strings without matching double quotes are returned unchanged.
    /// </summary>
    public static string Unquote(string value)
        => value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"')
            ? UnescapeQuotedString(value[1..^1])
            : value;

    /// <summary>
    /// Inverse of <see cref="EscapeQuotedString"/>. The \\\\ → NUL placeholder pass
    /// ensures an already-escaped backslash (\\n) is not re-unescaped as a newline.
    /// </summary>
    private static string UnescapeQuotedString(string value)
        => value
            .Replace("\\\\", "\u0000", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\u0000", "\\", StringComparison.Ordinal);

    /// <summary>
    /// Sprig <c>indent</c>/<c>nindent</c>: prefixes every line with
    /// <paramref name="spaces"/> spaces. Empty lines are left untouched so YAML
    /// blank lines stay blank. <c>nindent</c> additionally prepends a newline.
    /// </summary>
    public static string Indent(string value, int spaces, bool prependNewLine)
    {
        var prefix = new string(' ', spaces);
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = string.Join('\n', lines.Select(line => line.Length == 0 ? line : prefix + line));
        return prependNewLine ? "\n" + result : result;
    }

    /// <summary>Replaces only the first occurrence of <paramref name="oldValue"/> (ordinal).</summary>
    public static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newValue + text[(index + oldValue.Length)..];
    }

    /// <summary>Sprig <c>sha256sum</c>: lowercase hex SHA-256 of the UTF-8 bytes.</summary>
    public static string Sha256Sum(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
