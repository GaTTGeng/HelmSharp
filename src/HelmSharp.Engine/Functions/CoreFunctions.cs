namespace HelmSharp.Engine;

/// <summary>
/// Core template functions that require token evaluation via IEvaluationContext.
/// </summary>
internal static class CoreFunctions
{
    // ── Default / required / fail / tpl ──

    public static object? Default(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var def = eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var val = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context);
        return TypeConverters.IsTruthy(val) ? val : def;
    }

    public static object? FnFail(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var msg = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var message = TypeConverters.ToTemplateString(msg);
        throw new InvalidOperationException(message.Length > 0 ? message : "fail called");
    }

    public static object? Required(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var message = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var value = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context);
        if (!TypeConverters.IsTruthy(value))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "required value is missing" : message);
        return value;
    }

    public static string Tpl(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var template = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        return eval.RenderSection(template, context);
    }

    public static object? Ternary(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var trueValue = eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var falseValue = eval.EvaluateToken(tokens.ElementAtOrDefault(2), context);
        var test = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(3), context);
        return TypeConverters.IsTruthy(test) ? trueValue : falseValue;
    }

    // ── String manipulation ──

    public static string Cat(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var parts = new List<string>();
        if (pipelineValue != null) parts.Add(TypeConverters.ToTemplateString(pipelineValue));
        foreach (var t in tokens.Skip(1))
            parts.Add(TypeConverters.ToTemplateString(eval.EvaluateToken(t, context)));
        return string.Join(' ', parts);
    }

    public static string Replace(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var oldValue = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var newValue = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(3), context));
        return input.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    public static string Trunc(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var length = (int)TypeConverters.ToLong(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        if (length == 0)
            return string.Empty;
        if (Math.Abs(length) >= input.Length)
            return input;
        return length > 0 ? input[..length] : input[^Math.Abs(length)..];
    }

    public static string TrimSuffix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var suffix = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.EndsWith(suffix, StringComparison.Ordinal) ? input[..^suffix.Length] : input;
    }

    public static string TrimPrefix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var prefix = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.StartsWith(prefix, StringComparison.Ordinal) ? input[prefix.Length..] : input;
    }

    public static bool Contains(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var needle = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.Contains(needle, StringComparison.Ordinal);
    }

    public static string Printf(IReadOnlyList<string> tokens, TemplateContext context, IEvaluationContext eval)
    {
        var format = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var args = tokens.Skip(2).Select(t => TypeConverters.ToTemplateString(eval.EvaluateToken(t, context))).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            format = StringHelpers.ReplaceFirst(format, "%s", "{" + i + "}");
            format = StringHelpers.ReplaceFirst(format, "%v", "{" + i + "}");
            format = StringHelpers.ReplaceFirst(format, "%d", "{" + i + "}");
            format = StringHelpers.ReplaceFirst(format, "%f", "{" + i + "}");
            format = StringHelpers.ReplaceFirst(format, "%q", "{" + i + "}");
        }
        return string.Format(format, args);
    }

    // ── List operations ──

    public static object? Prepend(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var item = eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var result = new List<object?> { item };
        result.AddRange(list);
        return result;
    }

    public static object? Append(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var item = eval.EvaluateToken(tokens.ElementAtOrDefault(2), context);
        var result = new List<object?>(list) { item };
        return result;
    }

    public static object? Without(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var exclude = tokens.Skip(2).Select(t => TypeConverters.ToTemplateString(eval.EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        return list.Where(x => !exclude.Contains(TypeConverters.ToTemplateString(x))).ToList();
    }

    public static object? Has(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var needle = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return list.Any(x => TypeConverters.ToTemplateString(x) == needle);
    }

    public static object? Concat(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var result = new List<object?>();
        if (pipelineValue != null) result.AddRange(CollectionsHelpers.ToList(pipelineValue));
        foreach (var t in tokens.Skip(1))
            result.AddRange(CollectionsHelpers.ToList(eval.EvaluateToken(t, context)));
        return result;
    }

    // ── Comparison ──

    public static object? Eq(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var a = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var b = eval.EvaluateToken(tokens.ElementAtOrDefault(pipelineValue != null ? 1 : 2), context);
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.GetType() == b.GetType())
        {
            if (a is long la && b is long lb) return la == lb;
            if (a is double da && b is double db) return Math.Abs(da - db) < 1e-10;
            if (a is bool ba && b is bool bb) return ba == bb;
        }
        return string.Equals(TypeConverters.ToTemplateString(a), TypeConverters.ToTemplateString(b), StringComparison.Ordinal);
    }

    public static object? CompareOp(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, Func<int, int, bool> cmp, IEvaluationContext eval)
    {
        var a = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var b = eval.EvaluateToken(tokens.ElementAtOrDefault(pipelineValue != null ? 1 : 2), context);
        var result = TypeFunctions.CompareValues(a, b);
        return cmp(result, 0);
    }
}
