using System.Security.Cryptography;
using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// Template text/string functions that require token evaluation via IEvaluationContext.
/// </summary>
internal static class TextFunctions
{
    // ── String operations ──

    /// <summary>
    /// Sprig <c>plural</c>: returns the plural or singular form based on a count.
    /// Signature: <c>plural PLURAL SINGULAR COUNT</c>, or via pipeline with the
    /// word as the pipeline value: <c>"word" | plural PLURAL SINGULAR COUNT</c>.
    /// </summary>
    public static string Plural(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var word = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(3), context));
        var plural = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var singular = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var count = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(4), context));
        return count == 1 ? singular : plural;
    }

    /// <summary>Sprig <c>wrap</c>: word-wraps text at WIDTH columns (no indent).</summary>
    public static string Wrap(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var width = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return StringFunctions.WrapText(input, width);
    }

    /// <summary>Sprig <c>wrapWith</c>: word-wraps text at WIDTH columns, indenting continuation lines.</summary>
    public static string WrapWith(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var width = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var indent = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(3), context));
        return StringFunctions.WrapText(input, width, indent);
    }

    /// <summary>
    /// Sprig <c>abbrev</c>: truncates to MAX width characters with no ellipsis.
    /// Unlike Sprig's strings.abbrev, a trailing space is not removed before truncating.
    /// </summary>
    public static string Abbrev(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var maxWidth = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.Length <= maxWidth ? input : input[..maxWidth];
    }

    /// <summary>Sprig <c>trimAll</c>: strips every character in CUTSET from both ends of the string.</summary>
    public static string TrimAll(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var cutset = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        foreach (var ch in cutset)
            input = input.Trim(ch);
        return input;
    }

    /// <summary>Sprig <c>hasPrefix</c>: ordinal prefix test.</summary>
    public static bool HasPrefix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var prefix = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Sprig <c>hasSuffix</c>: ordinal suffix test.</summary>
    public static bool HasSuffix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var suffix = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.EndsWith(suffix, StringComparison.Ordinal);
    }

    /// <summary>Sprig <c>repeat</c>: concatenates the string COUNT times.</summary>
    public static string Repeat(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var count = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return string.Concat(Enumerable.Repeat(input, count));
    }

    /// <summary>
    /// Sprig <c>substr</c>: extracts [START, END) from the string.
    /// Negative START clamps to 0; negative END means "to the end". An inverted or
    /// out-of-range slice throws a Go-style <c>slice bounds out of range</c> error.
    /// </summary>
    public static string Substr(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var start = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var end = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(3), context));
        if (start < 0) start = 0;
        if (end < 0) end = input.Length;
        if (start > input.Length || end < start)
            throw new InvalidOperationException($"runtime error: slice bounds out of range [{start}:{Math.Min(end, input.Length)}]");
        if (end > input.Length) end = input.Length;
        return input[start..end];
    }

    // ── Crypto / random ──

    /// <summary>
    /// Backs Sprig's <c>randAlphaNum</c>/<c>randAlpha</c>/<c>randNumeric</c>/<c>randAscii</c>:
    /// generates a random string of LENGTH characters from the named charset
    /// (default length 10). Uses a CSPRNG so output is suitable for generated secrets.
    /// </summary>
    public static string RandString(IReadOnlyList<string> tokens, TemplateContext context, string charset, IEvaluationContext eval)
    {
        var length = tokens.Count > 1 ? (int)TypeConverters.ToLong(eval.EvaluateToken(tokens[1], context)) : 10;
        var chars = charset switch
        {
            "alphanum" => "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "alpha" => "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
            "numeric" => "0123456789",
            "ascii" => "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()",
            _ => "abcdefghijklmnopqrstuvwxyz"
        };
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

    /// <summary>
    /// Sprig <c>randInt</c>: random integer in [MIN, MAX) — note MAX is exclusive,
    /// unlike Sprig's inclusive upper bound. Defaults: MIN=0, MAX=int.MaxValue.
    /// </summary>
    public static long RandInt(IReadOnlyList<string> tokens, TemplateContext context, IEvaluationContext eval)
    {
        var min = tokens.Count > 1 ? (int)TypeConverters.ToLong(eval.EvaluateToken(tokens[1], context)) : 0;
        var max = tokens.Count > 2 ? (int)TypeConverters.ToLong(eval.EvaluateToken(tokens[2], context)) : int.MaxValue;
        return RandomNumberGenerator.GetInt32(min, max);
    }

    /// <summary>
    /// Sprig <c>genPrivateKey</c>. Returns a PEM-shaped placeholder block rather than a
    /// real key — this renderer does not generate cryptographic key material. Templates
    /// that embed the result in a Secret will not match Helm CLI output.
    /// </summary>
    public static string GenPrivateKey(IReadOnlyList<string> tokens, TemplateContext context, IEvaluationContext eval)
    {
        var algo = tokens.Count > 1 ? TypeConverters.ToTemplateString(eval.EvaluateToken(tokens[1], context)) : "rsa";
        return $"-----BEGIN {algo.ToUpperInvariant()} PRIVATE KEY-----\n(managed-helm-placeholder)\n-----END {algo.ToUpperInvariant()} PRIVATE KEY-----";
    }

    // Sprig: until COUNT → [0, 1, ..., COUNT-1]
    /// <summary>Sprig <c>until</c>: [0, 1, …, COUNT-1]; non-positive COUNT yields an empty list.</summary>
    public static List<object?> Until(int count)
    {
        var result = new List<object?>(count);
        for (var i = 0; i < count; i++) result.Add(i);
        return result;
    }

    // Sprig: untilStep START STOP STEP → [START, START+STEP, ..., < STOP (step>0) or > STOP (step<0)]
    // Default START=0, STEP=1. Step=0 or wrong-direction step returns empty list.
    /// <summary>
    /// Sprig <c>untilStep</c>: half-open arithmetic sequence from START to STOP
    /// (exclusive) by STEP. A zero or wrong-direction STEP yields an empty list.
    /// </summary>
    public static List<object?> UntilStep(int start, int stop, int step)
    {
        var result = new List<object?>();
        if (step > 0)
        {
            for (var i = start; i < stop; i += step) result.Add(i);
        }
        else if (step < 0)
        {
            for (var i = start; i > stop; i += step) result.Add(i);
        }
        return result;
    }
}
