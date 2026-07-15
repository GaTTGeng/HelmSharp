namespace HelmSharp.Action;

/// <summary>
/// Describes options for a dependency update operation.
/// </summary>
public sealed class HelmDependencyUpdateRequest
{
    /// <summary>Gets or sets the path to the chart directory.</summary>
    public string ChartPath { get; set; } = string.Empty;

    /// <summary>Gets or sets an isolated repository configuration path. The configured default is used when <see langword="null"/>.</summary>
    public string? RepositoryConfigPath { get; set; }

    /// <summary>Gets or sets an isolated repository cache directory. The configured default is used when <see langword="null"/>.</summary>
    public string? RepositoryCachePath { get; set; }

    /// <summary>Gets or sets whether skipping repository index refresh is requested. The default is <see langword="false"/>.</summary>
    public bool SkipRepositoryRefresh { get; set; }
}

/// <summary>
/// Describes options for a dependency build operation.
/// </summary>
public sealed class HelmDependencyBuildRequest
{
    /// <summary>Gets or sets the path to the chart directory.</summary>
    public string ChartPath { get; set; } = string.Empty;

    /// <summary>Gets or sets an isolated repository configuration path. The configured default is used when <see langword="null"/>.</summary>
    public string? RepositoryConfigPath { get; set; }

    /// <summary>Gets or sets an isolated repository cache directory. The configured default is used when <see langword="null"/>.</summary>
    public string? RepositoryCachePath { get; set; }

    /// <summary>Gets or sets whether lock-digest validation is requested for downloaded dependencies. The default is <see langword="true"/>.</summary>
    public bool VerifyDigests { get; set; } = true;
}

/// <summary>
/// Describes options for a dependency list operation.
/// </summary>
public sealed class HelmDependencyListRequest
{
    /// <summary>Gets or sets the path to the chart directory.</summary>
    public string ChartPath { get; set; } = string.Empty;

    /// <summary>Gets or sets whether dependency status diagnostics are requested. The default is <see langword="true"/>.</summary>
    public bool IncludeDiagnostics { get; set; } = true;
}
