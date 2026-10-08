namespace HelmSharp.Chart;

/// <summary>
/// A dependency declared in Chart.yaml, including the enablement rules Helm applies
/// to decide whether the subchart is rendered.
/// </summary>
public sealed class HelmChartDependency
{
    /// <summary>Dependency chart name as it appears under the parent's <c>charts/</c> directory.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Required chart version, expressed as a SemVer constraint (for example <c>^1.2.0</c>).</summary>
    public string? Version { get; init; }
    /// <summary>Repository URL, configured repo alias (<c>@name</c>), or <c>file://</c> path.</summary>
    public string? Repository { get; init; }
    /// <summary>Dotted values path (for example <c>subchart.enabled</c>) that toggles the dependency.</summary>
    public string? Condition { get; init; }
    /// <summary>
    /// Values keys under <c>tags</c> that enable the dependency. Any truthy tag enables it;
    /// explicit falsy tags disable it when no condition rule matches.
    /// </summary>
    public List<string>? Tags { get; set; }
    /// <summary>
    /// Declared enablement from Chart.yaml's <c>enabled</c> field. Defaults to true; only an
    /// explicit <c>false</c> disables the dependency (Helm treats other spellings as enabled).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Values the dependency exports into the parent scope (Chart.yaml <c>import-values</c>).
    /// </summary>
    public List<object?>? ImportValues { get; set; }

    /// <summary>
    /// Alternate name the dependency is exposed under in <c>.Values</c>, <c>.Subcharts</c>,
    /// and template paths. When null, <see cref="Name"/> is used.
    /// </summary>
    public string? Alias { get; init; }
}
