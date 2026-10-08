namespace HelmSharp.Engine;

/// <summary>
/// Pure math operations used by template functions.
/// All arguments are pre-resolved — no TemplateContext or token dependency.
/// </summary>
internal static class MathFunctions
{
    /// <summary>
    /// Applies a binary operator (<c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>, <c>%</c>),
    /// backing Sprig's <c>add</c>/<c>sub</c>/<c>mul</c>/<c>div</c>/<c>mod</c> via
    /// <see cref="HelmTemplateRenderer"/>'s left-fold. Division or modulo by zero
    /// yields 0 rather than throwing (Sprig/Go template behavior).
    /// Results are boxed as <see cref="long"/> when integral so templates can
    /// print values without a trailing <c>.0</c>.
    /// </summary>
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

    /// <summary>Sprig <c>max</c>: largest of the arguments; integral results are boxed as <see cref="long"/>.</summary>
    public static object MathMax(IEnumerable<double> args)
    {
        var max = args.Max();
        return max == Math.Floor(max) ? (long)max : max;
    }

    /// <summary>Sprig <c>min</c>: smallest of the arguments; integral results are boxed as <see cref="long"/>.</summary>
    public static object MathMin(IEnumerable<double> args)
    {
        var min = args.Min();
        return min == Math.Floor(min) ? (long)min : min;
    }

    /// <summary>Sprig <c>ceil</c>: smallest integer ≥ <paramref name="val"/>.</summary>
    public static long Ceil(double val) => (long)Math.Ceiling(val);

    /// <summary>Sprig <c>floor</c>: largest integer ≤ <paramref name="val"/>.</summary>
    public static long Floor(double val) => (long)Math.Floor(val);

    /// <summary>
    /// Sprig <c>round</c>: rounds to <paramref name="precision"/> decimal places.
    /// .NET defaults to banker's rounding (to even) while Go's math.Round rounds
    /// half away from zero, so exact .5 boundaries may diverge slightly from Helm CLI.
    /// </summary>
    public static double Round(double val, int precision = 0) => Math.Round(val, precision);
}
