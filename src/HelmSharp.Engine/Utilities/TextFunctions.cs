using System.Security.Cryptography;
using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// Template text/string functions. Most resolve arguments via EvaluateTokenStatic.
/// </summary>
internal static class TextFunctions
{
    // ── String operations ──

    public static string Plural(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var word = TypeConverters.ToTemplateString(pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(3), context));
        var plural = TypeConverters.ToTemplateString(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var singular = TypeConverters.ToTemplateString(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        var count = (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(4), context));
        return count == 1 ? singular : plural;
    }

    public static string Wrap(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var width = (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        return StringFunctions.WrapText(input, width);
    }

    public static string WrapWith(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var width = (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var indent = TypeConverters.ToTemplateString(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(3), context));
        return StringFunctions.WrapText(input, width, indent);
    }

    public static string Abbrev(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var maxWidth = (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        return input.Length <= maxWidth ? input : input[..maxWidth];
    }

    public static string Abbrevinitial(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var maxWidth = (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        var parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (sb.Length >= maxWidth) break;
            sb.Append(part.Length > 0 ? part[0] : "");
        }
        return sb.ToString()[..Math.Min(sb.Length, maxWidth)];
    }

    public static string TrimAll(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var cutset = TypeConverters.ToTemplateString(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        foreach (var ch in cutset)
            input = input.Trim(ch);
        return input;
    }

    public static bool HasPrefix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var prefix = TypeConverters.ToTemplateString(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        return input.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static bool HasSuffix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var suffix = TypeConverters.ToTemplateString(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        return input.EndsWith(suffix, StringComparison.Ordinal);
    }

    public static string Repeat(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var count = (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        return string.Concat(Enumerable.Repeat(input, count));
    }

    public static string Substr(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var start = (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var length = (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(3), context));
        if (start < 0) start = 0;
        if (start >= input.Length) return string.Empty;
        if (start + length > input.Length) length = input.Length - start;
        return input.Substring(start, length);
    }

    // ── Crypto / random ──

    public static string RandString(IReadOnlyList<string> tokens, TemplateContext context, string charset)
    {
        var length = tokens.Count > 1 ? (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens[1], context)) : 10;
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

    public static long RandInt(IReadOnlyList<string> tokens, TemplateContext context)
    {
        var min = tokens.Count > 1 ? (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens[1], context)) : 0;
        var max = tokens.Count > 2 ? (int)TypeConverters.ToLong(HelmTemplateRenderer.EvaluateTokenStatic(tokens[2], context)) : int.MaxValue;
        return RandomNumberGenerator.GetInt32(min, max);
    }

    public static string GenPrivateKey(IReadOnlyList<string> tokens, TemplateContext context)
    {
        var algo = tokens.Count > 1 ? TypeConverters.ToTemplateString(HelmTemplateRenderer.EvaluateTokenStatic(tokens[1], context)) : "rsa";
        return $"-----BEGIN {algo.ToUpperInvariant()} PRIVATE KEY-----\n(managed-helm-placeholder)\n-----END {algo.ToUpperInvariant()} PRIVATE KEY-----";
    }
}
