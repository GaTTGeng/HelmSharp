using HelmSharp.Chart;

namespace HelmSharp.Kube;

/// <summary>
/// Merges the previous desired manifest, new chart output, and current live resource during upgrades.
/// </summary>
internal static class ThreeWayMerge
{
    // Mark schema-declared atomic lists so built-in list-map defaults cannot apply.
    internal const string AtomicListSentinel = "\0";

    /// <summary>
    /// Performs a three-way merge on a single resource and returns the merged YAML to apply.
    /// </summary>
    /// <param name="lastManifest">Manifest as of the previously deployed revision (the common ancestor).</param>
    /// <param name="newManifest">Desired manifest produced by the new chart render.</param>
    /// <param name="liveManifest">Current live resource as returned by the cluster, or null when it does not exist.</param>
    /// <param name="customListMapKeys">Declared list-map keys for custom resource paths, or null to use built-in Kubernetes defaults.</param>
    /// <param name="preserveRootStatus">Whether root status is managed through a Kubernetes status subresource.</param>
    /// <param name="customListMapKeyDefaults">Schema defaults for custom list-map key fields.</param>
    /// <returns>
    /// The desired manifest when there is no live resource; otherwise the merged manifest that
    /// keeps user-side drift which does not conflict with chart changes.
    /// </returns>
    public static string MergeResource(
        string lastManifest,
        string newManifest,
        string? liveManifest,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys = null,
        bool preserveRootStatus = true,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? customListMapKeyDefaults = null)
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
        var resourceKind = HelmYaml.GetString(newDoc, "kind");
        DeepThreeWayMerge(lastDoc, newDoc, liveDoc, merged, isRoot: true, isMetadata: false, path: "", customListMapKeys: customListMapKeys, preserveRootStatus: preserveRootStatus, resourceKind: resourceKind, customListMapKeyDefaults: customListMapKeyDefaults);
        RemoveServiceFieldsDisallowedByType(merged);

