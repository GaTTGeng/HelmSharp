using HelmSharp.Chart;
using HelmSharp.Repo;

namespace HelmSharp.Action;

internal sealed record HelmStagedDependency(string ArchivePath, string Version);

internal static class HelmDependencySource
{
    public static async Task<HelmStagedDependency> StageAsync(
        HelmChartRepository repository,
        IReadOnlyList<HelmRepository> configuredRepositories,
        ISet<string> refreshedRepositories,
        string parentChartPath,
        string dependencyName,
        string? versionConstraint,
        string repositoryReference,
        string destination,
        bool verifyDigest,
        bool refreshConfiguredRepository,
        bool requireConfiguredCache,
        CancellationToken cancellationToken)
    {
        if (repositoryReference.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return await StageFileDependencyAsync(
                parentChartPath,
                dependencyName,
                versionConstraint,
                repositoryReference,
                destination,
                cancellationToken);
        }

        var configured = ResolveConfiguredRepository(configuredRepositories, repositoryReference);
        HelmPullRequest pullRequest;
        if (configured is not null)
        {
            var cachePath = Path.Combine(
                repository.CacheDirectory,
                HelmChartRepository.GetRepositoryIndexCacheFileName(configured.Name));
            if (refreshConfiguredRepository && refreshedRepositories.Add(configured.Name))
                await repository.FetchRepoIndexAsync(configured, cancellationToken);
            else if (requireConfiguredCache && !File.Exists(cachePath))
            {
                throw new InvalidOperationException(
                    $"Cached repository index for '{configured.Name}' was not found. Run repository update first.");
            }

            pullRequest = new HelmPullRequest
            {
                ChartReference = $"{configured.Name}/{dependencyName}",
                Version = versionConstraint,
                Destination = destination,
                VerifyDigest = verifyDigest
            };
        }
        else
        {
            if (TryGetRepositoryAlias(repositoryReference, out var missingAlias))
                throw new InvalidOperationException($"Repository alias '{missingAlias}' is not configured.");
            if (!Uri.TryCreate(repositoryReference, UriKind.Absolute, out var repositoryUri) ||
                (repositoryUri.Scheme != Uri.UriSchemeHttp && repositoryUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"Dependency '{dependencyName}' has unsupported repository reference '{repositoryReference}'.");
            }

            pullRequest = new HelmPullRequest
            {
                ChartReference = dependencyName,
                RepositoryUrl = repositoryReference,
                Version = versionConstraint,
                Destination = destination,
                VerifyDigest = verifyDigest
            };
        }

        var archivePath = await repository.PullChartAsync(pullRequest, cancellationToken);
        var chart = await HelmChartLoader.LoadAsync(archivePath, cancellationToken);
        if (!string.Equals(chart.Name, dependencyName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Downloaded chart '{chart.Name}' does not match dependency '{dependencyName}'.");
        }

        return new HelmStagedDependency(archivePath, chart.Version);
    }

    private static async Task<HelmStagedDependency> StageFileDependencyAsync(
        string parentChartPath,
        string dependencyName,
        string? versionConstraint,
        string repositoryReference,
        string destination,
        CancellationToken cancellationToken)
    {
        var fileReference = Uri.UnescapeDataString(repositoryReference["file://".Length..]);
        var localPath = Path.IsPathRooted(fileReference)
            ? Path.GetFullPath(fileReference)
            : Path.GetFullPath(Path.Combine(
                parentChartPath,
                fileReference.Replace('/', Path.DirectorySeparatorChar)));
        if (!Directory.Exists(localPath))
            throw new DirectoryNotFoundException($"File dependency directory was not found: {localPath}");

        var chart = await HelmChartLoader.LoadAsync(localPath, cancellationToken);
        if (!string.Equals(chart.Name, dependencyName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"File dependency chart '{chart.Name}' does not match dependency '{dependencyName}'.");
        }
        if (!HelmChartVersionResolver.Satisfies(chart.Version, versionConstraint))
        {
            throw new InvalidDataException(
                $"File dependency '{dependencyName}' version '{chart.Version}' does not satisfy " +
                $"constraint '{versionConstraint}'.");
        }

        var archivePath = await HelmChartPackager.PackageAsync(
            localPath,
            destination,
            cancellationToken: cancellationToken);
        return new HelmStagedDependency(archivePath, chart.Version);
    }

    private static HelmRepository? ResolveConfiguredRepository(
        IReadOnlyList<HelmRepository> repositories,
        string repositoryReference)
    {
        if (TryGetRepositoryAlias(repositoryReference, out var alias))
        {
            return repositories.FirstOrDefault(repository =>
                string.Equals(repository.Name, alias, StringComparison.Ordinal));
        }

        var normalizedReference = repositoryReference.TrimEnd('/');
        return repositories.FirstOrDefault(repository =>
            string.Equals(repository.Url.TrimEnd('/'), normalizedReference, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetRepositoryAlias(string repositoryReference, out string alias)
    {
        if (repositoryReference.StartsWith('@') && repositoryReference.Length > 1)
        {
            alias = repositoryReference[1..];
            return true;
        }
        const string aliasPrefix = "alias:";
        if (repositoryReference.StartsWith(aliasPrefix, StringComparison.OrdinalIgnoreCase) &&
            repositoryReference.Length > aliasPrefix.Length)
        {
            alias = repositoryReference[aliasPrefix.Length..];
            return true;
        }

        alias = string.Empty;
        return false;
    }
}
