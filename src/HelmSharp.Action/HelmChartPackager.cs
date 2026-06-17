using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HelmSharp.Chart;

namespace HelmSharp.Action;

/// <summary>
/// Packages Helm charts into .tgz archives, matching `helm package` behavior.
/// </summary>
internal static class HelmChartPackager
{
    /// <summary>
    /// Packages a chart directory into a .tgz archive.
    /// Returns the path to the created archive.
    /// </summary>
    public static async Task<string> PackageAsync(
        string chartPath,
        string? destination = null,
        string? version = null,
        string? appVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(chartPath))
            throw new DirectoryNotFoundException($"Chart directory not found: {chartPath}");

        var chartYamlPath = Path.Combine(chartPath, "Chart.yaml");
        if (!File.Exists(chartYamlPath))
            throw new FileNotFoundException("Chart.yaml not found in chart directory");

        var chartYamlContent = await File.ReadAllTextAsync(chartYamlPath, Encoding.UTF8, cancellationToken);
        var metadata = HelmYaml.DeserializeDictionary(chartYamlContent);

        var chartName = HelmYaml.GetString(metadata, "name") ?? Path.GetFileName(chartPath);
        var chartVersion = version ?? HelmYaml.GetString(metadata, "version") ?? "0.1.0";

        if (version is not null)
        {
            metadata["version"] = version;
            chartYamlContent = HelmYaml.Serialize(metadata);
        }
        if (appVersion is not null)
        {
            metadata["appVersion"] = appVersion;
            chartYamlContent = HelmYaml.Serialize(metadata);
        }

        var fileName = $"{chartName}-{chartVersion}.tgz";
        var destDir = destination ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(destDir);
        var outputPath = Path.Combine(destDir, fileName);

        await using var fileStream = File.Create(outputPath);
        await using var gzip = new GZipStream(fileStream, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip);

        await AddDirectoryToTarAsync(tar, chartPath, chartName, chartYamlContent, cancellationToken);

        return outputPath;
    }

    private static async Task AddDirectoryToTarAsync(
        TarWriter tar,
        string sourceDir,
        string archiveRoot,
        string chartYamlContent,
        CancellationToken cancellationToken)
    {
        foreach (var filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDir, filePath)
                .Replace('\\', '/');
            var archivePath = $"{archiveRoot}/{relativePath}";

            var entry = new GnuTarEntry(TarEntryType.RegularFile, archivePath);
            var fileBytes = relativePath.Equals("Chart.yaml", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetBytes(chartYamlContent)
                : await File.ReadAllBytesAsync(filePath, cancellationToken);
            entry.DataStream = new MemoryStream(fileBytes);
            await tar.WriteEntryAsync(entry, cancellationToken);
        }
    }
}
