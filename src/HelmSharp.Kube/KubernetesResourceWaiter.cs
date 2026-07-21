using System.Runtime.CompilerServices;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace HelmSharp.Kube;

/// <summary>
/// Waits for Kubernetes resources to reach a ready state, matching Helm's --wait behavior.
/// Supports Deployments, StatefulSets, DaemonSets, Jobs, Pods, PVCs, and more.
/// </summary>
public sealed class KubernetesResourceWaiter
{
    private readonly k8s.Kubernetes _client;
    private readonly int _timeoutSeconds;

    public KubernetesResourceWaiter(k8s.Kubernetes client, int timeoutSeconds = 300)
    {
        _client = client;
        _timeoutSeconds = timeoutSeconds;
    }

    /// <summary>
    /// Waits for all resources in the manifest to become ready.
    /// </summary>
    public async IAsyncEnumerable<string> WaitForReadyAsync(
        string manifest,
        string defaultNamespace,
        bool waitForJobs = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_timeoutSeconds);
        var identities = new List<ManifestIdentity>();

        foreach (var doc in KubernetesManifestApplier.SplitDocumentsPublic(manifest))
        {
            var identity = ManifestIdentity.Parse(doc, defaultNamespace);
            if (identity is not null)
                identities.Add(identity);
        }

        var waitable = identities
            .Where(id => IsWaitableKind(id.Kind))
            .ToList();

        if (waitable.Count == 0)
        {
            yield return "No waitable resources found";
            yield break;
        }

        yield return $"Waiting for {waitable.Count} resources to become ready...";

        var pending = new HashSet<string>(waitable.Select(id => id.DisplayName));
        var failed = new HashSet<string>();
        var pollInterval = TimeSpan.FromSeconds(3);
        var consecutiveErrors = 0;

        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var newlyReady = new List<string>();
            var newlyFailed = new List<string>();

            foreach (var id in waitable.Where(id => pending.Contains(id.DisplayName)))
            {
                var ns = string.IsNullOrWhiteSpace(id.Namespace) ? defaultNamespace : id.Namespace;
                (bool Ready, bool Failed, string Status) result;
                try
                {
                    result = await CheckResourceStatusAsync(id, ns, waitForJobs, cancellationToken);
                }
                catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
                {
                    consecutiveErrors = 0;
                    continue;
                }
                catch (Exception)
                {
                    consecutiveErrors++;
                    if (consecutiveErrors > 10) throw;
                    continue;
                }

                if (result.Ready)
                {
                    newlyReady.Add(id.DisplayName);
                    yield return $"  {id.DisplayName} is ready";
                }
                else if (result.Failed)
                {
                    newlyFailed.Add(id.DisplayName);
                    yield return $"  {id.DisplayName} failed: {result.Status}";
                }
                consecutiveErrors = 0;
            }

            foreach (var r in newlyReady)
                pending.Remove(r);
            foreach (var r in newlyFailed)
            {
                pending.Remove(r);
                failed.Add(r);
            }

            // Report progress
            if (pending.Count > 0)
            {
                var done = waitable.Count - pending.Count;
                var pct = (int)(done * 100.0 / waitable.Count);
                yield return $"  Progress: {done}/{waitable.Count} ({pct}%) - waiting for: {string.Join(", ", pending.Take(3))}{(pending.Count > 3 ? "..." : "")}";
            }

