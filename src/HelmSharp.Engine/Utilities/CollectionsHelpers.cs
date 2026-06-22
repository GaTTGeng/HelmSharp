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
}
