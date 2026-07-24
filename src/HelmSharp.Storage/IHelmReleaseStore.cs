using HelmSharp.Release;

namespace HelmSharp.Storage;

public interface IHelmReleaseStore
{
    Task SaveAsync(HelmReleaseRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HelmReleaseRecord>> ListAsync(string? namespaceName, bool allNamespaces, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HelmReleaseRecord>> HistoryAsync(string name, string namespaceName, CancellationToken cancellationToken = default);
    Task<HelmReleaseRecord?> GetLatestAsync(string name, string namespaceName, CancellationToken cancellationToken = default);
    Task MarkUninstalledAsync(HelmReleaseRecord record, CancellationToken cancellationToken = default);
    Task MarkStatusAsync(HelmReleaseRecord record, string status, CancellationToken cancellationToken = default);
    Task<int> NextRevisionAsync(string name, string namespaceName, CancellationToken cancellationToken = default);
}

public interface IHelmReleasePurgeStore : IHelmReleaseStore
{
    Task PurgeAsync(string name, string namespaceName, CancellationToken cancellationToken = default);
}
