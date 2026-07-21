using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HelmSharp.Chart;
using HelmSharp.Kube;
using k8s;
using k8s.Autorest;

namespace HelmSharp.Action;

/// <summary>
/// Supported Helm hook events.
/// </summary>
public enum HelmHookEvent
{
    PreInstall,
    PostInstall,
    PreUpgrade,
    PostUpgrade,
    PreDelete,
    PostDelete,
    PreRollback,
    PostRollback,
    Test
}

/// <summary>
/// Hook delete policy.
/// </summary>
public enum HelmHookDeletePolicy
{
    BeforeHookCreation,
    HookSucceeded,
    HookFailed
}

/// <summary>
/// Represents a parsed Helm hook from a Kubernetes manifest.
/// </summary>
internal sealed class HelmHook
{
    public string Path { get; init; } = string.Empty;
    public string Manifest { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public List<HelmHookEvent> Events { get; } = new();
    public List<HelmHookDeletePolicy> DeletePolicies { get; } = new();
    public int Weight { get; init; }
}

/// <summary>
/// Parses and executes Helm hooks from rendered manifests.
/// </summary>
internal sealed class HelmHookExecutor
{
    private readonly k8s.Kubernetes _client;
    private readonly string _fieldManager;

    public HelmHookExecutor(k8s.Kubernetes client, string fieldManager)
    {
        _client = client;
        _fieldManager = fieldManager;
    }

    /// <summary>
    /// Extracts hook resources from the manifest, returning (remainingManifest, hooks).
    /// </summary>
    public static (string RemainingManifest, List<HelmHook> Hooks) ExtractHooks(string manifest, string defaultNamespace)
    {
        var hooks = new List<HelmHook>();
        var remaining = new StringBuilder();
        var hookIndex = 0;

        foreach (var doc in SplitDocuments(manifest))
        {
            var identity = ManifestIdentity.Parse(doc, defaultNamespace);
            if (identity is null)
            {
                remaining.AppendLine("---");
                remaining.AppendLine(doc);
                continue;
            }

            var parsed = HelmYaml.DeserializeDictionary(doc);
            if (!parsed.TryGetValue("metadata", out var metaObj) || metaObj is not IDictionary<string, object?> meta)
            {
                remaining.AppendLine("---");
                remaining.AppendLine(doc);
                continue;
            }
            if (!meta.TryGetValue("annotations", out var annObj) || annObj is not IDictionary<string, object?> annotations)
            {
                remaining.AppendLine("---");
                remaining.AppendLine(doc);
                continue;
            }

            var hookAnnotation = annotations.TryGetValue("helm.sh/hook", out var hookVal)
                ? Convert.ToString(hookVal)
                : null;

            if (string.IsNullOrWhiteSpace(hookAnnotation))
            {
                remaining.AppendLine("---");
                remaining.AppendLine(doc);
                continue;
            }

            var hook = new HelmHook
            {
                Path = $"hook-{hookIndex++}",
                Manifest = doc,
                Kind = identity.Kind,
                Name = identity.Name,
                Namespace = identity.Namespace,
                Weight = annotations.TryGetValue("helm.sh/hook-weight", out var wVal) &&
                         int.TryParse(Convert.ToString(wVal), out var w) ? w : 0
            };

            foreach (var evt in hookAnnotation.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParseHookEvent(evt, out var hookEvent))
                    hook.Events.Add(hookEvent);
            }

            if (annotations.TryGetValue("helm.sh/hook-delete-policy", out var delVal))
            {
                var delPolicy = Convert.ToString(delVal) ?? string.Empty;
                foreach (var policy in delPolicy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (TryParseDeletePolicy(policy, out var deletePolicy))
                        hook.DeletePolicies.Add(deletePolicy);
                }
            }

            if (hook.DeletePolicies.Count == 0)
                hook.DeletePolicies.Add(HelmHookDeletePolicy.BeforeHookCreation);

            hooks.Add(hook);
        }

        return (remaining.ToString().Trim(), hooks);
    }

    /// <summary>
    /// Executes all hooks for the given event, in weight order.
    /// </summary>
    public async IAsyncEnumerable<string> ExecuteHooksAsync(
        List<HelmHook> hooks,
        HelmHookEvent hookEvent,
        string releaseNamespace,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var executing = hooks
            .Where(h => h.Events.Contains(hookEvent))
            .OrderBy(h => h.Weight)
            .ThenBy(h => h.Name)
            .ToList();

        foreach (var hook in executing)
        {
            var ns = string.IsNullOrWhiteSpace(hook.Namespace) ? releaseNamespace : hook.Namespace;

            // Delete according to delete policy (before-hook-creation)
            if (hook.DeletePolicies.Contains(HelmHookDeletePolicy.BeforeHookCreation))
            {
                await DeleteHookResourceAsync(hook, ns, cancellationToken);
            }

            // Apply the hook resource
            yield return $"Applying hook {hookEvent}: {hook.Kind}/{hook.Name}";

            var applier = new KubernetesManifestApplier(_client, _fieldManager);
            await foreach (var resource in applier.ApplyAsync(hook.Manifest, ns, cancellationToken))
            {
                yield return $"  Hook resource applied: {resource}";
            }

            // For test hooks, we could wait for completion here
            // For now, just log that the hook was applied
        }

        // Clean up hooks based on delete policies after all hooks complete
        foreach (var hook in executing)
        {
            var ns = string.IsNullOrWhiteSpace(hook.Namespace) ? releaseNamespace : hook.Namespace;
            if (hook.DeletePolicies.Contains(HelmHookDeletePolicy.HookSucceeded))
            {
                await DeleteHookResourceAsync(hook, ns, cancellationToken);
                yield return $"Deleted hook (succeeded policy): {hook.Kind}/{hook.Name}";
            }
        }
    }

