using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace HelmSharp.Chart;

/// <summary>
/// A node in the chart dependency graph. <c>Identity</c> is the dependency alias when
/// declared, otherwise the chart name — the key used under parent values.
/// </summary>
internal sealed record HelmDependencyNode(
    string Identity,
    HelmChart Chart,
    HelmChartDependency? Metadata,
    IReadOnlyList<HelmChartDependency> Dependencies,
    IReadOnlyList<HelmDependencyNode> Children);

/// <summary>
/// Builds the chart dependency graph and applies Helm's enablement rules
/// (<c>condition</c>, <c>tags</c>, <c>enabled</c>) before values coalescing.
/// </summary>
internal static class HelmDependencyProcessor
{
    // Memoizes dependency graphs per chart instance keyed by a values fingerprint;
    // repeated renders of the same chart/values pair skip the rebuild.
    private static readonly ConditionalWeakTable<HelmChart, ProcessedChartGraphs> ProcessedGraphs = new();

    /// <summary>
    /// Builds the full graph including disabled dependencies. Used for the enablement
    /// evaluation pass, where a disabled node may still contribute condition paths.
    /// </summary>
    internal static HelmDependencyNode BuildAll(HelmChart chart)
        => BuildNode(chart, chart.Name, null, null, string.Empty, includeDisabled: true);

    /// <summary>
    /// Builds the graph with disabled dependencies pruned, matching the set of
    /// subcharts Helm actually renders and value-coalesces.
    /// </summary>
    internal static HelmDependencyNode BuildEffective(
        HelmChart chart,
        IDictionary<string, object?> values)
        => BuildNode(chart, chart.Name, null, values, string.Empty, includeDisabled: false);

    /// <summary>
    /// Returns a previously computed graph for this chart and values fingerprint, if any.
    /// </summary>
    internal static bool TryGetProcessedGraph(
        HelmChart chart,
        Dictionary<string, object?> values,
        out HelmDependencyNode graph)
    {
        if (ProcessedGraphs.TryGetValue(chart, out var cached) &&
            cached.TryGet(GetValuesFingerprint(values), out graph))
            return true;

        graph = null!;
        return false;
    }

    /// <summary>
    /// Caches the graph computed for a chart and values pair for later renders.
    /// </summary>
    internal static void RegisterProcessedValues(
        HelmChart chart,
        Dictionary<string, object?> values,
        HelmDependencyNode graph)
    {
        var cached = ProcessedGraphs.GetValue(chart, _ => new ProcessedChartGraphs());
        cached.Set(GetValuesFingerprint(values), graph);
    }

    private static HelmDependencyNode BuildNode(
        HelmChart chart,
        string identity,
        HelmChartDependency? metadata,
        IDictionary<string, object?>? rootValues,
        string path,
        bool includeDisabled)
    {
        var children = new List<HelmDependencyNode>();
        var effectiveDependencies = new List<HelmChartDependency>();
        if (chart.Dependencies.Count == 0)
        {
            // Charts without a dependencies list still may have charts/ content;
            // those subcharts are always included, with no enablement to evaluate.
            foreach (var (name, subchart) in chart.Subcharts)
            {
                children.Add(BuildNode(
                    subchart,
                    name,
                    null,
                    rootValues,
                    path + name + ".",
                    includeDisabled));
            }

            return new HelmDependencyNode(identity, chart, metadata, effectiveDependencies, children);
        }

        var tags = rootValues is not null && rootValues.TryGetValue("tags", out var tagsValue)
            ? tagsValue as IDictionary<string, object?>
            : null;

        foreach (var dependency in chart.Dependencies)
        {
            var dependencyIdentity = dependency.Alias ?? dependency.Name;
            var enabled = true;

            if (!includeDisabled)
            {
                // Helm enablement: tag overrides apply first; a matching true tag
                // enables even if another tag is false. Conditions are comma-separated
                // values paths; the first path resolving to a boolean decides, and a
                // missing path leaves the current decision untouched.
                var tagOverride = EvaluateTags(dependencyIdentity, dependency.Tags, tags);
                if (tagOverride.HasValue)
                    enabled = tagOverride.Value;

                foreach (var condition in (dependency.Condition ?? string.Empty)
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!TryGetPath(rootValues!, path + condition, out var conditionValue))
                        continue;

                    if (conditionValue is bool enabledByCondition)
                    {
                        enabled = enabledByCondition;
                        break;
                    }

                    System.Diagnostics.Trace.TraceWarning(
                        "Dependency condition '{0}' for chart '{1}' returned a non-boolean value.",
                        condition,
                        dependencyIdentity);
                }
            }

            if (!enabled)
                continue;

            // Effective metadata records the resolved identity (alias) so import-values
            // and value scoping later use the same key as the values tree.
            var effectiveMetadata = CloneDependency(dependency, dependencyIdentity);
            effectiveDependencies.Add(effectiveMetadata);
            if (!TryGetSubchart(chart, dependency, dependencyIdentity, out var subchart))
                continue;
            children.Add(BuildNode(
                subchart,
                dependencyIdentity,
                effectiveMetadata,
                rootValues,
                path + dependencyIdentity + ".",
                includeDisabled));
        }

