using HelmSharp.Chart;

namespace HelmSharp.Engine;

/// <summary>
/// Immutable snapshot of the state one template (or nested scope) is rendered against:
/// the chart, release identity, values, and scope roots. Nested scopes (subcharts, range/with
/// bodies) receive derived copies rather than mutating the parent context.
/// </summary>
/// <param name="Chart">Chart whose templates are being rendered (a subchart in nested scopes).</param>
/// <param name="ReleaseName">Release name exposed as <c>.Release.Name</c>.</param>
/// <param name="ReleaseNamespace">Target namespace exposed as <c>.Release.Namespace</c>.</param>
/// <param name="Values">Merged chart/user values exposed as <c>.Values</c>.</param>
/// <param name="Dot">
/// Current <c>.</c> scope root. Starts as the values object; <c>range</c>/<c>with</c> bodies
/// replace it with the iterated item, so helpers must read this instead of assuming values.
/// </param>
/// <param name="Variables">
/// Template variables (<c>$name</c>, including <c>$</c> itself) visible in this scope.
/// Copied on scope entry so assignments do not leak to the parent scope.
/// </param>
internal sealed record TemplateContext(
    HelmChart Chart,
    string ReleaseName,
    string ReleaseNamespace,
    Dictionary<string, object?> Values,
    object? Dot,
    Dictionary<string, object?> Variables)
{
    /// <summary>True when rendering as an install (<c>.Release.IsInstall</c>); mutually exclusive with <see cref="IsUpgrade"/>.</summary>
    public bool IsInstall { get; init; } = true;
    /// <summary>True when rendering as an upgrade (<c>.Release.IsUpgrade</c>).</summary>
    public bool IsUpgrade { get; init; }
    /// <summary>Release revision exposed as <c>.Release.Revision</c>.</summary>
    public int Revision { get; init; } = 1;
    /// <summary>Override for <c>.Capabilities.KubeVersion</c>; null lets the engine pick its default.</summary>
    public string? KubeVersion { get; init; }
    /// <summary>Set exposed as <c>.Capabilities.APIVersions</c>.</summary>
    public ApiVersionSet? ApiVersions { get; init; }
    /// <summary>Relative path of the template currently being rendered; feeds <c>.Template.Name</c> diagnostics.</summary>
    public string? CurrentTemplatePath { get; init; }
    /// <summary>Name of the chart that owns the current template (differs from the root chart in subchart scopes).</summary>
    public string? TemplateChartName { get; init; }
    /// <summary>
    /// Archive path of the chart that owns the current template, used to build
    /// <c>.Template.BasePath</c> and <c>.Subcharts</c> keys exactly as Helm prefixes them.
    /// </summary>
    public string? TemplateChartPath { get; init; }
    /// <summary>Effective dependencies of the chart being rendered.</summary>
    public List<HelmChartDependency> Dependencies { get; init; } = [];
    /// <summary>
    /// Position of the current chart in the dependency graph. Null only at scopes that have no
    /// graph node; used to resolve <c>.Subcharts</c> lookups and to derive child subchart contexts.
    /// </summary>
    public HelmDependencyNode? DependencyNode { get; init; }
}
