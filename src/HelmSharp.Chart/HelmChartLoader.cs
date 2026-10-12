using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace HelmSharp.Chart;

/// <summary>
/// Chart metadata and contents loaded from a chart directory or a packaged
/// <c>.tgz</c> archive, matching the shape Helm derives from Chart.yaml and the
/// chart file layout (<c>values.yaml</c>, <c>templates/</c>, <c>crds/</c>, <c>charts/</c>).
/// </summary>
public sealed class HelmChart
{
    /// <summary>
    /// Gets the chart metadata API version from Chart.yaml.
    /// </summary>
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>Chart name from Chart.yaml. Falls back to the chart path's file name when absent.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>SemVer chart version from Chart.yaml (not the application version).</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Optional application version from Chart.yaml; informational only.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Optional chart description from Chart.yaml.</summary>
    public string? Description { get; init; }

    /// <summary>Optional project home page URL from Chart.yaml.</summary>
    public string? Home { get; init; }

    /// <summary>Optional icon URL from Chart.yaml.</summary>
    public string? Icon { get; init; }

    /// <summary>Source repository URLs from Chart.yaml. Entries are kept as raw YAML values.</summary>
    public List<object?>? Sources { get; set; }

    /// <summary>Keywords from Chart.yaml, used by chart repository search.</summary>
    public List<object?>? Keywords { get; set; }

    /// <summary>Maintainer entries from Chart.yaml. Entries are kept as raw YAML values.</summary>
    public List<object?>? Maintainers { get; set; }

    /// <summary>Chart type from Chart.yaml; <c>application</c> or <c>library</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Whether Chart.yaml marks the chart as deprecated.</summary>
    public bool Deprecated { get; init; }

    /// <summary>SemVer range of compatible Kubernetes versions from Chart.yaml (<c>kubeVersion</c>).</summary>
    public string? KubeVersion { get; init; }

    /// <summary>Arbitrary annotation map from Chart.yaml, including Helm's artifact hub metadata.</summary>
    public Dictionary<string, object?>? Annotations { get; set; }

    /// <summary>Dependency declarations from Chart.yaml's <c>dependencies</c> list.</summary>
    public List<HelmChartDependency> Dependencies { get; } = new();

    /// <summary>Resolved dependency versions from Chart.lock, parallel to <see cref="Dependencies"/>.</summary>
    public List<HelmChartLockEntry> LockEntries { get; } = new();

    /// <summary>Digest recorded in Chart.lock; identifies the locked dependency set.</summary>
    public string? LockDigest { get; set; }

    /// <summary>Timestamp recorded in Chart.lock when the lock file was generated.</summary>
    public string? LockGenerated { get; set; }

    /// <summary>
    /// Raw contents of <c>values.yaml</c> at the chart root. Empty when the chart ships no values file.
    /// </summary>
    public string ValuesYaml { get; init; } = string.Empty;

    /// <summary>Template file contents keyed by chart-relative path (for example <c>templates/deploy.yaml</c>).</summary>
    public Dictionary<string, string> Templates { get; } = new(StringComparer.Ordinal);

    /// <summary>Non-template chart files keyed by chart-relative path, exposed to templates via <c>.Files</c>.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Parsed CRD manifests from <c>crds/</c>, applied before templates during install.</summary>
    public List<Dictionary<string, object?>> Crds { get; } = new();

