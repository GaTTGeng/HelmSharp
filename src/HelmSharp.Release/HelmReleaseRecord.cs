namespace HelmSharp.Release;

public sealed record HelmReleaseRecord
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "default";
    public int Revision { get; init; }
    public string Status { get; set; } = "deployed";
    public string ChartName { get; init; } = string.Empty;
    public string ChartVersion { get; init; } = string.Empty;
    public string? AppVersion { get; init; }
    public string? ChartApiVersion { get; init; }
    public string? ChartDescription { get; init; }
    public string? ChartType { get; init; }
    public string? ChartKubeVersion { get; init; }
    public string ChartValuesYaml { get; init; } = string.Empty;
    public string? RawChartJson { get; init; }
    public string Manifest { get; init; } = string.Empty;
    public string ValuesYaml { get; init; } = string.Empty;
    /// <summary>Fully computed values retained by HelmSharp for <c>--all</c> queries.</summary>
    public string ComputedValuesYaml { get; init; } = string.Empty;
    public DateTimeOffset? FirstDeployedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; init; }
    public string? Description { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<HelmReleaseHookRecord> Hooks { get; init; } = Array.Empty<HelmReleaseHookRecord>();
    public Dictionary<string, string>? Labels { get; init; }
}

public sealed record HelmReleaseHookRecord
{
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Manifest { get; init; } = string.Empty;
    public IReadOnlyList<string> Events { get; init; } = Array.Empty<string>();
    public DateTimeOffset? LastRunStartedAt { get; init; }
    public DateTimeOffset? LastRunCompletedAt { get; init; }
    public string? LastRunPhase { get; init; }
    public int Weight { get; init; }
    public IReadOnlyList<string> DeletePolicies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OutputLogPolicies { get; init; } = Array.Empty<string>();
}
