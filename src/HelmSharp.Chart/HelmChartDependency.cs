namespace HelmSharp.Chart;

public sealed class HelmChartDependency
{
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Repository { get; init; }
    public string? Condition { get; init; }
    public List<string>? Tags { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets the dependency value imports declared by Chart.yaml.
    /// </summary>
    public List<object?>? ImportValues { get; set; }

    /// <summary>
    /// Gets the optional alias used for the dependency.
    /// </summary>
    public string? Alias { get; init; }
}
