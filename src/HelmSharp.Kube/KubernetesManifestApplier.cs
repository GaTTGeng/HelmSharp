using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HelmSharp.Chart;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace HelmSharp.Kube;

/// <summary>
/// Applies and deletes Kubernetes resources described by multi-document YAML manifests.
/// Built-in kinds go through the typed client; other kinds (CRs) use API discovery
/// and the custom-objects endpoints.
/// </summary>
public sealed class KubernetesManifestApplier
{
    // Helm's default delete propagation: dependents are reaped in the background so
    // uninstall does not block on cascading deletions.
    private const string DefaultDeletionPropagationPolicy = "Background";

    // A CRD endpoint can take a few seconds to register after create. Apply retries
    // discovery briefly so a chart that ships a CRD and its CR in one release does not
    // fail before the control plane is ready; delete does not retry.
    private const int DiscoveryRetryAttempts = 25;
    private static readonly TimeSpan DiscoveryRetryDelay = TimeSpan.FromMilliseconds(300);

    private readonly Kubernetes _client;
    private readonly string _fieldManager;
    private readonly Func<TimeSpan, CancellationToken, Task> _discoveryDelay;
    private readonly Dictionary<(string ApiVersion, string Kind), DiscoveredResource> _discoveredResources = new();
    private bool _lastApplyMayHaveCommitted;
    private IReadOnlyDictionary<ManifestIdentity, string> _previousManifestByIdentity =
        new Dictionary<ManifestIdentity, string>();

