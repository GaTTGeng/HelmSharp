namespace HelmSharp.Engine;

/// <summary>
/// Core template functions that require token evaluation via IEvaluationContext.
/// </summary>
internal static class CoreFunctions
{
    // ── Default / required / fail / tpl ──

    /// <summary>
    /// Sprig <c>default</c>: returns the second argument when it is truthy, else the
    /// first (fallback) value. Usable as <c>default DEF VAL</c> or <c>VAL | default DEF</c>.
    /// </summary>
    public static object? Default(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var def = eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var val = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context);
        return TypeConverters.IsTruthy(val) ? val : def;
    }

    /// <summary>Sprig <c>fail</c>: aborts rendering with the given message ("fail called" when empty).</summary>
    public static object? FnFail(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var msg = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var message = TypeConverters.ToTemplateString(msg);
        throw new InvalidOperationException(message.Length > 0 ? message : "fail called");
    }

    /// <summary>
    /// Sprig <c>required</c>: like <c>default</c> but aborts with <c>message</c> when the
    /// value is missing/falsy instead of substituting a fallback. The message argument
    /// comes first: <c>required "msg" .Value</c>.
    /// </summary>
    public static object? Required(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var message = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var value = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context);
        if (!TypeConverters.IsTruthy(value))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "required value is missing" : message);
        return value;
    }

    /// <summary>
    /// Sprig/Helm <c>tpl</c>: renders a string as a nested template with an optional
    /// scope context. Variable definitions made inside the nested render are kept in a
    /// copied scope so they do not leak outward, matching Go template scoping.
    /// </summary>
    public static string Tpl(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var template = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        // Token layout depends on whether the template string arrives via pipeline:
        //   tpl "str" .        → tokens[1]="str",      tokens[2]="."     (pipelineValue=null, scope at index 2)
        //   "str" | tpl .      → tokens[1]=".",         tokens[2]=null    (pipelineValue="str", scope at index 1)
        var scopeIndex = pipelineValue is null ? 2 : 1;
        if (scopeIndex >= tokens.Count)
            return eval.RenderSection(template, context);

        var dot = eval.EvaluateToken(tokens.ElementAtOrDefault(scopeIndex), context);
        var renderContext = context with
        {
            Dot = dot,
            Variables = new Dictionary<string, object?>(context.Variables, StringComparer.Ordinal)
        };
        return eval.RenderSection(template, renderContext);
    }

    /// <summary>
    /// Sprig <c>ternary</c>: returns TRUE-VAL or FALSE-VAL based on a truthy test.
    /// Argument order is (trueValue, falseValue, test): <c>ternary "a" "b" .Flag</c>
    /// or <c>.Flag | ternary "a" "b"</c> — the value-first order of the pipeline form
    /// is a common Helm pitfall.
    /// </summary>
    public static object? Ternary(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var trueValue = eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var falseValue = eval.EvaluateToken(tokens.ElementAtOrDefault(2), context);
        var test = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(3), context);
        return TypeConverters.IsTruthy(test) ? trueValue : falseValue;
    }

    // ── String manipulation ──

    /// <summary>Sprig <c>cat</c>: concatenates all arguments (and the pipeline value, if any) with single spaces.</summary>
    public static string Cat(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var parts = new List<string>();
        if (pipelineValue != null) parts.Add(TypeConverters.ToTemplateString(pipelineValue));
        foreach (var t in tokens.Skip(1))
            parts.Add(TypeConverters.ToTemplateString(eval.EvaluateToken(t, context)));
        return string.Join(' ', parts);
    }

    /// <summary>Sprig <c>replace</c>: replaces every occurrence of OLD with NEW (ordinal).</summary>
    public static string Replace(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var oldValue = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var newValue = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(3), context));
        return input.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sprig <c>trunc</c>: keeps the first N characters when N is positive, or the last
    /// |N| when negative. N=0 empties the string; |N| ≥ length returns the input unchanged.
    /// </summary>
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

    /// <summary>Sprig <c>trimSuffix</c>: removes a trailing suffix when present.</summary>
    public static string TrimSuffix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var suffix = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.EndsWith(suffix, StringComparison.Ordinal) ? input[..^suffix.Length] : input;
    }

    /// <summary>Sprig <c>trimPrefix</c>: removes a leading prefix when present.</summary>
    public static string TrimPrefix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var prefix = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.StartsWith(prefix, StringComparison.Ordinal) ? input[prefix.Length..] : input;
    }

    /// <summary>Sprig <c>contains</c>: substring test (needle first).</summary>
    public static bool Contains(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var needle = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sprig <c>printf</c>: supports the <c>%s</c>/<c>%v</c>/<c>%d</c>/<c>%f</c>/<c>%q</c>
    /// verbs used in charts; width/precision/flags are not supported. Replacement order
    /// is left-to-right per argument, matching Go's sequential consumption of operands.
    /// </summary>
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

    /// <summary>Sprig <c>prepend</c>: new list with ITEM inserted before the list's elements.</summary>
    public static object? Prepend(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var item = eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var result = new List<object?> { item };
        result.AddRange(list);
        return result;
    }

    /// <summary>Sprig <c>append</c>: new list with ITEM appended after the list's elements.</summary>
    public static object? Append(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var item = eval.EvaluateToken(tokens.ElementAtOrDefault(2), context);
        var result = new List<object?>(list) { item };
        return result;
    }

    /// <summary>Sprig <c>without</c>: copy of the list with the given values removed (comparison by string form).</summary>
    public static object? Without(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var exclude = tokens.Skip(2).Select(t => TypeConverters.ToTemplateString(eval.EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        return list.Where(x => !exclude.Contains(TypeConverters.ToTemplateString(x))).ToList();
    }

    /// <summary>
    /// Sprig <c>has</c>: membership test — Helm signature is <c>has NEEDLE LIST</c>
    /// (needle first), or <c>LIST | has NEEDLE</c>. Elements compare by string form.
    /// </summary>
    public static object? Has(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        // Helm signature: has NEEDLE LIST  or  LIST | has NEEDLE
        var needle = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var list = CollectionsHelpers.ToList(pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return list.Any(x => TypeConverters.ToTemplateString(x) == needle);
    }

    /// <summary>Sprig <c>concat</c>: flattens all argument lists (and the pipeline value) into one new list.</summary>
    public static object? Concat(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var result = new List<object?>();
        if (pipelineValue != null) result.AddRange(CollectionsHelpers.ToList(pipelineValue));
        foreach (var t in tokens.Skip(1))
            result.AddRange(CollectionsHelpers.ToList(eval.EvaluateToken(t, context)));
        return result;
    }

    // ── Comparison ──

    /// <summary>
    /// Sprig <c>eq</c>: same-type values compare directly (numeric with a small epsilon);
    /// cross-type values compare by their string forms. Null equals only null.
    /// </summary>
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

    /// <summary>
    /// Shared implementation for Sprig's <c>lt</c>/<c>gt</c>/<c>le</c>/<c>ge</c>.
    /// Ordering is delegated to <see cref="TypeFunctions.CompareValues"/>; the
    /// <paramref name="cmp"/> predicate tests the comparison result against zero.
    /// </summary>
    public static object? CompareOp(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, Func<int, int, bool> cmp, IEvaluationContext eval)
    {
        var a = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var b = eval.EvaluateToken(tokens.ElementAtOrDefault(pipelineValue != null ? 1 : 2), context);
        var result = TypeFunctions.CompareValues(a, b);
        return cmp(result, 0);
    }
}
