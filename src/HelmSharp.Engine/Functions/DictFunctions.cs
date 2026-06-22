namespace HelmSharp.Engine;

/// <summary>
/// Dictionary and lookup template functions. All use IEvaluationContext for token resolution.
/// </summary>
internal static class DictFunctions
{
    // ── Dict construction ──

    public static object Dict(IReadOnlyList<string> tokens, TemplateContext context, IEvaluationContext eval)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var args = tokens.Skip(1).ToList();
        for (var i = 0; i < args.Count; i += 2)
        {
            var key = TypeConverters.ToTemplateString(eval.EvaluateToken(args[i], context));
            var value = i + 1 < args.Count ? eval.EvaluateToken(args[i + 1], context) : null;
            dict[key] = value;
        }
        return dict;
    }

    public static object? Set(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var dict = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var key = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var value = eval.EvaluateToken(tokens.ElementAtOrDefault(3), context);
        if (dict is Dictionary<string, object?> d)
        {
            d[key] = value;
            return d;
        }
        return dict;
    }

    public static object? Unset(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var dict = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var key = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(2), context));
        if (dict is Dictionary<string, object?> d)
        {
            d.Remove(key);
            return d;
        }
        return dict;
    }

    public static object? MergeDicts(IReadOnlyList<string> tokens, TemplateContext context, IEvaluationContext eval)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens.Skip(1))
        {
            var val = eval.EvaluateToken(t, context);
            if (val is IDictionary<string, object?> dict)
                CollectionsHelpers.MergeInto(result, dict);
        }
        return result;
    }

    public static object? Pick(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var dict = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var keys = tokens.Skip(2).Select(t => TypeConverters.ToTemplateString(eval.EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        if (pipelineValue != null)
            keys = tokens.Skip(1).Select(t => TypeConverters.ToTemplateString(eval.EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (dict is IDictionary<string, object?> d)
        {
            foreach (var kvp in d)
                if (keys.Contains(kvp.Key))
                    result[kvp.Key] = kvp.Value;
        }
        return result;
    }

    public static object? Omit(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var dict = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var keys = tokens.Skip(2).Select(t => TypeConverters.ToTemplateString(eval.EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        if (pipelineValue != null)
            keys = tokens.Skip(1).Select(t => TypeConverters.ToTemplateString(eval.EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (dict is IDictionary<string, object?> d)
        {
            foreach (var kvp in d)
                if (!keys.Contains(kvp.Key))
                    result[kvp.Key] = kvp.Value;
        }
        return result;
    }

    public static object? Pluck(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var key = TypeConverters.ToTemplateString(eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var dicts = tokens.Skip(2).Select(t => eval.EvaluateToken(t, context));
        if (pipelineValue != null)
            dicts = tokens.Skip(1).Select(t => eval.EvaluateToken(t, context));
        var result = new List<object?>();
        foreach (var d in dicts)
        {
            if (d is IDictionary<string, object?> dict && dict.TryGetValue(key, out var val))
                result.Add(val);
        }
        return result;
    }

    public static object? Dig(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var args = tokens.Skip(1).Select(t => eval.EvaluateToken(t, context)).ToList();
        if (pipelineValue != null) args.Insert(0, pipelineValue);
        if (args.Count < 2) return null;

        var current = args[0];
        var defaultVal = args[^1];
        for (var i = 1; i < args.Count - 1; i++)
        {
            var key = TypeConverters.ToTemplateString(args[i]);
            current = current switch
            {
                Dictionary<string, object?> dict when dict.TryGetValue(key, out var next) => next,
                IDictionary<string, object?> dict when dict.TryGetValue(key, out var next) => next,
                _ => null
            };
            if (current is null) return defaultVal;
        }
        return current;
    }

    public static object? Index(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var value = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var keyTokens = pipelineValue is null ? tokens.Skip(2) : tokens.Skip(1);
        foreach (var keyToken in keyTokens)
        {
            value = IndexOne(value, eval.EvaluateToken(keyToken, context));
        }
        return value;
    }

    public static object? Get(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var dict = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var key = pipelineValue is null
            ? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context)
            : eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        return IndexOne(dict, key);
    }

    public static bool HasKey(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var dict = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var key = TypeConverters.ToTemplateString(pipelineValue is null
            ? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context)
            : eval.EvaluateToken(tokens.ElementAtOrDefault(1), context));
        return dict switch
        {
            Dictionary<string, object?> d => d.ContainsKey(key),
            IDictionary<string, object?> d => d.ContainsKey(key),
            _ => false
        };
    }

    public static object? Lookup(IReadOnlyList<string> tokens, TemplateContext context, IEvaluationContext eval)
    {
        // In managed mode, return empty dict — no cluster access during template rendering
        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    // ── Helpers ──

    internal static object? IndexOne(object? value, object? key)
    {
        var keyString = TypeConverters.ToTemplateString(key);
        return value switch
        {
            Dictionary<string, object?> dict when dict.TryGetValue(keyString, out var next) => next,
            IDictionary<string, object?> dict when dict.TryGetValue(keyString, out var next) => next,
            IReadOnlyList<object?> list when int.TryParse(keyString, out var index) && index >= 0 && index < list.Count => list[index],
            IList<object?> list when int.TryParse(keyString, out var index) && index >= 0 && index < list.Count => list[index],
            _ => null
        };
    }
}
