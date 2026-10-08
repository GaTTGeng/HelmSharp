using System.Text.RegularExpressions;

namespace HelmSharp.Engine;

/// <summary>
/// Semantic version comparison helpers used by template functions.
/// Implements the subset of semver constraints that Sprig's <c>semverCompare</c>
/// supports (operators, <c>~</c>/<c>^</c> ranges). Pre-release/build metadata is
/// ignored — only major.minor.patch participate in comparisons.
/// </summary>
internal static class SemverFunctions
{
    /// <summary>
    /// Parses a leading <c>v?</c>MAJOR.MINOR.PATCH prefix. Unparseable input
    /// yields (0, 0, 0) rather than failing — callers use equality against
    /// that to reject the version where needed.
    /// </summary>
    public static (int Major, int Minor, int Patch) Parse(string v)
    {
        var m = Regex.Match(v, @"^v?(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) return (0, 0, 0);
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    /// <summary>Standard three-way version comparison: negative, zero, or positive.</summary>
    public static int Compare(int aMaj, int aMin, int aPat, int bMaj, int bMin, int bPat)
    {
        if (aMaj != bMaj) return aMaj.CompareTo(bMaj);
        if (aMin != bMin) return aMin.CompareTo(bMin);
        return aPat.CompareTo(bPat);
    }

    /// <summary>
    /// Sprig <c>semverCompare</c>: tests whether <paramref name="version"/> satisfies
    /// <paramref name="constraint"/>. Supports <c>&gt;=</c>, <c>&lt;=</c>, <c>&gt;</c>,
    /// <c>&lt;</c>, <c>=</c>, <c>~</c> (patch-level range), <c>^</c> (minor-level range),
    /// and bare exact match. Multiple constraints separated by commas are not supported.
    /// </summary>
    public static bool Satisfies(string version, string constraint)
    {
        var vMatch = Regex.Match(version, @"^v?(\d+)\.(\d+)\.(\d+)");
        if (!vMatch.Success) return false;
        var vMajor = int.Parse(vMatch.Groups[1].Value);
        var vMinor = int.Parse(vMatch.Groups[2].Value);
        var vPatch = int.Parse(vMatch.Groups[3].Value);

        constraint = constraint.Trim();

        if (constraint.StartsWith(">="))
        {
            var c = Parse(constraint[2..].Trim());
            return Compare(vMajor, vMinor, vPatch, c.Major, c.Minor, c.Patch) >= 0;
        }
        if (constraint.StartsWith("<="))
        {
            var c = Parse(constraint[2..].Trim());
            return Compare(vMajor, vMinor, vPatch, c.Major, c.Minor, c.Patch) <= 0;
        }
        if (constraint.StartsWith(">"))
        {
            var c = Parse(constraint[1..].Trim());
            return Compare(vMajor, vMinor, vPatch, c.Major, c.Minor, c.Patch) > 0;
        }
        if (constraint.StartsWith("<"))
        {
            var c = Parse(constraint[1..].Trim());
            return Compare(vMajor, vMinor, vPatch, c.Major, c.Minor, c.Patch) < 0;
        }
        if (constraint.StartsWith("="))
        {
            var c = Parse(constraint[1..].Trim());
            return Compare(vMajor, vMinor, vPatch, c.Major, c.Minor, c.Patch) == 0;
        }
        if (constraint.StartsWith('~'))
        {
            var c = Parse(constraint[1..].Trim());
            return Compare(vMajor, vMinor, vPatch, c.Major, c.Minor, c.Patch) >= 0 &&
                   Compare(vMajor, vMinor, vPatch, c.Major, c.Minor + 1, 0) < 0;
        }
        if (constraint.StartsWith('^'))
        {
            var c = Parse(constraint[1..].Trim());
            return Compare(vMajor, vMinor, vPatch, c.Major, c.Minor, c.Patch) >= 0 &&
                   Compare(vMajor, vMinor, vPatch, c.Major + 1, 0, 0) < 0;
        }
        {
            var c = Parse(constraint);
            return Compare(vMajor, vMinor, vPatch, c.Major, c.Minor, c.Patch) == 0;
        }
    }
}
