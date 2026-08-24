using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HelmSharp.Chart;
using HelmSharp.Kube;
using k8s;
using k8s.Autorest;
using k8s.Models;

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
    /// <summary>Delete an earlier instance before creating the hook. This is the default policy.</summary>
    BeforeHookCreation,

    /// <summary>Delete the hook after its execution succeeds.</summary>
    HookSucceeded,

    /// <summary>Attempt bounded cleanup after hook failure or operation cancellation.</summary>
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
    public List<string> OutputLogPolicies { get; } = new();
    public int Weight { get; init; }
    public DateTimeOffset? LastRunStartedAt { get; set; }
    public DateTimeOffset? LastRunCompletedAt { get; set; }
    public string? LastRunPhase { get; set; }
}

/// <summary>
/// Parses and executes Helm hooks from rendered manifests.
/// </summary>
internal sealed class HelmHookExecutor
{
    internal const string CleanupErrorDataKey = "HelmSharp.HookCleanupError";
    private readonly k8s.Kubernetes _client;
    private readonly string _fieldManager;
    private readonly int _timeoutSeconds;

    public HelmHookExecutor(k8s.Kubernetes client, string fieldManager, int timeoutSeconds)
    {
        _client = client;
        _fieldManager = fieldManager;
        _timeoutSeconds = timeoutSeconds;
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
        await foreach (var line in ExecuteHooksWithFailureHandlingAsync(hooks, hookEvent, releaseNamespace, cancellationToken))
            yield return line;
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
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ThenBy(h => h.Kind, StringComparer.Ordinal)
            .ThenBy(h => h.Path, StringComparer.Ordinal)
            .ToList();

        for (var hookIndex = 0; hookIndex < executing.Count; hookIndex++)
        {
            var hook = executing[hookIndex];
            var ns = string.IsNullOrWhiteSpace(hook.Namespace) ? releaseNamespace : hook.Namespace;

            if (hook.DeletePolicies.Contains(HelmHookDeletePolicy.BeforeHookCreation))
            {
                Exception? beforeCreationError = null;
                try
                {
                    await DeleteHookResourceAsync(hook, ns, _timeoutSeconds, cancellationToken);
                }
                catch (Exception ex)
                {
                    beforeCreationError = ex;
                }

                if (beforeCreationError is not null)
                {
                    var finalization = await CleanupSucceededHooksDuringFinalizationAsync(
                        executing.Take(hookIndex),
                        releaseNamespace);
                    AttachCleanupErrors(beforeCreationError, finalization.Errors);
                    throw beforeCreationError;
                }
            }

            hook.LastRunStartedAt = DateTimeOffset.UtcNow;
            hook.LastRunCompletedAt = null;
            hook.LastRunPhase = "Running";
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

                if (hook.Kind == "Job")
                {
                    var waiter = new KubernetesResourceWaiter(_client, _timeoutSeconds);
                    await foreach (var line in waiter.WaitForReadyAsync(
                                       hook.Manifest,
                                       ns,
                                       waitForJobs: true,
                                       cancellationToken: cancellationToken))
                    {
                        hookApplied.Add($"  {line}");
                    }
                }
                else if (hook.Kind == "Pod")
                {
                    await foreach (var line in WaitForPodCompletionAsync(hook, ns, cancellationToken))
                        hookApplied.Add($"  {line}");
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                hookError = ex;
            }

            foreach (var line in hookApplied) yield return line;

            if (hookError is not null)
            {
                hook.LastRunCompletedAt = DateTimeOffset.UtcNow;
                hook.LastRunPhase = "Failed";
                yield return $"  Hook failed: {hookError.Message}";
                // Helm cleans previously successful hooks only after the whole hook
                // batch has either completed or a later hook has failed.
                var finalization = await CleanupSucceededHooksDuringFinalizationAsync(
                    executing.Take(hookIndex),
                    releaseNamespace,
                    failedHook: hook);
                foreach (var line in finalization.Lines)
                    yield return line;

                AttachCleanupErrors(hookError, finalization.Errors);
                throw hookError;
            }

            hook.LastRunCompletedAt = DateTimeOffset.UtcNow;
            hook.LastRunPhase = "Succeeded";
            Exception? boundaryError = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                boundaryError = ex;
            }
            if (boundaryError is not null)
            {
                var finalization = await CleanupSucceededHooksDuringFinalizationAsync(
                    executing.Take(hookIndex + 1),
                    releaseNamespace);
                AttachCleanupErrors(boundaryError, finalization.Errors);
                throw boundaryError;
            }
        }

