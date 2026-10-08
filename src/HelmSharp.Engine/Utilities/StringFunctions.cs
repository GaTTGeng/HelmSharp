using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// String formatting helpers used by template functions.
/// </summary>
internal static class StringFunctions
{
    /// <summary>Sprig <c>squote</c>: wraps the value in single quotes, escaping embedded single quotes.</summary>
    public static string Squote(object? value)
        => "'" + TypeConverters.ToTemplateString(value).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Sprig <c>snakecase</c>: inserts <c>_</c> before each uppercase letter and lowercases it
    /// (<c>FooBar</c> → <c>foo_bar</c>). Does not split runs of capitals the way
    /// <c>HTTPServer</c> → <c>http_server</c> word-boundary heuristics would.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>camelcase</c>: PascalCase — splits on <c>_</c>/<c>-</c>, capitalizes each
    /// part, and lowercases the remainder (<c>foo_bar</c> → <c>FooBar</c>). Note this
    /// produces PascalCase, not lowerCamelCase as the name might suggest.
    /// </summary>
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

    /// <summary>Sprig <c>kebabcase</c>: like <see cref="Snakecase"/> but joins with <c>-</c>.</summary>
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

    /// <summary>
    /// Backs Sprig's <c>wrap</c>/<c>wrapWith</c>: word-wraps on spaces to
    /// <paramref name="width"/> columns, prefixing continuation lines with
    /// <paramref name="indent"/>. Words longer than the width are not split.
    /// </summary>
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

    /// <summary>Sprig <c>initials</c>: first character of each space-separated word.</summary>
    public static string Initials(string input)
        => string.Join("", input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length > 0 ? w[0].ToString() : ""));

    /// <summary>Sprig <c>nospace</c>: removes all whitespace characters.</summary>
    public static string Nospace(string input)
        => System.Text.RegularExpressions.Regex.Replace(input, @"\s+", string.Empty);

    /// <summary>Sprig <c>swapcase</c>: swaps the case of every letter.</summary>
    public static string Swapcase(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
            sb.Append(char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Sprig <c>shuffle</c>: random Fisher-Yates permutation of the characters.
    /// Not deterministic — each call produces different output.
    /// </summary>
    public static string Shuffle(string input)
    {
        var chars = input.ToCharArray();
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    /// <summary>
    /// Sprig <c>regexFind</c>: first match of <paramref name="pattern"/>, or empty
    /// string when there is none. Syntax is .NET regex, which is largely compatible
    /// with Go's RE2 but supports lookahead/lookbehind that RE2 rejects.
    /// </summary>
    public static string RegexFind(string input, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(input, pattern);
        return m.Success ? m.Value : string.Empty;
    }

    /// <summary>Sprig <c>regexFindAll</c>: all matches (unlimited count).</summary>
    public static List<object?> RegexFindAll(string input, string pattern)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(input, pattern);
        return matches.Select(m => (object?)m.Value).ToList();
    }

    /// <summary>Sprig <c>regexMatch</c>: true when the pattern matches anywhere in the input.</summary>
    public static bool RegexMatch(string input, string pattern)
        => System.Text.RegularExpressions.Regex.IsMatch(input, pattern);

    /// <summary>
    /// Sprig <c>regexReplaceAll</c>: replaces matches; <paramref name="replacement"/>
    /// may contain .NET substitution patterns (<c>$1</c>, <c>${name}</c>) rather than
    /// Go's <c>$1</c>/<c>${1}</c> — most simple group references work unchanged.
    /// </summary>
    public static string RegexReplaceAll(string input, string pattern, string replacement)
        => System.Text.RegularExpressions.Regex.Replace(input, pattern, replacement);

    /// <summary>Sprig <c>regexReplaceAllLiteral</c>: replacement is inserted verbatim, with no substitution patterns.</summary>
    public static string RegexReplaceAllLiteral(string input, string pattern, string replacement)
        => System.Text.RegularExpressions.Regex.Replace(input, pattern, _ => replacement);

    /// <summary>Sprig <c>regexSplit</c>: splits on pattern matches (unlimited splits).</summary>
    public static List<object?> RegexSplit(string input, string pattern)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(input, pattern);
        return parts.Select(p => (object?)p).ToList();
    }
}
