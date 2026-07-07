using System.Globalization;

namespace HelmSharp.Chart;

public static class HelmValues
{
    /// <summary>
    /// Builds merged values following Helm's precedence order (lowest to highest):
    /// chart defaults → subchart defaults → values files (in order) → values content → --set-file → --set-string → --set → --set-json.
    /// </summary>
    public static async Task<Dictionary<string, object?>> BuildAsync(
        HelmChart chart,
        IEnumerable<string>? valuesFiles,
        string? valuesContent,
        Dictionary<string, string>? setValues,
        Dictionary<string, string>? setFileValues,
        Dictionary<string, string>? setStringValues,
        Dictionary<string, string>? setJsonValues,
        CancellationToken cancellationToken)
    {
        var result = HelmYaml.DeserializeDictionary(chart.ValuesYaml);
        PruneNullMapEntries(result);

        // Merge subchart default values under each dependency alias (or dependency name).
        foreach (var (key, subchart) in GetSubchartValueInstances(chart))
        {
            MergeSubchartDefaults(result, key, subchart);
        }

        // Multiple values files (equivalent to helm -f / --values, applied in order)
        foreach (var filePath in valuesFiles ?? [])
        {
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var fileContent = await File.ReadAllTextAsync(filePath, cancellationToken);
                MergeInto(result, HelmYaml.DeserializeDictionary(fileContent));
            }
        }

        // Inline values content (equivalent to helm --values with stdin or direct YAML)
        if (!string.IsNullOrWhiteSpace(valuesContent))
        {
            MergeInto(result, HelmYaml.DeserializeDictionary(valuesContent));
        }

        // --set-file: raw file content as string (LOWEST precedence among set flags)
        foreach (var (key, value) in setFileValues ?? [])
            SetPath(result, key, value);

        // --set-string: force string values (no coercion)
        foreach (var (key, value) in setStringValues ?? [])
            SetPath(result, key, value);

        // --set: coerce scalar types (higher precedence than set-file and set-string)
        foreach (var (key, value) in setValues ?? [])
            SetPath(result, key, CoerceScalar(value));

        // --set-json: parse JSON values (HIGHEST precedence)
        foreach (var (key, value) in setJsonValues ?? [])
            SetPath(result, key, ParseJsonValue(value));

