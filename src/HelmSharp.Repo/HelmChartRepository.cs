using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
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
    {
        ArgumentNullException.ThrowIfNull(options);

        _cacheDir = ResolveCacheDirectory(options);
        _repositoryConfigPath = ResolveRepositoryConfigPath(options);
        Directory.CreateDirectory(_cacheDir);
        var configDirectory = Path.GetDirectoryName(_repositoryConfigPath);
        if (!string.IsNullOrWhiteSpace(configDirectory))
            Directory.CreateDirectory(configDirectory);

        _httpClient = new HttpClient();
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
        // Local path
        if (Directory.Exists(chartRef) || File.Exists(chartRef))
            return chartRef;

        // OCI reference
        if (chartRef.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
            return await PullFromOciAsync(chartRef, version, cancellationToken);

        // HTTP/HTTPS URL or repo/name reference
        if (chartRef.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            chartRef.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return await PullFromHttpAsync(chartRef, version, cancellationToken);

        // Assume it's a local path that doesn't exist yet
        return chartRef;
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
        return repos.Values.OrderBy(repo => repo.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
        var results = new List<HelmChartSearchResult>();

        foreach (var entry in index.Entries)
        {
            if (entry.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                var latest = HelmChartVersionResolver.Resolve(entry.Value, constraint: null);
                if (latest is not null)
                {
                    results.Add(new HelmChartSearchResult
                    {
                        Name = entry.Key,
                        Version = latest.Version,
                        Description = latest.Description,
                        AppVersion = latest.AppVersion
                    });
                }
            }
        }

        return results;
    }

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
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            PreAuthenticate = repository.PassCredentialsAll
        };
        if (repository.InsecureSkipTlsVerify)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        else if (!string.IsNullOrWhiteSpace(repository.CaFile))
        {
            var trustedRoots = LoadCertificates(repository.CaFile);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, sslPolicyErrors) =>
            {
                if (certificate is null)
                    return false;
                if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                    return false;
                if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
                    return false;

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                foreach (var trustedRoot in trustedRoots)
                {
                    chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
                }

                using var serverCertificate = new X509Certificate2(certificate);
                return chain.Build(serverCertificate);
            };
        }
        if (!string.IsNullOrWhiteSpace(repository.CertFile))
            handler.ClientCertificates.Add(LoadClientCertificate(repository.CertFile, repository.KeyFile));

        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("helmsharp", ProductVersion));
        using var response = await SendRepositoryIndexRequestAsync(client, repository, cancellationToken);
        response.EnsureSuccessStatusCode();
        var yaml = await response.Content.ReadAsStringAsync(cancellationToken);
        var index = ParseRepoIndex(yaml);
        await CacheRepositoryIndexAsync(repository.Url, yaml, repository.Name, cancellationToken);
        return index;
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

    private async Task<string> PullFromHttpAsync(
        string chartUrl,
        string? version,
        CancellationToken cancellationToken)
    {
        var url = chartUrl;
        if (!url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) &&
            !url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            // Treat as repo URL, resolve chart from index
            if (!Uri.TryCreate(url, UriKind.Absolute, out var chartUri))
                throw new ArgumentException($"Invalid chart reference: {url}");

            var segments = chartUri.Segments;
            var chartName = Uri.UnescapeDataString(segments[^1].Trim('/'));
            if (segments.Length < 2 || string.IsNullOrWhiteSpace(chartName))
                throw new ArgumentException($"Invalid chart reference: {url}");

            var repoUri = new UriBuilder(chartUri)
            {
                Path = string.Concat(segments.Take(segments.Length - 1)).TrimEnd('/'),
                Query = null
            };
            var repoUrl = repoUri.Uri.ToString().TrimEnd('/');
            var entry = await ResolveChartVersionAsync(repoUrl, chartName, version, cancellationToken: cancellationToken);

            url = entry.Urls.FirstOrDefault()
                  ?? throw new InvalidOperationException($"No download URL for chart '{chartName}' version '{entry.Version}'");

            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = repoUrl.TrimEnd('/') + "/" + url;
        }

        return await DownloadAndExtractChartAsync(url, cancellationToken);
    }

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
        return await DownloadAndExtractChartAsync(blobUrl, cancellationToken);
    }

    private async Task<string> DownloadAndExtractChartAsync(string url, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var chartBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

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
        await WriteAllTextAtomicallyAsync(path, HelmYaml.Serialize(document), ct);
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
        var name = repositoryName;
        if (string.IsNullOrWhiteSpace(name))
        {
            var repositories = await LoadRepositoriesAsync(_repositoryConfigPath, cancellationToken);
            name = repositories.Values.FirstOrDefault(repository =>
                string.Equals(repository.Url, NormalizeRepositoryUrl(repoUrl), StringComparison.OrdinalIgnoreCase))?.Name
                ?? "repository";
        }
        var cachePath = Path.Combine(_cacheDir, GetRepositoryIndexCacheFileName(name));
        await WriteAllTextAtomicallyAsync(cachePath, yaml, cancellationToken);
    }

    internal static async Task WriteAllTextAtomicallyAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? "." : directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);
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
    {
        var safeName = repositoryName
            .Replace(Path.DirectorySeparatorChar, '-')
            .Replace(Path.AltDirectorySeparatorChar, '-')
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(safeName))
            safeName = "repository";
        return $"{safeName}-index.yaml";
    }

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
    {
        var configuredDirectory = options.CacheDirectory
            ?? Environment.GetEnvironmentVariable("HELM_REPOSITORY_CACHE");
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
            return configuredDirectory;

        var helmCacheHome = Environment.GetEnvironmentVariable("HELM_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(helmCacheHome))
            return GetRepositoryCacheDirectory(helmCacheHome);

        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return !string.IsNullOrWhiteSpace(xdgCache)
            ? Path.Combine(xdgCache, "helm", "repository")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "helm", "repository");
    }

    internal static string GetRepositoryCacheDirectory(string helmCacheHome)
        => Path.Combine(helmCacheHome, "repository");

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
