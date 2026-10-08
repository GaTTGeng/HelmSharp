namespace HelmSharp.Engine;

/// <summary>
/// List and dictionary manipulation helpers used by template functions.
/// </summary>
internal static class CollectionsHelpers
{
    // ── List helpers ──

    /// <summary>Sprig <c>first</c>: first element, or null for an empty list.</summary>
    public static object? First(object? value)
        => value is IList<object?> { Count: > 0 } list ? list[0] : null;

    /// <summary>Sprig <c>last</c>: final element, or null for an empty list.</summary>
    public static object? Last(object? value)
        => value is IList<object?> { Count: > 0 } list ? list[^1] : null;

    /// <summary>Sprig <c>rest</c>: all but the first element; empty input yields an empty list (not null).</summary>
    public static object? Rest(object? value)
        => value is IList<object?> { Count: > 0 } list ? list.Skip(1).ToList() : new List<object?>();

    /// <summary>Sprig <c>initial</c>: all but the last element; empty input yields an empty list (not null).</summary>
    public static object? Initial(object? value)
        => value is IList<object?> { Count: > 0 } list ? list.Take(list.Count - 1).ToList() : new List<object?>();

    /// <summary>Sprig <c>reverse</c>: new reversed copy; non-list values pass through unchanged.</summary>
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

    /// <summary>
    /// Sprig <c>sortAlpha</c>: ascending sort by each element's string form,
    /// ordinal (culture-invariant) comparison — not a locale-aware sort.
    /// </summary>
    public static object? SortAlpha(object? value)
    {
        if (value is IList<object?> list)
            return list.OrderBy(x => TypeConverters.ToTemplateString(x), StringComparer.Ordinal).ToList();
        return value;
    }

    /// <summary>Sprig <c>compact</c>: drops falsy elements (null, false, 0, "", empty collections) via <see cref="TypeConverters.IsTruthy"/>.</summary>
    public static object? Compact(object? value)
    {
        if (value is IList<object?> list)
            return list.Where(TypeConverters.IsTruthy).ToList();
        return value;
    }

    /// <summary>
    /// Sprig <c>uniq</c>: first occurrence of each distinct element, preserving order.
    /// Distinctness is by string form, so 1 and "1" collide.
    /// </summary>
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

    /// <summary>Materializes any sequence as a mutable <see cref="List{T}"/> copy; scalars become an empty list.</summary>
    public static List<object?> ToList(object? value)
        => value switch
        {
            IList<object?> list => new List<object?>(list),
            IEnumerable<object?> e => e.ToList(),
            _ => new List<object?>()
        };

    // ── Dict helpers ──

    /// <summary>Sprig <c>keys</c>: sorted key list; non-dicts yield an empty list.</summary>
    public static object? Keys(object? value)
    {
        if (value is IDictionary<string, object?> dict)
            return dict.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        return new List<object?>();
    }

    /// <summary>
    /// Sprig <c>values</c>: values ordered by their keys (so each position lines up
    /// with <see cref="Keys"/>), not dictionary enumeration order.
    /// </summary>
    public static object? Values(object? value)
    {
        if (value is IDictionary<string, object?> dict)
            return dict.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => dict[k]).ToList();
        return new List<object?>();
    }

    /// <summary>
    /// Sprig <c>merge</c> semantics: deep-merges <paramref name="source"/> into
    /// <paramref name="target"/> in place. Nested maps merge recursively; existing
    /// non-map values are overwritten (left-to-right, first source wins at leaves).
    /// </summary>
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

    /// <summary>
    /// Sprig <c>deepCopy</c>: recursively copies nested maps and lists so the result
    /// can be mutated without affecting the source. Scalars and strings are returned
    /// as-is — both C# and Go treat them as immutable values.
    /// </summary>
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

    /// <summary>Sprig <c>join</c>: joins sequence elements with a separator using their string forms; scalars are stringified unchanged.</summary>
    public static string Join(object? value, string separator)
    {
        if (value is IEnumerable<object?> e)
            return string.Join(separator, e.Select(TypeConverters.ToTemplateString));
        return TypeConverters.ToTemplateString(value);
    }

    // Sprig split: returns dict with _0, _1, … keys for field access
    /// <summary>
    /// Sprig <c>split</c>: splits on a literal separator into a dict keyed
    /// <c>_0</c>, <c>_1</c>, … — designed for <c>(split "," "a,b")._0</c> access
    /// patterns. Use <see cref="SplitList"/> when a real list is wanted.
    /// </summary>
    public static Dictionary<string, object?> Split(string input, string separator)
    {
        var parts = input.Split(separator, StringSplitOptions.None);
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < parts.Length; i++)
            dict[$"_{i}"] = parts[i];
        return dict;
    }

    /// <summary>Sprig <c>splitList</c>: splits on a literal separator into a list.</summary>
    public static List<object?> SplitList(string input, string separator)
    {
        var parts = input.Split(separator, StringSplitOptions.None);
        return parts.Cast<object?>().ToList();
    }

    /// <summary>
    /// Sprig <c>slice</c>: extracts [START, END) from any sequence. Negative indices
    /// count from the end; omitted END means "to the end". Out-of-range or inverted
    /// bounds yield an empty list rather than an error (unlike <c>substr</c>).
    /// </summary>
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
    /// <summary>
    /// Sprig <c>mergeOverwrite</c>: like <c>merge</c> but later dictionaries win at
    /// leaf level (right-to-left precedence). Returns a new dictionary; inputs are
    /// not mutated. Non-dict arguments are ignored.
    /// </summary>
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

    // Sprig's must* family: same behavior as the non-must variant but throws on
    // invalid input instead of silently degrading. Keep pairs in sync.

    /// <summary>Sprig <c>mustReverse</c>: like <see cref="Reverse"/> but throws when the value is not a list.</summary>
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

    /// <summary>Sprig <c>mustSortAlpha</c>: like <see cref="SortAlpha"/> but throws when the value is not a list.</summary>
    public static object? MustSortAlpha(object? value)
    {
        if (value is IList<object?> list)
            return list.OrderBy(x => TypeConverters.ToTemplateString(x), StringComparer.Ordinal).ToList();
        throw new InvalidOperationException("mustSortAlpha: argument is not a list");
    }

    /// <summary>Sprig <c>mustCompact</c>: like <see cref="Compact"/> but throws when the value is not a list.</summary>
    public static object? MustCompact(object? value)
    {
        if (value is IList<object?> list)
            return list.Where(TypeConverters.IsTruthy).ToList();
        throw new InvalidOperationException("mustCompact: argument is not a list");
    }

    /// <summary>Sprig <c>mustUniq</c>: like <see cref="Uniq"/> but throws when the value is not a list.</summary>
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
