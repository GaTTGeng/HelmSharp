using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HelmSharp.Chart;

namespace HelmSharp.Repo;

/// <summary>
/// Chart repository client for downloading charts from HTTP repositories and OCI registries.
/// </summary>
public sealed class HelmChartRepository : IDisposable
{
    private static readonly string ProductVersion =
        typeof(HelmChartRepository).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly HttpClient _httpClient;
    private readonly Func<HelmRepository, HttpMessageHandler> _createRepositoryHandler;
    private readonly string _cacheDir;
    private readonly string _repositoryConfigPath;

    /// <summary>
    /// Creates a repository client using Helm-compatible environment settings and defaults.
    /// </summary>
    public HelmChartRepository(string? cacheDir = null)
        : this(cacheDir is null
            ? new HelmRepositoryOptions()
            : new HelmRepositoryOptions { CacheDirectory = cacheDir, ConfigDirectory = cacheDir })
    {
    }

    /// <summary>
    /// Creates a repository client with explicit configuration and cache locations.
    /// </summary>
    public HelmChartRepository(HelmRepositoryOptions options)
        : this(options, new HttpClientHandler(), CreateConfiguredRepositoryHandler)
    {
    }

    internal HelmChartRepository(
        HelmRepositoryOptions options,
        HttpMessageHandler httpMessageHandler,
        Func<HelmRepository, HttpMessageHandler>? createRepositoryHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpMessageHandler);

        _cacheDir = ResolveCacheDirectory(options);
        _repositoryConfigPath = ResolveRepositoryConfigPath(options);
        Directory.CreateDirectory(_cacheDir);
        var configDirectory = Path.GetDirectoryName(_repositoryConfigPath);
        if (!string.IsNullOrWhiteSpace(configDirectory))
            Directory.CreateDirectory(configDirectory);

