using System.Text.Json;
using HelmSharp.Chart;

namespace HelmSharp.Action;

/// <summary>
/// Validates chart values against a JSON Schema (values.schema.json).
/// Implements Helm's values schema validation.
/// </summary>
internal static class HelmSchemaValidator
{
    /// <summary>
    /// Validates values against a chart's schema file.
    /// Returns a list of validation errors (empty if valid).
    /// </summary>
    public static List<string> Validate(HelmChart chart, Dictionary<string, object?> values)
    {
        var errors = new List<string>();

        // Look for values.schema.json in chart files
        var schemaContent = FindSchemaFile(chart);
        if (schemaContent is null)
            return errors; // No schema = no validation

        try
        {
            var schema = JsonSerializer.Deserialize<JsonElement>(schemaContent);
            ValidateObject(schema, values, "", errors);
        }
        catch (Exception ex)
        {
            errors.Add($"Schema parse error: {ex.Message}");
        }

        return errors;
    }

    private static string? FindSchemaFile(HelmChart chart)
    {
        // Check in Files dictionary
        foreach (var (path, bytes) in chart.Files)
        {
            if (path.EndsWith("values.schema.json", StringComparison.OrdinalIgnoreCase))
                return System.Text.Encoding.UTF8.GetString(bytes);
        }
        return null;
    }

    private static void ValidateObject(
        JsonElement schema,
        IDictionary<string, object?> values,
        string path,
        List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        // Check required fields
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var req in required.EnumerateArray())
            {
                var fieldName = req.GetString();
                if (fieldName is not null && !values.ContainsKey(fieldName))
                    errors.Add($"{path}{fieldName}: required field is missing");
            }
        }

        // Check properties
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in properties.EnumerateObject())
            {
                var fieldPath = string.IsNullOrEmpty(path) ? $"{prop.Name}." : $"{path}{prop.Name}.";
                if (values.TryGetValue(prop.Name, out var value))
                {
                    ValidateValue(prop.Value, value, fieldPath, errors);
                }
            }
        }
    }

    private static void ValidateValue(
        JsonElement schema,
        object? value,
        string path,
        List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (!schema.TryGetProperty("type", out var typeElement))
            return;

        var expectedType = typeElement.GetString();
        var actualType = GetJsonType(value);

        if (expectedType is not null && actualType is not null && expectedType != actualType)
        {
            // Allow integer for number
            if (expectedType == "number" && actualType == "integer")
            { }
            else
            {
                errors.Add($"{path.TrimEnd('.')}: expected type '{expectedType}', got '{actualType}'");
                return;
            }
        }

        // Check nested object properties
        if (expectedType == "object" && value is IDictionary<string, object?> dict)
        {
            ValidateObject(schema, dict, path, errors);
        }

        // Check array items
        if (expectedType == "array" && value is IList<object?> list &&
            schema.TryGetProperty("items", out var items))
        {
            for (var i = 0; i < list.Count; i++)
            {
                ValidateValue(items, list[i], $"{path}[{i}].", errors);
            }
        }

        // Check enum
        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
        {
            var strValue = Convert.ToString(value);
            var found = false;
            foreach (var ev in enumValues.EnumerateArray())
            {
                if (ev.ValueKind == JsonValueKind.String && ev.GetString() == strValue)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                errors.Add($"{path.TrimEnd('.')}: value '{strValue}' not in allowed values");
        }

        // Check minimum/maximum for numbers
        if (value is long l)
        {
            if (schema.TryGetProperty("minimum", out var min) && l < min.GetInt64())
                errors.Add($"{path.TrimEnd('.')}: value {l} is less than minimum {min.GetInt64()}");
            if (schema.TryGetProperty("maximum", out var max) && l > max.GetInt64())
                errors.Add($"{path.TrimEnd('.')}: value {l} is greater than maximum {max.GetInt64()}");
        }

        // Check minLength/maxLength for strings
        if (value is string s)
        {
            if (schema.TryGetProperty("minLength", out var minLen) && s.Length < minLen.GetInt32())
                errors.Add($"{path.TrimEnd('.')}: string length {s.Length} is less than minLength {minLen.GetInt32()}");
            if (schema.TryGetProperty("maxLength", out var maxLen) && s.Length > maxLen.GetInt32())
                errors.Add($"{path.TrimEnd('.')}: string length {s.Length} is greater than maxLength {maxLen.GetInt32()}");
            if (schema.TryGetProperty("pattern", out var pattern))
            {
                try
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(s, pattern.GetString() ?? ""))
                        errors.Add($"{path.TrimEnd('.')}: value does not match pattern '{pattern.GetString()}'");
                }
                catch { }
            }
        }
    }

    private static string? GetJsonType(object? value)
        => value switch
        {
            string => "string",
            bool => "boolean",
            long or int => "integer",
            double or float => "number",
            IList<object?> => "array",
            IDictionary<string, object?> => "object",
            null => "null",
            _ => null
        };
}
