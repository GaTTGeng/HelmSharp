using System.Text.Json;

namespace HelmSharp.Engine;

/// <summary>
/// JSON serialization helpers used by template functions.
/// </summary>
internal static class SerializationFunctions
{
    private static readonly JsonSerializerOptions DefaultOptions = new() { PropertyNamingPolicy = null };
    private static readonly JsonSerializerOptions PrettyOptions = new() { PropertyNamingPolicy = null, WriteIndented = true };

    /// <summary>Sprig <c>toJson</c>: compact JSON. Property names keep their original casing.</summary>
    public static string ToJson(object? value)
        => JsonSerializer.Serialize(value, DefaultOptions);

    /// <summary>Sprig <c>toPrettyJson</c>: indented JSON.</summary>
    public static string ToPrettyJson(object? value)
        => JsonSerializer.Serialize(value, PrettyOptions);

    /// <summary>
    /// Sprig <c>fromJson</c>: parses JSON into template-native types
    /// (dictionaries, lists, scalars). Invalid JSON returns null rather than
    /// throwing, matching Helm's permissive behavior.
    /// </summary>
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

    /// <summary>
    /// Sprig toDecimal: converts Unix octal permission strings to decimal.
    /// "0777", "0644", and "644" all parse as octal values. Strings containing
    /// digits 8 or 9 (or non-digits) return 0 — the value cannot be octal.
    /// </summary>
    public static decimal ToDecimal(object? value)
    {
        var str = TypeConverters.ToTemplateString(value);
        if (str.Length > 0 && str.All(c => c >= '0' && c <= '7'))
            return Convert.ToInt64(str, 8);
        return 0m;
    }

    /// <summary>
    /// Sprig <c>toRawJson</c>: like <c>toJson</c> but without HTML escaping —
    /// <c>&amp;</c>, <c>&lt;</c>, and <c>&gt;</c> stay literal so the JSON is safe
    /// to embed in ConfigMap data without entity-escaped ampersands.
    /// </summary>
    public static string ToRawJson(object? value)
    {
        var json = JsonSerializer.Serialize(value, DefaultOptions);
        return json.Replace("\\u0026", "&")
                   .Replace("\\u003c", "<")
                   .Replace("\\u003e", ">");
    }

    /// <summary>
    /// Converts a parsed <see cref="JsonElement"/> tree into template-native types.
    /// Object keys use case-insensitive comparison so lookups match .NET dictionary
    /// conventions used elsewhere in the engine; JSON itself remains case-sensitive
    /// at parse time.
    /// </summary>
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
