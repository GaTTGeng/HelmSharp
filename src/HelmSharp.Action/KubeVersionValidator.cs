using System.Text.RegularExpressions;

namespace HelmSharp.Action;

/// <summary>
/// Validates chart kubeVersion requirements against the cluster version.
/// Implements Helm's kubeVersion compatibility check.
/// </summary>
public static class KubeVersionValidator
{
    /// <summary>
    /// Checks if a chart's kubeVersion requirement is satisfied by the cluster version.
    /// Supports SemVer ranges: >=1.20, ~1.25, ^1.20, 1.25.x, etc.
    /// </summary>
    public static bool IsCompatible(string chartKubeVersion, string clusterVersion)
    {
        if (string.IsNullOrWhiteSpace(chartKubeVersion))
            return true; // No requirement = always compatible

        var cluster = ParseVersion(clusterVersion);
        if (cluster is null)
            return true; // Can't parse cluster version = assume compatible

        // Parse the constraint
        var constraint = chartKubeVersion.Trim();
        return CheckConstraint(constraint, cluster.Value);
    }

    /// <summary>
    /// Returns a detailed compatibility result.
    /// </summary>
    public static (bool Compatible, string Message) Validate(string chartKubeVersion, string clusterVersion)
    {
        if (string.IsNullOrWhiteSpace(chartKubeVersion))
            return (true, "No kubeVersion requirement specified");

        var cluster = ParseVersion(clusterVersion);
        if (cluster is null)
            return (true, $"Could not parse cluster version: {clusterVersion}");

        var constraint = chartKubeVersion.Trim();
        if (CheckConstraint(constraint, cluster.Value))
            return (true, $"Chart requires kubeVersion {constraint}, cluster has {clusterVersion} (compatible)");

        return (false, $"Chart requires kubeVersion {constraint}, but cluster has {clusterVersion} (incompatible)");
    }

    private static bool CheckConstraint(string constraint, (int Major, int Minor, int Patch) cluster)
    {
        // Handle range constraints: >=1.20, <=1.25, >1.20, <1.25
        if (constraint.StartsWith(">="))
        {
            var req = ParseVersion(constraint[2..].Trim());
            return req is not null && CompareVersions(cluster, req.Value) >= 0;
        }
        if (constraint.StartsWith("<="))
        {
            var req = ParseVersion(constraint[2..].Trim());
            return req is not null && CompareVersions(cluster, req.Value) <= 0;
        }
        if (constraint.StartsWith(">"))
        {
            var req = ParseVersion(constraint[1..].Trim());
            return req is not null && CompareVersions(cluster, req.Value) > 0;
        }
        if (constraint.StartsWith("<"))
        {
            var req = ParseVersion(constraint[1..].Trim());
            return req is not null && CompareVersions(cluster, req.Value) < 0;
        }
        if (constraint.StartsWith("="))
        {
            var req = ParseVersion(constraint[1..].Trim());
            return req is not null && CompareVersions(cluster, req.Value) == 0;
        }

        // Handle tilde: ~1.25.0 means >=1.25.0 <1.26.0
        if (constraint.StartsWith('~'))
        {
            var req = ParseVersion(constraint[1..].Trim());
            if (req is null) return true;
            return CompareVersions(cluster, req.Value) >= 0 &&
                   cluster.Minor == req.Value.Minor &&
                   cluster.Major == req.Value.Major;
        }

        // Handle caret: ^1.20.0 means >=1.20.0 <2.0.0
        if (constraint.StartsWith('^'))
        {
            var req = ParseVersion(constraint[1..].Trim());
            if (req is null) return true;
            return CompareVersions(cluster, req.Value) >= 0 &&
                   cluster.Major == req.Value.Major;
        }

        // Handle wildcards: 1.25.x, 1.25.*
        if (constraint.EndsWith(".x") || constraint.EndsWith(".*"))
        {
            var prefix = constraint[..^2];
            var req = ParseVersion(prefix + ".0");
            if (req is null) return true;
            return cluster.Major == req.Value.Major && cluster.Minor == req.Value.Minor;
        }

        // Handle bare version: 1.25 (means >=1.25.0 <1.26.0)
        var bare = ParseVersion(constraint);
        if (bare is not null)
        {
            if (bare.Value.Patch == 0 && constraint.Count(c => c == '.') == 1)
            {
                // Bare minor version: >=1.25.0 <1.26.0
                return cluster.Major == bare.Value.Major && cluster.Minor >= bare.Value.Minor;
            }
            return CompareVersions(cluster, bare.Value) == 0;
        }

        return true; // Can't parse constraint = assume compatible
    }

    private static (int Major, int Minor, int Patch)? ParseVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        // Strip 'v' prefix
        version = version.TrimStart('v', 'V');

        var match = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)");
        if (match.Success)
        {
            return (
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value)
            );
        }

        // Try without patch: 1.25
        match = Regex.Match(version, @"^(\d+)\.(\d+)$");
        if (match.Success)
        {
            return (
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                0
            );
        }

        // Try major only: 1
        match = Regex.Match(version, @"^(\d+)$");
        if (match.Success)
        {
            return (int.Parse(match.Groups[1].Value), 0, 0);
        }

        return null;
    }

    private static int CompareVersions((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b)
    {
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
        return a.Patch.CompareTo(b.Patch);
    }
}
