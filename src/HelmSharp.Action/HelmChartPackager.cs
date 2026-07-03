using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using HelmSharp.Chart;
using YamlDotNet.Core;

namespace HelmSharp.Action;

/// <summary>
/// Packages Helm charts into .tgz archives, matching `helm package` behavior.
/// </summary>
internal static class HelmChartPackager
{
    private static readonly Regex ChartNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9_.-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VersionPattern = new(
        @"^v?[0-9]+(?:\.[0-9]+){0,2}(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
            throw new FileNotFoundException("Chart.yaml file is missing");

        var chartYamlContent = await File.ReadAllTextAsync(chartYamlPath, Encoding.UTF8, cancellationToken);
        var metadata = LoadChartMetadata(chartYamlContent);

        var chartName = GetRequiredMetadataString(metadata, "name");
        ValidateChartName(chartName);

        var sourceVersion = GetRequiredMetadataString(metadata, "version");
        ValidateChartVersion(sourceVersion);

        var apiVersion = GetRequiredMetadataString(metadata, "apiVersion");
        ValidateApiVersion(apiVersion);

        var chartType = GetMetadataString(metadata, "type");
        ValidateChartType(chartType);

        var chartVersion = sourceVersion;

        if (version is not null)
        {
            ValidateVersionOverride(version);
            chartVersion = version;
            metadata["version"] = version;
        }

        if (appVersion is not null)
        {
            metadata["appVersion"] = appVersion;
        }

        if (version is not null || appVersion is not null)
            chartYamlContent = HelmYaml.Serialize(metadata);

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

    private static Dictionary<string, object?> LoadChartMetadata(string chartYamlContent)
    {
        try
        {
            return HelmYaml.DeserializeDictionary(chartYamlContent);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"cannot load Chart.yaml: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException($"cannot load Chart.yaml: {ex.Message}", ex);
        }
    }

    private static string GetRequiredMetadataString(IDictionary<string, object?> metadata, string key)
    {
        var value = GetMetadataString(metadata, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"validation: chart.metadata.{key} is required");

        return value;
    }

    private static string? GetMetadataString(IDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return null;

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static void ValidateChartName(string chartName)
    {
        if (!ChartNamePattern.IsMatch(chartName) ||
            chartName is "." or ".." ||
            chartName.Contains('/', StringComparison.Ordinal) ||
            chartName.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"validation: chart.metadata.name \"{chartName}\" is invalid");
        }
    }

    private static void ValidateChartVersion(string chartVersion)
    {
        if (!VersionPattern.IsMatch(chartVersion))
            throw new InvalidDataException($"validation: chart.metadata.version \"{chartVersion}\" is invalid");
    }

    private static void ValidateVersionOverride(string chartVersion)
    {
        if (string.IsNullOrWhiteSpace(chartVersion) || !VersionPattern.IsMatch(chartVersion))
            throw new InvalidDataException("Invalid Semantic Version");
    }

    private static void ValidateApiVersion(string apiVersion)
    {
        if (!apiVersion.Equals("v1", StringComparison.Ordinal) &&
            !apiVersion.Equals("v2", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"validation: chart.metadata.apiVersion \"{apiVersion}\" is unsupported");
        }
    }

    private static void ValidateChartType(string? chartType)
    {
        if (string.IsNullOrEmpty(chartType))
            return;

        if (chartType is not ("application" or "library"))
            throw new InvalidDataException("validation: chart.metadata.type must be application or library");
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
