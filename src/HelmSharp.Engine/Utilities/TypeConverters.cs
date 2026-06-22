using System.Collections;
using System.Globalization;
using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// Type conversion helpers for template evaluation.
/// </summary>
internal static class TypeConverters
{
    /// <summary>
    /// Converts a value to its Go-style template string representation.
    /// </summary>
    public static string ToTemplateString(object? value)
        => value switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("G", CultureInfo.InvariantCulture),
            float f => f.ToString("G", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

    /// <summary>
    /// Converts a value to a long, matching Go template coercion rules.
    /// </summary>
    public static long ToLong(object? value)
        => value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            float f => (long)f,
            string s when long.TryParse(s, out var l) => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => (long)d,
            bool b => b ? 1 : 0,
            _ => 0
        };

    /// <summary>
    /// Converts a value to a double, matching Go template coercion rules.
    /// </summary>
    public static double ToDouble(object? value)
        => value switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            bool b => b ? 1.0 : 0.0,
            _ => 0.0
        };

    /// <summary>
    /// Determines whether a value is truthy per Go template semantics.
    /// </summary>
    public static bool IsTruthy(object? value)
        => value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s) && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
            long l => l != 0,
            int i => i != 0,
            double d => d != 0.0,
            IEnumerable<object?> e => e.Any(),
            _ => true
        };
}