    /// <summary>
    /// Loaded subcharts keyed by chart name, or by dependency alias when Chart.yaml declares one.
    /// Sourced from directory subcharts under <c>charts/</c> and from packaged dependency archives.
    /// </summary>
    public Dictionary<string, HelmChart> Subcharts { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Loads Helm charts from unpacked directories or packaged archives into <see cref="HelmChart"/> models.
/// </summary>
public static class HelmChartLoader
{
    /// <summary>
    /// Loads a chart from a directory or a <c>.tgz</c>/<c>.tar.gz</c> archive using
    /// <see cref="HelmChartArchiveLimits.Default"/>.
    /// </summary>
    /// <param name="chartPath">Chart directory path, or path to a packaged chart archive.</param>
    /// <param name="cancellationToken">Cancels file reads and recursive subchart loading.</param>
    /// <returns>The fully loaded chart, including subcharts and CRDs.</returns>
    /// <exception cref="InvalidOperationException">Thrown when Chart.yaml is missing from the chart.</exception>
    /// <exception cref="InvalidDataException">Thrown when an archive entry lies outside the chart root or a dependency archive is corrupt.</exception>
    /// <exception cref="ChartArchiveLimitExceededException">Thrown when a decompression resource limit is exceeded.</exception>
    public static Task<HelmChart> LoadAsync(string chartPath, CancellationToken cancellationToken)
        => LoadAsync(chartPath, HelmChartArchiveLimits.Default, cancellationToken);

    /// <summary>
    /// Loads a chart from a directory or a <c>.tgz</c>/<c>.tar.gz</c> archive with explicit
    /// decompression budgets. Null applies <see cref="HelmChartArchiveLimits.Default"/>. Use
    /// <see cref="HelmChartArchiveLimits.ForTrustedCharts"/> or a custom instance for trusted
    /// archives that exceed the default profile.
    /// </summary>
    /// <param name="chartPath">Chart directory path, or path to a packaged chart archive.</param>
    /// <param name="archiveLimits">Resource budgets enforced while reading chart archives; null applies the defaults.</param>
    /// <param name="cancellationToken">Cancels file reads and recursive subchart loading.</param>
    /// <returns>The fully loaded chart, including subcharts and CRDs.</returns>
    /// <exception cref="InvalidOperationException">Thrown when Chart.yaml is missing from the chart.</exception>
    /// <exception cref="InvalidDataException">Thrown when an archive entry lies outside the chart root or a dependency archive is corrupt.</exception>
    /// <exception cref="ChartArchiveLimitExceededException">Thrown when a decompression resource limit is exceeded.</exception>
    public static async Task<HelmChart> LoadAsync(
        string chartPath,
        HelmChartArchiveLimits? archiveLimits,
        CancellationToken cancellationToken)
    {
        var budget = new ChartArchiveBudget(archiveLimits ?? HelmChartArchiveLimits.Default);
        return await LoadAsync(chartPath, budget, depth: 0, cancellationToken);
    }

    private static async Task<HelmChart> LoadAsync(
        string chartPath,
        ChartArchiveBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        // Stage 1: flatten the chart to a path→bytes map — directory walk or tar
        // extraction with the archive's chart root re-rooted — then share one loader.
        var isDirectory = Directory.Exists(chartPath);
        var files = isDirectory
            ? await LoadDirectoryAsync(chartPath, cancellationToken)
            : await LoadArchiveFileAsync(chartPath, budget, cancellationToken);

        return await LoadFromFilesAsync(
            chartPath,
            files,
            isDirectory ? chartPath : null,
            budget,
            depth,
            cancellationToken);
    }

    private static async Task<HelmChart> LoadFromFilesAsync(
        string chartPath,
        Dictionary<string, byte[]> files,
        string? chartDir,
        ChartArchiveBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        // Load stages, in order:
        //   1. Chart.yaml — identity and dependency declarations (required).
        //   2. values.yaml defaults and optional Chart.lock pinning/digest.
        //   3. File walk into templates/ (rendered later), crds/ (applied before
        //      templates), and static files exposed via .Files; charts/ is skipped here.
        //   4. charts/ subcharts: directory trees (on-disk charts) and packaged
        //      dependency archives (directory or embedded in the parent archive).
        //   5. Alias registration for packaged deps, aligned against Chart.lock by
        //      declaration index when Chart.yaml carries a SemVer range.
        // Chart.yaml is the chart's identity file; Helm refuses to load a chart without it.
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

        // Chart.lock is optional; when present it pins dependency versions and
        // supplies the digest used to detect drift from the declarations in Chart.yaml.
        var lockContent = FindFile(files, "Chart.lock");
        if (lockContent is not null)
        {
            var lockDict = HelmYaml.DeserializeDictionary(DecodeText(lockContent));
            chart.LockDigest = HelmYaml.GetString(lockDict, "digest");
            chart.LockGenerated = HelmYaml.GetString(lockDict, "generated");
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
                // Chart.yaml dependency enabled flags are commonly written as strings
                // in the wild; Helm treats anything other than an explicit false as enabled.
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
                        var subchart = await LoadAsync(subchartDir, budget, depth, cancellationToken);
                        chart.Subcharts[subchartName] = subchart;
                    }
                    catch (ChartArchiveLimitExceededException)
                    {
                        // Resource budgets are operation-wide; never skip and continue past them.
                        throw;
                    }
                    catch
                    {
                        // Skip invalid subcharts
                    }
                }

                // Packaged dependency archives dropped into charts/ (helm dependency
                // build output) sit alongside directory subcharts and take part in
                // the same dependency-name/alias resolution.
                foreach (var dependencyArchive in Directory
                    .EnumerateFiles(chartsDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsDependencyArchiveFile)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dependencyPath = NormalizePath(Path.GetRelativePath(chartDir, dependencyArchive));
                    // Validate the known file size before allocating the archive bytes so an
                    // oversized dependency is rejected without ever being buffered in memory.
                    budget.CheckCompressedBytes(new FileInfo(dependencyArchive).Length);
                    var archiveBytes = await File.ReadAllBytesAsync(dependencyArchive, cancellationToken);
                    var subchart = await LoadDependencyArchiveAsync(
                        chartPath,
                        dependencyPath,
                        archiveBytes,
                        budget,
                        depth + 1,
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
                    budget,
                    depth + 1,
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
                        budget,
                        depth,
                        cancellationToken);
                    chart.Subcharts[subchartName] = subchart;
                }
                catch (ChartArchiveLimitExceededException)
                {
                    // Resource budgets are operation-wide; never skip and continue past them.
                    throw;
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

        // A packaged chart is registered under its dependency alias when Chart.yaml
        // declares one, so value overrides and template scoping see the alias key.
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

        // Chart.yaml may declare a SemVer range while the packaged chart carries the
        // resolved version; Chart.lock holds that resolution. Dependency and lock
        // entries are kept in the same order, so an index match substitutes for
        // full SemVer range evaluation.
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
            // Each directory subchart loads its own charts/ tree, while packaged
            // dependencies are checked against the compressed-size budget before
            // buffering. Flattening this subtree would read nested archives early.
            if (relative.StartsWith("charts/", StringComparison.Ordinal))
                continue;
            files[relative] = await File.ReadAllBytesAsync(file, cancellationToken);
        }

        return files;
    }

    private static async Task<Dictionary<string, byte[]>> LoadArchiveFileAsync(
        string chartPath,
        ChartArchiveBudget budget,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(chartPath);
        return await LoadArchiveAsync(file, chartPath, budget, cancellationToken);
    }

    private static async Task<Dictionary<string, byte[]>> LoadArchiveBytesAsync(
        byte[] archiveBytes,
        string chartPath,
        ChartArchiveBudget budget,
        CancellationToken cancellationToken)
    {
        await using var memory = new MemoryStream(archiveBytes, writable: false);
        return await LoadArchiveAsync(memory, chartPath, budget, cancellationToken);
    }

    private static async Task<Dictionary<string, byte[]>> LoadArchiveAsync(
        Stream input,
        string chartPath,
        ChartArchiveBudget budget,
        CancellationToken cancellationToken)
    {
        var archiveFiles = new List<ArchiveFileEntry>();
        // Compressed size is the archive stream's own length (file or byte array); nested
        // dependency archives pass their in-memory .tgz bytes so each stream gets its own
        // ratio bound while sharing the tree-wide extracted budget.
        var compressedBytes = input.CanSeek ? input.Length - input.Position : 0;
        var scope = budget.BeginArchive(compressedBytes);
        // Packaged charts are gzipped tarballs; bare tar input is tolerated for
        // dependency archives that omit the gzip layer.
        await using Stream archive = chartPath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
                                     chartPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(input, CompressionMode.Decompress)
            : input;

        // Meter every decompressed byte the tar reader consumes — headers and padding
        // included — so structural tar data cannot be decompressed outside the budgets.
        using var metered = scope.MeterDecompressedStream(archive);
        using var guarded = scope.GuardTarStream(metered);
        using var reader = new TarReader(guarded);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.Directory || entry.DataStream is null)
                continue;

            var entryName = HelmArchivePath.NormalizeEntryName(entry.Name);
            // Per-entry budget enforcement counts payload bytes as they stream; tar
            // header size fields are untrusted metadata and never used as the size of truth.
            var content = await scope.ReadEntryAsync(entry.DataStream, cancellationToken);
            archiveFiles.Add(new ArchiveFileEntry(entryName, content));
        }

