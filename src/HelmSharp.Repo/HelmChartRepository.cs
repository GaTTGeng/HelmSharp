using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelmSharp.Chart;

namespace HelmSharp.Repo;

/// <summary>
/// Chart repository client for downloading charts from HTTP repositories and OCI registries.
/// </summary>
public sealed class HelmChartRepository : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheDir;

    public HelmChartRepository(string? cacheDir = null)
    {
        _cacheDir = cacheDir ?? Path.Combine(Path.GetTempPath(), "helmsharp", "cache");
        Directory.CreateDirectory(_cacheDir);

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("helmsharp", "0.3.0"));
    }

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
        var repoFile = Path.Combine(_cacheDir, "repositories.json");
        var repos = await LoadRepositoriesAsync(repoFile, cancellationToken);
        repos[name] = new HelmRepository
        {
            Name = name,
            Url = url,
            Username = username,
            Password = password
        };
        await SaveRepositoriesAsync(repoFile, repos, cancellationToken);
    }

    /// <summary>
    /// Removes a chart repository.
    /// </summary>
    public async Task RemoveRepositoryAsync(string name, CancellationToken cancellationToken = default)
    {
        var repoFile = Path.Combine(_cacheDir, "repositories.json");
        var repos = await LoadRepositoriesAsync(repoFile, cancellationToken);
        repos.Remove(name);
        await SaveRepositoriesAsync(repoFile, repos, cancellationToken);
    }

    /// <summary>
    /// Lists configured repositories.
    /// </summary>
    public async Task<List<HelmRepository>> ListRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        var repoFile = Path.Combine(_cacheDir, "repositories.json");
        var repos = await LoadRepositoriesAsync(repoFile, cancellationToken);
        return repos.Values.ToList();
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
                var latest = entry.Value.OrderByDescending(v => v.Version).FirstOrDefault();
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
    public async Task<HelmRepoIndex> FetchRepoIndexAsync(
        string repoUrl,
        string? username = null,
        string? password = null,
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
        return ParseRepoIndex(yaml);
    }

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
            var index = await FetchRepoIndexAsync(repoUrl, cancellationToken: cancellationToken);

            if (!index.Entries.TryGetValue(chartName, out var versions))
                throw new InvalidOperationException($"Chart '{chartName}' not found in repository");

            var entry = version is not null
                ? versions.FirstOrDefault(v => v.Version == version)
                : versions.OrderByDescending(v => v.Version).FirstOrDefault();

            if (entry is null)
                throw new InvalidOperationException($"Chart '{chartName}' version '{version}' not found");

            url = entry.Urls.FirstOrDefault()
                  ?? throw new InvalidOperationException($"No download URL for chart '{chartName}' version '{entry.Version}'");

            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = repoUrl.TrimEnd('/') + "/" + url;
        }

        return await DownloadAndExtractChartAsync(url, cancellationToken);
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

    private static HelmRepoIndex ParseRepoIndex(string yaml)
    {
        var dict = HelmYaml.DeserializeDictionary(yaml);
        var index = new HelmRepoIndex
        {
            ApiVersion = HelmYaml.GetString(dict, "apiVersion") ?? "v1",
            Generated = HelmYaml.GetString(dict, "generated") ?? string.Empty
        };

        if (dict.TryGetValue("entries", out var entriesObj) &&
            entriesObj is IDictionary<string, object?> entries)
        {
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
        }

        return index;
    }

    private async Task<Dictionary<string, HelmRepository>> LoadRepositoriesAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new Dictionary<string, HelmRepository>(StringComparer.OrdinalIgnoreCase);

        var json = await File.ReadAllTextAsync(path, ct);
        var repos = JsonSerializer.Deserialize<Dictionary<string, HelmRepository>>(json);
        return repos ?? new Dictionary<string, HelmRepository>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task SaveRepositoriesAsync(string path, Dictionary<string, HelmRepository> repos, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(repos, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct);
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
