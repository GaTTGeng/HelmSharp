namespace HelmSharp.Registry;

public interface IOciRegistryClient
{
    Task LoginAsync(string server, string username, string password, CancellationToken cancellationToken = default);
    Task LogoutAsync(string server, CancellationToken cancellationToken = default);
    Task<string> PullAsync(string reference, CancellationToken cancellationToken = default);
    Task PushAsync(string reference, string chartTgzPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken cancellationToken = default);
}
