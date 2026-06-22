using HelmSharp.Chart;

namespace HelmSharp.Engine;

internal sealed record TemplateContext(
    HelmChart Chart,
    string ReleaseName,
    string ReleaseNamespace,
    Dictionary<string, object?> Values,
    object? Dot,
    Dictionary<string, object?> Variables)
{
    public bool IsInstall { get; init; } = true;
    public bool IsUpgrade { get; init; }
    public int Revision { get; init; } = 1;
    public string? KubeVersion { get; init; }
    public ApiVersionSet? ApiVersions { get; init; }
    public string? CurrentTemplatePath { get; init; }
    public string? TemplateChartName { get; init; }
    public string? TemplateChartPath { get; init; }
    public List<HelmChartDependency> Dependencies { get; init; } = [];
}
