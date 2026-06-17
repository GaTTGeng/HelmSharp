namespace HelmSharp.Chart;

public sealed class HelmChartDependency
{
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Repository { get; init; }
    public string? Condition { get; init; }
    public List<string>? Tags { get; set; }
    public bool Enabled { get; set; } = true;
}
