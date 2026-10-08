namespace HelmSharp.Engine;

/// <summary>
/// Type inspection and deep-equality helpers used by template functions.
/// </summary>
internal static class TypeFunctions
{
    /// <summary>
    /// Sprig <c>kindOf</c>: Go-style kind name for a value
    /// (<c>nil</c>, <c>bool</c>, <c>int64</c>, <c>float64</c>, <c>string</c>,
    /// <c>slice</c>, <c>map</c>, <c>invalid</c>). Numeric C# types are normalized
    /// to Go's canonical <c>int64</c>/<c>float64</c> kinds.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>deepEqual</c>: structural equality for nested maps/slices.
    /// Doubles are compared with a small epsilon so JSON round-trips compare equal.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>typeIs</c>: true when the second argument's Go type name matches
    /// the first. Go type names are used verbatim (<c>"int"</c>, <c>"float64"</c>,
    /// <c>"[]interface {}"</c>, <c>"map[string]interface {}"</c>, <c>"nil"</c>).
    /// When invoked through a pipeline, the pipeline value is the checked value.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>typeIsLike</c>: same matching rules as <see cref="TypeIs"/>.
    /// Sprig treats Go's <c>reflect.Type.String()</c> equality identically for both;
    /// kept separate so call sites document intent.
    /// </summary>
    public static bool TypeIsLike(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
        => TypeIs(tokens, context, pipelineValue);

    /// <summary>Sprig <c>kindIs</c>: compares the value's <see cref="KindOf"/> against the given kind name.</summary>
    public static bool KindIs(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var kind = TypeConverters.ToTemplateString(HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var val = pipelineValue ?? HelmTemplateRenderer.EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context);
        return KindOf(val) == kind;
    }

    /// <summary>
    /// Ordering for <c>lt</c>/<c>gt</c>/<c>le</c>/<c>ge</c>: numbers compare numerically,
    /// everything else falls back to ordinal string comparison of their template
    /// representations. Nulls sort first.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>len</c>: character count for strings, element count for collections,
    /// and 0 for everything else (including null) — matching Go's template <c>len</c>
    /// which reports 0 for types without a length rather than failing.
    /// </summary>
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
