using HelmSharp.Chart;
using YamlDotNet.Core;

namespace HelmSharp.Repo;

/// <summary>
/// Repository index generator — matches `helm repo index`.
/// Generates index.yaml from a directory of chart packages.
/// </summary>
public static class HelmRepoIndexer
{
    /// <summary>
    /// Generates an index.yaml for a directory containing .tgz chart packages.
    /// </summary>
    public static Task<string> GenerateIndexAsync(
        string dirPath,
        string? url = null,
        CancellationToken ct = default)
        => GenerateIndexAsync(dirPath, url, ct, mergeIndexPath: null);

    /// <summary>
    /// Generates an index.yaml for a directory containing .tgz chart packages, optionally merging an existing index.
    /// </summary>
    public static async Task<string> GenerateIndexAsync(
        string dirPath,
        string? url,
        CancellationToken ct,
        string? mergeIndexPath)
    {
        var result = await GenerateIndexWithDiagnosticsAsync(dirPath, url, ct, mergeIndexPath);
        return result.IndexPath;
    }

    /// <summary>
    /// Generates an index.yaml and returns diagnostics for packages that could not be indexed.
    /// </summary>
    public static Task<HelmRepoIndexGenerationResult> GenerateIndexWithDiagnosticsAsync(
        string dirPath,
        string? url = null,
        CancellationToken ct = default)
        => GenerateIndexWithDiagnosticsAsync(dirPath, url, ct, mergeIndexPath: null);

    /// <summary>
    /// Generates an index.yaml and returns diagnostics for packages that could not be indexed, optionally merging an existing index.
    /// </summary>
    public static async Task<HelmRepoIndexGenerationResult> GenerateIndexWithDiagnosticsAsync(
        string dirPath,
        string? url,
        CancellationToken ct,
        string? mergeIndexPath)
    {
        if (!Directory.Exists(dirPath))
            throw new DirectoryNotFoundException($"Directory not found: {dirPath}");

        var entries = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<HelmRepoIndexDiagnostic>();

        foreach (var tgzFile in Directory.GetFiles(dirPath, "*.tgz"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var chart = await HelmChartLoader.LoadAsync(tgzFile, ct);
                var fileInfo = new FileInfo(tgzFile);
                var digest = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(tgzFile, ct)))
                    .ToLowerInvariant();

                var entry = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["apiVersion"] = string.IsNullOrWhiteSpace(chart.ApiVersion) ? "v2" : chart.ApiVersion,
                    ["name"] = chart.Name,
                    ["version"] = chart.Version,
                    ["digest"] = digest,
                    ["urls"] = url is not null
                        ? new List<object?> { $"{url.TrimEnd('/')}/{Path.GetFileName(tgzFile)}" }
                        : new List<object?> { Path.GetFileName(tgzFile) },
                    ["created"] = fileInfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
                };
                AddIfNotNull(entry, "appVersion", chart.AppVersion);
                AddIfNotNull(entry, "description", chart.Description);
                AddIfNotNull(entry, "type", chart.Type);
                AddIfNotNull(entry, "home", chart.Home);
                AddIfNotNull(entry, "icon", chart.Icon);
                AddIfNotNull(entry, "sources", chart.Sources);
                AddIfNotNull(entry, "keywords", chart.Keywords);
                AddIfNotNull(entry, "maintainers", chart.Maintainers);
                AddIfNotNull(entry, "kubeVersion", chart.KubeVersion);
                AddIfNotNull(entry, "annotations", chart.Annotations);
                if (chart.Dependencies.Count > 0)
                    entry["dependencies"] = chart.Dependencies.Select(CreateDependencyIndexEntry).ToList();
                if (chart.Deprecated)
                    entry["deprecated"] = true;

