using HelmSharp.Release;

namespace HelmSharp.Storage;

/// <summary>
/// Persists release records for the Helm release lifecycle (the storage half of
/// Helm's Kubernetes Secret-backed release store).
/// </summary>
public interface IHelmReleaseStore
{
    /// <summary>Stores a release record, creating or replacing its Secret entry.</summary>
    Task SaveAsync(HelmReleaseRecord record, CancellationToken cancellationToken = default);
    /// <summary>
    /// Lists the latest record per release in a namespace, or across all namespaces when
    /// <paramref name="allNamespaces"/> is true (ignoring <paramref name="namespaceName"/>).
    /// </summary>
    Task<IReadOnlyList<HelmReleaseRecord>> ListAsync(string? namespaceName, bool allNamespaces, CancellationToken cancellationToken = default);
    /// <summary>All stored revisions for one release, ordered by revision number.</summary>
    Task<IReadOnlyList<HelmReleaseRecord>> HistoryAsync(string name, string namespaceName, CancellationToken cancellationToken = default);
    /// <summary>The highest-revision record that is not uninstalled, or null when none exists.</summary>
    Task<HelmReleaseRecord?> GetLatestAsync(string name, string namespaceName, CancellationToken cancellationToken = default);
    /// <summary>
    /// Records an uninstall: supersedes the current record and appends a new
    /// <c>uninstalled</c> revision so history is preserved for later purge.
    /// </summary>
    Task MarkUninstalledAsync(HelmReleaseRecord record, CancellationToken cancellationToken = default);
    /// <summary>Updates and re-saves a record with a new lifecycle status (for example <c>deployed</c> or <c>failed</c>).</summary>
    Task MarkStatusAsync(HelmReleaseRecord record, string status, CancellationToken cancellationToken = default);
    /// <summary>Allocates the next revision number for a release (previous maximum plus one).</summary>
    Task<int> NextRevisionAsync(string name, string namespaceName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extends <see cref="IHelmReleaseStore"/> with hard deletion of every record for a release
/// (equivalent to <c>helm uninstall --purge</c>).
/// </summary>
public interface IHelmReleasePurgeStore : IHelmReleaseStore
{
    /// <summary>Deletes all stored revisions for the release.</summary>
    Task PurgeAsync(string name, string namespaceName, CancellationToken cancellationToken = default);
}