        return new HelmDependencyNode(identity, chart, metadata, effectiveDependencies, children);
    }

    private static HelmChartDependency CloneDependency(HelmChartDependency dependency, string identity)
        => new()
        {
            Name = identity,
            Version = dependency.Version,
            Repository = dependency.Repository,
            Condition = dependency.Condition,
            Tags = dependency.Tags?.ToList(),
            Enabled = true,
            ImportValues = dependency.ImportValues?.Select(CloneValue).ToList(),
            Alias = dependency.Alias
        };

    private static object? CloneValue(object? value)
        => value switch
        {
            IDictionary<string, object?> dictionary => dictionary.ToDictionary(
                pair => pair.Key,
                pair => CloneValue(pair.Value),
                StringComparer.Ordinal),
            IList<object?> list => list.Select(CloneValue).ToList(),
            _ => value
        };

    private static bool? EvaluateTags(
        string dependencyIdentity,
        IEnumerable<string>? dependencyTags,
        IDictionary<string, object?>? valuesTags)
    {
        // Helm tag semantics: any true tag enables the dependency (true wins over
        // false); only when every resolved tag is false is the dependency disabled.
        // No tag present in values means tags do not decide enablement.
        var hasTrue = false;
        var hasFalse = false;
        foreach (var tag in dependencyTags ?? [])
        {
            if (valuesTags?.TryGetValue(tag, out var value) != true)
                continue;

            if (value is not bool enabled)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Dependency tag '{0}' for chart '{1}' returned a non-boolean value.",
                    tag,
                    dependencyIdentity);
                continue;
            }

            if (enabled)
                hasTrue = true;
            else
                hasFalse = true;
        }

        if (hasTrue)
            return true;
        if (hasFalse)
            return false;
        return null;
    }

    private static bool TryGetPath(
        IDictionary<string, object?> values,
        string path,
        out object? result)
    {
        object? current = values;
        foreach (var part in path.Split(
                     '.',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current is not IDictionary<string, object?> dictionary ||
                !dictionary.TryGetValue(part, out current))
            {
                result = null;
                return false;
            }
        }

        result = current;
        return true;
    }

    private static bool TryGetSubchart(
        HelmChart chart,
        HelmChartDependency dependency,
        string identity,
        out HelmChart subchart)
    {
        // Packaged subcharts are registered under alias or name depending on how they
        // were matched at load time; fall back to a name scan to cover both layouts.
        if (chart.Subcharts.TryGetValue(identity, out subchart!))
            return true;
        if (chart.Subcharts.TryGetValue(dependency.Name, out subchart!))
            return true;

        foreach (var candidate in chart.Subcharts.Values)
        {
            if (string.Equals(candidate.Name, dependency.Name, StringComparison.OrdinalIgnoreCase))
            {
                subchart = candidate;
                return true;
            }
        }

        subchart = null!;
        return false;
    }

    // SHA-256 over a canonical encoding (sorted keys, length-prefixed scalars) so the
    // cache key is stable across dictionary insertion orders but changes with any
    // value that could affect enablement.
    private static string GetValuesFingerprint(IDictionary<string, object?> values)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendCanonical(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append('n');
                return;
            case IDictionary<string, object?> dictionary:
                builder.Append('{');
                foreach (var (key, child) in dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    builder.Append(key.Length).Append(':').Append(key);
                    AppendCanonical(builder, child);
                }
                builder.Append('}');
                return;
            case IEnumerable<object?> sequence:
                builder.Append('[');
                foreach (var child in sequence)
                    AppendCanonical(builder, child);
                builder.Append(']');
                return;
            default:
                var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                builder.Append(value.GetType().FullName).Append(':').Append(text.Length).Append(':').Append(text);
                return;
        }
    }

    private sealed class ProcessedChartGraphs
    {
        private readonly Dictionary<string, HelmDependencyNode> _graphs = new(StringComparer.Ordinal);

        internal bool TryGet(string fingerprint, out HelmDependencyNode graph)
        {
            lock (_graphs)
                return _graphs.TryGetValue(fingerprint, out graph!);
        }

        internal void Set(string fingerprint, HelmDependencyNode graph)
        {
            lock (_graphs)
                _graphs[fingerprint] = graph;
        }
    }
}
