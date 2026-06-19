using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace HelmSharp.Chart;

public sealed class HelmChart
{
    /// <summary>
    /// Gets the chart metadata API version from Chart.yaml.
    /// </summary>
    public string ApiVersion { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? AppVersion { get; init; }
    public string? Description { get; init; }
    public string? Home { get; init; }
    public List<object?>? Sources { get; set; }
    public List<object?>? Keywords { get; set; }
    public List<object?>? Maintainers { get; set; }
    public string? Type { get; init; }
    public bool Deprecated { get; init; }
    public string? KubeVersion { get; init; }
    public Dictionary<string, object?>? Annotations { get; set; }
    public List<HelmChartDependency> Dependencies { get; } = new();
    public List<HelmChartLockEntry> LockEntries { get; } = new();
    public string ValuesYaml { get; init; } = string.Empty;
    public Dictionary<string, string> Templates { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public List<Dictionary<string, object?>> Crds { get; } = new();
    public Dictionary<string, HelmChart> Subcharts { get; } = new(StringComparer.Ordinal);
}

public static class HelmChartLoader
{
    public static async Task<HelmChart> LoadAsync(string chartPath, CancellationToken cancellationToken)
    {
        var isDirectory = Directory.Exists(chartPath);
        var files = isDirectory
            ? await LoadDirectoryAsync(chartPath, cancellationToken)
            : await LoadArchiveAsync(chartPath, cancellationToken);

        return await LoadFromFilesAsync(
            chartPath,
            files,
            isDirectory ? chartPath : null,
            cancellationToken);
    }

    private static async Task<HelmChart> LoadFromFilesAsync(
        string chartPath,
        Dictionary<string, string> files,
        string? chartDir,
        CancellationToken cancellationToken)
    {
        var chartYaml = files.TryGetValue("Chart.yaml", out var exact)
            ? exact
            : files.FirstOrDefault(x => x.Key.EndsWith("/Chart.yaml", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(chartYaml))
            throw new InvalidOperationException($"Chart.yaml was not found in chart {chartPath}.");

        var metadata = HelmYaml.DeserializeDictionary(chartYaml);
        var chart = new HelmChart
        {
            ApiVersion = HelmYaml.GetString(metadata, "apiVersion") ?? string.Empty,
            Name = HelmYaml.GetString(metadata, "name") ?? Path.GetFileNameWithoutExtension(chartPath),
            Version = HelmYaml.GetString(metadata, "version") ?? string.Empty,
            AppVersion = HelmYaml.GetString(metadata, "appVersion"),
            Description = HelmYaml.GetString(metadata, "description"),
            Home = HelmYaml.GetString(metadata, "home"),
            Type = HelmYaml.GetString(metadata, "type"),
            Deprecated = string.Equals(HelmYaml.GetString(metadata, "deprecated"), "true", StringComparison.OrdinalIgnoreCase),
            KubeVersion = HelmYaml.GetString(metadata, "kubeVersion"),
            ValuesYaml = FindFile(files, "values.yaml") ?? string.Empty
        };

        // Load Chart.lock if present
        var lockContent = FindFile(files, "Chart.lock");
        if (lockContent is not null)
        {
            var lockDict = HelmYaml.DeserializeDictionary(lockContent);
            if (lockDict.TryGetValue("dependencies", out var lockDeps) && lockDeps is IList<object?> lockDepsList)
            {
                foreach (var lockDep in lockDepsList)
                {
                    if (lockDep is not IDictionary<string, object?> lockDepDict) continue;
                    chart.LockEntries.Add(new HelmChartLockEntry
                    {
                        Name = HelmYaml.GetString(lockDepDict, "name") ?? string.Empty,
                        Version = HelmYaml.GetString(lockDepDict, "version") ?? string.Empty,
                        Repository = HelmYaml.GetString(lockDepDict, "repository"),
                        Digest = HelmYaml.GetString(lockDepDict, "digest"),
                    });
                }
            }
        }

        if (metadata.TryGetValue("sources", out var sourcesObj) && sourcesObj is IList<object?> sourcesList)
            chart.Sources = sourcesList.ToList();
        if (metadata.TryGetValue("keywords", out var kwObj) && kwObj is IList<object?> kwList)
            chart.Keywords = kwList.ToList();
        if (metadata.TryGetValue("maintainers", out var maintObj) && maintObj is IList<object?> maintList)
            chart.Maintainers = maintList.ToList();
        if (metadata.TryGetValue("annotations", out var annObj) && annObj is IDictionary<string, object?> annDict)
        {
            chart.Annotations = annDict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        }

        if (metadata.TryGetValue("dependencies", out var depsObj) && depsObj is IList<object?> depsList)
        {
            foreach (var dep in depsList)
            {
                if (dep is not IDictionary<string, object?> depDict) continue;
                var depEntry = new HelmChartDependency
                {
                    Name = HelmYaml.GetString(depDict, "name") ?? string.Empty,
                    Version = HelmYaml.GetString(depDict, "version"),
                    Repository = HelmYaml.GetString(depDict, "repository"),
                    Condition = HelmYaml.GetString(depDict, "condition"),
                };
                if (depDict.TryGetValue("tags", out var tagsObj) && tagsObj is IList<object?> tagsList)
                    depEntry.Tags = tagsList.Select(t => Convert.ToString(t) ?? string.Empty).ToList();
                if (depDict.TryGetValue("enabled", out var enabledObj))
                    depEntry.Enabled = enabledObj switch
                    {
                        bool b => b,
                        string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
                        _ => !string.Equals(Convert.ToString(enabledObj), "false", StringComparison.OrdinalIgnoreCase)
                    };
                chart.Dependencies.Add(depEntry);
            }
        }

        foreach (var (path, content) in files.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var normalized = NormalizePath(path);

            // Skip subchart files (they're loaded separately via Subcharts)
            if (normalized.StartsWith("charts/", StringComparison.Ordinal))
                continue;

            // Templates
            if (normalized.Contains("/templates/", StringComparison.Ordinal) ||
                normalized.StartsWith("templates/", StringComparison.Ordinal))
            {
                if (!normalized.EndsWith("/", StringComparison.Ordinal))
                    chart.Templates[normalized] = content;
                continue;
            }

            // CRDs
            if (normalized.Contains("/crds/", StringComparison.Ordinal) ||
                normalized.StartsWith("crds/", StringComparison.OrdinalIgnoreCase))
            {
                if (normalized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                {
                    var crdDict = HelmYaml.DeserializeDictionary(content);
                    if (crdDict.Count > 0) chart.Crds.Add(crdDict);
                }
                continue;
            }

            // Static files (for .Files access)
            if (!normalized.Equals("Chart.yaml", StringComparison.OrdinalIgnoreCase) &&
                !normalized.Equals("values.yaml", StringComparison.OrdinalIgnoreCase) &&
                !normalized.EndsWith("/", StringComparison.Ordinal))
            {
                chart.Files[normalized] = System.Text.Encoding.UTF8.GetBytes(content);
            }
        }

        // Load subcharts from charts/ directory
        if (chartDir is not null)
        {
            var chartsDir = Path.Combine(chartDir, "charts");
            if (Directory.Exists(chartsDir))
            {
                foreach (var subchartDir in Directory.GetDirectories(chartsDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var subchartName = Path.GetFileName(subchartDir);
                    try
                    {
                        var subchart = await LoadAsync(subchartDir, cancellationToken);
                        chart.Subcharts[subchartName] = subchart;
                    }
                    catch
                    {
                        // Skip invalid subcharts
                    }
                }
            }
        }
        else
        {
            // For archives, extract subcharts from the files dictionary
            var subchartGroups = files.Keys
                .Where(k => k.StartsWith("charts/", StringComparison.Ordinal))
                .Select(k => { var rest = k["charts/".Length..]; var slash = rest.IndexOf('/'); return slash > 0 ? rest[..slash] : null; })
                .Where(n => n is not null)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var subchartName in subchartGroups)
            {
                if (subchartName is null) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var prefix = $"charts/{subchartName}/";
                var subchartFiles = files
                    .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(
                        kv => kv.Key[prefix.Length..],
                        kv => kv.Value,
                        StringComparer.OrdinalIgnoreCase);

                if (!subchartFiles.ContainsKey("Chart.yaml"))
                    continue;

                try
                {
                    var subchart = await LoadFromFilesAsync(
                        $"{chartPath}!{prefix.TrimEnd('/')}",
                        subchartFiles,
                        null,
                        cancellationToken);
                    chart.Subcharts[subchartName] = subchart;
                }
                catch
                {
                    // Skip invalid subcharts
                }
            }
        }

        return chart;
    }

    private static async Task<Dictionary<string, string>> LoadDirectoryAsync(string chartPath, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(chartPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizePath(Path.GetRelativePath(chartPath, file));
            files[relative] = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken);
        }

        return files;
    }

    private static async Task<Dictionary<string, string>> LoadArchiveAsync(string chartPath, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var file = File.OpenRead(chartPath);
        await using Stream archive = chartPath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
                                     chartPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;

        using var reader = new TarReader(archive);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.Directory || entry.DataStream is null)
                continue;

            using var memory = new MemoryStream();
            await entry.DataStream.CopyToAsync(memory, cancellationToken);
            var text = Encoding.UTF8.GetString(memory.ToArray());
            files[StripChartRoot(NormalizePath(entry.Name))] = text;
        }

        return files;
    }

    private static string? FindFile(Dictionary<string, string> files, string fileName)
    {
        if (files.TryGetValue(fileName, out var exact))
            return exact;

        return files.FirstOrDefault(x =>
            x.Key.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string StripChartRoot(string path)
    {
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
            return path;

        var first = path[..slash];
        return first.Equals("templates", StringComparison.OrdinalIgnoreCase)
            ? path
            : path[(slash + 1)..];
    }

    internal static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
