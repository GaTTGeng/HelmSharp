using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// String formatting helpers used by template functions.
/// </summary>
internal static class StringFunctions
{
    public static string Squote(object? value)
        => "'" + TypeConverters.ToTemplateString(value).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    public static string Snakecase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var sb = new StringBuilder();
        for (var i = 0; i < input.Length; i++)
        {
            if (char.IsUpper(input[i]))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(input[i]));
            }
            else
            {
                sb.Append(input[i]);
            }
        }
        return sb.ToString();
    }

    public static string Camelcase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var parts = input.Split('_', '-', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1) sb.Append(part[1..].ToLowerInvariant());
        }
        return sb.ToString();
    }

    public static string Kebabcase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var sb = new StringBuilder();
        for (var i = 0; i < input.Length; i++)
        {
            if (char.IsUpper(input[i]))
            {
                if (i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(input[i]));
            }
            else
            {
                sb.Append(input[i]);
            }
        }
        return sb.ToString();
    }

    public static string WrapText(string input, int width, string indent = "")
    {
        if (width <= 0 || string.IsNullOrEmpty(input)) return input;
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        var lineLen = 0;
        foreach (var word in words)
        {
            if (lineLen > 0 && lineLen + 1 + word.Length > width)
            {
                sb.AppendLine();
                sb.Append(indent);
                lineLen = indent.Length;
            }
            if (lineLen > 0) { sb.Append(' '); lineLen++; }
            sb.Append(word);
            lineLen += word.Length;
        }
        return sb.ToString();
    }

    public static string Initials(string input)
        => string.Join("", input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length > 0 ? w[0].ToString() : ""));
}