        _httpClient = new HttpClient(httpMessageHandler);
        _createRepositoryHandler = createRepositoryHandler ?? CreateConfiguredRepositoryHandler;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("helmsharp", ProductVersion));
    }

    /// <summary>Gets the directory used for downloaded charts and repository indexes.</summary>
    public string CacheDirectory => _cacheDir;

    /// <summary>Gets the Helm-compatible <c>repositories.yaml</c> configuration path.</summary>
    public string RepositoryConfigPath => _repositoryConfigPath;

    /// <summary>
    /// Downloads a chart to a local directory. Supports:
    /// - Local paths (returned as-is)
    /// - HTTP/HTTPS chart repository URLs
    /// - OCI references (oci://registry/repo/chart)
    /// </summary>
    public async Task<string> PullChartAsync(
        string chartRef,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(chartRef) || File.Exists(chartRef))
            return chartRef;
        if (chartRef.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
            return await PullFromOciAsync(chartRef, version, cancellationToken);
        if (chartRef.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            chartRef.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return await PullFromHttpToCacheAsync(
                chartRef,
                version,
                username: null,
                password: null,
                passCredentialsAll: false,
                cancellationToken);
        }

        return chartRef;
    }

    /// <summary>
    /// Downloads a chart using an extensible request object.
    /// </summary>
    public async Task<string> PullChartAsync(
        HelmPullRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ChartReference))
            throw new ArgumentException("ChartReference is required.", nameof(request));
        var chartRef = request.ChartReference;

        // Local path
        if (Directory.Exists(chartRef) || File.Exists(chartRef))
            return chartRef;

        // OCI reference
        if (chartRef.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
            return await PullFromOciAsync(chartRef, request.Version, cancellationToken);

        // HTTP/HTTPS URL, explicit repository URL, or configured repo/name reference.
        if (chartRef.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            chartRef.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(request.RepositoryUrl) ||
            chartRef.Contains('/', StringComparison.Ordinal))
            return await PullTraditionalChartAsync(request, cancellationToken);

        throw new InvalidOperationException(
            $"Chart reference '{chartRef}' is neither a local chart, a direct URL, nor a configured repo/chart reference.");
    }

    /// <summary>
    /// Adds a chart repository.
    /// </summary>
    public async Task AddRepositoryAsync(
        string name,
        string url,
        string? username = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositoryName(name);
        var normalizedUrl = NormalizeRepositoryUrl(url);
        await using var repositoryLock = await AcquireRepositoryConfigurationLockAsync(cancellationToken);
        var repos = await LoadRepositoriesAsync(_repositoryConfigPath, cancellationToken);
        var candidate = new HelmRepository
        {
            Name = name, Url = normalizedUrl, Username = username, Password = password
        };
        if (repos.TryGetValue(name, out var existing))
        {
            if (RepositoryConfigurationsMatch(existing, candidate))
                return;
            throw new InvalidOperationException($"Repository '{name}' already exists. Remove it before adding it again.");
        }

        repos[name] = candidate;
        await SaveRepositoriesAsync(_repositoryConfigPath, repos, cancellationToken);
    }

    /// <summary>
    /// Removes a chart repository.
    /// </summary>
    public async Task RemoveRepositoryAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var repositoryLock = await AcquireRepositoryConfigurationLockAsync(cancellationToken);
        var repos = await LoadRepositoriesAsync(_repositoryConfigPath, cancellationToken);
        if (!repos.Remove(name))
            throw new InvalidOperationException($"Repository '{name}' does not exist.");
        await SaveRepositoriesAsync(_repositoryConfigPath, repos, cancellationToken);
        var cachePath = Path.Combine(_cacheDir, GetRepositoryIndexCacheFileName(name));
        if (File.Exists(cachePath))
            File.Delete(cachePath);
    }

    /// <summary>
    /// Lists configured repositories.
    /// </summary>
    public async Task<List<HelmRepository>> ListRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        var repos = await LoadRepositoriesAsync(_repositoryConfigPath, cancellationToken);
        return repos.Values
            .OrderBy(repo => repo.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(repo => repo.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Searches cached indexes for all configured repositories. This operation does not access the network.
    /// Run a repository update first to populate the caches.
    /// </summary>
    public async Task<List<HelmChartSearchResult>> SearchRepoAsync(
        string keyword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        var repositories = await ListRepositoriesAsync(cancellationToken);
        if (repositories.Count == 0)
            throw new InvalidOperationException("No chart repositories are configured.");

        var results = new List<HelmChartSearchResult>();
        foreach (var repository in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cachePath = Path.Combine(_cacheDir, GetRepositoryIndexCacheFileName(repository.Name));
            if (!File.Exists(cachePath))
                continue;

            try
            {
                var yaml = await File.ReadAllTextAsync(cachePath, cancellationToken);
                results.AddRange(SearchIndex(ParseRepoIndex(yaml), keyword, repository.Name));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Match Helm's repository search behavior: one missing or corrupt cache must not
                // prevent results from other configured repositories from being returned.
            }
        }

        return SortSearchResults(results);
    }

    /// <summary>
    /// Searches for charts in a repository by keyword.
    /// </summary>
    public async Task<List<HelmChartSearchResult>> SearchRepoAsync(
        string repoUrl,
        string keyword,
        string? username = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        var index = await FetchRepoIndexAsync(repoUrl, username, password, cancellationToken);
        return SortSearchResults(SearchIndex(index, keyword, repositoryName: null));
    }

    internal async Task<List<HelmRepositoryUpdateResult>> UpdateConfiguredRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var repositories = await ListRepositoriesAsync(cancellationToken);
        var results = new List<HelmRepositoryUpdateResult>(repositories.Count);
        foreach (var repository in repositories)
        {
            try
            {
                await FetchRepoIndexAsync(repository, cancellationToken);
                results.Add(new HelmRepositoryUpdateResult(repository.Name, true, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new HelmRepositoryUpdateResult(repository.Name, false, ex.Message));
            }
        }

        return results;
    }

    private static IEnumerable<HelmChartSearchResult> SearchIndex(
        HelmRepoIndex index,
        string keyword,
        string? repositoryName)
    {
        foreach (var entry in index.Entries)
        {
            var latest = HelmChartVersionResolver.Resolve(entry.Value, constraint: null);
            if (latest is null
                || (!entry.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    && !(latest.Description?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                continue;
            }

            yield return new HelmChartSearchResult
            {
                Name = repositoryName is null ? entry.Key : $"{repositoryName}/{entry.Key}",
                Version = latest.Version,
                Description = latest.Description,
                AppVersion = latest.AppVersion
            };
        }
    }

    private static List<HelmChartSearchResult> SortSearchResults(IEnumerable<HelmChartSearchResult> results)
        => results
            .OrderBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Fetches and caches a repository index.
    /// </summary>
    public Task<HelmRepoIndex> FetchRepoIndexAsync(
        string repoUrl,
        string? username = null,
        string? password = null,
        CancellationToken cancellationToken = default)
        => FetchRepoIndexAsync(repoUrl, username, password, repositoryName: null, cancellationToken: cancellationToken);

    /// <summary>
    /// Fetches and caches a repository index using the provided repository name for the cache filename.
    /// </summary>
    public async Task<HelmRepoIndex> FetchRepoIndexAsync(
        string repoUrl,
        string? username,
        string? password,
        string? repositoryName,
        CancellationToken cancellationToken = default)
    {
        var indexUrl = repoUrl.TrimEnd('/') + "/index.yaml";
        var request = new HttpRequestMessage(HttpMethod.Get, indexUrl);
        if (!string.IsNullOrWhiteSpace(username))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var yaml = await response.Content.ReadAsStringAsync(cancellationToken);
        var index = ParseRepoIndex(yaml);
        await CacheRepositoryIndexAsync(repoUrl, yaml, repositoryName, cancellationToken);
        return index;
    }

    /// <summary>Fetches an index using the TLS and credential options configured for a repository.</summary>
    public Task<HelmRepoIndex> FetchRepoIndexAsync(HelmRepository repository, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return FetchConfiguredRepositoryIndexAsync(repository, cancellationToken);
    }

    private async Task<HelmRepoIndex> FetchConfiguredRepositoryIndexAsync(HelmRepository repository, CancellationToken cancellationToken)
    {
        using var handler = _createRepositoryHandler(repository);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("helmsharp", ProductVersion));
        using var response = await SendRepositoryIndexRequestAsync(client, repository, cancellationToken);
        response.EnsureSuccessStatusCode();
        var yaml = await response.Content.ReadAsStringAsync(cancellationToken);
        var index = ParseRepoIndex(yaml);
        await CacheRepositoryIndexAsync(repository.Url, yaml, repository.Name, cancellationToken);
        return index;
    }

    private static HttpMessageHandler CreateConfiguredRepositoryHandler(HelmRepository repository)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            PreAuthenticate = repository.PassCredentialsAll
        };
        if (repository.InsecureSkipTlsVerify)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        else if (!string.IsNullOrWhiteSpace(repository.CaFile))
        {
            var trustedRoots = LoadCertificates(repository.CaFile);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, serverChain, sslPolicyErrors) =>
                ValidateCustomCertificate(certificate, serverChain, sslPolicyErrors, trustedRoots);
        }
        if (!string.IsNullOrWhiteSpace(repository.CertFile))
            handler.ClientCertificates.Add(LoadClientCertificate(repository.CertFile, repository.KeyFile));
        return handler;
    }

    internal static async Task<HttpResponseMessage> SendRepositoryIndexRequestAsync(
        HttpClient client,
        HelmRepository repository,
        CancellationToken cancellationToken)
    {
        var currentUri = new Uri(repository.Url.TrimEnd('/') + "/index.yaml", UriKind.Absolute);
        var repositoryUri = new Uri(repository.Url, UriKind.Absolute);
        const int maxRedirects = 10;
        for (var redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            using var request = CreateRepositoryIndexRequest(currentUri, repository, repositoryUri);
            var response = await client.SendAsync(request, cancellationToken);
            if (!IsRedirect(response.StatusCode))
                return response;

            var location = response.Headers.Location;
            if (location is null)
                return response;

            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            response.Dispose();
        }

        throw new HttpRequestException($"Repository index request exceeded {maxRedirects} redirects.");
    }

    internal static HttpRequestMessage CreateRepositoryIndexRequest(
        Uri indexUri,
        HelmRepository repository,
        Uri repositoryUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, indexUri);
        if (!string.IsNullOrWhiteSpace(repository.Username)
            && (repository.PassCredentialsAll || IsSameHost(indexUri, repositoryUri)))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{repository.Username}:{repository.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        return request;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect
            or HttpStatusCode.MultipleChoices;

    private static bool IsSameHost(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
           && left.Port == right.Port;

    internal static bool ValidateCustomCertificate(
        X509Certificate? certificate,
        X509Chain? serverChain,
        SslPolicyErrors sslPolicyErrors,
        X509Certificate2Collection trustedRoots)
    {
        if (certificate is null)
            return false;
        if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return false;
        if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return false;

        using var validationChain = new X509Chain();
        validationChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        validationChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        foreach (var trustedRoot in trustedRoots)
            validationChain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        AddServerChainCertificates(validationChain, serverChain);

        using var serverCertificate = new X509Certificate2(certificate);
        return validationChain.Build(serverCertificate);
    }

    internal static void AddServerChainCertificates(X509Chain validationChain, X509Chain? serverChain)
    {
        if (serverChain is null)
            return;

        // The first element is the leaf certificate supplied separately. Preserve every
        // remaining server-provided element so custom-root validation can build through
        // intermediates that are not installed locally or available through AIA.
        for (var index = 1; index < serverChain.ChainElements.Count; index++)
            validationChain.ChainPolicy.ExtraStore.Add(serverChain.ChainElements[index].Certificate);
    }

    private async Task<string> PullTraditionalChartAsync(
        HelmPullRequest request,
        CancellationToken cancellationToken)
    {
        var chartReference = request.ChartReference;
        string chartName;
        string archiveFileName;
        string archiveUrl;
        string? expectedDigest = null;
        var username = request.Username;
        var password = request.Password;
        var passCredentials = false;
        HelmRepository? configuredRepository = null;

        if (IsArchiveUrl(chartReference))
        {
            var archiveUri = ParseAbsoluteHttpUri(chartReference, "chart archive");
            if (!string.IsNullOrWhiteSpace(request.RepositoryUrl))
            {
                var repositoryUri = ParseAbsoluteHttpUri(request.RepositoryUrl, "repository");
                passCredentials = request.PassCredentialsAll || HaveSameOrigin(repositoryUri, archiveUri);
            }
            else
            {
                // Without a separate repository origin, request credentials belong to
                // the direct archive itself and remain valid for private direct URLs.
                passCredentials = true;
            }
            archiveUrl = archiveUri.AbsoluteUri;
            archiveFileName = Uri.UnescapeDataString(Path.GetFileName(archiveUri.AbsolutePath));
            chartName = RemoveChartArchiveExtension(archiveFileName);
        }
        else
        {
            HelmChartVersion entry;
            Uri repositoryUri;

            if (!string.IsNullOrWhiteSpace(request.RepositoryUrl))
            {
                repositoryUri = ParseAbsoluteHttpUri(request.RepositoryUrl, "repository");
                chartName = GetChartName(chartReference);
                entry = await ResolveChartVersionAsync(
                    repositoryUri.AbsoluteUri.TrimEnd('/'),
                    chartName,
                    request.Version,
                    username,
                    password,
                    cancellationToken);
            }
            else if (chartReference.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     chartReference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var chartUri = ParseAbsoluteHttpUri(chartReference, "chart reference");
                chartName = GetChartName(chartUri.AbsolutePath);
                repositoryUri = new Uri(chartUri, "./");
                entry = await ResolveChartVersionAsync(
                    repositoryUri.AbsoluteUri.TrimEnd('/'),
                    chartName,
                    request.Version,
                    username,
                    password,
                    cancellationToken);
            }
            else
            {
                var separator = chartReference.IndexOf('/', StringComparison.Ordinal);
                if (separator <= 0 || separator == chartReference.Length - 1)
                    throw new InvalidOperationException(
                        $"Configured repository references must use repo/chart syntax: '{chartReference}'.");

                var repositoryName = chartReference[..separator];
                chartName = chartReference[(separator + 1)..];
                var repositories = await LoadRepositoriesAsync(_repositoryConfigPath, cancellationToken);
                if (!repositories.TryGetValue(repositoryName, out configuredRepository))
                    throw new InvalidOperationException($"Repository '{repositoryName}' is not configured.");

                repositoryUri = ParseAbsoluteHttpUri(configuredRepository.Url, "repository");
                username ??= configuredRepository.Username;
                password ??= configuredRepository.Password;
                var indexRepository = CopyRepositoryWithCredentials(configuredRepository, username, password);
                var index = await LoadConfiguredRepositoryIndexAsync(indexRepository, cancellationToken);
                entry = ResolveChartVersion(index, chartName, request.Version);
            }

            var entryUrl = entry.Urls.FirstOrDefault()
                           ?? throw new InvalidOperationException(
                               $"No download URL for chart '{chartName}' version '{entry.Version}'.");
            var archiveUri = Uri.TryCreate(entryUrl, UriKind.Absolute, out var absoluteArchiveUri)
                ? absoluteArchiveUri
                : new Uri(new Uri(repositoryUri.AbsoluteUri.TrimEnd('/') + "/"), entryUrl);
            passCredentials = request.PassCredentialsAll
                              || configuredRepository?.PassCredentialsAll == true
                              || HaveSameOrigin(repositoryUri, archiveUri);
            archiveUrl = archiveUri.AbsoluteUri;
            archiveFileName = $"{chartName}-{entry.Version}.tgz";
            expectedDigest = entry.Digest;
        }

        var chartBytes = configuredRepository is null
            ? await DownloadChartArchiveAsync(
                archiveUrl,
                passCredentials ? username : null,
                passCredentials ? password : null,
                cancellationToken)
            : await DownloadConfiguredChartArchiveAsync(
                archiveUrl,
                configuredRepository,
                username,
                password,
                request.PassCredentialsAll || configuredRepository.PassCredentialsAll,
                cancellationToken);
        if (request.VerifyDigest && !string.IsNullOrWhiteSpace(expectedDigest))
            VerifyArchiveDigest(chartBytes, expectedDigest, chartName);

        var destination = Path.GetFullPath(string.IsNullOrWhiteSpace(request.Destination)
            ? Directory.GetCurrentDirectory()
            : request.Destination);
        Directory.CreateDirectory(destination);
        archiveFileName = Path.GetFileName(archiveFileName);
        if (string.IsNullOrWhiteSpace(archiveFileName))
            throw new InvalidDataException("The chart archive URL does not contain a valid filename.");
        var archivePath = Path.Combine(destination, archiveFileName);
        await WriteAllBytesAtomicallyAsync(archivePath, chartBytes, cancellationToken);

        if (!request.Untar)
            return archivePath;

        var extractionRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(request.UntarDirectory)
            ? destination
            : request.UntarDirectory);
        Directory.CreateDirectory(extractionRoot);
        var archiveRoot = GetChartArchiveRoot(chartBytes) ?? chartName;
        var extractDirectory = HelmArchivePath.ResolveSafeDestination(extractionRoot, archiveRoot);
        var tempExtractDirectory = $"{extractDirectory}.tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(tempExtractDirectory);
        try
        {
            await ExtractChartArchiveAsync(chartBytes, tempExtractDirectory, cancellationToken);
            if (Directory.Exists(extractDirectory))
                Directory.Delete(extractDirectory, recursive: true);

            Directory.Move(tempExtractDirectory, extractDirectory);
        }
        catch
        {
            if (Directory.Exists(tempExtractDirectory))
                Directory.Delete(tempExtractDirectory, recursive: true);
            throw;
        }

        return extractDirectory;
    }

    private async Task<string> PullFromHttpToCacheAsync(
        string chartUrl,
        string? version,
        string? username,
        string? password,
        bool passCredentialsAll,
        CancellationToken cancellationToken)
    {
        var url = chartUrl;
        var passCredentials = true;
        if (!IsArchiveUrl(url))
        {
            var chartUri = ParseAbsoluteHttpUri(url, "chart reference");
            var chartName = GetChartName(chartUri.AbsolutePath);
            var repositoryUri = new Uri(chartUri, "./");
            var entry = await ResolveChartVersionAsync(
                repositoryUri.AbsoluteUri.TrimEnd('/'),
                chartName,
                version,
                username,
                password,
                cancellationToken);
            var entryUrl = entry.Urls.FirstOrDefault()
                           ?? throw new InvalidOperationException(
                               $"No download URL for chart '{chartName}' version '{entry.Version}'.");
            var archiveUri = Uri.TryCreate(entryUrl, UriKind.Absolute, out var absoluteArchiveUri)
                ? absoluteArchiveUri
                : new Uri(repositoryUri, entryUrl);
            passCredentials = passCredentialsAll || HaveSameOrigin(repositoryUri, archiveUri);
            url = archiveUri.AbsoluteUri;
        }

        return await DownloadAndExtractChartAsync(
            url,
            passCredentials ? username : null,
            passCredentials ? password : null,
            cancellationToken);
    }

    private async Task<HelmRepoIndex> LoadConfiguredRepositoryIndexAsync(
        HelmRepository repository,
        CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(_cacheDir, GetRepositoryIndexCacheFileName(repository.Name));
        if (File.Exists(cachePath))
        {
            var yaml = await File.ReadAllTextAsync(cachePath, cancellationToken);
            return ParseRepoIndex(yaml);
        }

        return await FetchRepoIndexAsync(repository, cancellationToken);
    }

    private static HelmRepository CopyRepositoryWithCredentials(
        HelmRepository repository,
        string? username,
        string? password)
        => new()
        {
            Name = repository.Name,
            Url = repository.Url,
            Username = username,
            Password = password,
            CertFile = repository.CertFile,
            KeyFile = repository.KeyFile,
            CaFile = repository.CaFile,
            InsecureSkipTlsVerify = repository.InsecureSkipTlsVerify,
            PassCredentialsAll = repository.PassCredentialsAll
        };

    private static HelmChartVersion ResolveChartVersion(
        HelmRepoIndex index,
        string chartName,
        string? versionConstraint)
    {
        if (!index.Entries.TryGetValue(chartName, out var versions))
            throw new InvalidOperationException($"Chart '{chartName}' not found in repository.");

        var entry = HelmChartVersionResolver.Resolve(versions, versionConstraint);
        if (entry is not null)
            return entry;

        var requested = string.IsNullOrWhiteSpace(versionConstraint)
            ? "latest stable version"
            : $"version constraint '{versionConstraint}'";
        throw new InvalidOperationException(
            $"Chart '{chartName}' has no version satisfying {requested}. Available versions: " +
            string.Join(", ", versions.Select(candidate => candidate.Version)));
    }

    private static bool IsArchiveUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        return uri.AbsolutePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri ParseAbsoluteHttpUri(string value, string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"Invalid {description} URL: {value}");
        return uri;
    }

    private static string GetChartName(string chartReference)
    {
        var chartName = Uri.UnescapeDataString(chartReference.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(chartName))
            throw new ArgumentException($"Invalid chart reference: {chartReference}");
        return chartName;
    }

    private static string RemoveChartArchiveExtension(string fileName)
        => fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^7]
            : fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^4]
                : fileName;

    private static bool HaveSameOrigin(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
           && left.Port == right.Port;

    internal async Task<HelmChartVersion> ResolveChartVersionAsync(
        string repoUrl,
        string chartName,
        string? versionConstraint = null,
        string? username = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        var index = await FetchRepoIndexAsync(repoUrl, username, password, cancellationToken);
        if (!index.Entries.TryGetValue(chartName, out var versions))
            throw new InvalidOperationException($"Chart '{chartName}' not found in repository");

        var entry = HelmChartVersionResolver.Resolve(versions, versionConstraint);
        if (entry is not null)
            return entry;

        var requested = string.IsNullOrWhiteSpace(versionConstraint)
            ? "latest stable version"
            : $"version constraint '{versionConstraint}'";
        var available = string.Join(", ", versions.Select(version => version.Version));
        throw new InvalidOperationException(
            $"Chart '{chartName}' has no version satisfying {requested}. Available versions: {available}");
    }

    private async Task<string> PullFromOciAsync(
        string ociRef,
        string? version,
        CancellationToken cancellationToken)
    {
        // Parse OCI reference: oci://registry/repo/chart:tag
        var withoutScheme = ociRef["oci://".Length..];
        var parts = withoutScheme.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException($"Invalid OCI reference: {ociRef}");

        var registry = parts[0];
        var repository = string.Join("/", parts.Skip(1));

        // Remove tag if present
        var tagParts = repository.Split(':');
        var repo = tagParts[0];
        var tag = tagParts.Length > 1 ? tagParts[1] : (version ?? "latest");

        var manifestUrl = $"https://{registry}/v2/{repo}/manifests/{tag}";
        var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var manifestJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var manifest = JsonSerializer.Deserialize<JsonElement>(manifestJson);

        // Find the chart layer
        if (!manifest.TryGetProperty("layers", out var layers))
            throw new InvalidOperationException("OCI manifest has no layers");

        JsonElement chartLayer = default;
        foreach (var layer in layers.EnumerateArray())
        {
            if (layer.TryGetProperty("mediaType", out var mediaType) &&
                mediaType.GetString()?.Contains("tar", StringComparison.OrdinalIgnoreCase) == true)
            {
                chartLayer = layer;
                break;
            }
        }

        if (chartLayer.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("No chart layer found in OCI manifest");

        var digest = chartLayer.GetProperty("digest").GetString()
                     ?? throw new InvalidOperationException("Layer has no digest");

        var blobUrl = $"https://{registry}/v2/{repo}/blobs/{digest}";
        return await DownloadAndExtractChartAsync(blobUrl, username: null, password: null, cancellationToken);
    }

    private async Task<string> DownloadAndExtractChartAsync(
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var chartBytes = await DownloadChartArchiveAsync(url, username, password, cancellationToken);

        // Create a cache key from the URL hash
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(chartBytes))[..12];
        var extractDir = Path.Combine(_cacheDir, hash);
        if (Directory.Exists(extractDir))
            return extractDir;

        var tempExtractDir = Path.Combine(_cacheDir, $"{hash}.tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtractDir);
        try
        {
            await ExtractChartArchiveAsync(chartBytes, tempExtractDir, cancellationToken);
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(tempExtractDir, recursive: true);
                return extractDir;
            }

            Directory.Move(tempExtractDir, extractDir);
        }
        catch
        {
            if (Directory.Exists(tempExtractDir))
                Directory.Delete(tempExtractDir, recursive: true);
            throw;
        }

        return extractDir;
    }

    private async Task<byte[]> DownloadChartArchiveAsync(
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(username))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<byte[]> DownloadConfiguredChartArchiveAsync(
        string url,
        HelmRepository repository,
        string? username,
        string? password,
        bool passCredentialsAll,
        CancellationToken cancellationToken)
    {
        using var handler = _createRepositoryHandler(repository);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("helmsharp", ProductVersion));
        var repositoryUri = new Uri(repository.Url, UriKind.Absolute);
        var currentUri = new Uri(url, UriKind.Absolute);
        const int maxRedirects = 10;

        for (var redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            if (!string.IsNullOrWhiteSpace(username) &&
                (passCredentialsAll || HaveSameOrigin(repositoryUri, currentUri)))
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                response.EnsureSuccessStatusCode();
                throw new HttpRequestException("Chart archive redirect response did not include a Location header.");
            }

            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        }

        throw new HttpRequestException($"Chart archive request exceeded {maxRedirects} redirects.");
    }

    private static void VerifyArchiveDigest(byte[] chartBytes, string expectedDigest, string chartName)
    {
        const string sha256Prefix = "sha256:";
        var expectedHash = expectedDigest;
        var separator = expectedDigest.IndexOf(':', StringComparison.Ordinal);
        if (separator >= 0 && !expectedDigest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Chart '{chartName}' has unsupported digest '{expectedDigest}'. Only SHA-256 digests are supported.");
        if (expectedDigest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase))
            expectedHash = expectedDigest[sha256Prefix.Length..];

        if (expectedHash.Length != 64 || expectedHash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"Chart '{chartName}' has invalid SHA-256 digest '{expectedDigest}'.");

        var actualHash = Convert.ToHexString(SHA256.HashData(chartBytes));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Chart '{chartName}' digest mismatch: expected {expectedDigest}, actual sha256:{actualHash.ToLowerInvariant()}.");
    }

    private static async Task WriteAllBytesAtomicallyAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string? GetChartArchiveRoot(byte[] chartBytes)
    {
        using var memoryStream = new MemoryStream(chartBytes);
        using var gzip = new GZipStream(memoryStream, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        var entryNames = new List<string>();
        TarEntry? entry;
        while ((entry = tar.GetNextEntry(copyData: false)) is not null)
        {
            if (entry.EntryType is TarEntryType.Directory)
                continue;
            entryNames.Add(HelmArchivePath.NormalizeEntryName(entry.Name));
        }

        return HelmArchivePath.FindChartRoot(entryNames);
    }

    internal static async Task ExtractChartArchiveAsync(
        byte[] chartBytes,
        string extractDir,
        CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream(chartBytes);
        using var gzip = new GZipStream(memoryStream, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        var archiveFiles = new List<ArchiveFileEntry>();
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.Directory || entry.DataStream is null)
                continue;

            var entryName = HelmArchivePath.NormalizeEntryName(entry.Name);
            using var fileBytes = new MemoryStream();
            await entry.DataStream.CopyToAsync(fileBytes, cancellationToken);
            archiveFiles.Add(new ArchiveFileEntry(entryName, fileBytes.ToArray()));
        }

        var chartRoot = HelmArchivePath.FindChartRoot(archiveFiles.Select(fileEntry => fileEntry.Name));
        foreach (var fileEntry in archiveFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryPath = HelmArchivePath.GetChartRelativePath(fileEntry.Name, chartRoot);
            if (string.IsNullOrWhiteSpace(entryPath))
                throw new InvalidDataException($"Chart archive entry '{fileEntry.Name}' has no path below the chart root.");

            var fullPath = HelmArchivePath.ResolveSafeDestination(extractDir, entryPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is not null) Directory.CreateDirectory(dir);

            await using var fileStream = File.Create(fullPath);
            await fileStream.WriteAsync(fileEntry.Content, cancellationToken);
        }
    }

    private sealed record ArchiveFileEntry(string Name, byte[] Content);

    internal static HelmRepoIndex ParseRepoIndex(string yaml)
    {
        var dict = HelmYaml.DeserializeDictionary(yaml);
        var apiVersion = HelmYaml.GetString(dict, "apiVersion");
        if (string.IsNullOrWhiteSpace(apiVersion))
            throw new InvalidDataException("Repository index is missing the required apiVersion field.");
        if (!dict.TryGetValue("entries", out var entriesObj)
            || entriesObj is not IDictionary<string, object?> entries)
            throw new InvalidDataException("Repository index is missing the required entries mapping.");

        var index = new HelmRepoIndex
        {
            ApiVersion = apiVersion,
            Generated = HelmYaml.GetString(dict, "generated") ?? string.Empty
        };

        foreach (var (name, value) in entries)
        {
            if (value is IList<object?> versions)
            {
                var chartVersions = new List<HelmChartVersion>();
                foreach (var v in versions)
                {
                    if (v is IDictionary<string, object?> verDict)
                    {
                        var cv = new HelmChartVersion
                        {
                            Name = HelmYaml.GetString(verDict, "name") ?? name,
                            Version = HelmYaml.GetString(verDict, "version") ?? string.Empty,
                            Description = HelmYaml.GetString(verDict, "description"),
                            AppVersion = HelmYaml.GetString(verDict, "appVersion"),
                            Digest = HelmYaml.GetString(verDict, "digest"),
                            Created = HelmYaml.GetString(verDict, "created"),
                        };
                        if (verDict.TryGetValue("urls", out var urlsObj) && urlsObj is IList<object?> urls)
                        {
                            cv.Urls = urls.Select(u => Convert.ToString(u) ?? string.Empty).ToList();
                        }
                        chartVersions.Add(cv);
                    }
                }
                index.Entries[name] = chartVersions;
            }
        }

        return index;
    }

    private async Task<Dictionary<string, HelmRepository>> LoadRepositoriesAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return await LoadLegacyRepositoriesAsync(ct);

        var yaml = await File.ReadAllTextAsync(path, ct);
        var root = HelmYaml.DeserializeDictionary(yaml);
        var repos = new Dictionary<string, HelmRepository>(StringComparer.Ordinal);
        if (root.TryGetValue("repositories", out var repositoriesObject)
            && repositoriesObject is IList<object?> repositories)
        {
            foreach (var entry in repositories.OfType<IDictionary<string, object?>>())
            {
                var name = HelmYaml.GetString(entry, "name");
                var url = HelmYaml.GetString(entry, "url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    continue;

                repos[name] = new HelmRepository
                {
                    Name = name,
                    Url = url,
                    Username = HelmYaml.GetString(entry, "username"),
                    Password = HelmYaml.GetString(entry, "password"),
                    CertFile = HelmYaml.GetString(entry, "certFile"),
                    KeyFile = HelmYaml.GetString(entry, "keyFile"),
                    CaFile = HelmYaml.GetString(entry, "caFile"),
                    InsecureSkipTlsVerify = GetBoolean(entry, "insecure_skip_tls_verify"),
                    PassCredentialsAll = GetBoolean(entry, "pass_credentials_all")
                };
            }
        }

        return repos;
    }

    private async Task<Dictionary<string, HelmRepository>> LoadLegacyRepositoriesAsync(CancellationToken ct)
    {
        var legacyPath = Path.Combine(_cacheDir, "repositories.json");
        if (!File.Exists(legacyPath))
            return new Dictionary<string, HelmRepository>(StringComparer.Ordinal);

        var json = await File.ReadAllTextAsync(legacyPath, ct);
        var repositories = JsonSerializer.Deserialize<Dictionary<string, HelmRepository>>(json)
            ?? new Dictionary<string, HelmRepository>();
        return new Dictionary<string, HelmRepository>(repositories, StringComparer.Ordinal);
    }

    private async Task SaveRepositoriesAsync(string path, Dictionary<string, HelmRepository> repos, CancellationToken ct)
    {
        var document = new Dictionary<string, object?>
        {
            ["apiVersion"] = "v1",
            ["generated"] = DateTimeOffset.UtcNow.ToString("O"),
            ["repositories"] = repos.Values
                .OrderBy(repo => repo.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToRepositoryConfiguration)
                .Cast<object?>()
                .ToList()
        };
        await WriteAllTextAtomicallyAsync(
            path,
            HelmYaml.Serialize(document),
            ct,
            ownerOnlyWhenCreating: true);
    }

    private async Task<FileStream> AcquireRepositoryConfigurationLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = GetRepositoryConfigLockPath(_repositoryConfigPath);
        var lockDirectory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(lockDirectory))
            Directory.CreateDirectory(lockDirectory);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
    }

    private async Task CacheRepositoryIndexAsync(
        string repoUrl,
        string yaml,
        string? repositoryName,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(repositoryName))
        {
            names.Add(repositoryName);
        }
        else
        {
            var normalizedUrl = NormalizeRepositoryUrl(repoUrl);
            var repositories = await LoadRepositoriesAsync(_repositoryConfigPath, cancellationToken);
            names.AddRange(repositories.Values
                .Where(repository => string.Equals(
                    NormalizeRepositoryUrl(repository.Url),
                    normalizedUrl,
                    StringComparison.OrdinalIgnoreCase))
                .Select(repository => repository.Name));
        }

        if (names.Count == 0)
            names.Add("repository");

        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var cachePath = Path.Combine(_cacheDir, GetRepositoryIndexCacheFileName(name));
            await WriteAllTextAtomicallyAsync(cachePath, yaml, cancellationToken);
        }
    }

    internal static async Task WriteAllTextAtomicallyAsync(
        string path,
        string contents,
        CancellationToken cancellationToken,
        bool ownerOnlyWhenCreating = false)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        UnixFileMode? unixMode = null;
        if (!OperatingSystem.IsWindows())
        {
            unixMode = File.Exists(path)
                ? File.GetUnixFileMode(path)
                : ownerOnlyWhenCreating
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                    : null;
        }

        var tempPath = Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? "." : directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows() && unixMode is { } mode)
                streamOptions.UnixCreateMode = mode;

            await using (var stream = new FileStream(tempPath, streamOptions))
            {
                var bytes = Encoding.UTF8.GetBytes(contents);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    internal static string GetRepositoryIndexCacheFileName(string repositoryName)
        => GetRepositoryIndexCacheFileName(
            repositoryName,
            caseInsensitiveFileSystem: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    internal static string GetRepositoryIndexCacheFileName(
        string repositoryName,
        bool caseInsensitiveFileSystem)
    {
        var safeName = string.Concat(repositoryName.Select(character =>
            IsPortableFileNameCharacter(character) ? character : '-')).TrimEnd(' ', '.');
        if (string.IsNullOrEmpty(safeName))
            safeName = "repository";

        // Helm repository names are case-sensitive, while the default Windows and macOS
        // file systems are not. Always disambiguate uppercase identities on those platforms
        // so the filename remains derivable from the name alone and stays stable when a
        // case-distinct peer is later added or removed.
        var identitySuffix = (!string.Equals(safeName, repositoryName, StringComparison.Ordinal)
                              || caseInsensitiveFileSystem && repositoryName.Any(char.IsUpper))
            ? $"-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryName)))[..12].ToLowerInvariant()}"
            : string.Empty;
        return $"{safeName}{identitySuffix}-index.yaml";
    }

    private static bool IsPortableFileNameCharacter(char character)
        => character >= ' '
           && character is not '<' and not '>' and not ':' and not '"'
           && character is not '/' and not '\\' and not '|' and not '?' and not '*';

    internal static string GetRepositoryConfigLockPath(string repositoryConfigPath)
    {
        var lockPath = Path.ChangeExtension(repositoryConfigPath, ".lock");
        return string.IsNullOrWhiteSpace(lockPath) ? "repositories.lock" : lockPath;
    }

    private static Dictionary<string, object?> ToRepositoryConfiguration(HelmRepository repository)
    {
        var entry = new Dictionary<string, object?>
        {
            ["name"] = repository.Name,
            ["url"] = repository.Url,
            ["username"] = repository.Username,
            ["password"] = repository.Password,
            ["certFile"] = repository.CertFile,
            ["keyFile"] = repository.KeyFile,
            ["caFile"] = repository.CaFile,
            ["insecure_skip_tls_verify"] = repository.InsecureSkipTlsVerify,
            ["pass_credentials_all"] = repository.PassCredentialsAll
        };
        return entry.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static bool GetBoolean(IDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) && value is not null && Convert.ToBoolean(value);

    private static bool RepositoryConfigurationsMatch(HelmRepository left, HelmRepository right)
        => string.Equals(
               NormalizeRepositoryUrl(left.Url),
               NormalizeRepositoryUrl(right.Url),
               StringComparison.Ordinal)
           && string.Equals(left.Username, right.Username, StringComparison.Ordinal)
           && string.Equals(left.Password, right.Password, StringComparison.Ordinal)
           && string.Equals(left.CertFile, right.CertFile, StringComparison.Ordinal)
           && string.Equals(left.KeyFile, right.KeyFile, StringComparison.Ordinal)
           && string.Equals(left.CaFile, right.CaFile, StringComparison.Ordinal)
           && left.InsecureSkipTlsVerify == right.InsecureSkipTlsVerify
           && left.PassCredentialsAll == right.PassCredentialsAll;

    private static X509Certificate2 LoadClientCertificate(string certFile, string? keyFile)
    {
        if (!string.IsNullOrWhiteSpace(keyFile))
            return X509Certificate2.CreateFromPemFile(certFile, keyFile);
#pragma warning disable SYSLIB0057 // X509CertificateLoader is not available on all target frameworks.
        return File.ReadAllText(certFile).Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPemFile(certFile)
            : new X509Certificate2(certFile);
#pragma warning restore SYSLIB0057
    }

    private static X509Certificate2Collection LoadCertificates(string certFile)
    {
        var certificates = new X509Certificate2Collection();
        var content = File.ReadAllText(certFile);
        if (content.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            certificates.ImportFromPem(content);
        else
#pragma warning disable SYSLIB0057 // X509CertificateLoader is not available on all target frameworks.
            certificates.Import(certFile);
#pragma warning restore SYSLIB0057
        return certificates;
    }

    private static void ValidateRepositoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/', StringComparison.Ordinal))
            throw new ArgumentException("Repository names must not be empty or contain '/'.", nameof(name));
    }

    private static string NormalizeRepositoryUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Repository URL must be an absolute HTTP or HTTPS URL.", nameof(url));

        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static string ResolveCacheDirectory(HelmRepositoryOptions options)
        => ResolveCacheDirectory(
            options,
            Environment.GetEnvironmentVariable("HELM_REPOSITORY_CACHE"),
            Environment.GetEnvironmentVariable("HELM_CACHE_HOME"),
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME"),
            OperatingSystem.IsMacOS(),
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetTempPath());

    internal static string ResolveCacheDirectory(
        HelmRepositoryOptions options,
        string? environmentRepositoryCache,
        string? helmCacheHome,
        string? xdgCacheHome,
        bool isMacOS,
        bool isWindows,
        string userProfile,
        string temporaryDirectory)
    {
        var configuredDirectory = options.CacheDirectory
            ?? environmentRepositoryCache;
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
            return configuredDirectory;

        return ResolveHelmCacheDirectory(
            helmCacheHome,
            xdgCacheHome,
            isMacOS,
            isWindows,
            userProfile,
            temporaryDirectory);
    }

    internal static string GetRepositoryCacheDirectory(string helmCacheHome)
        => Path.Combine(helmCacheHome, "repository");

    internal static string ResolveHelmCacheDirectory(
        string? helmCacheHome,
        string? xdgCacheHome,
        bool isMacOS,
        bool isWindows,
        string userProfile,
        string temporaryDirectory)
    {
        if (!string.IsNullOrWhiteSpace(helmCacheHome))
            return GetRepositoryCacheDirectory(helmCacheHome);
        if (!string.IsNullOrWhiteSpace(xdgCacheHome))
            return Path.Combine(xdgCacheHome, "helm", "repository");
        if (isMacOS)
            return Path.Combine(userProfile, "Library", "Caches", "helm", "repository");
        if (isWindows)
            return Path.Combine(temporaryDirectory, "helm", "repository");
        return Path.Combine(userProfile, ".cache", "helm", "repository");
    }

    private static string ResolveRepositoryConfigPath(HelmRepositoryOptions options)
        => ResolveRepositoryConfigPath(options, Environment.GetEnvironmentVariable("HELM_REPOSITORY_CONFIG"));

    internal static string ResolveRepositoryConfigPath(HelmRepositoryOptions options, string? environmentRepositoryConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(options.RepositoryConfigPath))
            return options.RepositoryConfigPath;
        if (!string.IsNullOrWhiteSpace(options.ConfigDirectory))
            return Path.Combine(options.ConfigDirectory, "repositories.yaml");
        if (!string.IsNullOrWhiteSpace(environmentRepositoryConfigPath))
            return environmentRepositoryConfigPath;

        var configDirectory = ResolveHelmConfigDirectory(
            Environment.GetEnvironmentVariable("HELM_CONFIG_HOME"),
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            OperatingSystem.IsMacOS(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        return Path.Combine(configDirectory, "repositories.yaml");
    }

    internal static string ResolveHelmConfigDirectory(
        string? helmConfigHome,
        string? xdgConfigHome,
        bool isMacOS,
        string userProfile,
        string applicationData)
    {
        if (!string.IsNullOrWhiteSpace(helmConfigHome))
            return helmConfigHome;
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            return Path.Combine(xdgConfigHome, "helm");
        if (isMacOS && !string.IsNullOrWhiteSpace(userProfile))
            return Path.Combine(userProfile, "Library", "Preferences", "helm");
        return Path.Combine(applicationData, "helm");
    }

    /// <summary>
    /// Pushes a chart to an OCI registry.
    /// </summary>
    public async Task<string> PushToOciAsync(
        string chartTgzPath,
        string registry,
        string repository,
        string tag,
        string? username = null,
        string? password = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(chartTgzPath))
            throw new FileNotFoundException($"Chart archive not found: {chartTgzPath}");

        var chartBytes = await File.ReadAllBytesAsync(chartTgzPath, ct);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(chartBytes)).ToLowerInvariant();

        // Step 1: Upload blob
        var blobUrl = $"https://{registry}/v2/{repository}/blobs/uploads/";
        var initRequest = new HttpRequestMessage(HttpMethod.Post, blobUrl);
        if (username is not null && password is not null)
            initRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

        var initResponse = await _httpClient.SendAsync(initRequest, ct);
        initResponse.EnsureSuccessStatusCode();

        var uploadUrl = initResponse.Headers.Location?.ToString();
        if (uploadUrl is null)
            throw new InvalidOperationException("OCI registry did not return upload URL");

        if (!uploadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            uploadUrl = $"https://{registry}{uploadUrl}";

        // Upload the blob
        var putRequest = new HttpRequestMessage(HttpMethod.Put, $"{uploadUrl}&digest=sha256:{digest}");
        putRequest.Content = new ByteArrayContent(chartBytes);
        putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (username is not null && password is not null)
            putRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

        var putResponse = await _httpClient.SendAsync(putRequest, ct);
        putResponse.EnsureSuccessStatusCode();

        // Step 2: Upload config (empty OCI config)
        var configBytes = Encoding.UTF8.GetBytes("{}");
        var configDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(configBytes)).ToLowerInvariant();

        var configBlobUrl = $"https://{registry}/v2/{repository}/blobs/uploads/";
        var configInit = new HttpRequestMessage(HttpMethod.Post, configBlobUrl);
        if (username is not null && password is not null)
            configInit.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

        var configInitResp = await _httpClient.SendAsync(configInit, ct);
        configInitResp.EnsureSuccessStatusCode();

        var configUploadUrl = configInitResp.Headers.Location?.ToString() ?? throw new InvalidOperationException("No config upload URL");
        if (!configUploadUrl.StartsWith("http"))
            configUploadUrl = $"https://{registry}{configUploadUrl}";

        var configPut = new HttpRequestMessage(HttpMethod.Put, $"{configUploadUrl}&digest=sha256:{configDigest}");
        configPut.Content = new ByteArrayContent(configBytes);
        configPut.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (username is not null && password is not null)
            configPut.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

        await _httpClient.SendAsync(configPut, ct);

        // Step 3: Upload manifest
        var manifest = new
        {
            schemaVersion = 2,
            mediaType = "application/vnd.oci.image.manifest.v1+json",
            config = new
            {
                mediaType = "application/vnd.cncf.helm.config.v1+json",
                digest = $"sha256:{configDigest}",
                size = configBytes.Length
            },
            layers = new[]
            {
                new
                {
                    mediaType = "application/vnd.cncf.helm.chart.content.v1.tar+gzip",
                    digest = $"sha256:{digest}",
                    size = chartBytes.Length
                }
            },
            annotations = new Dictionary<string, string>
            {
                ["org.opencontainers.image.title"] = $"{repository}:{tag}",
                ["org.opencontainers.image.description"] = "Helm chart"
            }
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);

        var manifestRequest = new HttpRequestMessage(HttpMethod.Put, $"https://{registry}/v2/{repository}/manifests/{tag}");
        manifestRequest.Content = new ByteArrayContent(manifestBytes);
        manifestRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.oci.image.manifest.v1+json");
        if (username is not null && password is not null)
            manifestRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

        var manifestResponse = await _httpClient.SendAsync(manifestRequest, ct);
        manifestResponse.EnsureSuccessStatusCode();

        return $"oci://{registry}/{repository}:{tag}";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public class HelmRepository
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? CertFile { get; set; }
    public string? KeyFile { get; set; }
    public string? CaFile { get; set; }
    public bool InsecureSkipTlsVerify { get; set; }
    public bool PassCredentialsAll { get; set; }
}

public class HelmRepoIndex
{
    public string ApiVersion { get; set; } = "v1";
    public string Generated { get; set; } = string.Empty;
    public Dictionary<string, List<HelmChartVersion>> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class HelmChartVersion
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? AppVersion { get; set; }
    public string? Digest { get; set; }
    public string? Created { get; set; }
    public List<string> Urls { get; set; } = new();
}

public class HelmChartSearchResult
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? AppVersion { get; set; }
}

internal sealed record HelmRepositoryUpdateResult(string Name, bool Succeeded, string? Error);
