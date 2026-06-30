using System.Globalization;
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

    /// Sprig toDecimal: converts Unix octal permission strings to decimal.
    /// "0777" → 511, "0644" → 420. Non-octal strings parse as decimal.
    public static decimal ToDecimal(object? value)
    {
        var str = TypeConverters.ToTemplateString(value);
        if (str.Length > 1 && str[0] == '0' && str.All(c => c >= '0' && c <= '7'))
            return Convert.ToInt64(str, 8);
        return decimal.TryParse(str, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d : 0m;
    }

    public static string ToRawJson(object? value)
    {
        var json = JsonSerializer.Serialize(value, DefaultOptions);
        return json.Replace("\\u0026", "&")
                   .Replace("\\u003c", "<")
                   .Replace("\\u003e", ">");
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
