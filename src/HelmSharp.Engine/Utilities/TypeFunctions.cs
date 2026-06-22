namespace HelmSharp.Engine;

/// <summary>
/// Type inspection and deep-equality helpers used by template functions.
/// </summary>
internal static class TypeFunctions
{
    public static string KindOf(object? value)
        => value switch
        {
            null => "nil",
            bool => "bool",
            int or long => "int64",
            double or float => "float64",
            string => "string",
            IList<object?> => "slice",
            IDictionary<string, object?> => "map",
            IEnumerable<object?> => "slice",
            _ => "invalid"
        };

    public static bool DeepEquals(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.GetType() != b.GetType()) return false;
        if (a is string sa && b is string sb) return sa == sb;
        if (a is bool ba && b is bool bb) return ba == bb;
        if (a is long la && b is long lb) return la == lb;
        if (a is double da && b is double db) return Math.Abs(da - db) < 1e-10;
        if (a is Dictionary<string, object?> dictA && b is Dictionary<string, object?> dictB)
        {
            if (dictA.Count != dictB.Count) return false;
            foreach (var kvp in dictA)
            {
                if (!dictB.TryGetValue(kvp.Key, out var valB) || !DeepEquals(kvp.Value, valB))
                    return false;
            }
            return true;
        }
        if (a is IList<object?> listA && b is IList<object?> listB)
        {
            if (listA.Count != listB.Count) return false;
            for (var i = 0; i < listA.Count; i++)
                if (!DeepEquals(listA[i], listB[i])) return false;
            return true;
        }
        return a.Equals(b);
    }

    public static bool TypeIs(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var typeName = TypeConverters.ToTemplateString(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var val = pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context);
        return typeName switch
        {
            "string" => val is string,
            "bool" => val is bool,
            "int" => val is int or long,
            "float64" => val is double or float,
            "[]interface {}" => val is IList<object?>,
            "map[string]interface {}" => val is IDictionary<string, object?>,
            "nil" => val is null,
            _ => false
        };
    }

    public static bool TypeIsLike(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
        => TypeIs(tokens, context, pipelineValue);

    public static bool KindIs(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var kind = TypeConverters.ToTemplateString(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var val = pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context);
        return KindOf(val) == kind;
    }

    public static int CompareValues(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a is long la && b is long lb) return la.CompareTo(lb);
        if (a is double da && b is double db) return da.CompareTo(db);
        if (a is int ia && b is int ib) return ia.CompareTo(ib);
        return string.Compare(TypeConverters.ToTemplateString(a), TypeConverters.ToTemplateString(b), StringComparison.Ordinal);
    }

    public static int GetLength(object? value)
        => value switch
        {
            string s => s.Length,
            System.Collections.ICollection c => c.Count,
            IList<object?> l => l.Count,
            IDictionary<string, object?> d => d.Count,
            IEnumerable<object?> e => e.Count(),
            _ => 0
        };
}