        // Helm packages charts under a single top-level folder (chartname/version);
        // paths must be re-rooted so the chart tree looks like a directory chart.
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
        ChartArchiveBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        // Packaged dependency archives consume one nesting level; the shared budget is
        // not reset for the child so cumulative extracted bytes stay tree-bounded.
        budget.CheckDependencyDepth(depth);
        try
        {
            var subchartFiles = await LoadArchiveBytesAsync(
                archiveBytes,
                dependencyPath,
                budget,
                cancellationToken);

            return await LoadFromFilesAsync(
                $"{parentChartPath}!{dependencyPath}",
                subchartFiles,
                null,
                budget,
                depth,
                cancellationToken);
        }
        // Limit violations keep their stable exception type so callers can react to
        // budgets without unwrapping; corrupt-archive errors are rephrased with context.
        catch (ChartArchiveLimitExceededException)
        {
            throw;
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

    /// <summary>
    /// Normalizes a path to forward slashes without a leading slash so archive and
    /// directory entries share one key format.
    /// </summary>
    internal static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static bool IsEmbeddedDependencyArchivePath(string path)
    {
        var normalized = NormalizePath(path);
        if (!normalized.StartsWith("charts/", StringComparison.Ordinal))
            return false;

        // Only archives placed directly under charts/ count; nested paths belong
        // to already-expanded subchart trees and are not double-loaded.
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
