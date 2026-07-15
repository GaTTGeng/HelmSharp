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
    public string? Icon { get; init; }
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
            : await LoadArchiveFileAsync(chartPath, cancellationToken);

        return await LoadFromFilesAsync(
            chartPath,
            files,
            isDirectory ? chartPath : null,
            cancellationToken);
    }

    private static async Task<HelmChart> LoadFromFilesAsync(
        string chartPath,
        Dictionary<string, byte[]> files,
        string? chartDir,
        CancellationToken cancellationToken)
    {
        var chartYamlBytes = FindFile(files, "Chart.yaml");
        if (chartYamlBytes is null || chartYamlBytes.Length == 0)
            throw new InvalidOperationException($"Chart.yaml was not found in chart {chartPath}.");

        var chartYaml = DecodeText(chartYamlBytes);
        var metadata = HelmYaml.DeserializeDictionary(chartYaml);
        var chart = new HelmChart
        {
            ApiVersion = HelmYaml.GetString(metadata, "apiVersion") ?? string.Empty,
            Name = HelmYaml.GetString(metadata, "name") ?? Path.GetFileNameWithoutExtension(chartPath),
            Version = HelmYaml.GetString(metadata, "version") ?? string.Empty,
            AppVersion = HelmYaml.GetString(metadata, "appVersion"),
            Description = HelmYaml.GetString(metadata, "description"),
            Home = HelmYaml.GetString(metadata, "home"),
            Icon = HelmYaml.GetString(metadata, "icon"),
            Type = HelmYaml.GetString(metadata, "type"),
            Deprecated = string.Equals(HelmYaml.GetString(metadata, "deprecated"), "true", StringComparison.OrdinalIgnoreCase),
            KubeVersion = HelmYaml.GetString(metadata, "kubeVersion"),
            ValuesYaml = DecodeText(FindFile(files, "values.yaml"))
        };

        // Load Chart.lock if present
        var lockContent = FindFile(files, "Chart.lock");
        if (lockContent is not null)
        {
            var lockDict = HelmYaml.DeserializeDictionary(DecodeText(lockContent));
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
                    Alias = HelmYaml.GetString(depDict, "alias"),
                };
                if (depDict.TryGetValue("tags", out var tagsObj) && tagsObj is IList<object?> tagsList)
                    depEntry.Tags = tagsList.Select(t => Convert.ToString(t) ?? string.Empty).ToList();
                if (depDict.TryGetValue("import-values", out var importsObj) && importsObj is IList<object?> importsList)
                    depEntry.ImportValues = importsList.ToList();
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
                    chart.Templates[normalized] = DecodeText(content);
                continue;
            }

            // CRDs
            if (normalized.Contains("/crds/", StringComparison.Ordinal) ||
                normalized.StartsWith("crds/", StringComparison.OrdinalIgnoreCase))
            {
                if (normalized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                {
                    var crdDict = HelmYaml.DeserializeDictionary(DecodeText(content));
                    if (crdDict.Count > 0) chart.Crds.Add(crdDict);
                }
                continue;
            }

            // Static files (for .Files access)
            if (!normalized.Equals("Chart.yaml", StringComparison.OrdinalIgnoreCase) &&
                !normalized.Equals("values.yaml", StringComparison.OrdinalIgnoreCase) &&
                !normalized.EndsWith("/", StringComparison.Ordinal))
            {
                chart.Files[normalized] = content;
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

                foreach (var dependencyArchive in Directory
                    .EnumerateFiles(chartsDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsDependencyArchiveFile)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dependencyPath = NormalizePath(Path.GetRelativePath(chartDir, dependencyArchive));
                    var archiveBytes = await File.ReadAllBytesAsync(dependencyArchive, cancellationToken);
                    var subchart = await LoadDependencyArchiveAsync(
                        chartPath,
                        dependencyPath,
                        archiveBytes,
                        cancellationToken);
                    AddPackagedDependencyChart(chart, new PackagedDependencyChart(subchart));
                }
            }
        }
        else
        {
            foreach (var (dependencyPath, archiveBytes) in files
                .Where(kv => IsEmbeddedDependencyArchivePath(kv.Key))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subchart = await LoadDependencyArchiveAsync(
                    chartPath,
                    dependencyPath,
                    archiveBytes,
                    cancellationToken);
                AddPackagedDependencyChart(chart, new PackagedDependencyChart(subchart));
            }

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

    private static void AddPackagedDependencyChart(HelmChart chart, PackagedDependencyChart package)
    {
        var matchedDependencies = chart.Dependencies
            .Where(dependency => IsDependencyMatch(chart, dependency, package.Chart))
            .ToList();

        if (matchedDependencies.Count > 0)
        {
            foreach (var dependency in matchedDependencies)
                chart.Subcharts[dependency.Alias ?? dependency.Name] = package.Chart;
            return;
        }

        chart.Subcharts[package.Chart.Name] = package.Chart;
    }

    private static bool IsDependencyMatch(
        HelmChart parent,
        HelmChartDependency dependency,
        HelmChart subchart)
    {
        if (!string.Equals(dependency.Name, subchart.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(dependency.Version) ||
            string.Equals(dependency.Version, subchart.Version, StringComparison.OrdinalIgnoreCase))
            return true;

        var dependencyIndex = parent.Dependencies.IndexOf(dependency);
        if (dependencyIndex >= 0 && dependencyIndex < parent.LockEntries.Count)
        {
            var locked = parent.LockEntries[dependencyIndex];
            return string.Equals(locked.Name, dependency.Name, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(locked.Version, subchart.Version, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static async Task<Dictionary<string, byte[]>> LoadDirectoryAsync(string chartPath, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(chartPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizePath(Path.GetRelativePath(chartPath, file));
            files[relative] = await File.ReadAllBytesAsync(file, cancellationToken);
        }

        return files;
    }

    private static async Task<Dictionary<string, byte[]>> LoadArchiveFileAsync(string chartPath, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(chartPath);
        return await LoadArchiveAsync(file, chartPath, cancellationToken);
    }

    private static async Task<Dictionary<string, byte[]>> LoadArchiveBytesAsync(
        byte[] archiveBytes,
        string chartPath,
        CancellationToken cancellationToken)
    {
        await using var memory = new MemoryStream(archiveBytes, writable: false);
        return await LoadArchiveAsync(memory, chartPath, cancellationToken);
    }

    private static async Task<Dictionary<string, byte[]>> LoadArchiveAsync(
        Stream input,
        string chartPath,
        CancellationToken cancellationToken)
    {
        var archiveFiles = new List<ArchiveFileEntry>();
        await using Stream archive = chartPath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
                                     chartPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(input, CompressionMode.Decompress)
            : input;

        using var reader = new TarReader(archive);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.Directory || entry.DataStream is null)
                continue;

            var entryName = HelmArchivePath.NormalizeEntryName(entry.Name);

            using var memory = new MemoryStream();
            await entry.DataStream.CopyToAsync(memory, cancellationToken);
            archiveFiles.Add(new ArchiveFileEntry(entryName, memory.ToArray()));
        }

        var chartRoot = HelmArchivePath.FindChartRoot(archiveFiles.Select(fileEntry => fileEntry.Name));
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileEntry in archiveFiles)
        {
            var relativePath = HelmArchivePath.GetChartRelativePath(fileEntry.Name, chartRoot);
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new InvalidDataException($"Chart archive entry '{fileEntry.Name}' has no path below the chart root.");

            files[relativePath] = fileEntry.Content;
        }

        return files;
    }

    private static async Task<HelmChart> LoadDependencyArchiveAsync(
        string parentChartPath,
        string dependencyPath,
        byte[] archiveBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var subchartFiles = await LoadArchiveBytesAsync(
                archiveBytes,
                dependencyPath,
                cancellationToken);

            return await LoadFromFilesAsync(
                $"{parentChartPath}!{dependencyPath}",
                subchartFiles,
                null,
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            throw new InvalidDataException(
                $"Failed to load dependency archive '{dependencyPath}' in chart '{parentChartPath}': {ex.Message}",
                ex);
        }
    }

    private static byte[]? FindFile(Dictionary<string, byte[]> files, string fileName)
    {
        if (files.TryGetValue(fileName, out var exact))
            return exact;

        return files.FirstOrDefault(x =>
            x.Key.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string DecodeText(byte[]? content)
        => content is null ? string.Empty : Encoding.UTF8.GetString(content);

    internal static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static bool IsEmbeddedDependencyArchivePath(string path)
    {
        var normalized = NormalizePath(path);
        if (!normalized.StartsWith("charts/", StringComparison.Ordinal))
            return false;

        var rest = normalized["charts/".Length..];
        return rest.IndexOf('/') < 0 && IsDependencyArchivePath(rest);
    }

    private static bool IsDependencyArchiveFile(string path)
        => IsDependencyArchivePath(Path.GetFileName(path));

    private static bool IsDependencyArchivePath(string path)
        => path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    private sealed record ArchiveFileEntry(string Name, byte[] Content);

    private sealed record PackagedDependencyChart(HelmChart Chart);
}