                if (!entries.ContainsKey(chart.Name))
                    entries[chart.Name] = new List<Dictionary<string, object?>>();
                entries[chart.Name].Add(entry);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or YamlException)
            {
                diagnostics.Add(new HelmRepoIndexDiagnostic(tgzFile, ex.Message, ex));
            }
        }

        MergeExistingEntries(entries, mergeIndexPath);

        foreach (var (_, versions) in entries)
            versions.Sort(CompareChartVersionsDescending);

        var index = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["apiVersion"] = "v1",
            ["entries"] = entries,
            ["generated"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")
        };

        var yaml = HelmYaml.Serialize(index);
        var indexPath = Path.Combine(dirPath, "index.yaml");
        await File.WriteAllTextAsync(indexPath, yaml, ct);
        return new HelmRepoIndexGenerationResult(indexPath, diagnostics);
    }

    private static void MergeExistingEntries(
        Dictionary<string, List<Dictionary<string, object?>>> entries,
        string? mergeIndexPath)
    {
        if (string.IsNullOrWhiteSpace(mergeIndexPath) || !File.Exists(mergeIndexPath))
            return;

        var existingIndex = HelmYaml.DeserializeDictionary(File.ReadAllText(mergeIndexPath));
        if (!string.Equals(HelmYaml.GetString(existingIndex, "apiVersion"), "v1", StringComparison.Ordinal)
            || !existingIndex.TryGetValue("entries", out var existingEntriesObject)
            || existingEntriesObject is not IDictionary<string, object?> existingEntries)
            throw new InvalidDataException($"Merge index is not a valid Helm repository index: {mergeIndexPath}");

        foreach (var (chartName, existingVersionsObject) in existingEntries)
        {
            if (existingVersionsObject is not IEnumerable<object?> existingVersions)
                throw new InvalidDataException($"Merge index contains invalid entries for chart '{chartName}': {mergeIndexPath}");

            var parsedVersions = new List<Dictionary<string, object?>>();
            foreach (var version in existingVersions)
            {
                if (version is not IDictionary<string, object?> entry)
                    throw new InvalidDataException($"Merge index contains an invalid chart version for '{chartName}': {mergeIndexPath}");

                parsedVersions.Add(new Dictionary<string, object?>(entry, StringComparer.OrdinalIgnoreCase));
            }

            if (!entries.TryGetValue(chartName, out var generatedVersions))
            {
                entries[chartName] = parsedVersions;
                continue;
            }

            var generatedVersionNames = generatedVersions
                .Select(version => HelmYaml.GetString(version, "version"))
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .ToHashSet(StringComparer.Ordinal);
            generatedVersions.AddRange(parsedVersions.Where(version =>
                !generatedVersionNames.Contains(HelmYaml.GetString(version, "version"))));
        }
    }

    private static void AddIfNotNull(
        Dictionary<string, object?> entry,
        string key,
        object? value)
    {
        if (value is not null)
            entry[key] = value;
    }

    private static Dictionary<string, object?> CreateDependencyIndexEntry(HelmChartDependency dependency)
    {
        var entry = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = dependency.Name
        };

        AddIfNotNull(entry, "version", dependency.Version);
        AddIfNotNull(entry, "repository", dependency.Repository);
        AddIfNotNull(entry, "condition", dependency.Condition);
        AddIfNotNull(entry, "tags", dependency.Tags);
        AddIfNotNull(entry, "import-values", dependency.ImportValues);
        AddIfNotNull(entry, "alias", dependency.Alias);
        return entry;
    }

    private static int CompareChartVersionsDescending(
        Dictionary<string, object?> left,
        Dictionary<string, object?> right)
    {
        var leftVersion = HelmYaml.GetString(left, "version") ?? string.Empty;
        var rightVersion = HelmYaml.GetString(right, "version") ?? string.Empty;
        return HelmChartVersionResolver.CompareVersions(rightVersion, leftVersion);
    }
}

/// <summary>
/// Describes the generated repository index and packages that were skipped.
/// </summary>
/// <param name="IndexPath">The path to the generated index.yaml file.</param>
/// <param name="Diagnostics">Diagnostics for invalid chart packages skipped during indexing.</param>
public sealed record HelmRepoIndexGenerationResult(
    string IndexPath,
    IReadOnlyList<HelmRepoIndexDiagnostic> Diagnostics);

/// <summary>
/// Describes an invalid chart package skipped while generating a repository index.
/// </summary>
/// <param name="PackagePath">The invalid chart package path.</param>
/// <param name="Message">The diagnostic message describing why the package was skipped.</param>
/// <param name="Exception">The underlying exception.</param>
public sealed record HelmRepoIndexDiagnostic(
    string PackagePath,
    string Message,
    Exception Exception);
