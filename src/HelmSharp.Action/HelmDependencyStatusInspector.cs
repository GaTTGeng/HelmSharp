using System.Text.RegularExpressions;
using HelmSharp.Chart;
using HelmSharp.Repo;

namespace HelmSharp.Action;

/// <summary>
/// Computes the status text <c>helm dependency list/status</c> prints for one dependency:
/// <c>ok</c>, <c>unpacked</c>, <c>missing</c>, <c>wrong version</c>, <c>misnamed</c>,
/// <c>corrupt</c>, or <c>too many matches</c>.
/// </summary>
internal static class HelmDependencyStatusInspector
{
    // Strict SemVer (no leading zeros) used to tell a packaged chart's version suffix apart
    // from an arbitrary "name-something.tgz" file when several archives match a dependency.
    private static readonly Regex StrictSemanticVersionPattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Inspects a dependency against the chart's <c>charts/</c> directory (archives first,
    /// then unpacked directories) and falls back to charts embedded in the loaded parent.
    /// </summary>
    public static async Task<string> InspectAsync(
        string chartPath,
        HelmChart parent,
        HelmChartDependency dependency,
        CancellationToken cancellationToken)
    {
        var expectedVersion = dependency.Version;
        if (Directory.Exists(chartPath))
        {
            var chartsDirectory = Path.Combine(chartPath, "charts");
            var archiveStatus = await InspectArchivesAsync(
                chartsDirectory,
                dependency,
                expectedVersion,
                cancellationToken);
            if (archiveStatus is not null)
                return archiveStatus;

            var directoryStatus = await InspectDirectoriesAsync(
                chartsDirectory,
                dependency,
                expectedVersion,
                cancellationToken);
            if (directoryStatus is not null)
                return directoryStatus;
        }

        var embedded = FindEmbeddedDependency(parent, dependency);
        return embedded is null
            ? "missing"
            : InspectChart(embedded, dependency, expectedVersion, "unpacked");
    }

    private static async Task<string?> InspectArchivesAsync(
        string chartsDirectory,
        HelmChartDependency dependency,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(chartsDirectory))
            return null;

        var artifactNames = new[] { dependency.Name, dependency.Alias }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var archives = Directory
            .EnumerateFiles(chartsDirectory, "*.tgz", SearchOption.TopDirectoryOnly)
            .Where(path => artifactNames.Any(name =>
                Path.GetFileName(path).StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        // Match archives by "name-" (or alias) prefix. With several candidates, keep only
        // those whose suffix is a strict SemVer — the shape helm's packager produces — and
        // report ambiguity when that still does not pick one, matching helm's status text.
        if (archives.Count == 0)
            return null;
        if (archives.Count > 1)
        {
            archives = archives
                .Where(path => artifactNames.Any(name => HasStrictSemanticVersionSuffix(path, name)))
                .ToList();
            if (archives.Count == 0)
                return null;
            if (archives.Count > 1)
                return "too many matches";
        }

        try
        {
            var chart = await HelmChartLoader.LoadAsync(archives[0], cancellationToken);
            return InspectChart(chart, dependency, expectedVersion, "ok");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // An unreadable archive is reported as "corrupt" rather than failing the listing,
            // mirroring helm's dependency status behaviour for damaged packages.
            return "corrupt";
        }
    }

    private static bool HasStrictSemanticVersionSuffix(string archivePath, string artifactName)
    {
        var fileName = Path.GetFileName(archivePath);
        var prefix = artifactName + "-";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var version = fileName[prefix.Length..^".tgz".Length];
        return StrictSemanticVersionPattern.IsMatch(version);
    }

    private static async Task<string?> InspectDirectoriesAsync(
        string chartsDirectory,
        HelmChartDependency dependency,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(chartsDirectory))
            return null;

        // Prefer directories named after the alias (how aliased subcharts are vendored),
        // then the dependency name, then anything else loadable.
        var preferredNames = new[] { dependency.Alias, dependency.Name }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directories = Directory
            .EnumerateDirectories(chartsDirectory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path =>
            {
                var preferredIndex = Array.FindIndex(
                    preferredNames,
                    name => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
                return preferredIndex < 0 ? preferredNames.Length : preferredIndex;
            })
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HelmChart chart;
            try
            {
                chart = await HelmChartLoader.LoadAsync(directory, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (!string.Equals(chart.Name, dependency.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            return InspectChart(chart, dependency, expectedVersion, "unpacked");
        }

        return null;
    }

    private static HelmChart? FindEmbeddedDependency(HelmChart parent, HelmChartDependency dependency)
    {
        if (!string.IsNullOrWhiteSpace(dependency.Alias) &&
            parent.Subcharts.TryGetValue(dependency.Alias, out var aliased))
        {
            return aliased;
        }

        if (parent.Subcharts.TryGetValue(dependency.Name, out var named))
            return named;

        return parent.Subcharts.Values.FirstOrDefault(chart =>
            string.Equals(chart.Name, dependency.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Maps a located chart to a status string: name mismatch becomes <c>misnamed</c>, a version
    /// outside the constraint becomes <c>wrong version</c>, otherwise the supplied present status.
    /// </summary>
    private static string InspectChart(
        HelmChart chart,
        HelmChartDependency dependency,
        string? expectedVersion,
        string presentStatus)
    {
        if (!string.Equals(chart.Name, dependency.Name, StringComparison.OrdinalIgnoreCase))
            return "misnamed";

        return string.IsNullOrWhiteSpace(expectedVersion) ||
               HelmChartVersionResolver.Satisfies(chart.Version, expectedVersion)
            ? presentStatus
            : "wrong version";
    }

}
