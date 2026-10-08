namespace HelmSharp.Release;

/// <summary>
/// One stored release revision, matching the fields of a Helm v3 release object.
/// </summary>
public sealed record HelmReleaseRecord
{
    /// <summary>Release name; stable across revisions.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Namespace the release is installed into.</summary>
    public string Namespace { get; init; } = "default";

    /// <summary>Revision number; starts at 1 and increases on every install/upgrade/rollback.</summary>
    public int Revision { get; init; }

    /// <summary>Release lifecycle status, for example <c>deployed</c>, <c>pending-install</c>, <c>superseded</c>, or <c>uninstalled</c>.</summary>
    public string Status { get; set; } = "deployed";

    /// <summary>Chart name from the release's Chart.yaml snapshot.</summary>
    public string ChartName { get; init; } = string.Empty;

    /// <summary>SemVer chart version from the release's Chart.yaml snapshot.</summary>
    public string ChartVersion { get; init; } = string.Empty;

    /// <summary>Application version recorded in the chart snapshot, when present.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Chart metadata API version (<c>v1</c> or <c>v2</c>) from the chart snapshot.</summary>
    public string? ChartApiVersion { get; init; }

    /// <summary>Chart description from the chart snapshot.</summary>
    public string? ChartDescription { get; init; }

    /// <summary>Chart type (<c>application</c> or <c>library</c>) from the chart snapshot.</summary>
    public string? ChartType { get; init; }

    /// <summary>Compatible Kubernetes version range from the chart snapshot.</summary>
    public string? ChartKubeVersion { get; init; }

    /// <summary>Chart default values (values.yaml) captured in the chart snapshot.</summary>
    public string ChartValuesYaml { get; init; } = string.Empty;

    /// <summary>
    /// Preserved full chart snapshot JSON as Helm embeds it under <c>release.chart</c>.
    /// Null when the record was built without a chart snapshot.
    /// </summary>
    public string? RawChartJson { get; init; }

    /// <summary>Rendered multi-document manifest applied for this revision.</summary>
    public string Manifest { get; init; } = string.Empty;

    /// <summary>User-supplied values (<c>config</c>) for this revision, as YAML.</summary>
    public string ValuesYaml { get; init; } = string.Empty;

    /// <summary>Fully computed values retained by HelmSharp for <c>--all</c> queries.</summary>
    public string ComputedValuesYaml { get; init; } = string.Empty;

    /// <summary>Timestamp of the first deployment of this release name.</summary>
    public DateTimeOffset? FirstDeployedAt { get; init; }

    /// <summary>Timestamp of the operation that produced this revision.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Timestamp of uninstall, when the revision has been deleted; null for live revisions.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>Operator description recorded on the revision (for example the rollback reason).</summary>
    public string? Description { get; init; }

    /// <summary>NOTES.txt rendered with this revision, when the chart provides notes.</summary>
    public string? Notes { get; init; }

    /// <summary>Hook resources tracked for this revision, with their last run results.</summary>
    public IReadOnlyList<HelmReleaseHookRecord> Hooks { get; init; } = Array.Empty<HelmReleaseHookRecord>();

    /// <summary>Custom labels stored on the release Secret, merged with HelmSharp's system labels.</summary>
    public Dictionary<string, string>? Labels { get; init; }
}

/// <summary>
/// A hook resource and its most recent execution state within a release revision.
/// </summary>
public sealed record HelmReleaseHookRecord
{
    /// <summary>Hook resource name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Resource kind of the hook (for example <c>Job</c>).</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Chart-relative template path that produced the hook.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Rendered hook manifest.</summary>
    public string Manifest { get; init; } = string.Empty;

    /// <summary>Hook events that trigger execution (for example <c>pre-install</c>, <c>post-upgrade</c>).</summary>
    public IReadOnlyList<string> Events { get; init; } = Array.Empty<string>();

    /// <summary>When the last hook execution started, if it has run.</summary>
    public DateTimeOffset? LastRunStartedAt { get; init; }

    /// <summary>When the last hook execution completed, if it finished.</summary>
    public DateTimeOffset? LastRunCompletedAt { get; init; }

    /// <summary>Phase of the last hook run (for example <c>Succeeded</c> or <c>Failed</c>).</summary>
    public string? LastRunPhase { get; init; }

    /// <summary>Execution ordering weight; lower weights run first within an event.</summary>
    public int Weight { get; init; }

    /// <summary>Policies controlling when the hook resource is deleted (for example <c>before-hook-creation</c>).</summary>
    public IReadOnlyList<string> DeletePolicies { get; init; } = Array.Empty<string>();

    /// <summary>Policies controlling whether hook logs are retained.</summary>
    public IReadOnlyList<string> OutputLogPolicies { get; init; } = Array.Empty<string>();
}
