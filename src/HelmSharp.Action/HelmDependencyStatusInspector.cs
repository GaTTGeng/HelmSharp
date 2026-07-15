using System.Text.RegularExpressions;
using HelmSharp.Chart;
using HelmSharp.Repo;

namespace HelmSharp.Action;

internal static class HelmDependencyStatusInspector
{
    private static readonly Regex StrictSemanticVersionPattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<string> InspectAsync(
        string chartPath,
        HelmChart parent,
        HelmChartDependency dependency,
        CancellationToken cancellationToken)
    {
        if (IsDisabled(parent, dependency))
            return "disabled";

        var expectedVersion = GetExpectedVersion(parent, dependency);
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

    private static string? GetExpectedVersion(HelmChart parent, HelmChartDependency dependency)
    {
        var lockEntry = parent.LockEntries.FirstOrDefault(entry =>
            string.Equals(entry.Name, dependency.Name, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(entry.Repository) ||
             string.IsNullOrWhiteSpace(dependency.Repository) ||
             string.Equals(entry.Repository, dependency.Repository, StringComparison.Ordinal)));
        return string.IsNullOrWhiteSpace(lockEntry?.Version)
            ? dependency.Version
            : lockEntry.Version;
    }

    private static bool IsDisabled(HelmChart parent, HelmChartDependency dependency)
    {
        if (!dependency.Enabled)
            return true;

        if (string.IsNullOrWhiteSpace(dependency.Condition))
            return false;

        var values = HelmYaml.DeserializeDictionary(parent.ValuesYaml);
        foreach (var condition in dependency.Condition.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryGetBoolean(values, condition, out var enabled))
                return !enabled;
        }

        return false;
    }

    private static bool TryGetBoolean(
        IReadOnlyDictionary<string, object?> values,
        string path,
        out bool value)
    {
        object? current = values;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not IReadOnlyDictionary<string, object?> map ||
                !map.TryGetValue(segment, out current))
            {
                value = default;
                return false;
            }
        }

        if (current is bool boolean)
        {
            value = boolean;
            return true;
        }

        value = default;
        return false;
    }
}
