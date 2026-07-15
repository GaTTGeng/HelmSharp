using System.Runtime.CompilerServices;

namespace HelmSharp.Chart;

internal sealed record HelmDependencyNode(
    string Identity,
    HelmChart Chart,
    HelmChartDependency? Metadata,
    IReadOnlyList<HelmChartDependency> Dependencies,
    IReadOnlyList<HelmDependencyNode> Children);

internal static class HelmDependencyProcessor
{
    private static readonly ConditionalWeakTable<Dictionary<string, object?>, CachedGraph> ProcessedGraphs = new();

    internal static HelmDependencyNode BuildAll(HelmChart chart)
        => BuildNode(chart, chart.Name, null, null, string.Empty, includeDisabled: true);

    internal static HelmDependencyNode BuildEffective(
        HelmChart chart,
        IDictionary<string, object?> values)
        => BuildNode(chart, chart.Name, null, values, string.Empty, includeDisabled: false);

    internal static HelmDependencyNode GetEffectiveForRender(
        HelmChart chart,
        Dictionary<string, object?> values)
    {
        if (ProcessedGraphs.TryGetValue(values, out var cached) && ReferenceEquals(cached.Chart, chart))
            return cached.Graph;
        return BuildEffective(chart, values);
    }

    internal static void RegisterProcessedValues(
        HelmChart chart,
        Dictionary<string, object?> values,
        HelmDependencyNode graph)
    {
        ProcessedGraphs.Remove(values);
        ProcessedGraphs.Add(values, new CachedGraph(chart, graph));
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

    private sealed record CachedGraph(HelmChart Chart, HelmDependencyNode Graph);
}