        return HelmYaml.Serialize(merged);
    }

    private static void RemoveServiceFieldsDisallowedByType(IDictionary<string, object?> document)
    {
        if (!string.Equals(HelmYaml.GetString(document, "kind"), "Service", StringComparison.Ordinal) ||
            !document.TryGetValue("spec", out var specValue) || specValue is not IDictionary<string, object?> spec)
            return;

        var serviceType = HelmYaml.GetString(spec, "type") ?? "ClusterIP";
        if (serviceType != "LoadBalancer")
            spec.Remove("allocateLoadBalancerNodePorts");
        if (serviceType == "ExternalName")
        {
            spec.Remove("clusterIP");
            spec.Remove("clusterIPs");
            spec.Remove("ipFamilies");
            spec.Remove("ipFamilyPolicy");
        }

        if (serviceType != "LoadBalancer" ||
            !string.Equals(HelmYaml.GetString(spec, "externalTrafficPolicy"), "Local", StringComparison.Ordinal))
            spec.Remove("healthCheckNodePort");

        if ((serviceType is "ClusterIP" or "ExternalName") &&
            spec.TryGetValue("ports", out var portsValue) && portsValue is IList<object?> ports)
        {
            foreach (var port in ports.OfType<IDictionary<string, object?>>())
                port.Remove("nodePort");
        }
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
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys,
        bool preserveRootStatus,
        string? resourceKind,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? customListMapKeyDefaults)
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
            if (isRoot && key == "status" && preserveRootStatus)
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
            var childPath = AppendPath(path, key);

            if (hasLast && hasLive && IsAtomicMap(childPath, customListMapKeys, resourceKind))
            {
                // CRD atomic maps are a single owned field: preserve live drift only
                // while the chart leaves the complete map unchanged.
                if (hasNew && DeepEquals(lastVal, newVal))
                    merged[key] = DeepCopyValue(liveVal);
                else if (!hasNew)
                    merged.Remove(key);
                continue;
            }

            // An omitted map/set list is an empty desired list: reconcile ownership per item
            // so admission- or controller-added entries survive chart removal.
            if (hasLast && !hasNew && hasLive &&
                lastVal is IList<object?> removedLastList && liveVal is IList<object?> removedLiveList)
            {
                IList<object?> emptyList = Array.Empty<object?>();
                var listPath = AppendPath(path, key);
                if (TryMergeSetList(removedLastList, emptyList, removedLiveList, listPath, customListMapKeys, resourceKind, out var remainingSet))
                {
                    if (remainingSet.Count == 0)
                        merged.Remove(key);
                    else
                        merged[key] = remainingSet;
                    continue;
                }

                if (TryMergeKeyedLists(removedLastList, emptyList, removedLiveList, listPath, customListMapKeys, resourceKind, customListMapKeyDefaults, out var remainingList))
                {
                    if (remainingList.Count == 0)
                        merged.Remove(key);
                    else
                        merged[key] = remainingList;
                    continue;
                }
            }

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
                    customListMapKeys: customListMapKeys,
                    preserveRootStatus: preserveRootStatus,
                    resourceKind: resourceKind,
                    customListMapKeyDefaults: customListMapKeyDefaults);
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
                TryMergeSetList(lastList, newList, liveList, AppendPath(path, key), customListMapKeys, resourceKind, out var mergedSet))
            {
                merged[key] = mergedSet;
                continue;
            }

            if (hasLast && hasNew && hasLive &&
                lastVal is IList<object?> lastKeyedList &&
                newVal is IList<object?> newKeyedList &&
                liveVal is IList<object?> liveKeyedList &&
                TryMergeKeyedLists(
                    lastKeyedList,
                    newKeyedList,
                    liveKeyedList,
                    AppendPath(path, key),
                    customListMapKeys,
                    resourceKind,
                    customListMapKeyDefaults,
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
        string? resourceKind,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? customListMapKeyDefaults,
        out List<object?> merged)
    {
        merged = [];
        // A CRD schema is authoritative: paths absent from its map-key table are atomic lists.
        var keySets = GetListMapKeySets(path, customListMapKeys);
        var defaultTcpProtocol = IsBuiltInTcpPortList(path, customListMapKeys, resourceKind);
        var keyDefaults = customListMapKeyDefaults?.FirstOrDefault(entry => SchemaPathMatches(entry.Key, path)).Value;
        Dictionary<string, IDictionary<string, object?>> lastItems = [];
        Dictionary<string, IDictionary<string, object?>> newItems = [];
        Dictionary<string, IDictionary<string, object?>> liveItems = [];
        string[]? selectedKeySet = null;
        foreach (var keySet in keySets)
        {
            if (!TryIndexItems(last, keySet, defaultTcpProtocol, keyDefaults, out var candidateLast) ||
                !TryIndexItems(newValues, keySet, defaultTcpProtocol, keyDefaults, out var candidateNew) ||
                !TryIndexItems(live, keySet, defaultTcpProtocol, keyDefaults, out var candidateLive))
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
            var key = GetItemKey(newItem, selectedKeySet, defaultTcpProtocol, keyDefaults);
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
                    customListMapKeys: customListMapKeys,
                    preserveRootStatus: true,
                    resourceKind: resourceKind,
                    customListMapKeyDefaults: customListMapKeyDefaults);
            merged.Add(mergedItem);
        }

        // Preserve chart ordering while placing injected entries before their next live anchor.
        var liveKeys = live.Select(item => GetItemKey((IDictionary<string, object?>)item!, selectedKeySet, defaultTcpProtocol, keyDefaults)).ToArray();
        var ordered = new List<object?>();
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var newItem in merged)
        {
            var newKey = GetItemKey((IDictionary<string, object?>)newItem!, selectedKeySet, defaultTcpProtocol, keyDefaults);
            var anchorIndex = Array.IndexOf(liveKeys, newKey);
            if (anchorIndex >= 0)
            {
                for (var index = 0; index < anchorIndex; index++)
                {
                    var injectedKey = liveKeys[index];
                    if (!lastItems.ContainsKey(injectedKey) && !newItems.ContainsKey(injectedKey) && inserted.Add(injectedKey))
                        ordered.Add(DeepCopyDictionary((IDictionary<string, object?>)live[index]!));
                }
            }

            ordered.Add(newItem);
        }

        for (var index = 0; index < live.Count; index++)
        {
            var key = liveKeys[index];
            if (!lastItems.ContainsKey(key) && !newItems.ContainsKey(key) && inserted.Add(key))
                ordered.Add(DeepCopyDictionary((IDictionary<string, object?>)live[index]!));
        }
        merged = ordered;

        return true;
    }

    private static bool TryMergeSetList(
        IList<object?> last,
        IList<object?> newValues,
        IList<object?> live,
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys,
        string? resourceKind,
        out List<object?> merged)
    {
        merged = [];
        // Set lists are value keyed, so a live-only value survives unless the chart now owns that value.
        var customSet = customListMapKeys is not null &&
                        customListMapKeys.Any(entry => entry.Value.Count == 0 && SchemaPathMatches(entry.Key, path));
        var builtInSet = resourceKind == "ValidatingAdmissionPolicyBinding" && path == "spec.validationActions";
        if (!customSet && path != "metadata.finalizers" && !builtInSet)
            return false;

        foreach (var value in newValues)
        {
            // An unchanged chart value removed live is an operator deletion; only
            // reintroduce it when the chart itself changed the set membership.
            if (!last.Any(previous => DeepEquals(previous, value)) ||
                live.Any(current => DeepEquals(current, value)))
                merged.Add(DeepCopyValue(value));
        }

        foreach (var value in live)
        {
            if (!last.Any(previous => DeepEquals(previous, value)) &&
                !merged.Any(current => DeepEquals(current, value)))
                merged.Add(DeepCopyValue(value));
        }

        return true;
    }

    internal static string[][] GetListMapKeySets(
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys)
    {
        // ObjectMeta keeps Kubernetes list-map semantics even when a CRD supplies its own schema paths.
        if (path == "metadata.ownerReferences")
            return [["uid"]];

        if (customListMapKeys is not null)
        {
            var match = customListMapKeys.FirstOrDefault(entry => SchemaPathMatches(entry.Key, path));
            if (match.Key is not null)
            {
                if (match.Value is [] or [AtomicListSentinel])
                    return [];

                return [match.Value.ToArray()];
            }

            // CRD arrays default to atomic when their schema is unavailable or
            // does not declare list topology; built-in name heuristics do not apply.
            return [];
        }

        // Built-in keys follow Kubernetes list-map paths; other lists, including Ingress rules, are atomic.
        if (path == "spec.versions")
            return [["name"]];
        if (path == "webhooks")
            return [["name"]];
        if (path == "secrets")
            return [["name"]];
        if (path == "webhooks[].matchConditions")
            return [["name"]];
        if (path == "spec.matchConditions")
            return [["name"]];
        if (path.EndsWith(".volumeMounts", StringComparison.Ordinal))
            return [["mountPath"]];
        if (path.EndsWith(".volumeDevices", StringComparison.Ordinal))
            return [["devicePath"]];
        if (path.EndsWith(".containers[].ports", StringComparison.Ordinal) ||
            path.EndsWith(".initContainers[].ports", StringComparison.Ordinal) ||
            path.EndsWith(".ephemeralContainers[].ports", StringComparison.Ordinal))
            return [["containerPort", "protocol"]];
        if (path == "spec.ports")
            return [["port", "protocol"]];
        if (path.EndsWith(".hostAliases", StringComparison.Ordinal))
            return [["ip"]];
        if (path.EndsWith(".topologySpreadConstraints", StringComparison.Ordinal))
            return [["topologyKey", "whenUnsatisfiable"]];
        if (path.EndsWith(".readinessGates", StringComparison.Ordinal))
            return [["conditionType"]];
        if (path.EndsWith(".containers", StringComparison.Ordinal) ||
            path.EndsWith(".initContainers", StringComparison.Ordinal) ||
            path.EndsWith(".ephemeralContainers", StringComparison.Ordinal) ||
            path.EndsWith(".env", StringComparison.Ordinal) ||
            path.EndsWith(".volumes", StringComparison.Ordinal) ||
            path.EndsWith(".imagePullSecrets", StringComparison.Ordinal) ||
            path.EndsWith(".schedulingGates", StringComparison.Ordinal) ||
            path.EndsWith(".resourceClaims", StringComparison.Ordinal))
            return [["name"]];

        return [];
    }

    private static bool IsAtomicMap(
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys,
        string? resourceKind)
        => resourceKind == "ValidatingAdmissionPolicyBinding" && path == "spec.paramRef" ||
           customListMapKeys is null && IsBuiltInLabelSelectorPath(path, resourceKind) ||
           customListMapKeys is not null &&
           customListMapKeys.Any(entry =>
               entry.Value is [AtomicListSentinel] && SchemaPathMatches(entry.Key, path));

    internal static bool SchemaPathMatches(string schemaPath, string resourcePath)
    {
        var schemaParts = SplitPath(schemaPath);
        var resourceParts = SplitPath(resourcePath);
        return schemaParts.Length == resourceParts.Length &&
               schemaParts.Zip(resourceParts).All(pair => pair.First == "*" ||
                   string.Equals(pair.First, pair.Second, StringComparison.Ordinal));
    }

    private static string[] SplitPath(string path)
    {
        var parts = new List<string>();
        var part = new System.Text.StringBuilder();
        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (character == '\\' && index + 1 < path.Length)
                part.Append(path[++index]);
            else if (character == '.')
            {
                parts.Add(part.ToString());
                part.Clear();
            }
            else
                part.Append(character);
        }

        parts.Add(part.ToString());
        return parts.ToArray();
    }

    private static bool IsBuiltInTcpPortList(
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys,
        string? resourceKind)
        => customListMapKeys is null &&
           (resourceKind == "Service" && path == "spec.ports" ||
            path.EndsWith(".containers[].ports", StringComparison.Ordinal) ||
            path.EndsWith(".initContainers[].ports", StringComparison.Ordinal) ||
            path.EndsWith(".ephemeralContainers[].ports", StringComparison.Ordinal));

    private static bool IsBuiltInLabelSelectorPath(string path, string? resourceKind)
    {
        if (resourceKind is not ("Deployment" or "DaemonSet" or "ReplicaSet" or "StatefulSet" or
            "Job" or "CronJob" or "ReplicationController" or "Service" or "NetworkPolicy" or
            "PodDisruptionBudget" or "ResourceQuota" or "HorizontalPodAutoscaler" or
            "MutatingWebhookConfiguration" or "ValidatingWebhookConfiguration"))
            return false;

        var parts = SplitPath(path);
        return parts.Length > 1 && parts[^1] is "selector" or "podSelector" or "namespaceSelector" or
            "objectSelector" or "labelSelector";
    }

    private static string AppendPath(string path, string key)
    {
        var escapedKey = key.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(".", "\\.", StringComparison.Ordinal);
        return path.Length == 0 ? escapedKey : path + "." + escapedKey;
    }

    private static bool TryIndexItems(
        IList<object?> values,
        IReadOnlyList<string> keyFields,
        bool defaultTcpProtocol,
        IReadOnlyDictionary<string, string>? keyDefaults,
        out Dictionary<string, IDictionary<string, object?>> items)
    {
        items = new Dictionary<string, IDictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is not IDictionary<string, object?> item ||
                !TryGetItemKey(item, keyFields, defaultTcpProtocol, keyDefaults, out var key) ||
                !items.TryAdd(key, item))
                return false;
        }

        return true;
    }

    private static bool TryGetItemKey(
        IDictionary<string, object?> item,
        IReadOnlyList<string> keyFields,
        bool defaultTcpProtocol,
        IReadOnlyDictionary<string, string>? keyDefaults,
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
            else if (keyDefaults is not null && keyDefaults.TryGetValue(field, out var defaultValue))
            {
                values[index] = defaultValue;
            }
            else if (defaultTcpProtocol && field == "protocol" &&
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

    private static string GetItemKey(
        IDictionary<string, object?> item,
        IReadOnlyList<string> keyFields,
        bool defaultTcpProtocol,
        IReadOnlyDictionary<string, string>? keyDefaults)
    {
        _ = TryGetItemKey(item, keyFields, defaultTcpProtocol, keyDefaults, out var key);
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
    /// Structural equality for deserialized YAML values, including numeric values parsed
    /// into different CLR numeric types.
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
        if (a is double da && b is double db) return da.Equals(db);
        if (a is long l1 && b is int i1) return l1 == i1;
        if (a is int i2 && b is long l2) return i2 == l2;
        if (a is int i3 && b is double d3) return NumericEquals(i3, d3);
        if (a is double d4 && b is int i4) return NumericEquals(i4, d4);
        if (a is long l3 && b is double d1) return NumericEquals(l3, d1);
        if (a is double d2 && b is long l4) return NumericEquals(l4, d2);

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

    private static bool NumericEquals(long integer, double floating)
    {
        if (!double.IsFinite(floating) || floating < long.MinValue || floating >= 9223372036854775808d ||
            !Math.Truncate(floating).Equals(floating))
            return false;

        return integer == (long)floating;
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
