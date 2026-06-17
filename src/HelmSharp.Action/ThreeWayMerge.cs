using HelmSharp.Chart;

namespace HelmSharp.Action;

/// <summary>
/// Three-way merge for Kubernetes resources during upgrades.
/// Compares: last deployed manifest vs new manifest vs live resource.
/// Preserves manual modifications that don't conflict with the new manifest.
/// </summary>
internal static class ThreeWayMerge
{
    /// <summary>
    /// Performs a three-way merge on a single resource.
    /// Returns the merged YAML to apply.
    /// </summary>
    public static string MergeResource(
        string lastManifest,
        string newManifest,
        string? liveManifest)
    {
        var lastDoc = HelmYaml.DeserializeDictionary(lastManifest);
        var newDoc = HelmYaml.DeserializeDictionary(newManifest);

        if (liveManifest is null)
            return newManifest;

        var liveDoc = HelmYaml.DeserializeDictionary(liveManifest);

        // Start with the new manifest
        var merged = DeepCopyDict(newDoc);

        // Deep merge at every level
        DeepThreeWayMerge(lastDoc, newDoc, liveDoc, merged);

        return HelmYaml.Serialize(merged);
    }

    private static void DeepThreeWayMerge(
        IDictionary<string, object?> last,
        IDictionary<string, object?> newVals,
        IDictionary<string, object?> live,
        IDictionary<string, object?> merged)
    {
        // Collect all keys from all three sources
        var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in last.Keys) allKeys.Add(k);
        foreach (var k in newVals.Keys) allKeys.Add(k);
        foreach (var k in live.Keys) allKeys.Add(k);

        foreach (var key in allKeys)
        {
            // Skip fields managed by Kubernetes
            if (key is "resourceVersion" or "uid" or "generation" or "creationTimestamp" or
                "managedFields" or "selfLink" or "ownerReferences" or "status")
                continue;

            var hasLast = last.TryGetValue(key, out var lastVal);
            var hasNew = newVals.TryGetValue(key, out var newVal);
            var hasLive = live.TryGetValue(key, out var liveVal);

            // If both last and new have nested dicts, recurse
            if (hasLast && hasNew && hasLive &&
                lastVal is IDictionary<string, object?> lastDict &&
                newVal is IDictionary<string, object?> newDict &&
                liveVal is IDictionary<string, object?> liveDict)
            {
                // Ensure merged has a dict for this key
                if (!merged.TryGetValue(key, out var mergedChild) ||
                    mergedChild is not IDictionary<string, object?> mergedChildDict)
                {
                    mergedChildDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    merged[key] = mergedChildDict;
                }
                else
                {
                    mergedChildDict = (IDictionary<string, object?>)mergedChild;
                }

                DeepThreeWayMerge(lastDict, newDict, liveDict, mergedChildDict);
                continue;
            }

            // Three-way decision:
            // 1. Key only in live (added by user) -> preserve if last didn't have it
            // 2. Key only in new (added by chart) -> keep new
            // 3. Key in all three -> check if user changed it
            if (hasLive && !hasLast && !hasNew)
            {
                // User added this key, and new chart doesn't have it -> preserve
                merged[key] = liveVal;
            }
            else if (hasLive && hasLast && hasNew)
            {
                // Key exists in all three
                if (!DeepEquals(lastVal, liveVal) && DeepEquals(lastVal, newVal))
                {
                    // User changed it, chart didn't -> preserve user's change
                    merged[key] = liveVal;
                }
                // Otherwise keep new value (already in merged)
            }
            else if (hasLive && hasLast && !hasNew)
            {
                // Key was in last and live, but removed in new
                if (!DeepEquals(lastVal, liveVal))
                {
                    // User modified it before chart removed it -> preserve
                    merged[key] = liveVal;
                }
                // Otherwise let it be removed (chart intentionally removed it)
            }
            // All other cases: keep the merged (new) value
        }
    }

    internal static bool DeepEquals(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        if (a is string sa && b is string sb)
            return string.Equals(sa, sb, StringComparison.Ordinal);

        if (a is bool ba && b is bool bb) return ba == bb;

        // Numeric comparisons
        if (a is long la && b is long lb) return la == lb;
        if (a is int ia && b is int ib) return ia == ib;
        if (a is double da && b is double db) return Math.Abs(da - db) < 1e-10;
        if (a is long l1 && b is int i1) return l1 == i1;
        if (a is int i2 && b is long l2) return i2 == l2;
        if (a is long l3 && b is double d1) return l3 == d1;
        if (a is double d2 && b is long l4) return d2 == l4;

        // Dictionary comparison
        if (a is IDictionary<string, object?> dictA && b is IDictionary<string, object?> dictB)
        {
            if (dictA.Count != dictB.Count) return false;
            foreach (var kvp in dictA)
            {
                if (!dictB.TryGetValue(kvp.Key, out var bVal) || !DeepEquals(kvp.Value, bVal))
                    return false;
            }
            return true;
        }

        // List comparison
        if (a is IList<object?> listA && b is IList<object?> listB)
        {
            if (listA.Count != listB.Count) return false;
            for (var i = 0; i < listA.Count; i++)
                if (!DeepEquals(listA[i], listB[i])) return false;
            return true;
        }

        return a.Equals(b);
    }

    private static Dictionary<string, object?> DeepCopyDict(Dictionary<string, object?> dict)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in dict)
        {
            result[kvp.Key] = DeepCopyValue(kvp.Value);
        }
        return result;
    }

    private static object? DeepCopyValue(object? value)
    {
        return value switch
        {
            Dictionary<string, object?> d => DeepCopyDict(d),
            IDictionary<string, object?> d => new Dictionary<string, object?>(
                d.ToDictionary(kv => kv.Key, kv => DeepCopyValue(kv.Value)),
                StringComparer.OrdinalIgnoreCase),
            IList<object?> list => list.Select(DeepCopyValue).ToList(),
            string s => new string(s),
            _ => value
        };
    }
}
