namespace HelmSharp.Engine;

/// <summary>
/// Pure math operations used by template functions.
/// All arguments are pre-resolved — no TemplateContext or token dependency.
/// </summary>
internal static class MathFunctions
{
    public static object MathOp(double a, double b, string op)
    {
        var result = op switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" => b != 0 ? a / b : 0,
            "%" => b != 0 ? a % b : 0,
            _ => a
        };
        return result == Math.Floor(result) ? (long)result : result;
    }

    public static object MathMax(IEnumerable<double> args)
    {
        var max = args.Max();
        return max == Math.Floor(max) ? (long)max : max;
    }

    public static object MathMin(IEnumerable<double> args)
    {
        var min = args.Min();
        return min == Math.Floor(min) ? (long)min : min;
    }

    public static long Ceil(double val) => (long)Math.Ceiling(val);

    public static long Floor(double val) => (long)Math.Floor(val);

    public static double Round(double val, int precision = 0) => Math.Round(val, precision);
}
