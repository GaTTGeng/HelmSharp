namespace HelmSharp.Repo;

/// <summary>
/// Configures the durable repository configuration and cache locations.
/// </summary>
public sealed class HelmRepositoryOptions
{
    /// <summary>
    /// Gets or sets the directory that contains <c>repositories.yaml</c>.
    /// </summary>
    public string? ConfigDirectory { get; init; }

    /// <summary>
    /// Gets or sets the directory used for downloaded charts and repository indexes.
    /// </summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    /// Gets or sets the full path of the Helm-compatible repository configuration file.
    /// This takes precedence over <see cref="ConfigDirectory"/> and
    /// <c>HELM_REPOSITORY_CONFIG</c>.
    /// </summary>
    public string? RepositoryConfigPath { get; init; }
}
