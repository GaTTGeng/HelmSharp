using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace HelmSharp.Kube;

/// <summary>
/// Waits for Kubernetes resources to reach a ready state, matching Helm's --wait behavior.
/// Supports Deployments, StatefulSets, DaemonSets, Jobs, Pods, PVCs, Services, CRDs, and more.
/// </summary>
public sealed class KubernetesResourceWaiter
{
    private readonly k8s.Kubernetes _client;
    private readonly int _timeoutSeconds;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Dictionary<(string ApiVersion, string Kind), DeletionResource> _deletionResources = new();

    /// <param name="client">Kubernetes client used for readiness reads.</param>
    /// <param name="timeoutSeconds">Maximum wait duration. Must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the timeout is zero or negative.</exception>
    public KubernetesResourceWaiter(k8s.Kubernetes client, int timeoutSeconds = 300)
        : this(client, timeoutSeconds, TimeProvider.System, Task.Delay)
    {
    }

    internal KubernetesResourceWaiter(
        k8s.Kubernetes client,
        int timeoutSeconds,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutSeconds);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(delayAsync);
        _client = client;
        _timeoutSeconds = timeoutSeconds;
        _timeProvider = timeProvider;
        _delayAsync = delayAsync;
    }

    /// <summary>
    /// Waits for all resources in the manifest to become ready.
    /// </summary>
    /// <param name="manifest">Multi-document YAML manifest whose waitable kinds are polled.</param>
    /// <param name="defaultNamespace">Namespace used when a document omits <c>metadata.namespace</c>.</param>
    /// <param name="waitForJobs">When true, Jobs must reach Complete; otherwise Jobs are treated as ready immediately.</param>
    /// <param name="cancellationToken">Cancels polling.</param>
    /// <returns>Progress lines as resources become ready.</returns>
    /// <exception cref="TimeoutException">Thrown when resources are still pending at the deadline; the message lists each resource's last observed status.</exception>
    /// <exception cref="InvalidOperationException">Thrown when any resource reaches a terminal failed state.</exception>
    public async IAsyncEnumerable<string> WaitForReadyAsync(
        string manifest,
        string defaultNamespace,
        bool waitForJobs = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var deadline = _timeProvider.GetUtcNow().AddSeconds(_timeoutSeconds);
        var identities = new List<ManifestIdentity>();

        // Stage 1: parse manifest documents into identities (unparseable docs are skipped).
        foreach (var doc in KubernetesManifestApplier.SplitDocumentsPublic(manifest))
        {
            var identity = ManifestIdentity.Parse(doc, defaultNamespace);
            if (identity is not null)
                identities.Add(identity);
        }

        // Stage 2: keep only waitable kinds; inert config objects never block --wait.
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
        var lastStatuses = new Dictionary<string, string>();
        // Poll strategy: fixed 3s cadence matching Helm's --wait loop; transient API
        // read errors are tolerated and back off up to 30s, but a run of more than 10
        // consecutive failures aborts instead of burning the whole timeout on a dead API.
        var pollInterval = TimeSpan.FromSeconds(3);
        var consecutiveErrors = 0;

        // Stage 3: poll every still-pending resource until ready, failed, or the deadline.
        while (pending.Count > 0 && _timeProvider.GetUtcNow() < deadline)
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
                    // Not found yet — the apply may still be creating the object.
                    consecutiveErrors = 0;
                    lastStatuses[id.DisplayName] = "Resource not found yet";
                    continue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    lastStatuses[id.DisplayName] = "Kubernetes API read failed";
                    if (consecutiveErrors > 10)
                        throw new KubernetesResourceOperationException(id, ex);
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
                else
                {
                    lastStatuses[id.DisplayName] = result.Status;
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
                await _delayAsync(interval, cancellationToken);
            }
        }

        // Stage 4: settle — terminal failures are reported first; otherwise any
        // resource still pending at the deadline becomes a timeout with diagnostics.
        if (failed.Count > 0)
        {
            throw new InvalidOperationException($"Resources failed: {string.Join(", ", failed)}");
        }

        if (pending.Count > 0)
        {
            // Timeout diagnostics: attach each pending resource's last observed status
            // so the message shows where the rollout stalled, not just what is left.
            var timeoutMsg = $"Timed out after {_timeoutSeconds}s waiting for: {string.Join(", ", pending)}";
            var statuses = pending
                .Where(lastStatuses.ContainsKey)
                .Select(resource => $"{resource}: {lastStatuses[resource]}")
                .ToList();
            if (statuses.Count > 0)
                timeoutMsg += $" (last observed status: {string.Join("; ", statuses)})";
            throw new TimeoutException(timeoutMsg);
        }

        yield return $"All {waitable.Count} resources are ready";
    }

    /// <summary>
    /// Waits for all resources targeted by the manifest applier to disappear after deletion.
    /// </summary>
    /// <param name="manifest">Multi-document YAML manifest of the deleted release.</param>
    /// <param name="defaultNamespace">Namespace used when a document omits <c>metadata.namespace</c>.</param>
    /// <param name="cancellationToken">Cancels polling.</param>
    /// <returns>Progress lines as resources disappear.</returns>
    /// <exception cref="TimeoutException">Thrown when resources still exist at the deadline.</exception>
    public async IAsyncEnumerable<string> WaitForDeletedAsync(
        string manifest,
        string defaultNamespace,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var identities = KubernetesManifestApplier.SplitDocumentsPublic(manifest)
            .Select(doc => ManifestIdentity.Parse(doc, defaultNamespace))
            .Where(identity => identity is not null)
            .Cast<ManifestIdentity>()
            .Select(identity => new DeletionWaitState(identity))
            .ToList();
        if (identities.Count == 0)
            yield break;

        var deadline = _timeProvider.GetUtcNow().AddSeconds(_timeoutSeconds);
        var pending = identities.ToList();
        yield return $"Waiting for {pending.Count} resources to be deleted...";
        while (pending.Count > 0 && _timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = new List<string>();
            foreach (var state in pending.ToList())
            {
                try
                {
                    await ResolveDeletionScopeAsync(state, cancellationToken);
                    await ReadForDeletionAsync(state.Identity, state.Resource, cancellationToken);
                }
                catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
                {
                    pending.Remove(state);
                    deleted.Add(state.Identity.DisplayName);
                }
                catch (Exception ex) when (ex is KubernetesApiResourceNotFoundException or
                                           KubernetesApiResourceUnsupportedException)
                {
                    // The kind itself is gone (CRD deleted, or a kind this client
                    // cannot address) — nothing left to wait for, so count as deleted.
                    pending.Remove(state);
                    deleted.Add(state.Identity.DisplayName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new KubernetesResourceOperationException(state.Identity, ex);
                }
            }
            foreach (var displayName in deleted)
                yield return $"  {displayName} deleted";
            if (pending.Count > 0)
                await _delayAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }
        if (pending.Count > 0)
            throw new TimeoutException(
                $"Timed out after {_timeoutSeconds}s waiting for deletion of: " +
                string.Join(", ", pending.Select(state => state.Identity.DisplayName)));
    }

    private async Task ResolveDeletionScopeAsync(DeletionWaitState state, CancellationToken ct)
    {
        if (state.ScopeResolved)
            return;
        // Prefer the static typed scope table (no discovery round-trip); discovery is
        // only needed for CRs and other unknown kinds. Cluster-scoped kinds drop the
        // namespace so the subsequent existence read targets the cluster scope.
        if (KubernetesManifestApplier.TryGetTypedResourceScope(state.Identity, out _))
        {
            state.Identity = KubernetesManifestApplier.NormalizeTypedIdentity(state.Identity);
            state.ScopeResolved = true;
            return;
        }

        state.Resource = await DiscoverDeletionResourceAsync(state.Identity, ct);
        var resolvedApiVersion = $"{state.Resource.Group}/{state.Resource.Version}";
        state.Identity = state.Resource.Namespaced
            ? state.Identity with { ApiVersion = resolvedApiVersion }
            : state.Identity with { ApiVersion = resolvedApiVersion, Namespace = string.Empty };
        state.ScopeResolved = true;
    }

    private async Task ReadForDeletionAsync(
        ManifestIdentity identity,
        DeletionResource? discoveredResource,
        CancellationToken ct)
    {
        switch (identity.ApiVersion, identity.Kind)
        {
            case ("v1", "Namespace"): _ = await _client.CoreV1.ReadNamespaceAsync(identity.Name, cancellationToken: ct); break;
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
            case ("autoscaling/v2", "HorizontalPodAutoscaler"): _ = await _client.AutoscalingV2.ReadNamespacedHorizontalPodAutoscalerAsync(identity.Name, identity.Namespace, cancellationToken: ct); break;
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
            default:
                var resource = discoveredResource ?? await DiscoverDeletionResourceAsync(identity, ct);
                await ReadDiscoveredResourceAsync(identity, resource, ct);
                break;
        }
    }

    private async Task ReadDiscoveredResourceAsync(
        ManifestIdentity identity,
        DeletionResource resource,
        CancellationToken ct)
    {
        if (resource.Namespaced)
        {
            _ = await _client.CustomObjects.GetNamespacedCustomObjectAsync(
                resource.Group, resource.Version, identity.Namespace, resource.Plural, identity.Name, ct);
            return;
        }

        _ = await _client.CustomObjects.GetClusterCustomObjectAsync(
            resource.Group, resource.Version, resource.Plural, identity.Name, ct);
    }

    private async Task<DeletionResource> DiscoverDeletionResourceAsync(ManifestIdentity identity, CancellationToken ct)
    {
        var key = (identity.ApiVersion, identity.Kind);
        if (_deletionResources.TryGetValue(key, out var cached))
            return cached;

        var resource = await KubernetesManifestApplier.DiscoverResourceForDeletionAsync(_client, identity, ct);
        var discovered = new DeletionResource(
            resource.Group,
            resource.Version,
            resource.Plural,
            resource.Namespaced);
        _deletionResources.Add(key, discovered);
        return discovered;
    }

    private sealed class DeletionWaitState(ManifestIdentity identity)
    {
        internal ManifestIdentity Identity { get; set; } = identity;
        internal DeletionResource? Resource { get; set; }
        internal bool ScopeResolved { get; set; }
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckResourceStatusAsync(
        ManifestIdentity identity,
        string ns,
        bool waitForJobs,
        CancellationToken ct)
    {
        // Readiness dispatch. Kinds without a meaningful readiness condition
        // (ConfigMap, Secret, Ingress, CronJob, ...) report ready immediately;
        // only kinds Helm's ReadyChecker polls have real status predicates.
        return (identity.ApiVersion, identity.Kind) switch
        {
            ("apps/v1", "Deployment") => await CheckDeploymentAsync(identity.Name, ns, ct),
            ("apps/v1", "StatefulSet") => await CheckStatefulSetAsync(identity.Name, ns, ct),
            ("apps/v1", "DaemonSet") => await CheckDaemonSetAsync(identity.Name, ns, ct),
            ("apps/v1", "ReplicaSet") => await CheckReplicaSetAsync(identity.Name, ns, ct),
            ("batch/v1", "Job") => waitForJobs
                // Helm's default is not to wait for Jobs; the flag promotes Job
                // completion to a readiness requirement when requested.
                ? await CheckJobAsync(identity.Name, ns, ct)
                : (true, false, ""),
            ("batch/v1", "CronJob") => (true, false, ""),
            ("v1", "Pod") => await CheckPodAsync(identity.Name, ns, ct),
            ("v1", "ReplicationController") => await CheckReplicationControllerAsync(identity.Name, ns, ct),
            ("v1", "PersistentVolumeClaim") => await CheckPvcAsync(identity.Name, ns, ct),
            ("v1", "Service") => await CheckServiceAsync(identity.Name, ns, ct),
            ("v1", "ConfigMap") => (true, false, ""),
            ("v1", "Secret") => (true, false, ""),
            ("v1", "ServiceAccount") => (true, false, ""),
            ("v1", "Endpoints") => await CheckEndpointsAsync(identity.Name, ns, ct),
            ("networking.k8s.io/v1", "Ingress") => (true, false, ""),
            ("apiextensions.k8s.io/v1", "CustomResourceDefinition") => await CheckCrdAsync(identity.ApiVersion, identity.Name, ct),
            ("apiextensions.k8s.io/v1beta1", "CustomResourceDefinition") => await CheckCrdAsync(identity.ApiVersion, identity.Name, ct),
            ("autoscaling/v2", "HorizontalPodAutoscaler") => await CheckHpaAsync(identity.Name, ns, ct),
            ("autoscaling/v1", "HorizontalPodAutoscaler") => (true, false, ""),
            // Unknown kinds expose no readiness signal — treated as ready, matching Helm.
            _ => (true, false, "")
        };
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckDeploymentAsync(
        string name, string ns, CancellationToken ct)
    {
        // Deployment readiness mirrors Helm/kubectl: the newest ReplicaSet whose pod
        // template matches (ignoring the controller-added pod-template-hash label) must
        // have at least desired - maxUnavailable ready replicas. Paused deployments
        // are complete by definition, and a stale observedGeneration is not ready.
        var deploy = await _client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct);
        if (deploy.Spec.Paused == true)
            return (true, false, "");

        if ((deploy.Status?.ObservedGeneration ?? 0) != (deploy.Metadata.Generation ?? 0))
            return (false, false, "Deployment controller has not observed the current generation");

        var selector = BuildLabelSelector(deploy.Spec.Selector);
        var replicaSets = await _client.AppsV1.ListNamespacedReplicaSetAsync(
            ns,
            labelSelector: selector,
            cancellationToken: ct);
        var newReplicaSet = replicaSets.Items
            .Where(replicaSet => replicaSet.Metadata.OwnerReferences?.Any(owner =>
                owner.Uid == deploy.Metadata.Uid && owner.Kind == "Deployment" && owner.Controller == true) == true)
            .Where(replicaSet => PodTemplatesMatch(replicaSet.Spec.Template, deploy.Spec.Template))
            .OrderBy(replicaSet => replicaSet.Metadata.CreationTimestamp)
            .ThenBy(replicaSet => replicaSet.Metadata.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (newReplicaSet is null)
            return (false, false, "Deployment has no new ReplicaSet");

        if ((newReplicaSet.Status?.ObservedGeneration ?? 0) != (newReplicaSet.Metadata.Generation ?? 0))
            return (false, false, "Deployment ReplicaSet controller has not observed the current generation");

        var desired = deploy.Spec.Replicas ?? 1;
        var maxUnavailable = ResolveDeploymentMaxUnavailable(deploy, desired);
        var expectedReady = desired - maxUnavailable;
        var ready = newReplicaSet.Status?.ReadyReplicas ?? 0;
        if (ready >= expectedReady)
            return (true, false, "");

        return (false, false, $"{ready}/{desired} replicas ready");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckStatefulSetAsync(
        string name, string ns, CancellationToken ct)
    {
        // StatefulSet readiness: every replica ready, the rolled-out (partitioned)
        // portion updated, and — when partition is 0 — current and update revisions
        // equal so the roll is fully converged. Non-RollingUpdate strategies have no
        // roll to wait for.
        var sts = await _client.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: ct);
        if ((sts.Status?.ObservedGeneration ?? 0) != (sts.Metadata.Generation ?? 0))
            return (false, false, "StatefulSet controller has not observed the current generation");

        var updateStrategy = sts.Spec.UpdateStrategy;
        if (updateStrategy?.Type != "RollingUpdate")
            return (true, false, "");

        var desired = sts.Spec.Replicas ?? 1;
        var ready = sts.Status?.ReadyReplicas ?? 0;
        var updated = sts.Status?.UpdatedReplicas ?? 0;
        var partition = updateStrategy.RollingUpdate?.Partition ?? 0;
        var expectedUpdated = Math.Max(0, desired - partition);

        if (updated < expectedUpdated)
            return (false, false, $"{updated}/{expectedUpdated} updated replicas");

        if (ready != desired)
            return (false, false, $"{ready}/{desired} replicas ready");

        if (partition == 0 && sts.Status?.CurrentRevision != sts.Status?.UpdateRevision)
            return (false, false, "StatefulSet revisions do not match");

        if (ready == desired)
            return (true, false, "");

        return (false, false, $"{ready}/{desired} replicas ready");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckDaemonSetAsync(
        string name, string ns, CancellationToken ct)
    {
        // DaemonSet readiness: all scheduled nodes run the updated pod, and ready
        // count meets desired - maxUnavailable (default 1, matching the controller).
        var ds = await _client.AppsV1.ReadNamespacedDaemonSetAsync(name, ns, cancellationToken: ct);
        if ((ds.Status?.ObservedGeneration ?? 0) != (ds.Metadata.Generation ?? 0))
            return (false, false, "DaemonSet controller has not observed the current generation");

        var updateStrategy = ds.Spec.UpdateStrategy;
        if (updateStrategy?.Type != "RollingUpdate")
            return (true, false, "");

        var desired = ds.Status?.DesiredNumberScheduled ?? 0;
        var updated = ds.Status?.UpdatedNumberScheduled ?? 0;
        if (updated != desired)
            return (false, false, $"{updated}/{desired} updated pods scheduled");

        var defaultUnavailable = desired == 0 ? 0 : 1;
        var maxUnavailable = ResolveScaledValue(
            updateStrategy.RollingUpdate?.MaxUnavailable,
            desired,
            roundUp: true,
            defaultValue: defaultUnavailable,
            invalidValueFallback: desired);
        var expectedAvailable = desired - maxUnavailable;
        var ready = ds.Status?.NumberReady ?? 0;

        if (ready >= expectedAvailable)
            return (true, false, "");

        return (false, false, $"{ready}/{expectedAvailable} pods ready");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckReplicaSetAsync(
        string name, string ns, CancellationToken ct)
    {
        // ReplicaSet readiness is pod-level: every pod matching its selector must
        // report Ready=True (there is no richer ReplicaSet status condition).
        var rs = await _client.AppsV1.ReadNamespacedReplicaSetAsync(name, ns, cancellationToken: ct);
        if ((rs.Status?.ObservedGeneration ?? 0) != (rs.Metadata.Generation ?? 0))
            return (false, false, "ReplicaSet controller has not observed the current generation");

        var podStatus = await CheckSelectedPodsAsync(
            ns,
            rs.Spec.Selector,
            "ReplicaSet",
            ct);
        if (!podStatus.Ready)
            return (false, false, podStatus.Status);

        return (true, false, "");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckReplicationControllerAsync(
        string name, string ns, CancellationToken ct)
    {
        // Same pod-level readiness as ReplicaSet; an RC selector is plain labels
        // (matchLabels only), so it is adapted to a label selector for the pod list.
        var controller = await _client.CoreV1.ReadNamespacedReplicationControllerAsync(name, ns, cancellationToken: ct);
        if ((controller.Status?.ObservedGeneration ?? 0) != (controller.Metadata.Generation ?? 0))
            return (false, false, "ReplicationController has not observed the current generation");

        var podStatus = await CheckSelectedPodsAsync(
            ns,
            controller.Spec.Selector is null
                ? null
                : new V1LabelSelector { MatchLabels = controller.Spec.Selector },
            "ReplicationController",
            ct);
        return podStatus.Ready
            ? (true, false, "")
            : (false, false, podStatus.Status);
    }

    private async Task<(bool Ready, string Status)> CheckSelectedPodsAsync(
        string ns,
        V1LabelSelector? selector,
        string ownerKind,
        CancellationToken ct)
    {
        var pods = await _client.CoreV1.ListNamespacedPodAsync(
            ns,
            labelSelector: BuildLabelSelector(selector),
            cancellationToken: ct);
        var unreadyPod = pods.Items.FirstOrDefault(pod =>
            pod.Status?.Conditions?.Any(condition => condition.Type == "Ready" && condition.Status == "True") != true);
        return unreadyPod is null
            ? (true, "")
            : (false, $"{ownerKind} pod {unreadyPod.Metadata.Name} is not ready");
    }

    private static string BuildLabelSelector(V1LabelSelector? selector)
    {
        if (selector is null)
            throw new InvalidOperationException("A workload selector is required to check readiness");

        var requirements = new List<string>();
        if (selector.MatchLabels is not null)
            requirements.AddRange(selector.MatchLabels.Select(label => $"{label.Key}={label.Value}"));

        if (selector.MatchExpressions is not null)
        {
            foreach (var expression in selector.MatchExpressions)
            {
                var values = expression.Values ?? [];
                requirements.Add(expression.OperatorProperty switch
                {
                    "In" when values.Count > 0 => $"{expression.Key} in ({string.Join(",", values)})",
                    "NotIn" when values.Count > 0 => $"{expression.Key} notin ({string.Join(",", values)})",
                    "Exists" when values.Count == 0 => expression.Key,
                    "DoesNotExist" when values.Count == 0 => $"!{expression.Key}",
                    _ => throw new InvalidOperationException(
                        $"Unsupported workload label selector expression '{expression.OperatorProperty}' for '{expression.Key}'")
                });
            }
        }

        return string.Join(",", requirements);
    }

    private static bool PodTemplatesMatch(V1PodTemplateSpec? left, V1PodTemplateSpec? right)
    {
        var leftNode = JsonSerializer.SerializeToNode(left);
        var rightNode = JsonSerializer.SerializeToNode(right);
        RemovePodTemplateHash(leftNode);
        RemovePodTemplateHash(rightNode);
        return JsonNode.DeepEquals(leftNode, rightNode);
    }

    private static void RemovePodTemplateHash(JsonNode? podTemplate)
    {
        if (podTemplate?["metadata"]?["labels"] is JsonObject labels)
            labels.Remove("pod-template-hash");
    }

    private static int ResolveDeploymentMaxUnavailable(V1Deployment deployment, int desired)
    {
        // Recreate (and desired == 0) never tolerates unavailable replicas. RollingUpdate
        // defaults match the API: surge 25% rounded up, unavailable 25% rounded down.
        // The API forbids surge and unavailable both being zero, so force one unavailable
        // to let the roll make progress; the result is also clamped to desired.
        if (deployment.Spec.Strategy?.Type == "Recreate" || desired == 0)
            return 0;

        var rollingUpdate = deployment.Spec.Strategy?.RollingUpdate;
        var maxSurge = ResolveScaledValue(
            rollingUpdate?.MaxSurge,
            desired,
            roundUp: true,
            defaultValue: (int)Math.Ceiling(desired * 0.25));
        var maxUnavailable = ResolveScaledValue(
            rollingUpdate?.MaxUnavailable,
            desired,
            roundUp: false,
            defaultValue: (int)Math.Floor(desired * 0.25));

        if (maxSurge == 0 && maxUnavailable == 0)
            maxUnavailable = 1;

        return Math.Min(maxUnavailable, desired);
    }

    private static int ResolveScaledValue(
        object? value,
        int total,
        bool roundUp,
        int defaultValue,
        int? invalidValueFallback = null)
    {
        // IntOrString resolution: ints pass through; "N%" scales by total using the
        // caller's rounding direction (surge rounds up, unavailable rounds down).
        // Unparseable or absent values fall back per policy: invalidValueFallback
        // when supplied (fail-safe like "treat as fully unavailable"), else defaultValue.
        if (value is null)
            return defaultValue;

        var scalarValue = value.GetType().GetProperty("Value")?.GetValue(value);
        if (scalarValue is int intValue)
            return intValue;

        if (scalarValue is string stringValue)
        {
            if (stringValue.EndsWith('%') &&
                int.TryParse(stringValue.AsSpan(0, stringValue.Length - 1), out var percentage))
            {
                var scaled = total * percentage / 100.0;
                return roundUp ? (int)Math.Ceiling(scaled) : (int)Math.Floor(scaled);
            }

            return int.TryParse(stringValue, out var parsedString) ? parsedString : invalidValueFallback ?? defaultValue;
        }

        return invalidValueFallback ?? defaultValue;
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckJobAsync(
        string name, string ns, CancellationToken ct)
    {
        // Job readiness mirrors Helm ReadyChecker.jobReady: fail only when the
        // failure count strictly exceeds backoffLimit, then require completions.
        // Terminal Failed conditions keep their reason as diagnostics so a
        // DeadlineExceeded or similar failure is not reduced to a bare count.
        var job = await _client.BatchV1.ReadNamespacedJobAsync(name, ns, cancellationToken: ct);
        var conditions = job.Status?.Conditions;
        var failures = job.Status?.Failed ?? 0;
        var backoffLimit = job.Spec?.BackoffLimit ?? 6;
        var succeeded = job.Status?.Succeeded ?? 0;
        var completions = job.Spec?.Completions;

        if (failures > backoffLimit)
            return (false, true, $"Job exceeded backoff limit ({failures}/{backoffLimit})");

        var failedCondition = conditions?.FirstOrDefault(c => c.Type == "Failed" && c.Status == "True");
        if (failedCondition is not null)
            return (false, true, $"Job failed: {failedCondition.Reason ?? "unknown"}");

        if (conditions?.Any(c => c.Type == "Complete" && c.Status == "True") == true)
            return (true, false, "Job completed");

        // Helm skips the completion check when completions is unset.
        if (completions is null)
            return (true, false, "");

        if (succeeded >= completions.Value)
            return (true, false, "Job completed");

        return (false, false, $"Job in progress (completions: {succeeded}/{completions.Value})");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckServiceAsync(
        string name, string ns, CancellationToken ct)
    {
        // Service readiness mirrors Helm ReadyChecker.serviceReady: ExternalName is
        // external to the cluster and ready immediately; otherwise a ClusterIP must
        // be assigned; LoadBalancer additionally waits for external exposure unless
        // ExternalIPs already pin an address.
        var service = await _client.CoreV1.ReadNamespacedServiceAsync(name, ns, cancellationToken: ct);
        if (service.Spec.Type == "ExternalName")
            return (true, false, "");

        if (string.IsNullOrEmpty(service.Spec.ClusterIP))
            return (false, false, "Service does not have cluster IP address");

        if (service.Spec.Type == "LoadBalancer")
        {
            if (service.Spec.ExternalIPs is { Count: > 0 })
                return (true, false, "");

            // Helm treats a missing LoadBalancer ingress as pending. An empty list
            // is the same practical state, so both wait for an external address.
            if (service.Status?.LoadBalancer?.Ingress is not { Count: > 0 })
                return (false, false, "Service does not have load balancer ingress IP address");
        }

        return (true, false, "");
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckCrdAsync(
        string apiVersion,
        string name,
        CancellationToken ct)
    {
        // CRD readiness mirrors Helm ReadyChecker.crdReady: Established=True is
        // ready, and NamesAccepted=False (a naming conflict) is deliberately
        // treated as ready so install can continue; every other state is pending.
        // Read through the manifest's declared API version so a cluster that serves
        // only apiextensions.k8s.io/v1beta1 still answers instead of timing out on
        // a v1 typed-client 404.
        var version = apiVersion.EndsWith("/v1beta1", StringComparison.Ordinal)
            ? "v1beta1"
            : "v1";
        var raw = await _client.CustomObjects.GetClusterCustomObjectAsync(
            "apiextensions.k8s.io", version, "customresourcedefinitions", name, ct);

        foreach (var (type, status) in ReadCrdConditions(raw))
        {
            if (type == "Established" && status == "True")
                return (true, false, "");

            if (type == "NamesAccepted" && status == "False")
                return (true, false, "");
        }

        return (false, false, "CustomResourceDefinition is not established");
    }

    private static IEnumerable<(string Type, string Status)> ReadCrdConditions(object raw)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(raw));
        if (!document.RootElement.TryGetProperty("status", out var status) ||
            !status.TryGetProperty("conditions", out var conditions) ||
            conditions.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var condition in conditions.EnumerateArray())
        {
            if (!condition.TryGetProperty("type", out var typeElement) ||
                !condition.TryGetProperty("status", out var statusElement))
                continue;

            var type = typeElement.GetString();
            var conditionStatus = statusElement.GetString();
            if (type is not null && conditionStatus is not null)
                yield return (type, conditionStatus);
        }
    }

    private async Task<(bool Ready, bool Failed, string Status)> CheckPodAsync(
        string name, string ns, CancellationToken ct)
    {
        // Pod readiness is the Ready condition; Failed phase and CrashLoopBackOff are
        // terminal so a crash-looping pod fails the wait instead of timing out.
        var pod = await _client.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: ct);
        var phase = pod.Status?.Phase;

        var conditions = pod.Status?.Conditions;
        var readyCondition = conditions?.FirstOrDefault(c => c.Type == "Ready");
        if (readyCondition?.Status == "True")
            return (true, false, "");

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
        // PVC is ready when Bound; Lost is terminal (the volume will not bind).
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
        // Endpoints are ready once at least one subset has ready addresses —
        // the signal that a Service's backing pods have registered.
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
        // HPA readiness is ScalingActive=True; ScalingActive=False (for example
        // missing metrics) is terminal because retrying will not fix it.
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

    // Only kinds with a real readiness condition are waitable; everything else in the
    // manifest is ignored here so --wait never blocks on inert config objects.
    private static bool IsWaitableKind(string kind)
        => kind is "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet"
            or "Job" or "Pod" or "PersistentVolumeClaim" or "Endpoints"
            or "HorizontalPodAutoscaler" or "ReplicationController"
            or "Service" or "CustomResourceDefinition";

}

internal sealed record DeletionResource(string Group, string Version, string Plural, bool Namespaced);
