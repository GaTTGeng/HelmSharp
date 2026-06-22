using System.Text.RegularExpressions;

namespace HelmSharp.Engine;

/// <summary>
/// Semantic version comparison helpers used by template functions.
/// </summary>
internal static class SemverFunctions
{
    public static (int Major, int Minor, int Patch) Parse(string v)
    {
        var m = Regex.Match(v, @"^v?(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) return (0, 0, 0);
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    public static int Compare(int aMaj, int aMin, int aPat, int bMaj, int bMin, int bPat)
    {
        if (aMaj != bMaj) return aMaj.CompareTo(bMaj);
        if (aMin != bMin) return aMin.CompareTo(bMin);
        return aPat.CompareTo(bPat);
    }

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
