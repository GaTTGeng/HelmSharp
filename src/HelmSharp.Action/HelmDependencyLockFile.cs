using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HelmSharp.Chart;

namespace HelmSharp.Action;

/// <summary>A dependency as resolved and pinned by Chart.lock.</summary>
internal sealed record HelmResolvedDependency(
    string Name,
    string Version,
    string Repository,
    string? ArchiveDigest = null);

/// <summary>Parsed Chart.lock contents: the lock digest and the pinned dependency list.</summary>
internal sealed record HelmDependencyLock(
    string Digest,
    IReadOnlyList<HelmResolvedDependency> Dependencies);

/// <summary>
/// Reads and writes Chart.lock and computes its digest. Digest computation and field
/// normalization mirror Helm so lock files stay byte-compatible with <c>helm dependency</c>.
/// </summary>
internal static class HelmDependencyLockFile
{
    /// <summary>Loads Chart.lock from the chart directory, or null when the file is absent.</summary>
    /// <exception cref="InvalidDataException">The lock file is missing required fields or has invalid entries.</exception>
    public static async Task<HelmDependencyLock?> LoadAsync(
        string chartPath,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(chartPath, "Chart.lock");
        if (!File.Exists(lockPath))
            return null;

        var root = HelmYaml.DeserializeDictionary(await File.ReadAllTextAsync(lockPath, cancellationToken));
        var digest = HelmYaml.GetString(root, "digest");
        if (string.IsNullOrWhiteSpace(digest))
            throw new InvalidDataException("Chart.lock is missing the required digest.");
        if (!root.TryGetValue("dependencies", out var dependenciesObject) ||
            dependenciesObject is not IList<object?> dependencies)
            throw new InvalidDataException("Chart.lock is missing the required dependencies list.");

        var lockedDependencies = new List<HelmResolvedDependency>(dependencies.Count);
        foreach (var dependencyObject in dependencies)
        {
            if (dependencyObject is not IDictionary<string, object?> dependency)
                throw new InvalidDataException("Chart.lock contains an invalid dependency entry.");

            var name = HelmYaml.GetString(dependency, "name");
            var version = HelmYaml.GetString(dependency, "version");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                throw new InvalidDataException("Chart.lock dependency entries require name and version values.");
            lockedDependencies.Add(new HelmResolvedDependency(
                name,
                version,
                HelmYaml.GetString(dependency, "repository") ?? string.Empty,
                HelmYaml.GetString(dependency, "digest")));
        }

        return new HelmDependencyLock(digest, lockedDependencies);
    }

    /// <summary>
    /// Loads the dependency declarations from Chart.yaml, normalized into the shape Helm
    /// feeds into the lock digest. Returns an empty list when the chart declares none.
    /// </summary>
    public static async Task<IReadOnlyList<Dictionary<string, object?>>> LoadRequestedDependenciesAsync(
        string chartPath,
        CancellationToken cancellationToken)
    {
        var chartYamlPath = Path.Combine(chartPath, "Chart.yaml");
        var metadata = HelmYaml.DeserializeDictionary(await File.ReadAllTextAsync(chartYamlPath, cancellationToken));
        if (!metadata.TryGetValue("dependencies", out var dependenciesObject) ||
            dependenciesObject is not IList<object?> dependencies)
            return [];

        return dependencies
            .OfType<IDictionary<string, object?>>()
            .Select(NormalizeRequestedDependency)
            .ToList();
    }

