namespace HelmSharp.Engine;

/// <summary>
/// List and dictionary manipulation helpers used by template functions.
/// </summary>
internal static class CollectionsHelpers
{
    // ── List helpers ──

    public static object? First(object? value)
        => value is IList<object?> { Count: > 0 } list ? list[0] : null;

    public static object? Last(object? value)
        => value is IList<object?> { Count: > 0 } list ? list[^1] : null;

    public static object? Rest(object? value)
        => value is IList<object?> { Count: > 0 } list ? list.Skip(1).ToList() : new List<object?>();

    public static object? Initial(object? value)
        => value is IList<object?> { Count: > 0 } list ? list.Take(list.Count - 1).ToList() : new List<object?>();

    public static object? Reverse(object? value)
    {
        if (value is IList<object?> list)
        {
            var copy = new List<object?>(list);
            copy.Reverse();
            return copy;
        }
        return value;
    }

    public static object? SortAlpha(object? value)
    {
        if (value is IList<object?> list)
            return list.OrderBy(x => TypeConverters.ToTemplateString(x), StringComparer.Ordinal).ToList();
        return value;
    }

    public static object? Compact(object? value)
    {
        if (value is IList<object?> list)
            return list.Where(TypeConverters.IsTruthy).ToList();
        return value;
    }

    public static object? Uniq(object? value)
    {
        if (value is IList<object?> list)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<object?>();
            foreach (var item in list)
            {
                var key = TypeConverters.ToTemplateString(item);
                if (seen.Add(key))
                    result.Add(item);
            }
            return result;
        }
        return value;
    }

    public static List<object?> ToList(object? value)
        => value switch
        {
            IList<object?> list => new List<object?>(list),
            IEnumerable<object?> e => e.ToList(),
            _ => new List<object?>()
        };

    // ── Dict helpers ──

    public static object? Keys(object? value)
    {
        if (value is IDictionary<string, object?> dict)
            return dict.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        return new List<object?>();
    }

    public static object? Values(object? value)
    {
        if (value is IDictionary<string, object?> dict)
            return dict.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => dict[k]).ToList();
        return new List<object?>();
    }

    public static void MergeInto(Dictionary<string, object?> target, IDictionary<string, object?> source)
    {
        foreach (var kvp in source)
        {
            if (target.TryGetValue(kvp.Key, out var existing) &&
                existing is Dictionary<string, object?> existingDict &&
                kvp.Value is IDictionary<string, object?> valueDict)
            {
                MergeInto(existingDict, valueDict);
                continue;
            }
            target[kvp.Key] = kvp.Value;
        }
    }

    public static object? DeepCopy(object? value)
    {
        return value switch
        {
            Dictionary<string, object?> dict => dict.ToDictionary(
                kvp => kvp.Key,
                kvp => DeepCopy(kvp.Value),
                StringComparer.OrdinalIgnoreCase),
            IList<object?> list => list.Select(DeepCopy).ToList(),
            // Strings are immutable in both C# and Go — no defensive copy needed.
            // Identical to Helm's deepCopy semantics.
            _ => value
        };
    }

    // ── Sprig additional helpers ──

    public static string Join(object? value, string separator)
    {
        if (value is IEnumerable<object?> e)
            return string.Join(separator, e.Select(TypeConverters.ToTemplateString));
        return TypeConverters.ToTemplateString(value);
    }

    // Sprig split: returns dict with _0, _1, … keys for field access
    public static Dictionary<string, object?> Split(string input, string separator)
    {
        var parts = input.Split(separator, StringSplitOptions.None);
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < parts.Length; i++)
            dict[$"_{i}"] = parts[i];
        return dict;
    }

    public static List<object?> SplitList(string input, string separator)
    {
        var parts = input.Split(separator, StringSplitOptions.None);
        return parts.Cast<object?>().ToList();
    }

    public static object? Slice(object? value, int start, int? end)
    {
        var list = ToList(value);
        if (start >= list.Count) return new List<object?>();
        if (start < 0) start = Math.Max(0, list.Count + start);
        var stop = end ?? list.Count;
        if (stop < 0) stop = Math.Max(0, list.Count + stop);
        if (stop > list.Count) stop = list.Count;
        if (start >= stop) return new List<object?>();
        return list.Skip(start).Take(stop - start).ToList();
    }

    // Sprig mergeOverwrite: deep merge from left to right, right wins at leaf level.
    // Nested dicts are merged recursively; scalar values are overwritten.
    public static Dictionary<string, object?> MergeOverwrite(IReadOnlyList<object?> dicts)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dicts)
        {
            if (d is IDictionary<string, object?> dict)
                MergeOverwriteInto(result, dict);
        }
        return result;
    }

    private static void MergeOverwriteInto(Dictionary<string, object?> target, IDictionary<string, object?> source)
    {
        foreach (var kvp in source)
        {
            if (target.TryGetValue(kvp.Key, out var existing) &&
                existing is Dictionary<string, object?> existingDict &&
                kvp.Value is IDictionary<string, object?> sourceDict)
            {
                // Deep merge nested dictionaries
                MergeOverwriteInto(existingDict, sourceDict);
            }
            else
            {
                // Overwrite leaf values (including replacing a dict with a scalar)
                target[kvp.Key] = kvp.Value;
            }
        }
    }

    public static object? MustReverse(object? value)
    {
        if (value is IList<object?> list)
        {
            var copy = new List<object?>(list);
            copy.Reverse();
            return copy;
        }
        throw new InvalidOperationException("mustReverse: argument is not a list");
    }

    public static object? MustSortAlpha(object? value)
    {
        if (value is IList<object?> list)
            return list.OrderBy(x => TypeConverters.ToTemplateString(x), StringComparer.Ordinal).ToList();
        throw new InvalidOperationException("mustSortAlpha: argument is not a list");
    }

    public static object? MustCompact(object? value)
    {
        if (value is IList<object?> list)
            return list.Where(TypeConverters.IsTruthy).ToList();
        throw new InvalidOperationException("mustCompact: argument is not a list");
    }

    public static object? MustUniq(object? value)
    {
        if (value is IList<object?> list)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<object?>();
            foreach (var item in list)
            {
                var key = TypeConverters.ToTemplateString(item);
                if (seen.Add(key)) result.Add(item);
            }
            return result;
        }
        throw new InvalidOperationException("mustUniq: argument is not a list");
    }
}
