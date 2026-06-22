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
}
