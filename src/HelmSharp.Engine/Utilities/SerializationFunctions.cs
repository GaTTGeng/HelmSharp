using System.Text.Json;

namespace HelmSharp.Engine;

/// <summary>
/// JSON serialization helpers used by template functions.
/// </summary>
internal static class SerializationFunctions
{
    private static readonly JsonSerializerOptions DefaultOptions = new() { PropertyNamingPolicy = null };
    private static readonly JsonSerializerOptions PrettyOptions = new() { PropertyNamingPolicy = null, WriteIndented = true };

    public static string ToJson(object? value)
        => JsonSerializer.Serialize(value, DefaultOptions);

    public static string ToPrettyJson(object? value)
        => JsonSerializer.Serialize(value, PrettyOptions);

    public static object? FromJson(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            return JsonElementToObject(doc);
        }
        catch
        {
            return null;
        }
    }

    public static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                p => p.Name,
                p => JsonElementToObject(p.Value),
                StringComparer.OrdinalIgnoreCase),
            _ => null
        };
    }
}
