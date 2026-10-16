using HelmSharp.Chart;

namespace HelmSharp.Kube;

/// <summary>
/// Merges the previous desired manifest, new chart output, and current live resource during upgrades.
/// </summary>
internal static class ThreeWayMerge
{
    /// <summary>
    /// Performs a three-way merge on a single resource and returns the merged YAML to apply.
    /// </summary>
    /// <param name="lastManifest">Manifest as of the previously deployed revision (the common ancestor).</param>
    /// <param name="newManifest">Desired manifest produced by the new chart render.</param>
    /// <param name="liveManifest">Current live resource as returned by the cluster, or null when it does not exist.</param>
    /// <param name="customListMapKeys">Declared list-map keys for custom resource paths, or null to use built-in Kubernetes defaults.</param>
    /// <returns>
    /// The desired manifest when there is no live resource; otherwise the merged manifest that
    /// keeps user-side drift which does not conflict with chart changes.
    /// </returns>
    public static string MergeResource(
        string lastManifest,
        string newManifest,
        string? liveManifest,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys = null)
    {
        var lastDoc = HelmYaml.DeserializeDictionary(lastManifest, StringComparer.Ordinal);
        var newDoc = HelmYaml.DeserializeDictionary(newManifest, StringComparer.Ordinal);

        // No live resource: nothing to reconcile, apply the desired manifest as-is.
        if (liveManifest is null)
            return newManifest;

        var liveDoc = HelmYaml.DeserializeDictionary(liveManifest, StringComparer.Ordinal);

        // Start with the new manifest
        var merged = DeepCopyDict(newDoc);

        // Deep merge at every level
        DeepThreeWayMerge(lastDoc, newDoc, liveDoc, merged, isRoot: true, isMetadata: false, path: "", customListMapKeys: customListMapKeys);

        return HelmYaml.Serialize(merged);
    }

    // Recursive three-way merge. Invariant: `merged` starts as a copy of `newVals` and is
    // only rewritten where the live resource diverged from `last` while the chart did not —
    // that pattern is user intent and must survive the upgrade (Helm's three-way merge rule).
    private static void DeepThreeWayMerge(
        IDictionary<string, object?> last,
        IDictionary<string, object?> newVals,
        IDictionary<string, object?> live,
        IDictionary<string, object?> merged,
        bool isRoot,
        bool isMetadata,
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys)
    {
        // Collect all keys from all three sources
        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in last.Keys) allKeys.Add(k);
        foreach (var k in newVals.Keys) allKeys.Add(k);
        foreach (var k in live.Keys) allKeys.Add(k);

