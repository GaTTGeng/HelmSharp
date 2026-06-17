namespace HelmSharp.Chart;

public sealed class HelmChartLockEntry
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? Repository { get; init; }
    public string? Digest { get; init; }
}