    /// <summary>
    /// Executes hooks and handles failures with appropriate cleanup.
    /// </summary>
    public async IAsyncEnumerable<string> ExecuteHooksWithFailureHandlingAsync(
        List<HelmHook> hooks,
        HelmHookEvent hookEvent,
        string releaseNamespace,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var executing = hooks
            .Where(h => h.Events.Contains(hookEvent))
            .OrderBy(h => h.Weight)
            .ThenBy(h => h.Name)
            .ToList();

        foreach (var hook in executing)
        {
            var ns = string.IsNullOrWhiteSpace(hook.Namespace) ? releaseNamespace : hook.Namespace;

            if (hook.DeletePolicies.Contains(HelmHookDeletePolicy.BeforeHookCreation))
            {
                await DeleteHookResourceAsync(hook, ns, cancellationToken);
            }

            yield return $"Applying hook {hookEvent}: {hook.Kind}/{hook.Name}";
            var hookApplied = new List<string>();
            Exception? hookError = null;
            try
            {
                var applier = new KubernetesManifestApplier(_client, _fieldManager);
                await foreach (var resource in applier.ApplyAsync(hook.Manifest, ns, cancellationToken))
                {
                    hookApplied.Add($"  Hook resource applied: {resource}");
                }
            }
            catch (Exception ex)
            {
                hookError = ex;
            }

            foreach (var line in hookApplied) yield return line;

            if (hookError is not null)
            {
                yield return $"  Hook failed: {hookError.Message}";
                if (hook.DeletePolicies.Contains(HelmHookDeletePolicy.HookFailed))
                {
                    await DeleteHookResourceAsync(hook, ns, cancellationToken);
                    yield return $"  Deleted failed hook: {hook.Kind}/{hook.Name}";
                }
                throw hookError;
            }

            if (hook.DeletePolicies.Contains(HelmHookDeletePolicy.HookSucceeded))
            {
                await DeleteHookResourceAsync(hook, ns, cancellationToken);
                yield return $"  Deleted hook (succeeded policy): {hook.Kind}/{hook.Name}";
            }
        }
    }

    private async Task DeleteHookResourceAsync(HelmHook hook, string ns, CancellationToken ct)
    {
        try
        {
            var applier = new KubernetesManifestApplier(_client, _fieldManager);
            await foreach (var _ in applier.DeleteAsync(hook.Manifest, ns, cancellationToken: ct))
            {
                // drain the async enumerable
            }
        }
        catch
        {
            // Ignore errors during hook cleanup
        }
    }

    private static bool TryParseHookEvent(string value, out HelmHookEvent result)
    {
        switch (value.ToLowerInvariant())
        {
            case "pre-install":
                result = HelmHookEvent.PreInstall;
                return true;
            case "post-install":
                result = HelmHookEvent.PostInstall;
                return true;
            case "pre-upgrade":
                result = HelmHookEvent.PreUpgrade;
                return true;
            case "post-upgrade":
                result = HelmHookEvent.PostUpgrade;
                return true;
            case "pre-delete":
                result = HelmHookEvent.PreDelete;
                return true;
            case "post-delete":
                result = HelmHookEvent.PostDelete;
                return true;
            case "pre-rollback":
                result = HelmHookEvent.PreRollback;
                return true;
            case "post-rollback":
                result = HelmHookEvent.PostRollback;
                return true;
            case "test":
                result = HelmHookEvent.Test;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static bool TryParseDeletePolicy(string value, out HelmHookDeletePolicy result)
    {
        switch (value.ToLowerInvariant())
        {
            case "before-hook-creation":
                result = HelmHookDeletePolicy.BeforeHookCreation;
                return true;
            case "hook-succeeded":
                result = HelmHookDeletePolicy.HookSucceeded;
                return true;
            case "hook-failed":
                result = HelmHookDeletePolicy.HookFailed;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static IEnumerable<string> SplitDocuments(string manifest)
    {
        var current = new List<string>();
        foreach (var line in manifest.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Trim() == "---")
            {
                var doc = string.Join('\n', current).Trim();
                if (!string.IsNullOrWhiteSpace(doc))
                    yield return doc;
                current.Clear();
                continue;
            }
            current.Add(line);
        }
        var last = string.Join('\n', current).Trim();
        if (!string.IsNullOrWhiteSpace(last))
            yield return last;
    }
}
