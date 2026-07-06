using HelmSharp.Chart;

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
    public static async Task<string> GenerateIndexAsync(
        string dirPath,
        string? url = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(dirPath))
            throw new DirectoryNotFoundException($"Directory not found: {dirPath}");

        var entries = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);

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
                    ["apiVersion"] = "v2",
                    ["name"] = chart.Name,
                    ["version"] = chart.Version,
                    ["description"] = chart.Description ?? "",
                    ["type"] = chart.Type ?? "application",
                    ["appVersion"] = chart.AppVersion,
                    ["home"] = chart.Home,
                    ["sources"] = chart.Sources,
                    ["keywords"] = chart.Keywords,
                    ["maintainers"] = chart.Maintainers,
                    ["deprecated"] = chart.Deprecated ? true : null,
                    ["digest"] = digest,
                    ["urls"] = url is not null
                        ? new List<object?> { $"{url.TrimEnd('/')}/{Path.GetFileName(tgzFile)}" }
                        : new List<object?> { Path.GetFileName(tgzFile) },
                    ["created"] = fileInfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
                };

                if (!entries.ContainsKey(chart.Name))
                    entries[chart.Name] = new List<Dictionary<string, object?>>();
                entries[chart.Name].Add(entry);
            }
            catch
            {
                // Skip invalid chart packages
            }
        }

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
        return indexPath;
    }

    private static int CompareChartVersionsDescending(
        Dictionary<string, object?> left,
        Dictionary<string, object?> right)
    {
        var leftVersion = HelmYaml.GetString(left, "version") ?? string.Empty;
        var rightVersion = HelmYaml.GetString(right, "version") ?? string.Empty;
        return CompareChartVersions(rightVersion, leftVersion);
    }

    private static int CompareChartVersions(string left, string right)
    {
        var leftParts = SplitVersion(left);
        var rightParts = SplitVersion(right);
        for (var i = 0; i < Math.Max(leftParts.Core.Count, rightParts.Core.Count); i++)
        {
            var leftValue = i < leftParts.Core.Count ? leftParts.Core[i] : 0;
            var rightValue = i < rightParts.Core.Count ? rightParts.Core[i] : 0;
            var comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0)
                return comparison;
        }

        if (leftParts.Prerelease is null && rightParts.Prerelease is not null)
            return 1;
        if (leftParts.Prerelease is not null && rightParts.Prerelease is null)
            return -1;
        return string.CompareOrdinal(leftParts.Prerelease, rightParts.Prerelease);
    }

    private static (List<int> Core, string? Prerelease) SplitVersion(string version)
    {
        var withoutBuild = version.Split('+', 2)[0];
        var prereleaseSplit = withoutBuild.Split('-', 2);
        var core = prereleaseSplit[0]
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var value) ? value : 0)
            .ToList();
        return (core, prereleaseSplit.Length > 1 ? prereleaseSplit[1] : null);
    }
}
