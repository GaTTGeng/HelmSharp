namespace HelmSharp.Repo;

/// <summary>
/// Describes a chart pull operation from a traditional chart repository or direct archive URL.
/// </summary>
public sealed class HelmPullRequest
{
    /// <summary>Gets or sets the chart reference, such as <c>repo/chart</c> or an archive URL.</summary>
    public string ChartReference { get; set; } = string.Empty;

    /// <summary>Gets or sets an exact version or semantic version constraint. The latest stable version is selected by default.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the requested archive output directory.</summary>
    public string? Destination { get; set; }

    /// <summary>Gets or sets whether extraction of the downloaded archive is requested. The default is <see langword="false"/>.</summary>
    public bool Untar { get; set; }

    /// <summary>Gets or sets the requested extraction directory.</summary>
    public string? UntarDirectory { get; set; }

    /// <summary>Gets or sets an explicit repository URL used to resolve the chart reference.</summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>Gets or sets the repository username.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the repository password.</summary>
    public string? Password { get; set; }

    /// <summary>Gets or sets whether repository-provided archive digest validation is requested. The default is <see langword="true"/>.</summary>
    public bool VerifyDigest { get; set; } = true;
}