        foreach (var key in allKeys)
        {
            // Status is cluster-owned at the resource root; identity bookkeeping is cluster-owned
            // only beneath metadata. Nested user fields with the same names remain chart data.
            if (isRoot && key == "status")
            {
                // A chart cannot own status, but replacing a CRD without a status subresource
                // can persist it. Keep the controller's value when the chart omits status.
                if (!newVals.ContainsKey(key) && live.TryGetValue(key, out var liveStatus))
                    merged[key] = DeepCopyValue(liveStatus);
                else
                    merged.Remove(key);
                continue;
            }

            if (isMetadata && key is "resourceVersion" or "uid" or "generation" or "creationTimestamp" or
                "managedFields" or "selfLink")
            {
                // `merged` begins with chart output, so skipping reconciliation alone would
                // still send chart-supplied cluster bookkeeping fields in the replacement.
                merged.Remove(key);
                continue;
            }

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
                    mergedChildDict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    merged[key] = mergedChildDict;
                }
                DeepThreeWayMerge(
                    lastDict,
                    newDict,
                    liveDict,
                    mergedChildDict,
                    isRoot: false,
                    isMetadata: isRoot && key == "metadata",
                    path: AppendPath(path, key),
                    customListMapKeys: customListMapKeys);
                continue;
            }

            // If the chart removes an entire mapping, reconcile its children individually so
            // unchanged chart fields disappear while live edits and live-only children survive.
            if (hasLive && hasLast && !hasNew &&
                lastVal is IDictionary<string, object?> removedLastMap &&
                liveVal is IDictionary<string, object?> removedLiveMap)
            {
                var remaining = PreserveLiveOnlyFields(removedLastMap, removedLiveMap);
                if (remaining.Count == 0)
                    merged.Remove(key);
                else
                    merged[key] = remaining;
                continue;
            }

            // Kubernetes list-map fields such as pod containers are owned per named item;
            // a chart edit to one item must not discard admission-added live items.
            if (hasLast && hasNew && hasLive &&
                lastVal is IList<object?> lastList &&
                newVal is IList<object?> newList &&
                liveVal is IList<object?> liveList &&
                TryMergeKeyedLists(
                    lastList,
                    newList,
                    liveList,
                    AppendPath(path, key),
                    customListMapKeys,
                    out var mergedList))
            {
                merged[key] = mergedList;
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
            else if (!hasLive && hasLast && hasNew)
            {
                // A live-only deletion survives when the chart leaves the prior value unchanged.
                if (DeepEquals(lastVal, newVal))
                    merged.Remove(key);
            }
            // All other cases: keep the merged (new) value
        }
    }

    private static Dictionary<string, object?> PreserveLiveOnlyFields(
        IDictionary<string, object?> last,
        IDictionary<string, object?> live)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, liveValue) in live)
        {
            if (!last.TryGetValue(key, out var lastValue))
            {
                result[key] = DeepCopyValue(liveValue);
            }
            else if (lastValue is IDictionary<string, object?> lastChild &&
                     liveValue is IDictionary<string, object?> liveChild)
            {
                var liveOnlyChildren = PreserveLiveOnlyFields(lastChild, liveChild);
                if (liveOnlyChildren.Count > 0)
                    result[key] = liveOnlyChildren;
            }
        }

        return result;
    }

    private static bool TryMergeKeyedLists(
        IList<object?> last,
        IList<object?> newValues,
        IList<object?> live,
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys,
        out List<object?> merged)
    {
        merged = [];
        // A CRD schema is authoritative: paths absent from its map-key table are atomic lists.
        var keySets = GetListMapKeySets(path, customListMapKeys);
        Dictionary<string, IDictionary<string, object?>> lastItems = [];
        Dictionary<string, IDictionary<string, object?>> newItems = [];
        Dictionary<string, IDictionary<string, object?>> liveItems = [];
        string[]? selectedKeySet = null;
        foreach (var keySet in keySets)
        {
            if (!TryIndexItems(last, keySet, out var candidateLast) ||
                !TryIndexItems(newValues, keySet, out var candidateNew) ||
                !TryIndexItems(live, keySet, out var candidateLive))
                continue;

            selectedKeySet = keySet;
            lastItems = candidateLast;
            newItems = candidateNew;
            liveItems = candidateLive;
            break;
        }

        if (selectedKeySet is null)
            return false;

        foreach (var item in newValues)
        {
            var newItem = (IDictionary<string, object?>)item!;
            var key = GetItemKey(newItem, selectedKeySet);
            var hasLast = lastItems.TryGetValue(key, out var lastItem);
            var hasLive = liveItems.TryGetValue(key, out var liveItem);
            if (hasLast && !hasLive && DeepEquals(lastItem, newItem))
                continue;

            var mergedItem = DeepCopyDictionary(newItem);
            if (hasLast && hasLive)
                DeepThreeWayMerge(
                    lastItem!,
                    newItem,
                    liveItem!,
                    mergedItem,
                    isRoot: false,
                    isMetadata: false,
                    path: path + "[]",
                    customListMapKeys: customListMapKeys);
            merged.Add(mergedItem);
        }

        foreach (var item in live)
        {
            var liveItem = (IDictionary<string, object?>)item!;
            var key = GetItemKey(liveItem, selectedKeySet);
            if (!lastItems.ContainsKey(key) && !newItems.ContainsKey(key))
                merged.Add(DeepCopyDictionary(liveItem));
        }

        return true;
    }

    internal static string[][] GetListMapKeySets(
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys)
        => customListMapKeys is not null
            ? customListMapKeys.TryGetValue(path, out var customKeys)
                ? [customKeys.ToArray()]
                : []
            : path.EndsWith(".volumeMounts", StringComparison.Ordinal)
                ? [["mountPath"]]
            :
            [
                ["containerPort", "protocol"],
                ["port", "protocol"],
                ["name"],
                ["mountPath"],
                ["devicePath"],
                ["ip"],
                ["path"],
                ["host"],
                ["topologyKey", "whenUnsatisfiable"]
            ];

    private static string AppendPath(string path, string key)
        => path.Length == 0 ? key : path + "." + key;

    private static bool TryIndexItems(
        IList<object?> values,
        IReadOnlyList<string> keyFields,
        out Dictionary<string, IDictionary<string, object?>> items)
    {
        items = new Dictionary<string, IDictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is not IDictionary<string, object?> item ||
                !TryGetItemKey(item, keyFields, out var key) ||
                !items.TryAdd(key, item))
                return false;
        }

        return true;
    }

    private static bool TryGetItemKey(
        IDictionary<string, object?> item,
        IReadOnlyList<string> keyFields,
        out string key)
    {
        var values = new string[keyFields.Count];
        for (var index = 0; index < keyFields.Count; index++)
        {
            var field = keyFields[index];
            if (item.TryGetValue(field, out var value) && value is not null)
            {
                values[index] = System.Text.Json.JsonSerializer.Serialize(value);
            }
            else if (field == "protocol" &&
                     (keyFields.Contains("containerPort") || keyFields.Contains("port")))
            {
                values[index] = "\"TCP\"";
            }
            else if (value is null)
            {
                key = string.Empty;
                return false;
            }
        }

        key = string.Join("\u001f", values);
        return true;
    }

    private static string GetItemKey(IDictionary<string, object?> item, IReadOnlyList<string> keyFields)
    {
        _ = TryGetItemKey(item, keyFields, out var key);
        return key;
    }

    private static Dictionary<string, object?> DeepCopyDictionary(IDictionary<string, object?> value)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, item) in value)
            copy[key] = DeepCopyValue(item);
        return copy;
    }

    /// <summary>
    /// Structural equality for deserialized YAML values. Numeric types are compared across
    /// long/int/double with a small epsilon for doubles so YAML type inference differences
    /// (e.g. an int in one document, a double in another) do not count as drift.
    /// </summary>
    private static bool DeepEquals(object? a, object? b)
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
        if (a is long l3 && b is double d1) return Math.Abs(l3 - d1) < 1e-10;
        if (a is double d2 && b is long l4) return Math.Abs(d2 - l4) < 1e-10;

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

    // Ordinal deep copy so case-sensitive Kubernetes map keys remain distinct.
    private static Dictionary<string, object?> DeepCopyDict(Dictionary<string, object?> dict)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
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
                StringComparer.Ordinal),
            IList<object?> list => list.Select(DeepCopyValue).ToList(),
            string s => new string(s),
            _ => value
        };
    }
}
