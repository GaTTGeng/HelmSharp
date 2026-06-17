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
                    System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(tgzFile, ct)));

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
}