        return result;
    }

    private static IEnumerable<(string Key, HelmChart Chart)> GetSubchartValueInstances(HelmChart chart)
    {
        if (chart.Dependencies.Count == 0)
        {
            foreach (var (name, subchart) in chart.Subcharts)
                yield return (name, subchart);
            yield break;
        }

        foreach (var dependency in chart.Dependencies)
        {
            var key = dependency.Alias ?? dependency.Name;
            if (TryGetSubchartForDependency(chart, dependency, key, out var subchart))
                yield return (key, subchart);
        }
    }

    private static bool TryGetSubchartForDependency(
        HelmChart chart,
        HelmChartDependency dependency,
        string key,
        out HelmChart subchart)
    {
        if (chart.Subcharts.TryGetValue(key, out subchart!))
            return true;

        if (chart.Subcharts.TryGetValue(dependency.Name, out subchart!))
            return true;

        foreach (var candidate in chart.Subcharts.Values)
        {
            if (string.Equals(candidate.Name, dependency.Name, StringComparison.OrdinalIgnoreCase))
            {
                subchart = candidate;
                return true;
            }
        }

        subchart = null!;
        return false;
    }

    private static void MergeSubchartDefaults(
        Dictionary<string, object?> result,
        string key,
        HelmChart subchart)
    {
        var subchartDefaults = HelmYaml.DeserializeDictionary(subchart.ValuesYaml);
        PruneNullMapEntries(subchartDefaults);
        if (!result.ContainsKey(key))
        {
            result[key] = subchartDefaults;
        }
        else if (result[key] is Dictionary<string, object?> existingDict &&
                 subchartDefaults is Dictionary<string, object?> subDefaults)
        {
            // Merge parent overrides into a copy of subchart defaults so parent values take precedence
            MergeInto(subDefaults, existingDict);
            result[key] = subDefaults;
        }
    }

    /// <summary>
    /// Builds merged values. Convenience overload that accepts a single values file.
    /// Equivalent to calling <see cref="BuildAsync(HelmChart,IEnumerable{string}?,string?,Dictionary{string,string}?,Dictionary{string,string}?,Dictionary{string,string}?,Dictionary{string,string}?,CancellationToken)"/>
    /// with a single-element enumerable when <paramref name="valuesFile"/> is non-null.
    /// </summary>
    public static Task<Dictionary<string, object?>> BuildAsync(
        HelmChart chart,
        string? valuesFile,
        string? valuesContent,
        Dictionary<string, string>? setValues,
        Dictionary<string, string>? setFileValues,
        Dictionary<string, string>? setStringValues,
        Dictionary<string, string>? setJsonValues,
        CancellationToken cancellationToken)
    {
        var valuesFiles = valuesFile is not null ? new[] { valuesFile } : null;
        return BuildAsync(chart, valuesFiles, valuesContent, setValues, setFileValues, setStringValues, setJsonValues, cancellationToken);
    }

    /// <summary>
    /// Builds scoped values for a subchart. Extracts the subchart's portion from parent values
    /// and merges with the subchart's own defaults.
    /// </summary>
    public static Dictionary<string, object?> BuildSubchartValues(
        HelmChart subchart,
        Dictionary<string, object?> parentValues,
        string subchartName)
    {
        var result = HelmYaml.DeserializeDictionary(subchart.ValuesYaml);
        PruneNullMapEntries(result);

        // Extract subchart-scoped values from parent
        if (parentValues.TryGetValue(subchartName, out var scopedObj) &&
            scopedObj is Dictionary<string, object?> scopedValues)
        {
            MergeInto(result, scopedValues);
        }

        // Also merge global values
        if (parentValues.TryGetValue("global", out var globalObj) &&
            globalObj is Dictionary<string, object?> globalValues)
        {
            if (!result.ContainsKey("global"))
                result["global"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (result["global"] is Dictionary<string, object?> resultGlobal)
                MergeInto(resultGlobal, globalValues);
        }

        return result;
    }

    public static string ToYaml(Dictionary<string, object?> values)
        => HelmYaml.Serialize(values);

    internal static void MergeInto(Dictionary<string, object?> target, Dictionary<string, object?> source)
    {
        foreach (var (key, value) in source)
        {
            if (target.TryGetValue(key, out var existing) &&
                existing is Dictionary<string, object?> existingDict &&
                value is Dictionary<string, object?> valueDict)
            {
                MergeInto(existingDict, valueDict);
                continue;
            }

            target[key] = value;
        }
    }

    private static void PruneNullMapEntries(Dictionary<string, object?> values)
    {
        foreach (var key in values.Keys.ToList())
        {
            if (values[key] is null)
            {
                values.Remove(key);
                continue;
            }

            if (values[key] is Dictionary<string, object?> child)
                PruneNullMapEntries(child);
        }
    }

    /// <summary>
    /// Sets a value at a dotted path, supporting list index notation:
    /// e.g. "servers[0].port" or "items[1].name".
    /// Matches Helm's --set path syntax.
    /// </summary>
    private static void SetPath(Dictionary<string, object?> root, string path, object? value)
    {
        // Regex matches: fieldName or fieldName[index] (capturing name and optional index)
        var segmentRegex = new System.Text.RegularExpressions.Regex(
            @"([^\[\].]+)(?:\[(\d+)\])?", System.Text.RegularExpressions.RegexOptions.Compiled);

        var matches = segmentRegex.Matches(path);
        if (matches.Count == 0)
            return;

        var current = root;
        for (var i = 0; i < matches.Count - 1; i++)
        {
            var name = matches[i].Groups[1].Value;
            var indexGroup = matches[i].Groups[2];

            if (indexGroup.Success)
            {
                var index = int.Parse(indexGroup.Value);
                EnsureListElement(current, name, index, out var nextDict);
                current = nextDict;
            }
            else
            {
                if (!current.TryGetValue(name, out var child) || child is not Dictionary<string, object?> childDict)
                {
                    childDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    current[name] = childDict;
                }
                current = childDict;
            }
        }

        // Last segment — if it has an index, set directly in the list element
        var lastName = matches[matches.Count - 1].Groups[1].Value;
        var lastIndexGroup = matches[matches.Count - 1].Groups[2];

        if (lastIndexGroup.Success)
        {
            var index = int.Parse(lastIndexGroup.Value);
            if (!current.TryGetValue(lastName, out var existing) || existing is not List<object?> list)
            {
                list = new List<object?>();
                current[lastName] = list;
            }
            while (list.Count <= index)
                list.Add(null);
            list[index] = value;
        }
        else
        {
            if (current.TryGetValue(lastName, out var existing) &&
                existing is Dictionary<string, object?> existingDict &&
                value is Dictionary<string, object?> valueDict)
            {
                MergeInto(existingDict, valueDict);
                return;
            }

            current[lastName] = value;
        }
    }

    /// <summary>
    /// Ensures a list exists at <paramref name="name"/> with at least
    /// <paramref name="index"/>+1 elements. Element at <paramref name="index"/>
    /// is a Dictionary, and the method outputs it via <paramref name="element"/>.
    /// </summary>
    private static void EnsureListElement(
        Dictionary<string, object?> parent,
        string name,
        int index,
        out Dictionary<string, object?> element)
    {
        if (!parent.TryGetValue(name, out var existing) || existing is not List<object?> list)
        {
            list = new List<object?>();
            parent[name] = list;
        }

        // Pad the list to ensure index exists
        while (list.Count <= index)
            list.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

        var item = list[index];
        if (item is not Dictionary<string, object?> itemDict)
        {
            itemDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            list[index] = itemDict;
        }

        element = itemDict;
    }

    /// <summary>
    /// Coerces a --set value following Helm's semantics: first attempt YAML parsing
    /// (which handles numbers, booleans, null, lists, and dicts), then fall back to string.
    /// This matches Helm's approach of YAML-parsing --set values for type detection.
    /// </summary>
    private static object? CoerceScalar(string value)
    {
        // Empty string stays as empty string (Helm semantics)
        if (value.Length == 0)
            return string.Empty;

        // Try YAML parse first — matches Helm's approach
        // YAML handles: int, float, bool, null, lists, dicts, quoted strings
        try
        {
            var yamlValue = HelmYaml.DeserializeAny(value);
            // DeserializeAny returns null for bare null/~, but for scalar coercion
            // we only want structured types; a bare string "hello" would also parse to "hello"
            if (yamlValue is not string || value.StartsWith('"') || value.StartsWith('\''))
                return yamlValue;

            // If YAML returned a string identical to input (no quotes), try scalar coercion
            var str = (string)yamlValue;
            // bool
            if (bool.TryParse(str, out var b)) return b;
            // long
            if (long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                // A bare "true"/"false" should be bool, not long (YAML handles this already)
                // A bare integer should be long
                return l;
            }
            // double (only if it looks like a number)
            if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                str.Length > 0 && (char.IsDigit(str[0]) || str[0] == '-' || str[0] == '.'))
                return d;
            // string
            return str;
        }
        catch
        {
            return value;
        }
    }

    private static object? ParseJsonValue(string value)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(value);
            return JsonElementToObject(doc.RootElement);
        }
        catch
        {
            return value;
        }
    }

    private static object? JsonElementToObject(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                p => p.Name,
                p => JsonElementToObject(p.Value),
                StringComparer.OrdinalIgnoreCase),
            _ => null
        };
    }
}
