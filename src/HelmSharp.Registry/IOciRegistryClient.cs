namespace HelmSharp.Registry;

/// <summary>
/// Client for OCI chart registries: credentials, chart push/pull, and tag listing.
/// </summary>
public interface IOciRegistryClient
{
    /// <summary>Stores credentials for an OCI registry server.</summary>
    Task LoginAsync(string server, string username, string password, CancellationToken cancellationToken = default);
    /// <summary>Removes stored credentials for an OCI registry server.</summary>
    Task LogoutAsync(string server, CancellationToken cancellationToken = default);
    /// <summary>Pulls a chart reference and returns the local path of the downloaded archive.</summary>
    Task<string> PullAsync(string reference, CancellationToken cancellationToken = default);
    /// <summary>Pushes a packaged chart archive (<c>.tgz</c>) to an OCI registry reference.</summary>
    Task PushAsync(string reference, string chartTgzPath, CancellationToken cancellationToken = default);
    /// <summary>Lists the tags published for an OCI repository.</summary>
    Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken cancellationToken = default);
}
