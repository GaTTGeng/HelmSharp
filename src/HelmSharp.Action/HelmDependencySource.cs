using HelmSharp.Chart;
using HelmSharp.Repo;

namespace HelmSharp.Action;

/// <summary>A dependency chart staged locally: the downloaded/packaged archive path and its resolved version.</summary>
internal sealed record HelmStagedDependency(string ArchivePath, string Version);

/// <summary>
/// Resolves and stages chart dependencies for update/build: pulls from a configured repository,
/// an ad-hoc HTTP(S) URL, or a local <c>file://</c> directory.
/// </summary>
internal static class HelmDependencySource
{
    /// <summary>
    /// Stages one dependency into <paramref name="destination"/> and returns the archive path and
    /// resolved version. The resolved chart's name must match <paramref name="dependencyName"/>.
    /// </summary>
    /// <param name="repository">Chart repository facade used for index fetch and chart pull.</param>
    /// <param name="configuredRepositories">Known repository configurations used to resolve aliases and URLs.</param>
    /// <param name="refreshedRepositories">Names of repositories already refreshed in this run; mutated to dedupe fetches.</param>
    /// <param name="parentChartPath">Path of the chart declaring the dependency; base for <c>file://</c> resolution.</param>
    /// <param name="dependencyName">Expected chart name of the dependency.</param>
    /// <param name="versionConstraint">SemVer constraint or exact version; null accepts any version.</param>
    /// <param name="repositoryReference">Repository alias, URL, or <c>file://</c> path from Chart.yaml.</param>
    /// <param name="destination">Directory the dependency archive is staged into.</param>
    /// <param name="verifyDigest">Verify the downloaded archive digest against the repository index.</param>
    /// <param name="refreshConfiguredRepository">Refresh the repository index before pulling (once per repository per run).</param>
    /// <param name="requireConfiguredCache">Fail when no cached index exists instead of refreshing (dependency list without update).</param>
    /// <param name="exactVersion">Treat <paramref name="versionConstraint"/> as an exact locked version rather than a range.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
        bool exactVersion,
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
                exactVersion,
                cancellationToken);
        }

        var configured = ResolveConfiguredRepository(configuredRepositories, repositoryReference);
        HelmPullRequest pullRequest;
        if (configured is not null)
        {
            var cachePath = Path.Combine(
                repository.CacheDirectory,
                HelmChartRepository.GetRepositoryIndexCacheFileName(configured.Name));
            // Refresh each configured repository index at most once per run so multiple
            // dependencies from the same repo share a single index fetch.
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
                VerifyDigest = verifyDigest,
                ExactVersion = exactVersion
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
                VerifyDigest = verifyDigest,
                ExactVersion = exactVersion
            };
        }

        var archivePath = await repository.PullChartAsync(pullRequest, cancellationToken);
        var chart = await HelmChartLoader.LoadAsync(archivePath, cancellationToken);
        // Guard against a repository serving a different chart for the requested name;
        // Helm rejects the dependency in that case rather than trusting the download.
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
        bool exactVersion,
        CancellationToken cancellationToken)
    {
        // file:// references are relative to the parent chart directory unless rooted;
        // percent-escapes are decoded first so paths with spaces survive the URI syntax.
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
        // Locked dependencies demand an exact version match; declared constraints accept any
        // version the resolver satisfies. Both rejections keep Chart.lock and Chart.yaml honest.
        if (exactVersion
                ? !string.Equals(chart.Version, versionConstraint?.Trim(), StringComparison.Ordinal)
                : !HelmChartVersionResolver.Satisfies(chart.Version, versionConstraint))
        {
            var expectation = exactVersion
                ? $"locked version '{versionConstraint}'"
                : $"constraint '{versionConstraint}'";
            throw new InvalidDataException(
                $"File dependency '{dependencyName}' version '{chart.Version}' does not match {expectation}.");
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

        // URL comparison ignores trailing slashes and case because repositories.yaml entries
        // and Chart.yaml references routinely disagree on those details.
        var normalizedReference = repositoryReference.TrimEnd('/');
        return repositories.FirstOrDefault(repository =>
            string.Equals(repository.Url.TrimEnd('/'), normalizedReference, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts the alias from Helm's two alias spellings: <c>@name</c> and <c>alias:name</c>.
    /// </summary>
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
