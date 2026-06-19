namespace HelmSharp.Chart;

public static class HelmValues
{
    public static async Task<Dictionary<string, object?>> BuildAsync(
        HelmChart chart,
        string? valuesFile,
        string? valuesContent,
        Dictionary<string, string>? setValues,
        Dictionary<string, string>? setFileValues,
        Dictionary<string, string>? setStringValues,
        Dictionary<string, string>? setJsonValues,
        CancellationToken cancellationToken)
    {
        var result = HelmYaml.DeserializeDictionary(chart.ValuesYaml);

        // Merge subchart default values under their alias (or directory name)
        foreach (var (name, subchart) in chart.Subcharts)
        {
            var dependency = chart.Dependencies.FirstOrDefault(
                d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            var key = dependency?.Alias ?? name;
            var subchartDefaults = HelmYaml.DeserializeDictionary(subchart.ValuesYaml);
            if (!result.ContainsKey(key))
                result[key] = subchartDefaults;
            else if (result[key] is Dictionary<string, object?> existingDict &&
                     subchartDefaults is Dictionary<string, object?> subDefaults)
                MergeInto(existingDict, subDefaults);
        }

        if (!string.IsNullOrWhiteSpace(valuesFile))
        {
            var fileContent = await File.ReadAllTextAsync(valuesFile, cancellationToken);
            MergeInto(result, HelmYaml.DeserializeDictionary(fileContent));
        }

        if (!string.IsNullOrWhiteSpace(valuesContent))
        {
            MergeInto(result, HelmYaml.DeserializeDictionary(valuesContent));
        }

        // --set: coerce scalar types
        foreach (var (key, value) in setValues ?? [])
            SetPath(result, key, CoerceScalar(value));

        // --set-file: raw file content as string
        foreach (var (key, value) in setFileValues ?? [])
            SetPath(result, key, value);

        // --set-string: force string values (no coercion)
        foreach (var (key, value) in setStringValues ?? [])
            SetPath(result, key, value);

        // --set-json: parse JSON values
        foreach (var (key, value) in setJsonValues ?? [])
            SetPath(result, key, ParseJsonValue(value));

        return result;
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

    private static void SetPath(Dictionary<string, object?> root, string path, object? value)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return;

        var current = root;
        foreach (var part in parts.Take(parts.Length - 1))
        {
            if (!current.TryGetValue(part, out var child) || child is not Dictionary<string, object?> childDict)
            {
                childDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                current[part] = childDict;
            }

            current = childDict;
        }

        current[parts[^1]] = value;
    }

    private static object CoerceScalar(string value)
    {
        if (bool.TryParse(value, out var b)) return b;
        if (long.TryParse(value, out var l)) return l;
        if (double.TryParse(value, out var d)) return d;
        return value;
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
