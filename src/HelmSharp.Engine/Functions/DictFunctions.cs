namespace HelmSharp.Engine;

/// <summary>
/// Dictionary and lookup template functions. All use IEvaluationContext for token resolution.
/// </summary>
internal static class DictFunctions
{
    // ── Dict construction ──

    /// <summary>
    /// Sprig <c>dict</c>: builds a map from alternating KEY VALUE arguments.
    /// A trailing key without a value maps to null. Keys compare case-insensitively.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>set</c>: mutates the dict in place (set DICT KEY VALUE) and returns it
    /// for chaining. Non-dict values are returned unchanged without error.
    /// </summary>
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

    /// <summary>Sprig <c>unset</c>: removes a key in place and returns the dict; missing keys are a no-op.</summary>
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

    /// <summary>
    /// Sprig <c>merge</c>: deep-merges every dict argument into a new dict
    /// (first source wins at conflicting leaves — see <see cref="CollectionsHelpers.MergeInto"/>).
    /// </summary>
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

    /// <summary>
    /// Sprig <c>pick</c>: new dict containing only the listed keys.
    /// Signature: <c>pick DICT KEY…</c> or <c>DICT | pick KEY…</c> (keys shift
    /// earlier in the token stream when a pipeline value is present).
    /// </summary>
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

    /// <summary>Sprig <c>omit</c>: new dict excluding the listed keys (pipeline form shifts key tokens as in <see cref="Pick"/>).</summary>
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

    /// <summary>
    /// Sprig <c>pluck</c>: collects the value of KEY from each dict argument into a
    /// list, skipping dicts that lack the key. Signature: <c>plucks KEY DICT…</c>.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>dig</c>: walks a chain of keys (<c>dig KEY1 KEY2 … DEFAULT</c>) and
    /// returns DEFAULT as soon as any level is missing or not a map. The last
    /// argument is always the default, not a key.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>index</c>: multi-level lookup — each remaining argument is resolved
    /// and used to index the previous result (map key or list index). Missing
    /// intermediate values yield null rather than an error.
    /// </summary>
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

    /// <summary>Sprig <c>get</c>: single-key map lookup; returns null when the key is absent (unlike direct <c>.key</c> access which errors).</summary>
    public static object? Get(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, IEvaluationContext eval)
    {
        var dict = pipelineValue ?? eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var key = pipelineValue is null
            ? eval.EvaluateToken(tokens.ElementAtOrDefault(2), context)
            : eval.EvaluateToken(tokens.ElementAtOrDefault(1), context);
        return IndexOne(dict, key);
    }

    /// <summary>Sprig <c>hasKey</c>: true when the map contains the key (case-insensitive key comparison).</summary>
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

    /// <summary>
    /// Sprig <c>lookup</c>: in managed (non-cluster) mode this always returns an empty
    /// dict — no live cluster access happens during template rendering. Templates that
    /// branch on lookup results will see "resource not found" behavior.
    /// </summary>
    public static object? Lookup(IReadOnlyList<string> tokens, TemplateContext context, IEvaluationContext eval)
    {
        // In managed mode, return empty dict — no cluster access during template rendering
        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    // ── Helpers ──

    /// <summary>
    /// Single-step indexing used by <c>index</c>/<c>get</c>/<c>dig</c>. Keys are
    /// matched by their string form: map keys directly, lists by zero-based integer
    /// index. Out-of-range or non-numeric list keys yield null, matching Go's
    /// missing-key behavior in templates rather than panicking.
    /// </summary>
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