    /// <param name="client">Kubernetes client used for all API operations.</param>
    /// <param name="fieldManager">Field manager name recorded on create/replace calls (server-side apply bookkeeping).</param>
    public KubernetesManifestApplier(Kubernetes client, string fieldManager)
        : this(client, fieldManager, static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    /// <summary>
    /// Test seam that replaces the discovery retry delay so registration-lag tests stay fast.
    /// </summary>
    internal KubernetesManifestApplier(
        Kubernetes client,
        string fieldManager,
        Func<TimeSpan, CancellationToken, Task> discoveryDelay)
    {
        _client = client;
        _fieldManager = fieldManager;
        _discoveryDelay = discoveryDelay;
    }

    /// <summary>
    /// Creates the namespace if it does not already exist (read-then-create). An existing
    /// namespace is left untouched; a concurrent create race is not specially handled.
    /// </summary>
    public static async Task EnsureNamespaceAsync(
        Kubernetes client,
        string name,
        CancellationToken cancellationToken)
    {
        // Read-then-create: only a 404 read miss falls through to create; an
        // already-existing namespace is left untouched.
        try
        {
            await client.CoreV1.ReadNamespaceAsync(name, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            await client.CoreV1.CreateNamespaceAsync(new V1Namespace
            {
                Metadata = new V1ObjectMeta { Name = name }
            }, cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Applies every resource document in manifest order (create or replace, matching
    /// Helm's install/upgrade apply behavior). Yields each applied resource's display name.
    /// </summary>
    /// <param name="manifest">Multi-document YAML manifest.</param>
    /// <param name="defaultNamespace">Namespace used when a document omits <c>metadata.namespace</c>.</param>
    /// <param name="cancellationToken">Cancels API calls.</param>
    /// <exception cref="KubernetesResourceOperationException">Thrown when an apply fails; wraps the resource identity and the underlying error.</exception>
    public async IAsyncEnumerable<string> ApplyAsync(
        string manifest,
        string defaultNamespace,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Stage: split into documents → parse one identity per doc → ApplyOneAsync
        // (typed upsert or discovery fallback). Failures are rewrapped with the
        // resource identity so the error names what could not be applied.
        foreach (var doc in SplitDocuments(manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = ManifestIdentity.Parse(doc, defaultNamespace);
            if (identity is null)
                continue;

            _lastApplyMayHaveCommitted = false;
            Exception? applyError = null;
            try
            {
                identity = await ApplyOneAsync(identity, doc, cancellationToken);
            }
            catch (KubernetesResourceOperationException ex)
            {
                applyError = ex;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                applyError = new KubernetesResourceOperationException(identity, ex);
            }
            catch (OperationCanceledException ex) when (_lastApplyMayHaveCommitted)
            {
                // Preserve confirmed write progress before propagating in-flight cancellation.
                applyError = ex;
            }

            if (applyError is not null)
            {
                // A request can time out after the API server commits the write. Report
                // that resource as attempted when read-back confirms the write advanced,
                // so failure recovery records it in the failed revision's baseline.
                if (_lastApplyMayHaveCommitted)
                    yield return identity.DisplayName;
                throw applyError;
            }

            yield return identity.DisplayName;
        }
    }

    /// <summary>
    /// Applies an upgrade manifest while preserving live changes that the previous release
    /// manifest shows HelmSharp does not own.
    /// </summary>
    /// <param name="manifest">New multi-document YAML manifest rendered from the chart.</param>
    /// <param name="previousManifest">Manifest recorded by the previously deployed release revision.</param>
    /// <param name="defaultNamespace">Namespace used when a document omits <c>metadata.namespace</c>.</param>
    /// <param name="cancellationToken">Cancels API calls.</param>
    internal async IAsyncEnumerable<string> ApplyUpgradeAsync(
        string manifest,
        string previousManifest,
        string defaultNamespace,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var priorMergeBaseline = _previousManifestByIdentity;
        _previousManifestByIdentity = SplitDocuments(previousManifest)
            .Select(document => (Document: document, Identity: ManifestIdentity.Parse(document, defaultNamespace)))
            .Where(item => item.Identity is not null)
            .GroupBy(item => item.Identity!)
            .ToDictionary(group => group.Key, group => group.First().Document);

        try
        {
            await foreach (var resource in ApplyAsync(manifest, defaultNamespace, cancellationToken))
                yield return resource;
        }
        finally
        {
            // Atomic failure recovery reuses this applier to restore the prior manifest.
            // Keep the three-way baseline scoped to the upgrade apply, not recovery.
            _previousManifestByIdentity = priorMergeBaseline;
        }
    }

    /// <summary>
    /// Deletes resources in reverse manifest order using background dependent-resource propagation.
    /// Resources already absent are treated as successfully deleted.
    /// </summary>
    public async IAsyncEnumerable<string> DeleteAsync(
        string manifest,
        string defaultNamespace,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var resource in DeleteAsync(
                           manifest,
                           defaultNamespace,
                           DefaultDeletionPropagationPolicy,
                           cancellationToken))
            yield return resource;
    }

    /// <summary>
    /// Deletes resources in reverse manifest order with the requested dependent-resource propagation policy.
    /// Supported values are <c>Background</c>, <c>Foreground</c>, and <c>Orphan</c>. A missing value uses
    /// <c>Background</c>. Resources already absent are treated as successfully deleted; discovery and other
    /// Kubernetes API failures include the affected resource identity.
    /// </summary>
    public async IAsyncEnumerable<string> DeleteAsync(
        string manifest,
        string defaultNamespace,
        string? propagationPolicy,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var normalizedPropagationPolicy = NormalizeDeletionPropagationPolicy(propagationPolicy);
        // Reverse manifest order: dependents applied late (workloads, ingress) are
        // deleted before their prerequisites (config, RBAC, namespaces), mirroring
        // Helm's uninstall ordering on top of propagation-policy cascading.
        foreach (var doc in SplitDocuments(manifest).Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = ManifestIdentity.Parse(doc, defaultNamespace);
            if (identity is null)
                continue;

            try
            {
                identity = await DeleteOneAsync(identity, normalizedPropagationPolicy, cancellationToken);
            }
            catch (KubernetesResourceOperationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new KubernetesResourceOperationException(identity, ex);
            }
            yield return identity.DisplayName;
        }
    }

    private static string NormalizeDeletionPropagationPolicy(string? propagationPolicy)
    {
        if (string.IsNullOrWhiteSpace(propagationPolicy))
            return DefaultDeletionPropagationPolicy;

        if (string.Equals(propagationPolicy, "Background", StringComparison.OrdinalIgnoreCase))
            return "Background";
        if (string.Equals(propagationPolicy, "Foreground", StringComparison.OrdinalIgnoreCase))
            return "Foreground";
        if (string.Equals(propagationPolicy, "Orphan", StringComparison.OrdinalIgnoreCase))
            return "Orphan";

        throw new ArgumentException(
            $"Unsupported Kubernetes deletion propagation policy '{propagationPolicy}'. " +
            "Supported values are Background, Foreground, and Orphan.",
            nameof(propagationPolicy));
    }

    internal async Task<ManifestIdentity?> ResolveIdentityAsync(
        string manifestDocument,
        string defaultNamespace,
        CancellationToken cancellationToken)
    {
        var identity = ManifestIdentity.Parse(manifestDocument, defaultNamespace);
        if (identity is null)
            return null;

        if (TryGetTypedResourceScope(identity, out _))
            return NormalizeTypedIdentity(identity);

        DiscoveredResource resource;
        try
        {
            resource = await DiscoverResourceAsync(identity, retryRegistration: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new KubernetesResourceOperationException(identity, ex);
        }
        return resource.Namespaced
            ? identity
            : identity with { Namespace = string.Empty };
    }

    internal static bool TryGetTypedResourceScope(ManifestIdentity identity, out bool namespaced)
    {
        // Static scope table for built-in kinds avoids an API discovery round-trip on
        // every apply/delete and keeps cluster-scoped kinds free of a default namespace.
        bool? scope = (identity.ApiVersion, identity.Kind) switch
        {
            ("v1", "Namespace" or "PersistentVolume") => false,
            ("v1", "ConfigMap" or "Secret" or "Service" or "ServiceAccount"
                or "PersistentVolumeClaim" or "LimitRange" or "ResourceQuota"
                or "Pod" or "Endpoints" or "ReplicationController") => true,
            ("apps/v1", "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet") => true,
            ("batch/v1", "Job" or "CronJob") => true,
            ("networking.k8s.io/v1", "Ingress" or "NetworkPolicy") => true,
            ("networking.k8s.io/v1", "IngressClass") => false,
            ("networking.k8s.io/v1beta1", "IngressClass") => false,
            ("rbac.authorization.k8s.io/v1", "Role" or "RoleBinding") => true,
            ("rbac.authorization.k8s.io/v1", "ClusterRole" or "ClusterRoleBinding") => false,
            ("rbac.authorization.k8s.io/v1beta1", "Role" or "RoleBinding") => true,
            ("rbac.authorization.k8s.io/v1beta1", "ClusterRole" or "ClusterRoleBinding") => false,
            ("autoscaling/v1" or "autoscaling/v2", "HorizontalPodAutoscaler") => true,
            ("policy/v1", "PodDisruptionBudget") => true,
            ("storage.k8s.io/v1", "StorageClass" or "CSIDriver" or "CSINode" or "VolumeAttachment") => false,
            ("storage.k8s.io/v1", "CSIStorageCapacity") => true,
            ("storage.k8s.io/v1beta1", "CSIStorageCapacity") => true,
            ("storage.k8s.io/v1beta1", "StorageClass" or "CSIDriver" or "CSINode" or "VolumeAttachment") => false,
            ("scheduling.k8s.io/v1", "PriorityClass") => false,
            ("scheduling.k8s.io/v1beta1", "PriorityClass") => false,
            ("apiextensions.k8s.io/v1" or "apiextensions.k8s.io/v1beta1", "CustomResourceDefinition") => false,
            ("admissionregistration.k8s.io/v1", "MutatingWebhookConfiguration" or "ValidatingWebhookConfiguration") => false,
            ("apiregistration.k8s.io/v1", "APIService") => false,
            ("apiregistration.k8s.io/v1beta1", "APIService") => false,
            ("coordination.k8s.io/v1", "Lease") => true,
            ("coordination.k8s.io/v1beta1", "Lease") => true,
            ("node.k8s.io/v1", "RuntimeClass") => false,
            ("node.k8s.io/v1beta1", "RuntimeClass") => false,
            ("discovery.k8s.io/v1", "EndpointSlice") => true,
            ("flowcontrol.apiserver.k8s.io/v1", "FlowSchema" or "PriorityLevelConfiguration") => false,
            _ => null
        };
        namespaced = scope ?? false;
        return scope.HasValue;
    }

    internal static ManifestIdentity NormalizeTypedIdentity(ManifestIdentity identity)
        => TryGetTypedResourceScope(identity, out var namespaced) && !namespaced
            ? identity with { Namespace = string.Empty }
            : identity;

    private async Task<ManifestIdentity> ApplyOneAsync(ManifestIdentity identity, string yaml, CancellationToken ct)
    {
        Task UpsertWithIntentAsync<T>(
            Func<Task<T>> read,
            Func<Task<T>> create,
            Func<T, Task<T>> replace)
            => UpsertNamespacedAsync(
                read,
                create,
                replace,
                identity,
                yaml,
                () => ct.ThrowIfCancellationRequested(),
                () => ct = CancellationToken.None);

        // Typed-client dispatch: known built-in kinds deserialize to strongly typed
        // models and use read/replace/create upserts, preserving fields the typed
        // model carries (for example Service healthCheckNodePort). Unknown kinds fall
        // through to API discovery and the custom-objects endpoints below.
        switch (identity.ApiVersion, identity.Kind)
        {
            // ─── Core v1 ───
            case ("v1", "Namespace"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespaceAsync(identity.Name, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespaceAsync(KubernetesYaml.Deserialize<V1Namespace>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1Namespace>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespaceAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("v1", "ConfigMap"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespacedConfigMapAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedConfigMapAsync(KubernetesYaml.Deserialize<V1ConfigMap>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1ConfigMap>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedConfigMapAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "Secret"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespacedSecretAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedSecretAsync(KubernetesYaml.Deserialize<V1Secret>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1Secret>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedSecretAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "Service"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespacedServiceAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedServiceAsync(KubernetesYaml.Deserialize<V1Service>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1Service>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        // ClusterIP(s) and IpFamilyPolicy are allocated/immutable after
                        // creation; healthCheckNodePort is server-allocated for
                        // LoadBalancer+Local services. All must survive the replace.
                        item.Spec.ClusterIP = existing.Spec.ClusterIP;
                        item.Spec.ClusterIPs = existing.Spec.ClusterIPs;
                        item.Spec.IpFamilyPolicy ??= existing.Spec.IpFamilyPolicy;
                        if (item.Spec.HealthCheckNodePort is null && SupportsHealthCheckNodePort(item.Spec))
                            item.Spec.HealthCheckNodePort = existing.Spec.HealthCheckNodePort;
                        return _client.CoreV1.ReplaceNamespacedServiceAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "ServiceAccount"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespacedServiceAccountAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedServiceAccountAsync(KubernetesYaml.Deserialize<V1ServiceAccount>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1ServiceAccount>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedServiceAccountAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "PersistentVolumeClaim"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(KubernetesYaml.Deserialize<V1PersistentVolumeClaim>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1PersistentVolumeClaim>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedPersistentVolumeClaimAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "PersistentVolume"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadPersistentVolumeAsync(identity.Name, cancellationToken: ct),
                    () => _client.CoreV1.CreatePersistentVolumeAsync(KubernetesYaml.Deserialize<V1PersistentVolume>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1PersistentVolume>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplacePersistentVolumeAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("v1", "LimitRange"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespacedLimitRangeAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedLimitRangeAsync(KubernetesYaml.Deserialize<V1LimitRange>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1LimitRange>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedLimitRangeAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "ResourceQuota"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespacedResourceQuotaAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedResourceQuotaAsync(KubernetesYaml.Deserialize<V1ResourceQuota>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1ResourceQuota>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedResourceQuotaAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "Pod"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespacedPodAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedPodAsync(KubernetesYaml.Deserialize<V1Pod>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1Pod>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedPodAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "Endpoints"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespacedEndpointsAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedEndpointsAsync(KubernetesYaml.Deserialize<V1Endpoints>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1Endpoints>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedEndpointsAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "ReplicationController"):
                await UpsertWithIntentAsync(
                    () => _client.CoreV1.ReadNamespacedReplicationControllerAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedReplicationControllerAsync(KubernetesYaml.Deserialize<V1ReplicationController>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1ReplicationController>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedReplicationControllerAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── apps/v1 ───
            case ("apps/v1", "Deployment"):
                await UpsertWithIntentAsync(
                    () => _client.AppsV1.ReadNamespacedDeploymentAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AppsV1.CreateNamespacedDeploymentAsync(KubernetesYaml.Deserialize<V1Deployment>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1Deployment>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AppsV1.ReplaceNamespacedDeploymentAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("apps/v1", "StatefulSet"):
                await UpsertWithIntentAsync(
                    () => _client.AppsV1.ReadNamespacedStatefulSetAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AppsV1.CreateNamespacedStatefulSetAsync(KubernetesYaml.Deserialize<V1StatefulSet>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1StatefulSet>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AppsV1.ReplaceNamespacedStatefulSetAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("apps/v1", "DaemonSet"):
                await UpsertWithIntentAsync(
                    () => _client.AppsV1.ReadNamespacedDaemonSetAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AppsV1.CreateNamespacedDaemonSetAsync(KubernetesYaml.Deserialize<V1DaemonSet>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1DaemonSet>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AppsV1.ReplaceNamespacedDaemonSetAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("apps/v1", "ReplicaSet"):
                await UpsertWithIntentAsync(
                    () => _client.AppsV1.ReadNamespacedReplicaSetAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AppsV1.CreateNamespacedReplicaSetAsync(KubernetesYaml.Deserialize<V1ReplicaSet>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1ReplicaSet>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AppsV1.ReplaceNamespacedReplicaSetAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── batch/v1 ───
            case ("batch/v1", "Job"):
                await UpsertWithIntentAsync(
                    () => _client.BatchV1.ReadNamespacedJobAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.BatchV1.CreateNamespacedJobAsync(KubernetesYaml.Deserialize<V1Job>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1Job>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        // The Job selector is controller-assigned and immutable — carry
                        // the existing one over or the replace is rejected.
                        item.Spec.Selector = existing.Spec.Selector;
                        return _client.BatchV1.ReplaceNamespacedJobAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("batch/v1", "CronJob"):
                await UpsertWithIntentAsync(
                    () => _client.BatchV1.ReadNamespacedCronJobAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.BatchV1.CreateNamespacedCronJobAsync(KubernetesYaml.Deserialize<V1CronJob>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1CronJob>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.BatchV1.ReplaceNamespacedCronJobAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── networking.k8s.io/v1 ───
            case ("networking.k8s.io/v1", "Ingress"):
                await UpsertWithIntentAsync(
                    () => _client.NetworkingV1.ReadNamespacedIngressAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.NetworkingV1.CreateNamespacedIngressAsync(KubernetesYaml.Deserialize<V1Ingress>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1Ingress>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.NetworkingV1.ReplaceNamespacedIngressAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("networking.k8s.io/v1", "NetworkPolicy"):
                await UpsertWithIntentAsync(
                    () => _client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(KubernetesYaml.Deserialize<V1NetworkPolicy>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1NetworkPolicy>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.NetworkingV1.ReplaceNamespacedNetworkPolicyAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("networking.k8s.io/v1", "IngressClass"):
                await UpsertWithIntentAsync(
                    () => _client.NetworkingV1.ReadIngressClassAsync(identity.Name, cancellationToken: ct),
                    () => _client.NetworkingV1.CreateIngressClassAsync(KubernetesYaml.Deserialize<V1IngressClass>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1IngressClass>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.NetworkingV1.ReplaceIngressClassAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── rbac.authorization.k8s.io/v1 ───
            case ("rbac.authorization.k8s.io/v1", "Role"):
                await UpsertWithIntentAsync(
                    () => _client.RbacAuthorizationV1.ReadNamespacedRoleAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.RbacAuthorizationV1.CreateNamespacedRoleAsync(KubernetesYaml.Deserialize<V1Role>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1Role>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.RbacAuthorizationV1.ReplaceNamespacedRoleAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("rbac.authorization.k8s.io/v1", "RoleBinding"):
                await UpsertWithIntentAsync(
                    () => _client.RbacAuthorizationV1.ReadNamespacedRoleBindingAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.RbacAuthorizationV1.CreateNamespacedRoleBindingAsync(KubernetesYaml.Deserialize<V1RoleBinding>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1RoleBinding>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.RbacAuthorizationV1.ReplaceNamespacedRoleBindingAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("rbac.authorization.k8s.io/v1", "ClusterRole"):
                await UpsertWithIntentAsync(
                    () => _client.RbacAuthorizationV1.ReadClusterRoleAsync(identity.Name, cancellationToken: ct),
                    () => _client.RbacAuthorizationV1.CreateClusterRoleAsync(KubernetesYaml.Deserialize<V1ClusterRole>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1ClusterRole>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.RbacAuthorizationV1.ReplaceClusterRoleAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("rbac.authorization.k8s.io/v1", "ClusterRoleBinding"):
                await UpsertWithIntentAsync(
                    () => _client.RbacAuthorizationV1.ReadClusterRoleBindingAsync(identity.Name, cancellationToken: ct),
                    () => _client.RbacAuthorizationV1.CreateClusterRoleBindingAsync(KubernetesYaml.Deserialize<V1ClusterRoleBinding>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1ClusterRoleBinding>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.RbacAuthorizationV1.ReplaceClusterRoleBindingAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── autoscaling/v2 ───
            case ("autoscaling/v2", "HorizontalPodAutoscaler"):
                await UpsertWithIntentAsync(
                    () => _client.AutoscalingV2.ReadNamespacedHorizontalPodAutoscalerAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AutoscalingV2.CreateNamespacedHorizontalPodAutoscalerAsync(KubernetesYaml.Deserialize<V2HorizontalPodAutoscaler>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V2HorizontalPodAutoscaler>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AutoscalingV2.ReplaceNamespacedHorizontalPodAutoscalerAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("autoscaling/v1", "HorizontalPodAutoscaler"):
                await UpsertWithIntentAsync(
                    () => _client.AutoscalingV1.ReadNamespacedHorizontalPodAutoscalerAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AutoscalingV1.CreateNamespacedHorizontalPodAutoscalerAsync(KubernetesYaml.Deserialize<V1HorizontalPodAutoscaler>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1HorizontalPodAutoscaler>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AutoscalingV1.ReplaceNamespacedHorizontalPodAutoscalerAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── policy/v1 ───
            case ("policy/v1", "PodDisruptionBudget"):
                await UpsertWithIntentAsync(
                    () => _client.PolicyV1.ReadNamespacedPodDisruptionBudgetAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.PolicyV1.CreateNamespacedPodDisruptionBudgetAsync(KubernetesYaml.Deserialize<V1PodDisruptionBudget>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1PodDisruptionBudget>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.PolicyV1.ReplaceNamespacedPodDisruptionBudgetAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── storage.k8s.io/v1 ───
            case ("storage.k8s.io/v1", "StorageClass"):
                await UpsertWithIntentAsync(
                    () => _client.StorageV1.ReadStorageClassAsync(identity.Name, cancellationToken: ct),
                    () => _client.StorageV1.CreateStorageClassAsync(KubernetesYaml.Deserialize<V1StorageClass>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1StorageClass>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.StorageV1.ReplaceStorageClassAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("storage.k8s.io/v1", "CSIDriver"):
                await UpsertWithIntentAsync(
                    () => _client.StorageV1.ReadCSIDriverAsync(identity.Name, cancellationToken: ct),
                    () => _client.StorageV1.CreateCSIDriverAsync(KubernetesYaml.Deserialize<V1CSIDriver>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1CSIDriver>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.StorageV1.ReplaceCSIDriverAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("storage.k8s.io/v1", "CSINode"):
                await UpsertWithIntentAsync(
                    () => _client.StorageV1.ReadCSINodeAsync(identity.Name, cancellationToken: ct),
                    () => _client.StorageV1.CreateCSINodeAsync(KubernetesYaml.Deserialize<V1CSINode>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1CSINode>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.StorageV1.ReplaceCSINodeAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("storage.k8s.io/v1", "VolumeAttachment"):
                await UpsertWithIntentAsync(
                    () => _client.StorageV1.ReadVolumeAttachmentAsync(identity.Name, cancellationToken: ct),
                    () => _client.StorageV1.CreateVolumeAttachmentAsync(KubernetesYaml.Deserialize<V1VolumeAttachment>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1VolumeAttachment>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.StorageV1.ReplaceVolumeAttachmentAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── scheduling.k8s.io/v1 ───
            case ("scheduling.k8s.io/v1", "PriorityClass"):
                await UpsertWithIntentAsync(
                    () => _client.SchedulingV1.ReadPriorityClassAsync(identity.Name, cancellationToken: ct),
                    () => _client.SchedulingV1.CreatePriorityClassAsync(KubernetesYaml.Deserialize<V1PriorityClass>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1PriorityClass>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.SchedulingV1.ReplacePriorityClassAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── apiextensions.k8s.io/v1 ───
            case ("apiextensions.k8s.io/v1", "CustomResourceDefinition"):
                await UpsertWithIntentAsync(
                    () => _client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(identity.Name, cancellationToken: ct),
                    () => _client.ApiextensionsV1.CreateCustomResourceDefinitionAsync(KubernetesYaml.Deserialize<V1CustomResourceDefinition>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1CustomResourceDefinition>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.ApiextensionsV1.ReplaceCustomResourceDefinitionAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── admissionregistration.k8s.io/v1 ───
            case ("admissionregistration.k8s.io/v1", "MutatingWebhookConfiguration"):
                await UpsertWithIntentAsync(
                    () => _client.AdmissionregistrationV1.ReadMutatingWebhookConfigurationAsync(identity.Name, cancellationToken: ct),
                    () => _client.AdmissionregistrationV1.CreateMutatingWebhookConfigurationAsync(KubernetesYaml.Deserialize<V1MutatingWebhookConfiguration>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1MutatingWebhookConfiguration>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AdmissionregistrationV1.ReplaceMutatingWebhookConfigurationAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("admissionregistration.k8s.io/v1", "ValidatingWebhookConfiguration"):
                await UpsertWithIntentAsync(
                    () => _client.AdmissionregistrationV1.ReadValidatingWebhookConfigurationAsync(identity.Name, cancellationToken: ct),
                    () => _client.AdmissionregistrationV1.CreateValidatingWebhookConfigurationAsync(KubernetesYaml.Deserialize<V1ValidatingWebhookConfiguration>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1ValidatingWebhookConfiguration>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AdmissionregistrationV1.ReplaceValidatingWebhookConfigurationAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── apiregistration.k8s.io/v1 ───
            case ("apiregistration.k8s.io/v1", "APIService"):
                await UpsertWithIntentAsync(
                    () => _client.ApiregistrationV1.ReadAPIServiceAsync(identity.Name, cancellationToken: ct),
                    () => _client.ApiregistrationV1.CreateAPIServiceAsync(KubernetesYaml.Deserialize<V1APIService>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1APIService>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.ApiregistrationV1.ReplaceAPIServiceAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── coordination.k8s.io/v1 ───
            case ("coordination.k8s.io/v1", "Lease"):
                await UpsertWithIntentAsync(
                    () => _client.CoordinationV1.ReadNamespacedLeaseAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoordinationV1.CreateNamespacedLeaseAsync(KubernetesYaml.Deserialize<V1Lease>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1Lease>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoordinationV1.ReplaceNamespacedLeaseAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── node.k8s.io/v1 ───
            case ("node.k8s.io/v1", "RuntimeClass"):
                await UpsertWithIntentAsync(
                    () => _client.NodeV1.ReadRuntimeClassAsync(identity.Name, cancellationToken: ct),
                    () => _client.NodeV1.CreateRuntimeClassAsync(KubernetesYaml.Deserialize<V1RuntimeClass>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1RuntimeClass>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.NodeV1.ReplaceRuntimeClassAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── discovery.k8s.io/v1 ───
            case ("discovery.k8s.io/v1", "EndpointSlice"):
                await UpsertWithIntentAsync(
                    () => _client.DiscoveryV1.ReadNamespacedEndpointSliceAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.DiscoveryV1.CreateNamespacedEndpointSliceAsync(KubernetesYaml.Deserialize<V1EndpointSlice>(yaml), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1EndpointSlice>(upgradeYaml);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.DiscoveryV1.ReplaceNamespacedEndpointSliceAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── flowcontrol.apiserver.k8s.io/v1 ───
            case ("flowcontrol.apiserver.k8s.io/v1", "FlowSchema"):
                await UpsertWithIntentAsync(
                    () => _client.FlowcontrolApiserverV1.ReadFlowSchemaAsync(identity.Name, cancellationToken: ct),
                    () => _client.FlowcontrolApiserverV1.CreateFlowSchemaAsync(KubernetesYaml.Deserialize<V1FlowSchema>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1FlowSchema>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.FlowcontrolApiserverV1.ReplaceFlowSchemaAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("flowcontrol.apiserver.k8s.io/v1", "PriorityLevelConfiguration"):
                await UpsertWithIntentAsync(
                    () => _client.FlowcontrolApiserverV1.ReadPriorityLevelConfigurationAsync(identity.Name, cancellationToken: ct),
                    () => _client.FlowcontrolApiserverV1.CreatePriorityLevelConfigurationAsync(KubernetesYaml.Deserialize<V1PriorityLevelConfiguration>(yaml), cancellationToken: ct),
                    existing =>
                    {
                        var upgradeYaml = MergeWithLiveResource(identity, yaml, existing);
                        var item = KubernetesYaml.Deserialize<V1PriorityLevelConfiguration>(upgradeYaml);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.FlowcontrolApiserverV1.ReplacePriorityLevelConfigurationAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            default:
                // Discovery fallback: CRs and any kind without a typed arm go through
                // API discovery and the custom-objects endpoints.
                return await ApplyDiscoveredResourceAsync(identity, yaml, ct);
        }

        return identity;
    }

    private string MergeWithLiveResource(
        ManifestIdentity identity,
        string desiredManifest,
        object liveResource,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys = null)
    {
        if (!TryGetPreviousManifest(identity, out var previousManifest))
            return desiredManifest;

        var previousIdentity = ManifestIdentity.Parse(previousManifest, identity.Namespace);
        if (previousIdentity is null ||
            !KubernetesManifestSchemaConverter.TryConvertToApiVersion(
                previousManifest,
                previousIdentity,
                identity.ApiVersion,
                out previousManifest))
        {
            // A baseline from an unknown schema cannot safely drive field ownership in
            // the target version; skip merging rather than treating renamed fields as additions.
            return desiredManifest;
        }

        // The typed Kubernetes read result already contains the same live object whose
        // resourceVersion is used by the following Replace request.
        var liveManifest = JsonSerializer.Serialize(liveResource);
        return ThreeWayMerge.MergeResource(
            NormalizeSecretStringData(identity, previousManifest),
            NormalizeSecretStringData(identity, desiredManifest),
            liveManifest,
            customListMapKeys);
    }

    private static string NormalizeSecretStringData(ManifestIdentity identity, string manifest)
    {
        if (identity.ApiVersion != "v1" || identity.Kind != "Secret")
            return manifest;

        var document = HelmYaml.DeserializeDictionary(manifest, StringComparer.Ordinal);
        if (!document.TryGetValue("stringData", out var stringDataValue) ||
            stringDataValue is not IDictionary<string, object?> stringData)
            return manifest;

        var data = document.TryGetValue("data", out var dataValue) &&
                   dataValue is IDictionary<string, object?> existingData
            ? existingData
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        // Kubernetes stores stringData as base64 data and never returns stringData on reads.
        // Canonicalizing both chart revisions lets three-way merge detect removed keys.
        foreach (var (key, value) in stringData)
        {
            var text = value?.ToString() ?? string.Empty;
            data[key] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        document["data"] = data;
        document.Remove("stringData");
        return HelmYaml.Serialize(document);
    }

    private bool TryGetPreviousManifest(ManifestIdentity identity, out string previousManifest)
    {
        if (_previousManifestByIdentity.TryGetValue(identity, out previousManifest!))
            return true;

        // Discovery can reveal that a custom resource is cluster-scoped only after its
        // prior manifest was parsed, when the default namespace was still attached.
        if (string.IsNullOrWhiteSpace(identity.Namespace))
        {
            var matches = _previousManifestByIdentity
                .Where(pair => SameResourceTypeAndName(pair.Key, identity))
                .Select(pair => pair.Value)
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                previousManifest = matches[0];
                return true;
            }
        }

        // apiVersion can change while the chart keeps the same Kubernetes object. A
        // three-way baseline still applies because the API group, kind, scope, and name
        // identify that object independently of its currently served version.
        var versionMatches = _previousManifestByIdentity
            .Where(pair => SameResourceTypeAndName(pair.Key, identity) &&
                           string.Equals(pair.Key.Namespace, identity.Namespace, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .Take(2)
            .ToArray();
        if (versionMatches.Length == 1)
        {
            previousManifest = versionMatches[0];
            return true;
        }

        previousManifest = string.Empty;
        return false;
    }

    private static bool SameResourceTypeAndName(ManifestIdentity left, ManifestIdentity right)
        => string.Equals(GetCanonicalApiGroup(left), GetCanonicalApiGroup(right), StringComparison.Ordinal) &&
           string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
           string.Equals(left.Name, right.Name, StringComparison.Ordinal);

    private static string GetCanonicalApiGroup(ManifestIdentity identity)
    {
        var separator = identity.ApiVersion.IndexOf('/');
        var apiGroup = separator < 0 ? string.Empty : identity.ApiVersion[..separator];
        if (apiGroup != "extensions")
            return apiGroup;

        // Kubernetes moved several built-in resources out of the legacy extensions
        // group without changing their stored object identity. Keep aliases narrowly
        // limited to those known built-ins so unrelated custom resources cannot collide.
        return identity.Kind switch
        {
            "Deployment" or "DaemonSet" or "ReplicaSet" => "apps",
            "Ingress" or "NetworkPolicy" => "networking.k8s.io",
            "PodSecurityPolicy" => "policy",
            _ => apiGroup
        };
    }

    private async Task<ManifestIdentity> ApplyDiscoveredResourceAsync(ManifestIdentity identity, string yaml, CancellationToken ct)
    {
        // Retry discovery: the CR may be applied in the same pass that created its CRD.
        var resource = await DiscoverResourceAsync(identity, retryRegistration: true, ct);
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys = null;
        if (TryGetPreviousManifest(identity, out _))
            customListMapKeys = await ReadCustomListMapKeysAsync(resource, ct);
        var item = HelmYaml.DeserializeDictionary(yaml);

        if (resource.Namespaced)
        {
            SetNamespace(item, identity.Namespace);
            await UpsertNamespacedAsync(
                () => _client.CustomObjects.GetNamespacedCustomObjectAsync(resource.Group, resource.Version, identity.Namespace, resource.Plural, identity.Name, ct),
                () => _client.CustomObjects.CreateNamespacedCustomObjectAsync(item, resource.Group, resource.Version, identity.Namespace, resource.Plural, null, _fieldManager, null, null, ct),
                async existing =>
                {
                    item = HelmYaml.DeserializeDictionary(
                        MergeWithLiveResource(identity, yaml, existing, customListMapKeys),
                        StringComparer.Ordinal);
                    SetNamespace(item, identity.Namespace);
                    SetResourceVersion(item, existing);
                    return await _client.CustomObjects.ReplaceNamespacedCustomObjectAsync(item, resource.Group, resource.Version, identity.Namespace, resource.Plural, identity.Name, null, _fieldManager, null, ct);
                }, identity, yaml, () => ct.ThrowIfCancellationRequested(), () => ct = CancellationToken.None, customListMapKeys);
            return identity;
        }

        identity = identity with { Namespace = string.Empty };
        SetNamespace(item, namespaceName: null);
        await UpsertClusterAsync(
            () => _client.CustomObjects.GetClusterCustomObjectAsync(resource.Group, resource.Version, resource.Plural, identity.Name, ct),
            () => _client.CustomObjects.CreateClusterCustomObjectAsync(item, resource.Group, resource.Version, resource.Plural, null, _fieldManager, null, null, ct),
            async existing =>
            {
                item = HelmYaml.DeserializeDictionary(
                    MergeWithLiveResource(identity, yaml, existing, customListMapKeys),
                    StringComparer.Ordinal);
                SetNamespace(item, namespaceName: null);
                SetResourceVersion(item, existing);
                return await _client.CustomObjects.ReplaceClusterCustomObjectAsync(item, resource.Group, resource.Version, resource.Plural, identity.Name, null, _fieldManager, null, ct);
            }, identity, yaml, () => ct.ThrowIfCancellationRequested(), () => ct = CancellationToken.None, customListMapKeys);
        return identity;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadCustomListMapKeysAsync(
        DiscoveredResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var crd = await _client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(
                $"{resource.Plural}.{resource.Group}",
                cancellationToken: cancellationToken);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(crd));
            var versions = document.RootElement.GetProperty("spec").GetProperty("versions");
            var version = versions.EnumerateArray().FirstOrDefault(candidate =>
                candidate.GetProperty("name").GetString() == resource.Version &&
                (!candidate.TryGetProperty("served", out var served) || served.GetBoolean()));
            if (version.ValueKind == JsonValueKind.Undefined ||
                !version.TryGetProperty("schema", out var schema) ||
                !schema.TryGetProperty("openAPIV3Schema", out var rootSchema))
                return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            CollectCustomListMapKeys(rootSchema, string.Empty, result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without schema access, treating CRD lists atomically is safer than
            // inferring Kubernetes merge keys from coincidental property names.
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }
    }

    private static void CollectCustomListMapKeys(
        JsonElement schema,
        string path,
        IDictionary<string, IReadOnlyList<string>> result)
    {
        if (schema.TryGetProperty("x-kubernetes-list-type", out var listType) &&
            listType.GetString() == "map" &&
            schema.TryGetProperty("x-kubernetes-list-map-keys", out var mapKeys) &&
            mapKeys.ValueKind == JsonValueKind.Array)
        {
            var keys = mapKeys.EnumerateArray()
                .Where(key => key.ValueKind == JsonValueKind.String)
                .Select(key => key.GetString()!)
                .ToArray();
            if (keys.Length > 0)
                result[path] = keys;
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
                CollectCustomListMapKeys(property.Value, AppendSchemaPath(path, property.Name), result);
        }

        if (schema.TryGetProperty("items", out var items))
        {
            if (items.ValueKind == JsonValueKind.Object)
                CollectCustomListMapKeys(items, path + "[]", result);
            else if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var itemSchema in items.EnumerateArray())
                    CollectCustomListMapKeys(itemSchema, path + "[]", result);
            }
        }
    }

    private static string AppendSchemaPath(string path, string property)
        => path.Length == 0 ? property : path + "." + property;

    private async Task<DiscoveredResource> DiscoverResourceAsync(
        ManifestIdentity identity,
        bool retryRegistration,
        CancellationToken ct)
    {
        var cacheKey = (identity.ApiVersion, identity.Kind);
        if (_discoveredResources.TryGetValue(cacheKey, out var cached))
            return cached;

        var (group, version) = SplitApiVersion(identity.ApiVersion);
        if (string.IsNullOrEmpty(group))
            throw new KubernetesApiResourceUnsupportedException(identity.ApiVersion, identity.Kind);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var resources = await _client.CustomObjects.GetAPIResourcesAsync(group, version, ct);
                var match = resources.Resources?.SingleOrDefault(resource =>
                    string.Equals(resource.Kind, identity.Kind, StringComparison.Ordinal) &&
                    !resource.Name.Contains('/', StringComparison.Ordinal));

                if (match is null || string.IsNullOrWhiteSpace(match.Name))
                    throw new KubernetesApiResourceNotFoundException(identity.ApiVersion, identity.Kind);

                var discovered = new DiscoveredResource(group, version, match.Name, match.Namespaced);
                _discoveredResources.Add(cacheKey, discovered);
                return discovered;
            }
            catch (Exception ex) when (
                retryRegistration &&
                attempt < DiscoveryRetryAttempts &&
                ex is not OperationCanceledException &&
                IsRetryableDiscoveryFailure(ex))
            {
                await _discoveryDelay(DiscoveryRetryDelay, ct);
            }
        }
    }

    /// <summary>
    /// Discovery not-found is retryable during apply because a CRD created moments earlier
    /// may not have registered its endpoint yet. Other discovery failures are permanent.
    /// </summary>
    private static bool IsRetryableDiscoveryFailure(Exception ex)
        => ex is KubernetesApiResourceNotFoundException
            || (ex is HttpOperationException http && (int)http.Response.StatusCode == 404);

    private static (string Group, string Version) SplitApiVersion(string apiVersion)
    {
        var separator = apiVersion.IndexOf('/');
        return separator < 0
            ? (string.Empty, apiVersion)
            : (apiVersion[..separator], apiVersion[(separator + 1)..]);
    }

    private static void SetResourceVersion(Dictionary<string, object?> item, object existing)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(existing));
        if (!document.RootElement.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("resourceVersion", out var resourceVersion))
            return;

        if (!item.TryGetValue("metadata", out var metadataObject) ||
            metadataObject is not Dictionary<string, object?> itemMetadata)
            return;

        itemMetadata["resourceVersion"] = resourceVersion.GetString();
    }

    private static void SetNamespace(Dictionary<string, object?> item, string? namespaceName)
    {
        if (!item.TryGetValue("metadata", out var metadataObject) ||
            metadataObject is not Dictionary<string, object?> metadata)
            return;

        if (string.IsNullOrWhiteSpace(namespaceName))
            metadata.Remove("namespace");
        else
            metadata["namespace"] = namespaceName;
    }

    private static bool SupportsHealthCheckNodePort(V1ServiceSpec spec)
        => string.Equals(spec.Type, "LoadBalancer", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(spec.ExternalTrafficPolicy, "Local", StringComparison.OrdinalIgnoreCase);

    private async Task<ManifestIdentity> DeleteOneAsync(
        ManifestIdentity identity,
        string propagationPolicy,
        CancellationToken ct)
    {
        try
        {
            var deleteOptions = new V1DeleteOptions { PropagationPolicy = propagationPolicy };
            // Typed delete dispatch (mechanical per-kind arms); the 404 catch below
            // provides shared idempotent-delete semantics, and unknown kinds fall
            // through to discovery-based deletion.
            switch (identity.ApiVersion, identity.Kind)
            {
                // Core v1
                case ("v1", "Namespace"):
                    await _client.CoreV1.DeleteNamespaceAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "ConfigMap"):
                    await _client.CoreV1.DeleteNamespacedConfigMapAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "Secret"):
                    await _client.CoreV1.DeleteNamespacedSecretAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "Service"):
                    await _client.CoreV1.DeleteNamespacedServiceAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "ServiceAccount"):
                    await _client.CoreV1.DeleteNamespacedServiceAccountAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "PersistentVolumeClaim"):
                    await _client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "PersistentVolume"):
                    await _client.CoreV1.DeletePersistentVolumeAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "LimitRange"):
                    await _client.CoreV1.DeleteNamespacedLimitRangeAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "ResourceQuota"):
                    await _client.CoreV1.DeleteNamespacedResourceQuotaAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "Pod"):
                    await _client.CoreV1.DeleteNamespacedPodAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "Endpoints"):
                    await _client.CoreV1.DeleteNamespacedEndpointsAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("v1", "ReplicationController"):
                    await _client.CoreV1.DeleteNamespacedReplicationControllerAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                // apps/v1
                case ("apps/v1", "Deployment"):
                    await _client.AppsV1.DeleteNamespacedDeploymentAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("apps/v1", "StatefulSet"):
                    await _client.AppsV1.DeleteNamespacedStatefulSetAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("apps/v1", "DaemonSet"):
                    await _client.AppsV1.DeleteNamespacedDaemonSetAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("apps/v1", "ReplicaSet"):
                    await _client.AppsV1.DeleteNamespacedReplicaSetAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                // batch/v1
                case ("batch/v1", "Job"):
                    await _client.BatchV1.DeleteNamespacedJobAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("batch/v1", "CronJob"):
                    await _client.BatchV1.DeleteNamespacedCronJobAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                // networking
                case ("networking.k8s.io/v1", "Ingress"):
                    await _client.NetworkingV1.DeleteNamespacedIngressAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("networking.k8s.io/v1", "NetworkPolicy"):
                    await _client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("networking.k8s.io/v1", "IngressClass"):
                    await _client.NetworkingV1.DeleteIngressClassAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                // rbac
                case ("rbac.authorization.k8s.io/v1", "Role"):
                    await _client.RbacAuthorizationV1.DeleteNamespacedRoleAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("rbac.authorization.k8s.io/v1", "RoleBinding"):
                    await _client.RbacAuthorizationV1.DeleteNamespacedRoleBindingAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("rbac.authorization.k8s.io/v1", "ClusterRole"):
                    await _client.RbacAuthorizationV1.DeleteClusterRoleAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("rbac.authorization.k8s.io/v1", "ClusterRoleBinding"):
                    await _client.RbacAuthorizationV1.DeleteClusterRoleBindingAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                // autoscaling
                case ("autoscaling/v2", "HorizontalPodAutoscaler"):
                    await _client.AutoscalingV2.DeleteNamespacedHorizontalPodAutoscalerAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("autoscaling/v1", "HorizontalPodAutoscaler"):
                    await _client.AutoscalingV1.DeleteNamespacedHorizontalPodAutoscalerAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                // policy
                case ("policy/v1", "PodDisruptionBudget"):
                    await _client.PolicyV1.DeleteNamespacedPodDisruptionBudgetAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                // storage
                case ("storage.k8s.io/v1", "StorageClass"):
                    await _client.StorageV1.DeleteStorageClassAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("storage.k8s.io/v1", "CSIDriver"):
                    await _client.StorageV1.DeleteCSIDriverAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("storage.k8s.io/v1", "CSINode"):
                    await _client.StorageV1.DeleteCSINodeAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("storage.k8s.io/v1", "VolumeAttachment"):
                    await _client.StorageV1.DeleteVolumeAttachmentAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                // scheduling
                case ("scheduling.k8s.io/v1", "PriorityClass"):
                    await _client.SchedulingV1.DeletePriorityClassAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                // apiextensions
                case ("apiextensions.k8s.io/v1", "CustomResourceDefinition"):
                    await _client.ApiextensionsV1.DeleteCustomResourceDefinitionAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                // admissionregistration
                case ("admissionregistration.k8s.io/v1", "MutatingWebhookConfiguration"):
                    await _client.AdmissionregistrationV1.DeleteMutatingWebhookConfigurationAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("admissionregistration.k8s.io/v1", "ValidatingWebhookConfiguration"):
                    await _client.AdmissionregistrationV1.DeleteValidatingWebhookConfigurationAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                // apiregistration
                case ("apiregistration.k8s.io/v1", "APIService"):
                    await _client.ApiregistrationV1.DeleteAPIServiceAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                // coordination
                case ("coordination.k8s.io/v1", "Lease"):
                    await _client.CoordinationV1.DeleteNamespacedLeaseAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                // node
                case ("node.k8s.io/v1", "RuntimeClass"):
                    await _client.NodeV1.DeleteRuntimeClassAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                // discovery
                case ("discovery.k8s.io/v1", "EndpointSlice"):
                    await _client.DiscoveryV1.DeleteNamespacedEndpointSliceAsync(identity.Name, identity.Namespace, body: deleteOptions, cancellationToken: ct);
                    break;
                // flowcontrol
                case ("flowcontrol.apiserver.k8s.io/v1", "FlowSchema"):
                    await _client.FlowcontrolApiserverV1.DeleteFlowSchemaAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                case ("flowcontrol.apiserver.k8s.io/v1", "PriorityLevelConfiguration"):
                    await _client.FlowcontrolApiserverV1.DeletePriorityLevelConfigurationAsync(identity.Name, body: deleteOptions, cancellationToken: ct);
                    break;
                default:
                    return await DeleteDiscoveredResourceAsync(identity, deleteOptions, ct);
            }
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            // Already gone. Delete must be idempotent so uninstall of an
            // already-partially-removed release still completes successfully.
        }

        return identity;
    }

    private async Task<ManifestIdentity> DeleteDiscoveredResourceAsync(
        ManifestIdentity identity,
        V1DeleteOptions deleteOptions,
        CancellationToken ct)
    {
        DiscoveredResource resource;
        try
        {
            resource = await DiscoverResourceForDeletionAsync(_client, identity, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new KubernetesResourceOperationException(identity, ex);
        }
        var resolvedApiVersion = $"{resource.Group}/{resource.Version}";
        var resolvedIdentity = resource.Namespaced
            ? identity with { ApiVersion = resolvedApiVersion }
            : identity with { ApiVersion = resolvedApiVersion, Namespace = string.Empty };

        try
        {
            if (resource.Namespaced)
            {
                await _client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                    resource.Group, resource.Version, resolvedIdentity.Namespace, resource.Plural, resolvedIdentity.Name,
                    deleteOptions, null, null, null, null, ct);
            }
            else
            {
                await _client.CustomObjects.DeleteClusterCustomObjectAsync(
                    resource.Group, resource.Version, resource.Plural, resolvedIdentity.Name,
                    deleteOptions, null, null, null, null, ct);
            }
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            // Already gone.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new KubernetesResourceOperationException(resolvedIdentity, ex);
        }

        return resolvedIdentity;
    }

    internal static async Task<DiscoveredResource> DiscoverResourceForDeletionAsync(
        Kubernetes client,
        ManifestIdentity identity,
        CancellationToken cancellationToken)
    {
        var (group, declaredVersion) = SplitApiVersion(identity.ApiVersion);
        if (string.IsNullOrEmpty(group))
            throw new KubernetesApiResourceUnsupportedException(identity.ApiVersion, identity.Kind);

        var declared = await TryDiscoverResourceVersionAsync(
            client,
            group,
            declaredVersion,
            identity.Kind,
            cancellationToken);
        if (declared is not null)
            return declared;

        // CRD disappearance / version-skew edge case: the manifest may name an API
        // version that is no longer served (CRD upgraded or partially removed), while
        // the kind still exists under another served version. Enumerate the group's
        // versions (preferred first) so the delete can still find the resource.
        var groups = await client.Apis.GetAPIVersionsAsync(cancellationToken);
        var apiGroup = groups.Groups?.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, group, StringComparison.Ordinal));
        if (apiGroup is not null)
        {
            var versions = new[] { apiGroup.PreferredVersion?.Version }
                .Concat(apiGroup.Versions?.Select(version => version.Version) ?? [])
                .Where(version => !string.IsNullOrWhiteSpace(version) &&
                                  !string.Equals(version, declaredVersion, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal);
            foreach (var version in versions)
            {
                var alternate = await TryDiscoverResourceVersionAsync(
                    client,
                    group,
                    version!,
                    identity.Kind,
                    cancellationToken);
                if (alternate is not null)
                    return alternate;
            }
        }

        throw new KubernetesApiResourceNotFoundException(identity.ApiVersion, identity.Kind);
    }

    private static async Task<DiscoveredResource?> TryDiscoverResourceVersionAsync(
        Kubernetes client,
        string group,
        string version,
        string kind,
        CancellationToken cancellationToken)
    {
        V1APIResourceList resources;
        try
        {
            resources = await client.CustomObjects.GetAPIResourcesAsync(group, version, cancellationToken);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            // The whole group/version is unpublished (e.g. its CRD was deleted);
            // report "not here" so callers can try alternate served versions.
            return null;
        }

        var match = resources.Resources?.SingleOrDefault(resource =>
            string.Equals(resource.Kind, kind, StringComparison.Ordinal) &&
            !resource.Name.Contains('/', StringComparison.Ordinal));
        return match is null || string.IsNullOrWhiteSpace(match.Name)
            ? null
            : new DiscoveredResource(group, version, match.Name, match.Namespaced);
    }

    private async Task UpsertNamespacedAsync<T>(
        Func<Task<T>> read,
        Func<Task<T>> create,
        Func<T, Task<T>> replace,
        ManifestIdentity identity,
        string desiredYaml,
        Action throwIfCancellationRequested,
        Action disableCancellationForRecovery,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys = null)
    {
        // 404 from the read means "not created yet" — the create path. Replace is
        // preferred over patch so the applied manifest fully defines the object,
        // matching Helm's create-or-replace semantics (resourceVersion is set by callers).
        try
        {
            var existing = await read();
            // Keep cancellation before the write outside ambiguous-write recovery.
            throwIfCancellationRequested();
            try
            {
                await replace(existing);
            }
            catch (Exception ex) when (IsAmbiguousWriteFailure(ex))
            {
                if (ex is OperationCanceledException)
                    // The request was invoked, so finish read-back without the canceled operation token.
                    disableCancellationForRecovery();
                _lastApplyMayHaveCommitted |= await ReplaceMayHaveCommittedAsync(read, existing, identity, desiredYaml, customListMapKeys);
                throw;
            }
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            // Do not dispatch a create that was already canceled after the initial read.
            throwIfCancellationRequested();
            try
            {
                await create();
            }
            catch (Exception writeError) when (IsAmbiguousWriteFailure(writeError))
            {
                if (writeError is OperationCanceledException)
                    // The create may have committed before cancellation interrupted its response.
                    disableCancellationForRecovery();
                _lastApplyMayHaveCommitted |= await CreateMayHaveCommittedAsync(read, identity, desiredYaml);
                throw;
            }
        }
    }

    private Task UpsertClusterAsync<T>(
        Func<Task<T>> read,
        Func<Task<T>> create,
        Func<T, Task<T>> replace,
        ManifestIdentity identity,
        string desiredYaml,
        Action throwIfCancellationRequested,
        Action disableCancellationForRecovery,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys = null)
        => UpsertNamespacedAsync(read, create, replace, identity, desiredYaml, throwIfCancellationRequested, disableCancellationForRecovery, customListMapKeys);

    private static async Task<bool> CreateMayHaveCommittedAsync<T>(
        Func<Task<T>> read,
        ManifestIdentity identity,
        string desiredYaml)
    {
        try
        {
            var after = await read();
            // A different actor may have created this identity after our initial 404.
            // Only count the ambiguous create as applied when the read-back matches our payload.
            return ContainsExpectedManifest(
                NormalizeSecretStringData(identity, desiredYaml),
                after!,
                TryGetTypedResourceScope(identity, out _),
                identity.Kind);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAmbiguousWriteFailure(Exception exception)
    {
        if (exception is not HttpOperationException http)
            return true;

        var statusCode = (int)http.Response.StatusCode;
        return statusCode == 0 || statusCode >= 500;
    }

    private async Task<bool> ReplaceMayHaveCommittedAsync<T>(
        Func<Task<T>> read,
        T before,
        ManifestIdentity identity,
        string desiredYaml,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys)
    {
        try
        {
            var after = await read();
            var beforeVersion = GetResourceVersion(before);
            var afterVersion = GetResourceVersion(after);
            if (beforeVersion is null || afterVersion is null ||
                string.Equals(beforeVersion, afterVersion, StringComparison.Ordinal))
            {
                return false;
            }

            // A controller can advance resourceVersion independently of this replace.
            // Only record progress if the read-back also reflects the chart-owned payload.
            var expectedYaml = MergeWithLiveResource(identity, desiredYaml, before!);
            var typedResource = TryGetTypedResourceScope(identity, out _);
            return ContainsExpectedManifest(expectedYaml, after!, typedResource, identity.Kind) &&
                   RemovedValuesAreAbsent(expectedYaml, before!, after!, typedResource, customListMapKeys);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsExpectedManifest<T>(string expectedYaml, T actualResource, bool allowCaseInsensitiveProperties, string resourceKind)
    {
        using var expected = JsonDocument.Parse(JsonSerializer.Serialize(
            HelmYaml.DeserializeDictionary(expectedYaml, StringComparer.Ordinal)));
        using var actual = JsonDocument.Parse(JsonSerializer.Serialize(actualResource));
        return ContainsExpectedValue(
            expected.RootElement,
            actual.RootElement,
            parentName: null,
            allowCaseInsensitiveProperties,
            isTypedResource: allowCaseInsensitiveProperties,
            inQuantityMap: false,
            resourceKind);
    }

    private static bool RemovedValuesAreAbsent<T>(
        string expectedYaml,
        T beforeResource,
        T actualResource,
        bool allowCaseInsensitiveProperties,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys)
    {
        using var expected = JsonDocument.Parse(JsonSerializer.Serialize(
            HelmYaml.DeserializeDictionary(expectedYaml, StringComparer.Ordinal)));
        using var before = JsonDocument.Parse(JsonSerializer.Serialize(beforeResource));
        using var actual = JsonDocument.Parse(JsonSerializer.Serialize(actualResource));
        return RemovedValuesAreAbsent(
            expected.RootElement,
            before.RootElement,
            actual.RootElement,
            parentName: null,
            allowCaseInsensitiveProperties,
            path: string.Empty,
            customListMapKeys);
    }

    private static bool ContainsExpectedValue(
        JsonElement expected,
        JsonElement actual,
        string? parentName,
        bool allowCaseInsensitiveProperties,
        bool isTypedResource,
        bool inQuantityMap,
        string resourceKind)
    {
        if (expected.ValueKind == JsonValueKind.Object)
        {
            if (actual.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var property in expected.EnumerateObject())
            {
                if (IsServerManagedField(parentName, property.Name))
                    continue;

                if (!TryGetPropertyExactThenIgnoreCase(actual, property.Name, allowCaseInsensitiveProperties, out var actualValue) ||
                    !ContainsExpectedValue(
                        property.Value,
                        actualValue,
                        property.Name,
                        allowCaseInsensitiveProperties,
                        isTypedResource,
                        inQuantityMap || IsQuantityMap(property.Name, parentName, resourceKind) ||
                        resourceKind == "HorizontalPodAutoscaler" && parentName == "target" &&
                        property.Name is "averageValue" or "value",
                        resourceKind))
                {
                    return false;
                }
            }

            return true;
        }

        if (expected.ValueKind == JsonValueKind.Array)
        {
            if (actual.ValueKind != JsonValueKind.Array)
                return false;

            var expectedItems = expected.EnumerateArray().ToArray();
            var actualItems = actual.EnumerateArray().ToArray();
            // Preserve submitted order while allowing admission webhooks to insert extra entries.
            var actualIndex = 0;
            foreach (var expectedItem in expectedItems)
            {
                while (actualIndex < actualItems.Length &&
                       !ContainsExpectedValue(expectedItem, actualItems[actualIndex], parentName, allowCaseInsensitiveProperties, isTypedResource, inQuantityMap, resourceKind))
                {
                    actualIndex++;
                }

                if (actualIndex == actualItems.Length)
                    return false;

                actualIndex++;
            }

            return true;
        }

        if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
            return expected.GetDecimal() == actual.GetDecimal();

        if (isTypedResource && inQuantityMap &&
            expected.ValueKind == JsonValueKind.String && actual.ValueKind == JsonValueKind.String)
        {
            try
            {
                // Quantity strings can differ while representing the same fixed-point value.
                var expectedQuantity = new ResourceQuantity(expected.GetString()!).ToString();
                var actualQuantity = new ResourceQuantity(actual.GetString()!).ToString();
                return string.Equals(expectedQuantity, actualQuantity, StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                // Invalid quantity text falls back to exact comparison below.
            }
        }

        return expected.ValueKind == actual.ValueKind &&
               string.Equals(expected.ToString(), actual.ToString(), StringComparison.Ordinal);
    }

    private static bool RemovedValuesAreAbsent(
        JsonElement expected,
        JsonElement before,
        JsonElement actual,
        string? parentName,
        bool allowCaseInsensitiveProperties,
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys)
    {
        if (before.ValueKind == JsonValueKind.Object && expected.ValueKind == JsonValueKind.Object)
        {
            foreach (var beforeProperty in before.EnumerateObject())
            {
                if (IsServerManagedField(parentName, beforeProperty.Name) || beforeProperty.Value.ValueKind == JsonValueKind.Null)
                    continue;

                if (!expected.TryGetProperty(beforeProperty.Name, out var expectedValue))
                {
                    if (TryGetPropertyExactThenIgnoreCase(actual, beforeProperty.Name, allowCaseInsensitiveProperties, out _))
                        return false;
                    continue;
                }

                if (TryGetPropertyExactThenIgnoreCase(actual, beforeProperty.Name, allowCaseInsensitiveProperties, out var actualValue) &&
                    beforeProperty.Value.ValueKind == JsonValueKind.Object && expectedValue.ValueKind == JsonValueKind.Object &&
                    !RemovedValuesAreAbsent(expectedValue, beforeProperty.Value, actualValue, beforeProperty.Name, allowCaseInsensitiveProperties, AppendJsonPath(path, beforeProperty.Name), customListMapKeys))
                {
                    return false;
                }

                if (TryGetPropertyExactThenIgnoreCase(actual, beforeProperty.Name, allowCaseInsensitiveProperties, out actualValue) &&
                    beforeProperty.Value.ValueKind == JsonValueKind.Array && expectedValue.ValueKind == JsonValueKind.Array &&
                    !RemovedArrayValuesAreAbsent(expectedValue, beforeProperty.Value, actualValue, AppendJsonPath(path, beforeProperty.Name), customListMapKeys))
                {
                    return false;
                }
            }

            return true;
        }

        if (before.ValueKind == JsonValueKind.Array && expected.ValueKind == JsonValueKind.Array)
            return RemovedArrayValuesAreAbsent(expected, before, actual, path, customListMapKeys);

        return true;
    }

    private static bool RemovedArrayValuesAreAbsent(
        JsonElement expected,
        JsonElement before,
        JsonElement actual,
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customListMapKeys)
    {
        if (actual.ValueKind != JsonValueKind.Array)
            return false;

        var expectedItems = expected.EnumerateArray().ToArray();
        var keyFields = SelectArrayIdentityKeys(
            expectedItems,
            before.EnumerateArray().ToArray(),
            actual.EnumerateArray().ToArray(),
            ThreeWayMerge.GetListMapKeySets(path, customListMapKeys));
        foreach (var beforeItem in before.EnumerateArray())
        {
            var expectedItem = expectedItems.FirstOrDefault(candidate => SameArrayItem(candidate, beforeItem, keyFields));
            if (expectedItem.ValueKind != JsonValueKind.Undefined)
            {
                if (keyFields is not null)
                {
                    var actualItem = actual.EnumerateArray().FirstOrDefault(candidate => SameArrayItem(candidate, beforeItem, keyFields));
                    if (actualItem.ValueKind != JsonValueKind.Undefined &&
                        !RemovedValuesAreAbsent(
                            expectedItem,
                            beforeItem,
                            actualItem,
                            parentName: null,
                            allowCaseInsensitiveProperties: false,
                            path + "[]",
                            customListMapKeys))
                        return false;
                }

                continue;
            }

            if (actual.EnumerateArray().Any(actualItem => SameArrayItem(actualItem, beforeItem, keyFields)))
                return false;
        }

        return true;
    }

    private static string[]? SelectArrayIdentityKeys(
        IReadOnlyList<JsonElement> expected,
        IReadOnlyList<JsonElement> before,
        IReadOnlyList<JsonElement> actual,
        IReadOnlyList<string[]> keySets)
    {
        foreach (var keySet in keySets)
        {
            if (HasUniqueArrayIdentity(expected, keySet) &&
                HasUniqueArrayIdentity(before, keySet) &&
                HasUniqueArrayIdentity(actual, keySet))
                return keySet;
        }

        return null;
    }

    private static bool HasUniqueArrayIdentity(IReadOnlyList<JsonElement> items, IReadOnlyList<string> keyFields)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;

            var values = new List<string>(keyFields.Count);
            foreach (var keyField in keyFields)
            {
                if (!item.TryGetProperty(keyField, out var value))
                    return false;
                values.Add(value.GetRawText());
            }

            if (!identities.Add(string.Join("\u001f", values)))
                return false;
        }

        return true;
    }

    private static bool SameArrayItem(JsonElement left, JsonElement right, IReadOnlyList<string>? keyFields)
    {
        if (keyFields is not null && left.ValueKind == JsonValueKind.Object && right.ValueKind == JsonValueKind.Object)
        {
            return keyFields.All(key =>
                left.TryGetProperty(key, out var leftValue) &&
                right.TryGetProperty(key, out var rightValue) &&
                JsonValuesEqual(leftValue, rightValue));
        }

        return JsonValuesEqual(left, right);
    }

    private static string AppendJsonPath(string path, string property)
        => path.Length == 0 ? property : path + "." + property;

    private static bool JsonValuesEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number &&
                   left.GetDecimal() == right.GetDecimal();

        if (left.ValueKind == JsonValueKind.Object)
        {
            var leftProperties = left.EnumerateObject().ToArray();
            var rightProperties = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            return leftProperties.Length == rightProperties.Count &&
                   leftProperties.All(property => rightProperties.TryGetValue(property.Name, out var value) && JsonValuesEqual(property.Value, value));
        }

        if (left.ValueKind == JsonValueKind.Array)
        {
            var leftItems = left.EnumerateArray().ToArray();
            var rightItems = right.EnumerateArray().ToArray();
            return leftItems.Length == rightItems.Length && leftItems.Zip(rightItems).All(pair => JsonValuesEqual(pair.First, pair.Second));
        }

        return left.ValueKind == JsonValueKind.Number
            ? left.GetDecimal() == right.GetDecimal()
            : string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
    }

    private static bool IsQuantityMap(string? propertyName, string? parentName, string resourceKind)
        => propertyName is "requests" or "limits" or "capacity" or "allocatable" ||
           resourceKind == "ResourceQuota" && parentName == "spec" && propertyName is "hard" or "used";

    private static bool IsServerManagedField(string? parentName, string propertyName)
        => parentName is null && string.Equals(propertyName, "status", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(parentName, "metadata", StringComparison.OrdinalIgnoreCase) &&
           propertyName is "resourceVersion" or "uid" or "generation" or "creationTimestamp" or "managedFields" or "selfLink";

    private static bool TryGetPropertyExactThenIgnoreCase(JsonElement element, string name, bool allowCaseInsensitiveProperties, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;

        if (allowCaseInsensitiveProperties && TryGetPropertyIgnoreCase(element, name, out value))
            return true;

        value = default;
        return false;
    }

    private static string? GetResourceVersion<T>(T resource)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(resource));
        if (!TryGetPropertyIgnoreCase(document.RootElement, "metadata", out var metadata) ||
            !TryGetPropertyIgnoreCase(metadata, "resourceVersion", out var resourceVersion))
        {
            return null;
        }

        return resourceVersion.ValueKind == JsonValueKind.String ? resourceVersion.GetString() : null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
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

    /// <summary>
    /// Splits a multi-document YAML manifest on <c>---</c> separators.
    /// Blank and comment-only documents are dropped.
    /// </summary>
    public static IEnumerable<string> SplitDocumentsPublic(string manifest)
        => SplitDocuments(manifest);
}

internal sealed record DiscoveredResource(string Group, string Version, string Plural, bool Namespaced);

internal sealed class KubernetesApiResourceNotFoundException(string apiVersion, string kind)
    : Exception($"Kubernetes API discovery did not find resource {apiVersion}/{kind}.")
// ReSharper disable once RedundantTypeDeclarationBody // Multi-targeted .NET 8/9 builds require an explicit class body here.
{
}

internal sealed class KubernetesApiResourceUnsupportedException(string apiVersion, string kind)
    : InvalidOperationException($"Kubernetes core resource {apiVersion}/{kind} is not supported by the typed manifest applier.")
// ReSharper disable once RedundantTypeDeclarationBody // Multi-targeted .NET 8/9 builds require an explicit class body here.
{
}

internal sealed class KubernetesResourceOperationException(ManifestIdentity identity, Exception innerException)
    : InvalidOperationException($"Kubernetes operation failed for {identity.ApiVersion} {identity.DisplayName}.", innerException)
// ReSharper disable once RedundantTypeDeclarationBody // Multi-targeted .NET 8/9 builds require an explicit class body here.
{
}

/// <summary>
/// Identity of a Kubernetes resource extracted from a manifest document.
/// </summary>
/// <param name="ApiVersion">Declared <c>apiVersion</c>, for example <c>apps/v1</c>.</param>
/// <param name="Kind">Declared <c>kind</c>, for example <c>Deployment</c>.</param>
/// <param name="Name">Resource name from <c>metadata.name</c>.</param>
/// <param name="Namespace">
/// Effective namespace: <c>metadata.namespace</c>, else the caller's default.
/// Empty for cluster-scoped kinds.
/// </param>
public sealed record ManifestIdentity(string ApiVersion, string Kind, string Name, string Namespace)
{
    /// <summary>Display form used in progress messages: <c>Kind/Name</c> or <c>Kind/Namespace/Name</c>.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Namespace)
        ? $"{Kind}/{Name}"
        : $"{Kind}/{Namespace}/{Name}";

    /// <summary>
    /// Parses a manifest document into an identity. Returns null for documents that
    /// lack apiVersion, kind, or metadata.name (for example empty or comment docs).
    /// </summary>
    public static ManifestIdentity? Parse(string yaml, string defaultNamespace)
    {
        var doc = HelmYaml.DeserializeDictionary(yaml);
        var apiVersion = HelmYaml.GetString(doc, "apiVersion");
        var kind = HelmYaml.GetString(doc, "kind");
        if (string.IsNullOrWhiteSpace(apiVersion) || string.IsNullOrWhiteSpace(kind))
            return null;

        if (!doc.TryGetValue("metadata", out var metadataObj) ||
            metadataObj is not Dictionary<string, object?> metadata)
            return null;

        var name = HelmYaml.GetString(metadata, "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var ns = HelmYaml.GetString(metadata, "namespace") ?? defaultNamespace;
        return KubernetesManifestApplier.NormalizeTypedIdentity(
            new ManifestIdentity(apiVersion, kind, name, ns));
    }
}