        // Match Helm's batch contract: successful hooks remain available to later
        // hooks and are cleaned in reverse execution order only after all succeed.
        var finalizedHooks = new HashSet<HelmHook>(ReferenceEqualityComparer.Instance);
        for (var hookIndex = executing.Count - 1; hookIndex >= 0; hookIndex--)
        {
            var hook = executing[hookIndex];
            if (!hook.DeletePolicies.Contains(HelmHookDeletePolicy.HookSucceeded))
                continue;
            var ns = string.IsNullOrWhiteSpace(hook.Namespace) ? releaseNamespace : hook.Namespace;
            bool deleted = false;
            Exception? cleanupError = null;
            try
            {
                deleted = await DeleteHookResourceAsync(
                    hook,
                    ns,
                    _timeoutSeconds,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                cleanupError = ex;
            }
            if (cleanupError is not null)
            {
                var finalization = await CleanupSucceededHooksDuringFinalizationAsync(
                    executing,
                    releaseNamespace,
                    finalizedHooks);
                AttachCleanupErrors(cleanupError, finalization.Errors);
                throw cleanupError;
            }
            finalizedHooks.Add(hook);
            if (deleted)
                yield return $"  Deleted hook (succeeded policy): {hook.Kind}/{hook.Name}";
            Exception? boundaryError = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                boundaryError = ex;
            }
            if (boundaryError is not null)
            {
                var finalization = await CleanupSucceededHooksDuringFinalizationAsync(
                    executing,
                    releaseNamespace,
                    finalizedHooks);
                AttachCleanupErrors(boundaryError, finalization.Errors);
                throw boundaryError;
            }
        }
    }

    private async Task<(List<string> Lines, List<Exception> Errors)> CleanupSucceededHooksDuringFinalizationAsync(
        IEnumerable<HelmHook> hooks,
        string releaseNamespace,
        IReadOnlySet<HelmHook>? finalizedHooks = null,
        HelmHook? failedHook = null)
    {
        var lines = new List<string>();
        var errors = new List<Exception>();
        var cleanupSeconds = Math.Clamp(_timeoutSeconds, 2, 10);
        using var cleanupSource = new CancellationTokenSource(TimeSpan.FromSeconds(cleanupSeconds));

        if (failedHook?.DeletePolicies.Contains(HelmHookDeletePolicy.HookFailed) == true)
        {
            var failedNs = string.IsNullOrWhiteSpace(failedHook.Namespace)
                ? releaseNamespace
                : failedHook.Namespace;
            var cleanup = await TryDeleteHookResourceAsync(
                failedHook,
                failedNs,
                cleanupSeconds,
                cleanupSource.Token);
            if (cleanup.Error is not null)
            {
                errors.Add(cleanup.Error);
                lines.Add($"  Hook cleanup failed for {failedHook.Kind}/{failedHook.Name}: {cleanup.Error.Message}");
            }
            else if (cleanup.Deleted)
            {
                lines.Add($"  Deleted failed hook: {failedHook.Kind}/{failedHook.Name}");
            }
        }

        foreach (var hook in hooks.Reverse())
        {
            if (!string.Equals(hook.LastRunPhase, "Succeeded", StringComparison.Ordinal) ||
                !hook.DeletePolicies.Contains(HelmHookDeletePolicy.HookSucceeded) ||
                finalizedHooks?.Contains(hook) == true)
                continue;
            var ns = string.IsNullOrWhiteSpace(hook.Namespace) ? releaseNamespace : hook.Namespace;
            var cleanup = await TryDeleteHookResourceAsync(
                hook,
                ns,
                cleanupSeconds,
                cleanupSource.Token);
            if (cleanup.Error is not null)
            {
                errors.Add(cleanup.Error);
                lines.Add($"  Hook cleanup failed for {hook.Kind}/{hook.Name}: {cleanup.Error.Message}");
                continue;
            }
            if (cleanup.Deleted)
                lines.Add($"  Deleted previously succeeded hook: {hook.Kind}/{hook.Name}");
        }
        return (lines, errors);
    }

    private static void AttachCleanupErrors(Exception hookError, List<Exception> cleanupErrors)
    {
        if (cleanupErrors.Count == 1)
            hookError.Data[CleanupErrorDataKey] = cleanupErrors[0];
        else if (cleanupErrors.Count > 1)
            hookError.Data[CleanupErrorDataKey] = new AggregateException(cleanupErrors);
    }

    private async IAsyncEnumerable<string> WaitForPodCompletionAsync(
        HelmHook hook,
        string ns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_timeoutSeconds);
        yield return $"Waiting for Pod/{hook.Name} to complete...";

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            V1Pod pod = await _client.CoreV1.ReadNamespacedPodAsync(hook.Name, ns, cancellationToken: cancellationToken);
            switch (pod.Status?.Phase)
            {
                case "Succeeded":
                    yield return $"Pod/{hook.Name} completed";
                    yield break;
                case "Failed":
                    throw new InvalidOperationException($"Pod hook failed: {pod.Status.Reason ?? "unknown"}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException($"Timed out after {_timeoutSeconds}s waiting for Pod/{hook.Name} to complete.");
    }

    private async Task<(bool Deleted, Exception? Error)> TryDeleteHookResourceAsync(
        HelmHook hook,
        string ns,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await DeleteHookResourceAsync(hook, ns, timeoutSeconds, cancellationToken);
            return (deleted, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private async Task<bool> DeleteHookResourceAsync(
        HelmHook hook,
        string ns,
        int timeoutSeconds,
        CancellationToken ct)
    {
        // Helm never deletes CRD hooks by policy because that could cascade-delete
        // every custom resource instance owned by the definition.
        if (string.Equals(hook.Kind, "CustomResourceDefinition", StringComparison.Ordinal))
            return false;

        var applier = new KubernetesManifestApplier(_client, _fieldManager);
        await foreach (var _ in applier.DeleteAsync(hook.Manifest, ns, cancellationToken: ct))
        {
            // Drain the async enumerable.
        }

        var waiter = new KubernetesResourceWaiter(_client, timeoutSeconds);
        await foreach (var _ in waiter.WaitForDeletedAsync(hook.Manifest, ns, ct))
        {
            // Wait until the old hook object is actually absent before recreating it.
        }
        return true;
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
