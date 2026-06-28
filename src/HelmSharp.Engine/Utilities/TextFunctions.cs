using System.Security.Cryptography;
using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// Template text/string functions that require token evaluation via IEvaluationContext.
/// </summary>
internal static class TextFunctions
{
    // ── String operations ──

    public static string Plural(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var word = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(3), context));
        var plural = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var singular = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var count = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(4), context));
        return count == 1 ? singular : plural;
    }

    public static string Wrap(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var width = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return StringFunctions.WrapText(input, width);
    }

    public static string WrapWith(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var width = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var indent = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(3), context));
        return StringFunctions.WrapText(input, width, indent);
    }

    public static string Abbrev(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var maxWidth = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.Length <= maxWidth ? input : input[..maxWidth];
    }

    public static string Abbrevinitial(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var maxWidth = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (sb.Length >= maxWidth) break;
            sb.Append(part.Length > 0 ? part[0] : "");
        }
        return sb.ToString()[..Math.Min(sb.Length, maxWidth)];
    }

    public static string TrimAll(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var cutset = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        foreach (var ch in cutset)
            input = input.Trim(ch);
        return input;
    }

    public static bool HasPrefix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var prefix = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static bool HasSuffix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var suffix = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.EndsWith(suffix, StringComparison.Ordinal);
    }

    public static string Repeat(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var count = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return string.Concat(Enumerable.Repeat(input, count));
    }

    public static string Substr(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var start = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var end = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(3), context));
        if (start < 0) start = 0;
        if (start >= input.Length) return string.Empty;
        if (end < 0) end = input.Length;
        if (end < start) return string.Empty;
        if (end > input.Length) end = input.Length;
        return input[start..end];
    }

    // ── Crypto / random ──

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

    public static long RandInt(IReadOnlyList<string> tokens, TemplateContext context, IEvaluationContext eval)
    {
        var min = tokens.Count > 1 ? (int)TypeConverters.ToLong(eval.EvaluateToken(tokens[1], context)) : 0;
        var max = tokens.Count > 2 ? (int)TypeConverters.ToLong(eval.EvaluateToken(tokens[2], context)) : int.MaxValue;
        return RandomNumberGenerator.GetInt32(min, max);
    }

    public static string GenPrivateKey(IReadOnlyList<string> tokens, TemplateContext context, IEvaluationContext eval)
    {
        var algo = tokens.Count > 1 ? TypeConverters.ToTemplateString(eval.EvaluateToken(tokens[1], context)) : "rsa";
        return $"-----BEGIN {algo.ToUpperInvariant()} PRIVATE KEY-----\n(managed-helm-placeholder)\n-----END {algo.ToUpperInvariant()} PRIVATE KEY-----";
    }
}
