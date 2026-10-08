namespace HelmSharp.Chart;

/// <summary>
/// One dependency pin from Chart.lock: the exact resolved version Helm recorded
/// for a Chart.yaml dependency.
/// </summary>
public sealed class HelmChartLockEntry
{
    /// <summary>Dependency chart name.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Exact resolved chart version, not a constraint range.</summary>
    public string Version { get; init; } = string.Empty;
    /// <summary>Repository URL or alias the dependency was locked against.</summary>
    public string? Repository { get; init; }
    /// <summary>Content digest recorded for the locked artifact, used to detect drift.</summary>
    public string? Digest { get; init; }
}