            if (pending.Count > 0)
            {
                // Adaptive poll interval: increase on errors, decrease on progress
                var interval = consecutiveErrors > 0
                    ? TimeSpan.FromSeconds(Math.Min(30, 3 * consecutiveErrors))
                    : pollInterval;
                await Task.Delay(interval, cancellationToken);
            }
        }

        if (failed.Count > 0)
        {
            throw new InvalidOperationException($"Resources failed: {string.Join(", ", failed)}");
        }

        if (pending.Count > 0)
        {
            var timeoutMsg = $"Timed out after {_timeoutSeconds}s waiting for: {string.Join(", ", pending)}";
            throw new TimeoutException(timeoutMsg);
        }

        yield return $"All {waitable.Count} resources are ready";
    }

    /// <summary>Waits for resources targeted by the manifest applier to disappear after deletion.</summary>
    public async IAsyncEnumerable<string> WaitForDeletedAsync(
        string manifest,
        string defaultNamespace,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var allIdentities = KubernetesManifestApplier.SplitDocumentsPublic(manifest)
            .Select(doc => ManifestIdentity.Parse(doc, defaultNamespace))
            .Where(identity => identity is not null)
            .Cast<ManifestIdentity>()
            .ToList();
        var identities = allIdentities.Where(IsDeletionWaitable).ToList();
        if (identities.Count == 0)
            yield break;

        var deadline = DateTime.UtcNow.AddSeconds(_timeoutSeconds);
        var pending = new HashSet<string>(identities.Select(identity => identity.DisplayName));
        yield return $"Waiting for {pending.Count} resources to be deleted...";
        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = new List<string>();
            foreach (var identity in identities.Where(identity => pending.Contains(identity.DisplayName)))
            {
                try
                {
                    await ReadForDeletionAsync(identity, cancellationToken);
                }
                catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
                {
                    pending.Remove(identity.DisplayName);
                    deleted.Add(identity.DisplayName);
                }
            }
            foreach (var displayName in deleted)
                yield return $"  {displayName} deleted";
            if (pending.Count > 0)
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        if (pending.Count > 0)
            throw new TimeoutException($"Timed out after {_timeoutSeconds}s waiting for deletion of: {string.Join(", ", pending)}");
    }

    private async Task ReadForDeletionAsync(ManifestIdentity identity, CancellationToken ct)
    {
        switch (identity.ApiVersion, identity.Kind)
        {
            case ("v1", "ConfigMap"): _ = await _client.CoreV1.ReadNamespacedConfigMapAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("v1", "Secret"): _ = await _client.CoreV1.ReadNamespacedSecretAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("v1", "Service"): _ = await _client.CoreV1.ReadNamespacedServiceAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("v1", "ServiceAccount"): _ = await _client.CoreV1.ReadNamespacedServiceAccountAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("v1", "PersistentVolumeClaim"): _ = await _client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("v1", "Pod"): _ = await _client.CoreV1.ReadNamespacedPodAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("apps/v1", "Deployment"): _ = await _client.AppsV1.ReadNamespacedDeploymentAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("apps/v1", "StatefulSet"): _ = await _client.AppsV1.ReadNamespacedStatefulSetAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("apps/v1", "DaemonSet"): _ = await _client.AppsV1.ReadNamespacedDaemonSetAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("apps/v1", "ReplicaSet"): _ = await _client.AppsV1.ReadNamespacedReplicaSetAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("batch/v1", "Job"): _ = await _client.BatchV1.ReadNamespacedJobAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("batch/v1", "CronJob"): _ = await _client.BatchV1.ReadNamespacedCronJobAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("v1", "PersistentVolume"): _ = await _client.CoreV1.ReadPersistentVolumeAsync(identity.Name, cancellationToken: ct); break;
            case ("v1", "LimitRange"): _ = await _client.CoreV1.ReadNamespacedLimitRangeAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("v1", "ResourceQuota"): _ = await _client.CoreV1.ReadNamespacedResourceQuotaAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("v1", "Endpoints"): _ = await _client.CoreV1.ReadNamespacedEndpointsAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("v1", "ReplicationController"): _ = await _client.CoreV1.ReadNamespacedReplicationControllerAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("networking.k8s.io/v1", "Ingress"): _ = await _client.NetworkingV1.ReadNamespacedIngressAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("networking.k8s.io/v1", "NetworkPolicy"): _ = await _client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("networking.k8s.io/v1", "IngressClass"): _ = await _client.NetworkingV1.ReadIngressClassAsync(identity.Name, cancellationToken: ct); break;
            case ("rbac.authorization.k8s.io/v1", "Role"): _ = await _client.RbacAuthorizationV1.ReadNamespacedRoleAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("rbac.authorization.k8s.io/v1", "RoleBinding"): _ = await _client.RbacAuthorizationV1.ReadNamespacedRoleBindingAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("rbac.authorization.k8s.io/v1", "ClusterRole"): _ = await _client.RbacAuthorizationV1.ReadClusterRoleAsync(identity.Name, cancellationToken: ct); break;
            case ("rbac.authorization.k8s.io/v1", "ClusterRoleBinding"): _ = await _client.RbacAuthorizationV1.ReadClusterRoleBindingAsync(identity.Name, cancellationToken: ct); break;
            case ("autoscaling/v2", "HorizontalPodAutoscaler"):
            case ("autoscaling/v2beta2", "HorizontalPodAutoscaler"): _ = await _client.AutoscalingV2.ReadNamespacedHorizontalPodAutoscalerAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("autoscaling/v1", "HorizontalPodAutoscaler"): _ = await _client.AutoscalingV1.ReadNamespacedHorizontalPodAutoscalerAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("policy/v1", "PodDisruptionBudget"): _ = await _client.PolicyV1.ReadNamespacedPodDisruptionBudgetAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("storage.k8s.io/v1", "StorageClass"): _ = await _client.StorageV1.ReadStorageClassAsync(identity.Name, cancellationToken: ct); break;
            case ("storage.k8s.io/v1", "CSIDriver"): _ = await _client.StorageV1.ReadCSIDriverAsync(identity.Name, cancellationToken: ct); break;
            case ("storage.k8s.io/v1", "CSINode"): _ = await _client.StorageV1.ReadCSINodeAsync(identity.Name, cancellationToken: ct); break;
            case ("storage.k8s.io/v1", "VolumeAttachment"): _ = await _client.StorageV1.ReadVolumeAttachmentAsync(identity.Name, cancellationToken: ct); break;
            case ("scheduling.k8s.io/v1", "PriorityClass"): _ = await _client.SchedulingV1.ReadPriorityClassAsync(identity.Name, cancellationToken: ct); break;
            case ("apiextensions.k8s.io/v1", "CustomResourceDefinition"): _ = await _client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(identity.Name, cancellationToken: ct); break;
            case ("admissionregistration.k8s.io/v1", "MutatingWebhookConfiguration"): _ = await _client.AdmissionregistrationV1.ReadMutatingWebhookConfigurationAsync(identity.Name, cancellationToken: ct); break;
            case ("admissionregistration.k8s.io/v1", "ValidatingWebhookConfiguration"): _ = await _client.AdmissionregistrationV1.ReadValidatingWebhookConfigurationAsync(identity.Name, cancellationToken: ct); break;
            case ("apiregistration.k8s.io/v1", "APIService"): _ = await _client.ApiregistrationV1.ReadAPIServiceAsync(identity.Name, cancellationToken: ct); break;
            case ("coordination.k8s.io/v1", "Lease"): _ = await _client.CoordinationV1.ReadNamespacedLeaseAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("node.k8s.io/v1", "RuntimeClass"): _ = await _client.NodeV1.ReadRuntimeClassAsync(identity.Name, cancellationToken: ct); break;
            case ("discovery.k8s.io/v1", "EndpointSlice"): _ = await _client.DiscoveryV1.ReadNamespacedEndpointSliceAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
            case ("flowcontrol.apiserver.k8s.io/v1", "FlowSchema"): _ = await _client.FlowcontrolApiserverV1.ReadFlowSchemaAsync(identity.Name, cancellationToken: ct); break;
            case ("flowcontrol.apiserver.k8s.io/v1", "PriorityLevelConfiguration"): _ = await _client.FlowcontrolApiserverV1.ReadPriorityLevelConfigurationAsync(identity.Name, cancellationToken: ct); break;
            default: return;
        }
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckResourceStatusAsync(
        ManifestIdentity identity,
        string ns,
        bool waitForJobs,
        CancellationToken ct)
    {
        return (identity.ApiVersion, identity.Kind) switch
        {
            ("apps/v1", "Deployment") => await CheckDeploymentAsync(identity.Name, ns, ct),
            ("apps/v1", "StatefulSet") => await CheckStatefulSetAsync(identity.Name, ns, ct),
            ("apps/v1", "DaemonSet") => await CheckDaemonSetAsync(identity.Name, ns, ct),
            ("apps/v1", "ReplicaSet") => await CheckReplicaSetAsync(identity.Name, ns, ct),
            ("batch/v1", "Job") => waitForJobs
                ? await CheckJobAsync(identity.Name, ns, ct)
                : (true, false, ""),
            ("batch/v1", "CronJob") => (true, false, ""),
            ("v1", "Pod") => await CheckPodAsync(identity.Name, ns, ct),
            ("v1", "PersistentVolumeClaim") => await CheckPvcAsync(identity.Name, ns, ct),
            ("v1", "Service") => (true, false, ""),
            ("v1", "ConfigMap") => (true, false, ""),
            ("v1", "Secret") => (true, false, ""),
            ("v1", "ServiceAccount") => (true, false, ""),
            ("v1", "Endpoints") => await CheckEndpointsAsync(identity.Name, ns, ct),
            ("networking.k8s.io/v1", "Ingress") => (true, false, ""),
            ("autoscaling/v2", "HorizontalPodAutoscaler") => await CheckHpaAsync(identity.Name, ns, ct),
            ("autoscaling/v1", "HorizontalPodAutoscaler") => (true, false, ""),
            _ => (true, false, "")
        };
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckDeploymentAsync(
        string name, string ns, CancellationToken ct)
    {
        var deploy = await _client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct);
        var desired = deploy.Spec.Replicas ?? 1;
        var ready = deploy.Status?.ReadyReplicas ?? 0;
        var updated = deploy.Status?.UpdatedReplicas ?? 0;
        var available = deploy.Status?.AvailableReplicas ?? 0;

        if (ready >= desired && updated >= desired && available >= desired)
            return (true, false, "");

        // Check for failure conditions
        var conditions = deploy.Status?.Conditions;
        if (conditions is not null)
        {
            var progressing = conditions.FirstOrDefault(c => c.Type == "Progressing");
            if (progressing?.Status == "False" && progressing.Reason == "ProgressDeadlineExceeded")
                return (false, true, $"Deployment exceeded progress deadline");
        }

        return (false, false, $"{ready}/{desired} replicas ready");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckStatefulSetAsync(
        string name, string ns, CancellationToken ct)
    {
        var sts = await _client.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: ct);
        var desired = sts.Spec.Replicas ?? 1;
        var ready = sts.Status?.ReadyReplicas ?? 0;
        var updated = sts.Status?.UpdatedReplicas ?? 0;

        if (ready >= desired && updated >= desired)
            return (true, false, "");

        return (false, false, $"{ready}/{desired} replicas ready");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckDaemonSetAsync(
        string name, string ns, CancellationToken ct)
    {
        var ds = await _client.AppsV1.ReadNamespacedDaemonSetAsync(name, ns, cancellationToken: ct);
        var desired = ds.Status?.DesiredNumberScheduled ?? 0;
        var ready = ds.Status?.NumberReady ?? 0;
        var updated = ds.Status?.UpdatedNumberScheduled ?? 0;

        if (ready >= desired && updated >= desired)
            return (true, false, "");

        return (false, false, $"{ready}/{desired} pods ready");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckReplicaSetAsync(
        string name, string ns, CancellationToken ct)
    {
        var rs = await _client.AppsV1.ReadNamespacedReplicaSetAsync(name, ns, cancellationToken: ct);
        var desired = rs.Spec.Replicas ?? 1;
        var ready = rs.Status?.ReadyReplicas ?? 0;

        if (ready >= desired)
            return (true, false, "");

        return (false, false, $"{ready}/{desired} replicas ready");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckJobAsync(
        string name, string ns, CancellationToken ct)
    {
        var job = await _client.BatchV1.ReadNamespacedJobAsync(name, ns, cancellationToken: ct);
        var conditions = job.Status?.Conditions;

        if (conditions is not null)
        {
            if (conditions.Any(c => c.Type == "Complete" && c.Status == "True"))
                return (true, false, "Job completed");
            if (conditions.Any(c => c.Type == "Failed" && c.Status == "True"))
            {
                var failed = conditions.First(c => c.Type == "Failed");
                return (false, true, $"Job failed: {failed.Reason ?? "unknown"}");
            }
        }

        // Check for too many failures
        var failures = job.Status?.Failed ?? 0;
        var backoffLimit = job.Spec?.BackoffLimit ?? 6;
        if (failures >= backoffLimit)
            return (false, true, $"Job exceeded backoff limit ({failures}/{backoffLimit})");

        return (false, false, $"Job in progress (completions: {job.Status?.Succeeded ?? 0}/{job.Spec?.Completions ?? 1})");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckPodAsync(
        string name, string ns, CancellationToken ct)
    {
        var pod = await _client.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: ct);
        var phase = pod.Status?.Phase;

        if (phase == "Succeeded" || phase == "Running")
        {
            var conditions = pod.Status?.Conditions;
            var readyCondition = conditions?.FirstOrDefault(c => c.Type == "Ready");
            if (readyCondition?.Status == "True")
                return (true, false, "");
        }

        if (phase == "Failed")
            return (false, true, $"Pod failed: {pod.Status?.Reason ?? "unknown"}");

        // Check container statuses for crash loops
        var containerStatuses = pod.Status?.ContainerStatuses;
        if (containerStatuses is not null)
        {
            var waiting = containerStatuses.FirstOrDefault(c => c.State?.Waiting?.Reason == "CrashLoopBackOff");
            if (waiting is not null)
                return (false, true, "Pod in CrashLoopBackOff");
        }

        return (false, false, $"Pod phase: {phase ?? "Pending"}");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckPvcAsync(
        string name, string ns, CancellationToken ct)
    {
        var pvc = await _client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(name, ns, cancellationToken: ct);
        if (pvc.Status?.Phase == "Bound")
            return (true, false, "");

        if (pvc.Status?.Phase == "Lost")
            return (false, true, "PVC lost");

        return (false, false, $"PVC phase: {pvc.Status?.Phase ?? "Pending"}");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckEndpointsAsync(
        string name, string ns, CancellationToken ct)
    {
        var endpoints = await _client.CoreV1.ReadNamespacedEndpointsAsync(name, ns, cancellationToken: ct);
        var subsets = endpoints.Subsets;
        if (subsets is null || subsets.Count == 0)
            return (false, false, "No endpoints available");

        var hasReady = subsets.Any(s => s.Addresses is not null && s.Addresses.Count > 0);
        if (hasReady)
            return (true, false, "");

        return (false, false, "No ready addresses");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckHpaAsync(
        string name, string ns, CancellationToken ct)
    {
        var hpa = await _client.AutoscalingV2.ReadNamespacedHorizontalPodAutoscalerAsync(name, ns, cancellationToken: ct);
        var conditions = hpa.Status?.Conditions;
        if (conditions is null)
            return (false, false, "HPA not yet active");

        var scalingActive = conditions.FirstOrDefault(c => c.Type == "ScalingActive");
        if (scalingActive?.Status == "True")
            return (true, false, "");

        if (scalingActive?.Status == "False")
            return (false, true, $"HPA scaling not active: {scalingActive.Reason ?? "unknown"}");

        return (false, false, "HPA initializing");
    }

    private static bool IsWaitableKind(string kind)
        => kind is "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet"
            or "Job" or "Pod" or "PersistentVolumeClaim" or "Endpoints"
            or "HorizontalPodAutoscaler";

    private static bool IsDeletionWaitable(ManifestIdentity identity)
        => (identity.ApiVersion, identity.Kind) is
            ("v1", "ConfigMap") or ("v1", "Secret") or ("v1", "Service") or ("v1", "ServiceAccount")
            or ("v1", "PersistentVolumeClaim") or ("v1", "PersistentVolume") or ("v1", "LimitRange")
            or ("v1", "ResourceQuota") or ("v1", "Pod") or ("v1", "Endpoints") or ("v1", "ReplicationController")
            or ("apps/v1", "Deployment") or ("apps/v1", "StatefulSet") or ("apps/v1", "DaemonSet") or ("apps/v1", "ReplicaSet")
            or ("batch/v1", "Job") or ("batch/v1", "CronJob")
            or ("networking.k8s.io/v1", "Ingress") or ("networking.k8s.io/v1", "NetworkPolicy") or ("networking.k8s.io/v1", "IngressClass")
            or ("rbac.authorization.k8s.io/v1", "Role") or ("rbac.authorization.k8s.io/v1", "RoleBinding")
            or ("rbac.authorization.k8s.io/v1", "ClusterRole") or ("rbac.authorization.k8s.io/v1", "ClusterRoleBinding")
            or ("autoscaling/v2", "HorizontalPodAutoscaler") or ("autoscaling/v2beta2", "HorizontalPodAutoscaler") or ("autoscaling/v1", "HorizontalPodAutoscaler")
            or ("policy/v1", "PodDisruptionBudget")
            or ("storage.k8s.io/v1", "StorageClass") or ("storage.k8s.io/v1", "CSIDriver") or ("storage.k8s.io/v1", "CSINode") or ("storage.k8s.io/v1", "VolumeAttachment")
            or ("scheduling.k8s.io/v1", "PriorityClass") or ("apiextensions.k8s.io/v1", "CustomResourceDefinition")
            or ("admissionregistration.k8s.io/v1", "MutatingWebhookConfiguration") or ("admissionregistration.k8s.io/v1", "ValidatingWebhookConfiguration")
            or ("apiregistration.k8s.io/v1", "APIService") or ("coordination.k8s.io/v1", "Lease")
            or ("node.k8s.io/v1", "RuntimeClass") or ("discovery.k8s.io/v1", "EndpointSlice")
            or ("flowcontrol.apiserver.k8s.io/v1", "FlowSchema") or ("flowcontrol.apiserver.k8s.io/v1", "PriorityLevelConfiguration");

}
