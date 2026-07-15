namespace HelmSharp.Action;

/// <summary>
/// Describes a chart package operation.
/// </summary>
public sealed class HelmPackageRequest
{
    /// <summary>
    /// Gets or sets the path to the chart directory to package.
    /// </summary>
    public string ChartPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the output directory. The current directory is used when this value is <see langword="null"/>.
    /// </summary>
    public string? Destination { get; set; }

    /// <summary>
    /// Gets or sets the version written to the packaged chart metadata. The source chart version is used by default.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the appVersion written to the packaged chart metadata. The source value is used by default.
    /// </summary>
    public string? AppVersion { get; set; }

    /// <summary>
    /// Gets or sets whether dependency update before packaging is requested. The default is <see langword="false"/>.
    /// </summary>
    public bool DependencyUpdate { get; set; }

    /// <summary>
    /// Gets or sets whether skipping values schema validation is requested. The default is <see langword="false"/>.
    /// </summary>
    public bool SkipSchemaValidation { get; set; }
}
