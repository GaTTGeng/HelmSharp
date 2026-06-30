using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.RepresentationModel;

namespace HelmSharp.Chart;

public static class HelmYaml
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithTypeConverter(new QuotedYamlStringConverter())
        .Build();

    public static Dictionary<string, object?> DeserializeDictionary(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var obj = DeserializeYamlNode(yaml);
        return Normalize(obj) as Dictionary<string, object?>
               ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deserializes any YAML value (scalar, list, or mapping) into its native .NET type.
    /// Returns null for YAML null / ~, string for quoted scalars, bool/long/double for
    /// typed scalars. Used by --set value coercion to match Helm's YAML-based parsing.
    /// </summary>
    public static object? DeserializeAny(string yaml)
    {
        if (string.IsNullOrEmpty(yaml))
            return string.Empty;

        return Normalize(DeserializeYamlNode(yaml));
    }

    private static object? DeserializeYamlNode(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0)
            return null;

        return ConvertYamlNode(stream.Documents[0].RootNode);
    }

    private static object? ConvertYamlNode(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => ConvertScalarNode(scalar),
            YamlSequenceNode sequence => sequence.Children.Select(ConvertYamlNode).ToList(),
            YamlMappingNode mapping => ConvertMappingNode(mapping),
            _ => null
        };
    }

    private static Dictionary<string, object?> ConvertMappingNode(YamlMappingNode mapping)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = keyNode is YamlScalarNode scalarKey
                ? scalarKey.Value ?? string.Empty
                : Convert.ToString(ConvertYamlNode(keyNode)) ?? string.Empty;
            result[key] = ConvertYamlNode(valueNode);
        }

        return result;
    }

    private static object? ConvertScalarNode(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)
            return value ?? string.Empty;
        if (string.IsNullOrEmpty(value))
            return null;

        if (value.Equals("null", StringComparison.OrdinalIgnoreCase) || value.Equals("~", StringComparison.Ordinal))
            return null;
        if (bool.TryParse(value, out var boolean))
            return boolean;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return integer;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            value.Any(c => c is '.' or 'e' or 'E'))
            return number;
        return value;
    }

    public static string Serialize(object? value)
    {
        if (value is null)
            return "null\n"; // Match Helm/Go yaml.Marshal(nil) -> "null\n"
        return NormalizeNullMapScalars(Serializer.Serialize(SortKeys(value)));
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
            string text when NeedsQuotedString(text) => new QuotedYamlString(text),
            _ => value
        };
    }

    private static bool NeedsQuotedString(string value)
        => value.Equals("null", StringComparison.OrdinalIgnoreCase)
           || value.Equals("~", StringComparison.Ordinal)
           || value.Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Equals("false", StringComparison.OrdinalIgnoreCase)
           || Regex.IsMatch(value, @"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$");

    private static string NormalizeNullMapScalars(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!Regex.IsMatch(line, @"^\s*[^:#][^:]*:\s*$"))
                continue;

            var indent = line.TakeWhile(char.IsWhiteSpace).Count();
            var next = string.Empty;
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Length == 0)
                    continue;

                next = lines[j];
                break;
            }
            var nextIndent = next.TakeWhile(char.IsWhiteSpace).Count();
            var nextTrimmed = next.Trim();
            if (next.Length > 0 && nextIndent > indent)
                continue;
            if (nextTrimmed is "[]" or "{}" || nextTrimmed.StartsWith("- ", StringComparison.Ordinal))
                continue;

            lines[i] = line.TrimEnd() + " null";
        }

        return string.Join('\n', lines);
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

    private sealed record QuotedYamlString(string Value);

    private sealed class QuotedYamlStringConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(QuotedYamlString);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
            => throw new NotSupportedException();

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var quoted = (QuotedYamlString)value!;
            emitter.Emit(new Scalar(
                anchor: null,
                tag: null,
                value: quoted.Value,
                style: ScalarStyle.DoubleQuoted,
                isPlainImplicit: true,
                isQuotedImplicit: false));
        }
    }
}
