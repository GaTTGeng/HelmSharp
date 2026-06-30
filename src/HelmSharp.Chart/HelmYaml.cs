using YamlDotNet.Serialization;

namespace HelmSharp.Chart;

public static class HelmYaml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();
    private static readonly ISerializer Serializer = new SerializerBuilder().Build();

    public static Dictionary<string, object?> DeserializeDictionary(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var obj = Deserializer.Deserialize<object>(yaml);
        return Normalize(obj) as Dictionary<string, object?>
               ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    public static string Serialize(object? value)
    {
        if (value is null)
            return "null\n"; // Match Helm/Go yaml.Marshal(nil) → "null\n"
        return Serializer.Serialize(SortKeys(value));
    }

    /// <summary>
    /// Recursively sorts dictionary keys alphabetically to match
    /// Helm/Go YAML marshaling output (Go sorts map keys by default).
    /// Pattern matching uses the same interface shapes as Normalize() for consistency.
    /// </summary>
    private static object? SortKeys(object? value)
    {
        return value switch
        {
            IDictionary<string, object?> dict => new SortedDictionary<string, object?>(
                dict.ToDictionary(kvp => kvp.Key, kvp => SortKeys(kvp.Value))!,
                StringComparer.Ordinal),
            // Dictionary<object, object> is already normalized by Normalize()
            // before Serialize is called — skip it to avoid key-collision crash.
            IEnumerable<object> list => list.Select(SortKeys).ToList(),
            _ => value
        };
    }

    public static string? GetString(IDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) ? Convert.ToString(value) : null;

    public static object? Normalize(object? value)
    {
        return value switch
        {
            Dictionary<object, object> dict => dict.ToDictionary(
                x => Convert.ToString(x.Key) ?? string.Empty,
                x => Normalize(x.Value),
                StringComparer.OrdinalIgnoreCase),
            IDictionary<string, object?> dict => dict.ToDictionary(
                x => x.Key,
                x => Normalize(x.Value),
                StringComparer.OrdinalIgnoreCase),
            IEnumerable<object> list => list.Select(Normalize).ToList(),
            _ => value
        };
    }
}