    /// <summary>
    /// Computes the Chart.lock digest as <c>sha256:</c> plus a hex hash over the JSON encoding
    /// of the requested (Chart.yaml) and resolved (locked) dependency lists.
    /// </summary>
    /// <exception cref="InvalidDataException">Requested and resolved dependency counts differ.</exception>
    public static string ComputeDigest(
        IReadOnlyList<Dictionary<string, object?>> requested,
        IReadOnlyList<HelmResolvedDependency> resolved)
    {
        if (requested.Count != resolved.Count)
            throw new InvalidDataException(
                $"Resolved dependency count ({resolved.Count}) does not match Chart.yaml ({requested.Count}).");

        var locked = resolved.Select(dependency => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = dependency.Name,
            ["version"] = dependency.Version,
            ["repository"] = dependency.Repository
        }).ToList();
        // Hash over both lists serialized together, matching Helm's digest input exactly;
        // any drift in either the declarations or the pins changes the digest.
        var json = JsonSerializer.Serialize(new object[] { requested, locked });
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return $"sha256:{digest}";
    }

    /// <summary>
    /// Writes Chart.lock when the digest differs from the stored one. Returns true when the
    /// file was rewritten; false when the existing lock already matches.
    /// </summary>
    public static async Task<bool> WriteIfChangedAsync(
        string chartPath,
        IReadOnlyList<HelmResolvedDependency> dependencies,
        string digest,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(chartPath, "Chart.lock");
        // Skip the write entirely when the digest matches so a no-op dependency build does
        // not churn the lock file's timestamp or content.
        if (File.Exists(lockPath))
        {
            var existing = HelmYaml.DeserializeDictionary(await File.ReadAllTextAsync(lockPath, cancellationToken));
            if (string.Equals(HelmYaml.GetString(existing, "digest"), digest, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var generated = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        var yaml = new StringBuilder("dependencies:\n");
        foreach (var dependency in dependencies)
        {
            yaml.Append("- name: ").AppendLine(SerializeScalar(dependency.Name));
            yaml.Append("  repository: ").AppendLine(SerializeScalar(dependency.Repository));
            yaml.Append("  version: ").AppendLine(SerializeScalar(dependency.Version));
        }
        yaml.Append("digest: ").AppendLine(digest);
        yaml.Append("generated: \"").Append(generated).AppendLine("\"");

        // Write to a temp file and swap it in so a crash mid-write cannot leave a truncated lock.
        var temporaryPath = $"{lockPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                yaml.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, lockPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return true;
    }

    /// <summary>
    /// Normalizes one Chart.yaml dependency into the canonical shape used for digesting:
    /// empty strings are dropped and <c>enabled</c> is kept only when true, so the digest is
    /// insensitive to omitted versus empty fields.
    /// </summary>
    private static Dictionary<string, object?> NormalizeRequestedDependency(
        IDictionary<string, object?> dependency)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = HelmYaml.GetString(dependency, "name") ?? string.Empty
        };
        AddStringIfNotEmpty(normalized, dependency, "version");
        normalized["repository"] = HelmYaml.GetString(dependency, "repository") ?? string.Empty;
        AddStringIfNotEmpty(normalized, dependency, "condition");
        AddListIfNotEmpty(normalized, dependency, "tags");
        if (dependency.TryGetValue("enabled", out var enabled) && enabled is true)
            normalized["enabled"] = true;
        AddListIfNotEmpty(normalized, dependency, "import-values");
        AddStringIfNotEmpty(normalized, dependency, "alias");
        return normalized;
    }

    private static void AddStringIfNotEmpty(
        IDictionary<string, object?> target,
        IDictionary<string, object?> source,
        string key)
    {
        var value = HelmYaml.GetString(source, key);
        if (!string.IsNullOrEmpty(value))
            target[key] = value;
    }

    private static void AddListIfNotEmpty(
        IDictionary<string, object?> target,
        IDictionary<string, object?> source,
        string key)
    {
        if (!source.TryGetValue(key, out var value) || value is not IList<object?> list || list.Count == 0)
            return;
        target[key] = list.Select(NormalizeJsonValue).ToList();
    }

    /// <summary>
    /// Recursively normalizes JSON values for digesting: object keys are sorted so the digest
    /// is stable regardless of the mapping order in the source YAML.
    /// </summary>
    private static object? NormalizeJsonValue(object? value)
        => value switch
        {
            IDictionary<string, object?> dictionary => new SortedDictionary<string, object?>(
                dictionary.ToDictionary(item => item.Key, item => NormalizeJsonValue(item.Value)),
                StringComparer.Ordinal),
            IList<object?> list => list.Select(NormalizeJsonValue).ToList(),
            _ => value
        };

    private static string SerializeScalar(string value)
        => value.Length == 0
            ? "\"\""
            : HelmYaml.Serialize(value).TrimEnd('\r', '\n');
}
