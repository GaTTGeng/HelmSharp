using System.Runtime.CompilerServices;
using System.Text.Json;
using HelmSharp.Chart;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace HelmSharp.Kube;

public sealed class KubernetesManifestApplier
{
    private readonly k8s.Kubernetes _client;
    private readonly string _fieldManager;
    private readonly Dictionary<(string ApiVersion, string Kind), DiscoveredResource> _discoveredResources = new();

    public KubernetesManifestApplier(k8s.Kubernetes client, string fieldManager)
    {
        _client = client;
        _fieldManager = fieldManager;
    }

    public static async Task EnsureNamespaceAsync(
        k8s.Kubernetes client,
        string name,
        CancellationToken cancellationToken)
    {
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

    public async IAsyncEnumerable<string> ApplyAsync(
        string manifest,
        string defaultNamespace,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var doc in SplitDocuments(manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = ManifestIdentity.Parse(doc, defaultNamespace);
            if (identity is null)
                continue;

            await ApplyOneAsync(identity, doc, cancellationToken);
            yield return identity.DisplayName;
        }
    }

    public async IAsyncEnumerable<string> DeleteAsync(
        string manifest,
        string defaultNamespace,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var resource in DeleteAsync(manifest, defaultNamespace, propagationPolicy: null, cancellationToken))
            yield return resource;
    }

    /// <summary>Deletes resources in reverse manifest order with the requested dependent-resource propagation policy.</summary>
    public async IAsyncEnumerable<string> DeleteAsync(
        string manifest,
        string defaultNamespace,
        string? propagationPolicy,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var doc in SplitDocuments(manifest).Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = ManifestIdentity.Parse(doc, defaultNamespace);
            if (identity is null)
                continue;

            await DeleteOneAsync(identity, propagationPolicy, cancellationToken);
            yield return identity.DisplayName;
        }
    }

    private async Task ApplyOneAsync(ManifestIdentity identity, string yaml, CancellationToken ct)
    {
        switch (identity.ApiVersion, identity.Kind)
        {
            // ─── Core v1 ───
            case ("v1", "Namespace"):
                await UpsertClusterAsync(
                    () => _client.CoreV1.ReadNamespaceAsync(identity.Name, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespaceAsync(KubernetesYaml.Deserialize<V1Namespace>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1Namespace>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespaceAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("v1", "ConfigMap"):
                await UpsertNamespacedAsync(
                    () => _client.CoreV1.ReadNamespacedConfigMapAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedConfigMapAsync(KubernetesYaml.Deserialize<V1ConfigMap>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1ConfigMap>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedConfigMapAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "Secret"):
                await UpsertNamespacedAsync(
                    () => _client.CoreV1.ReadNamespacedSecretAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedSecretAsync(KubernetesYaml.Deserialize<V1Secret>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1Secret>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedSecretAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "Service"):
                await UpsertNamespacedAsync(
                    () => _client.CoreV1.ReadNamespacedServiceAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedServiceAsync(KubernetesYaml.Deserialize<V1Service>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1Service>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        item.Spec.ClusterIP = existing.Spec.ClusterIP;
                        item.Spec.ClusterIPs = existing.Spec.ClusterIPs;
                        return _client.CoreV1.ReplaceNamespacedServiceAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "ServiceAccount"):
                await UpsertNamespacedAsync(
                    () => _client.CoreV1.ReadNamespacedServiceAccountAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedServiceAccountAsync(KubernetesYaml.Deserialize<V1ServiceAccount>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1ServiceAccount>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedServiceAccountAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "PersistentVolumeClaim"):
                await UpsertNamespacedAsync(
                    () => _client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(KubernetesYaml.Deserialize<V1PersistentVolumeClaim>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1PersistentVolumeClaim>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedPersistentVolumeClaimAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "PersistentVolume"):
                await UpsertClusterAsync(
                    () => _client.CoreV1.ReadPersistentVolumeAsync(identity.Name, cancellationToken: ct),
                    () => _client.CoreV1.CreatePersistentVolumeAsync(KubernetesYaml.Deserialize<V1PersistentVolume>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1PersistentVolume>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplacePersistentVolumeAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("v1", "LimitRange"):
                await UpsertNamespacedAsync(
                    () => _client.CoreV1.ReadNamespacedLimitRangeAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedLimitRangeAsync(KubernetesYaml.Deserialize<V1LimitRange>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1LimitRange>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedLimitRangeAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "ResourceQuota"):
                await UpsertNamespacedAsync(
                    () => _client.CoreV1.ReadNamespacedResourceQuotaAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedResourceQuotaAsync(KubernetesYaml.Deserialize<V1ResourceQuota>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1ResourceQuota>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedResourceQuotaAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "Pod"):
                await UpsertNamespacedAsync(
                    () => _client.CoreV1.ReadNamespacedPodAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedPodAsync(KubernetesYaml.Deserialize<V1Pod>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1Pod>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedPodAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "Endpoints"):
                await UpsertNamespacedAsync(
                    () => _client.CoreV1.ReadNamespacedEndpointsAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedEndpointsAsync(KubernetesYaml.Deserialize<V1Endpoints>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1Endpoints>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedEndpointsAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("v1", "ReplicationController"):
                await UpsertNamespacedAsync(
                    () => _client.CoreV1.ReadNamespacedReplicationControllerAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoreV1.CreateNamespacedReplicationControllerAsync(KubernetesYaml.Deserialize<V1ReplicationController>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1ReplicationController>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoreV1.ReplaceNamespacedReplicationControllerAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── apps/v1 ───
            case ("apps/v1", "Deployment"):
                await UpsertNamespacedAsync(
                    () => _client.AppsV1.ReadNamespacedDeploymentAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AppsV1.CreateNamespacedDeploymentAsync(KubernetesYaml.Deserialize<V1Deployment>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1Deployment>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AppsV1.ReplaceNamespacedDeploymentAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("apps/v1", "StatefulSet"):
                await UpsertNamespacedAsync(
                    () => _client.AppsV1.ReadNamespacedStatefulSetAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AppsV1.CreateNamespacedStatefulSetAsync(KubernetesYaml.Deserialize<V1StatefulSet>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1StatefulSet>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AppsV1.ReplaceNamespacedStatefulSetAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("apps/v1", "DaemonSet"):
                await UpsertNamespacedAsync(
                    () => _client.AppsV1.ReadNamespacedDaemonSetAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AppsV1.CreateNamespacedDaemonSetAsync(KubernetesYaml.Deserialize<V1DaemonSet>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1DaemonSet>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AppsV1.ReplaceNamespacedDaemonSetAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("apps/v1", "ReplicaSet"):
                await UpsertNamespacedAsync(
                    () => _client.AppsV1.ReadNamespacedReplicaSetAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AppsV1.CreateNamespacedReplicaSetAsync(KubernetesYaml.Deserialize<V1ReplicaSet>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1ReplicaSet>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AppsV1.ReplaceNamespacedReplicaSetAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── batch/v1 ───
            case ("batch/v1", "Job"):
                await UpsertNamespacedAsync(
                    () => _client.BatchV1.ReadNamespacedJobAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.BatchV1.CreateNamespacedJobAsync(KubernetesYaml.Deserialize<V1Job>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1Job>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        item.Spec.Selector = existing.Spec.Selector;
                        return _client.BatchV1.ReplaceNamespacedJobAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("batch/v1", "CronJob"):
                await UpsertNamespacedAsync(
                    () => _client.BatchV1.ReadNamespacedCronJobAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.BatchV1.CreateNamespacedCronJobAsync(KubernetesYaml.Deserialize<V1CronJob>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1CronJob>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.BatchV1.ReplaceNamespacedCronJobAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── networking.k8s.io/v1 ───
            case ("networking.k8s.io/v1", "Ingress"):
                await UpsertNamespacedAsync(
                    () => _client.NetworkingV1.ReadNamespacedIngressAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.NetworkingV1.CreateNamespacedIngressAsync(KubernetesYaml.Deserialize<V1Ingress>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1Ingress>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.NetworkingV1.ReplaceNamespacedIngressAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("networking.k8s.io/v1", "NetworkPolicy"):
                await UpsertNamespacedAsync(
                    () => _client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(KubernetesYaml.Deserialize<V1NetworkPolicy>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1NetworkPolicy>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.NetworkingV1.ReplaceNamespacedNetworkPolicyAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("networking.k8s.io/v1", "IngressClass"):
                await UpsertClusterAsync(
                    () => _client.NetworkingV1.ReadIngressClassAsync(identity.Name, cancellationToken: ct),
                    () => _client.NetworkingV1.CreateIngressClassAsync(KubernetesYaml.Deserialize<V1IngressClass>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1IngressClass>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.NetworkingV1.ReplaceIngressClassAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── rbac.authorization.k8s.io/v1 ───
            case ("rbac.authorization.k8s.io/v1", "Role"):
                await UpsertNamespacedAsync(
                    () => _client.RbacAuthorizationV1.ReadNamespacedRoleAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.RbacAuthorizationV1.CreateNamespacedRoleAsync(KubernetesYaml.Deserialize<V1Role>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1Role>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.RbacAuthorizationV1.ReplaceNamespacedRoleAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("rbac.authorization.k8s.io/v1", "RoleBinding"):
                await UpsertNamespacedAsync(
                    () => _client.RbacAuthorizationV1.ReadNamespacedRoleBindingAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.RbacAuthorizationV1.CreateNamespacedRoleBindingAsync(KubernetesYaml.Deserialize<V1RoleBinding>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1RoleBinding>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.RbacAuthorizationV1.ReplaceNamespacedRoleBindingAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("rbac.authorization.k8s.io/v1", "ClusterRole"):
                await UpsertClusterAsync(
                    () => _client.RbacAuthorizationV1.ReadClusterRoleAsync(identity.Name, cancellationToken: ct),
                    () => _client.RbacAuthorizationV1.CreateClusterRoleAsync(KubernetesYaml.Deserialize<V1ClusterRole>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1ClusterRole>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.RbacAuthorizationV1.ReplaceClusterRoleAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("rbac.authorization.k8s.io/v1", "ClusterRoleBinding"):
                await UpsertClusterAsync(
                    () => _client.RbacAuthorizationV1.ReadClusterRoleBindingAsync(identity.Name, cancellationToken: ct),
                    () => _client.RbacAuthorizationV1.CreateClusterRoleBindingAsync(KubernetesYaml.Deserialize<V1ClusterRoleBinding>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1ClusterRoleBinding>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.RbacAuthorizationV1.ReplaceClusterRoleBindingAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── autoscaling/v2 ───
            case ("autoscaling/v2", "HorizontalPodAutoscaler"):
            case ("autoscaling/v2beta2", "HorizontalPodAutoscaler"):
                await UpsertNamespacedAsync(
                    () => _client.AutoscalingV2.ReadNamespacedHorizontalPodAutoscalerAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AutoscalingV2.CreateNamespacedHorizontalPodAutoscalerAsync(KubernetesYaml.Deserialize<V2HorizontalPodAutoscaler>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V2HorizontalPodAutoscaler>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AutoscalingV2.ReplaceNamespacedHorizontalPodAutoscalerAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;
            case ("autoscaling/v1", "HorizontalPodAutoscaler"):
                await UpsertNamespacedAsync(
                    () => _client.AutoscalingV1.ReadNamespacedHorizontalPodAutoscalerAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.AutoscalingV1.CreateNamespacedHorizontalPodAutoscalerAsync(KubernetesYaml.Deserialize<V1HorizontalPodAutoscaler>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1HorizontalPodAutoscaler>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AutoscalingV1.ReplaceNamespacedHorizontalPodAutoscalerAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── policy/v1 ───
            case ("policy/v1", "PodDisruptionBudget"):
                await UpsertNamespacedAsync(
                    () => _client.PolicyV1.ReadNamespacedPodDisruptionBudgetAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.PolicyV1.CreateNamespacedPodDisruptionBudgetAsync(KubernetesYaml.Deserialize<V1PodDisruptionBudget>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1PodDisruptionBudget>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.PolicyV1.ReplaceNamespacedPodDisruptionBudgetAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── storage.k8s.io/v1 ───
            case ("storage.k8s.io/v1", "StorageClass"):
                await UpsertClusterAsync(
                    () => _client.StorageV1.ReadStorageClassAsync(identity.Name, cancellationToken: ct),
                    () => _client.StorageV1.CreateStorageClassAsync(KubernetesYaml.Deserialize<V1StorageClass>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1StorageClass>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.StorageV1.ReplaceStorageClassAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("storage.k8s.io/v1", "CSIDriver"):
                await UpsertClusterAsync(
                    () => _client.StorageV1.ReadCSIDriverAsync(identity.Name, cancellationToken: ct),
                    () => _client.StorageV1.CreateCSIDriverAsync(KubernetesYaml.Deserialize<V1CSIDriver>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1CSIDriver>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.StorageV1.ReplaceCSIDriverAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("storage.k8s.io/v1", "CSINode"):
                await UpsertClusterAsync(
                    () => _client.StorageV1.ReadCSINodeAsync(identity.Name, cancellationToken: ct),
                    () => _client.StorageV1.CreateCSINodeAsync(KubernetesYaml.Deserialize<V1CSINode>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1CSINode>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.StorageV1.ReplaceCSINodeAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("storage.k8s.io/v1", "VolumeAttachment"):
                await UpsertClusterAsync(
                    () => _client.StorageV1.ReadVolumeAttachmentAsync(identity.Name, cancellationToken: ct),
                    () => _client.StorageV1.CreateVolumeAttachmentAsync(KubernetesYaml.Deserialize<V1VolumeAttachment>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1VolumeAttachment>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.StorageV1.ReplaceVolumeAttachmentAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── scheduling.k8s.io/v1 ───
            case ("scheduling.k8s.io/v1", "PriorityClass"):
                await UpsertClusterAsync(
                    () => _client.SchedulingV1.ReadPriorityClassAsync(identity.Name, cancellationToken: ct),
                    () => _client.SchedulingV1.CreatePriorityClassAsync(KubernetesYaml.Deserialize<V1PriorityClass>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1PriorityClass>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.SchedulingV1.ReplacePriorityClassAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── apiextensions.k8s.io/v1 ───
            case ("apiextensions.k8s.io/v1", "CustomResourceDefinition"):
                await UpsertClusterAsync(
                    () => _client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(identity.Name, cancellationToken: ct),
                    () => _client.ApiextensionsV1.CreateCustomResourceDefinitionAsync(KubernetesYaml.Deserialize<V1CustomResourceDefinition>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1CustomResourceDefinition>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.ApiextensionsV1.ReplaceCustomResourceDefinitionAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── admissionregistration.k8s.io/v1 ───
            case ("admissionregistration.k8s.io/v1", "MutatingWebhookConfiguration"):
                await UpsertClusterAsync(
                    () => _client.AdmissionregistrationV1.ReadMutatingWebhookConfigurationAsync(identity.Name, cancellationToken: ct),
                    () => _client.AdmissionregistrationV1.CreateMutatingWebhookConfigurationAsync(KubernetesYaml.Deserialize<V1MutatingWebhookConfiguration>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1MutatingWebhookConfiguration>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AdmissionregistrationV1.ReplaceMutatingWebhookConfigurationAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("admissionregistration.k8s.io/v1", "ValidatingWebhookConfiguration"):
                await UpsertClusterAsync(
                    () => _client.AdmissionregistrationV1.ReadValidatingWebhookConfigurationAsync(identity.Name, cancellationToken: ct),
                    () => _client.AdmissionregistrationV1.CreateValidatingWebhookConfigurationAsync(KubernetesYaml.Deserialize<V1ValidatingWebhookConfiguration>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1ValidatingWebhookConfiguration>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.AdmissionregistrationV1.ReplaceValidatingWebhookConfigurationAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── apiregistration.k8s.io/v1 ───
            case ("apiregistration.k8s.io/v1", "APIService"):
                await UpsertClusterAsync(
                    () => _client.ApiregistrationV1.ReadAPIServiceAsync(identity.Name, cancellationToken: ct),
                    () => _client.ApiregistrationV1.CreateAPIServiceAsync(KubernetesYaml.Deserialize<V1APIService>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1APIService>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.ApiregistrationV1.ReplaceAPIServiceAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── coordination.k8s.io/v1 ───
            case ("coordination.k8s.io/v1", "Lease"):
                await UpsertNamespacedAsync(
                    () => _client.CoordinationV1.ReadNamespacedLeaseAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.CoordinationV1.CreateNamespacedLeaseAsync(KubernetesYaml.Deserialize<V1Lease>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1Lease>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.CoordinationV1.ReplaceNamespacedLeaseAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── node.k8s.io/v1 ───
            case ("node.k8s.io/v1", "RuntimeClass"):
                await UpsertClusterAsync(
                    () => _client.NodeV1.ReadRuntimeClassAsync(identity.Name, cancellationToken: ct),
                    () => _client.NodeV1.CreateRuntimeClassAsync(KubernetesYaml.Deserialize<V1RuntimeClass>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1RuntimeClass>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.NodeV1.ReplaceRuntimeClassAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            // ─── discovery.k8s.io/v1 ───
            case ("discovery.k8s.io/v1", "EndpointSlice"):
                await UpsertNamespacedAsync(
                    () => _client.DiscoveryV1.ReadNamespacedEndpointSliceAsync(identity.Name, identity.Namespace, cancellationToken: ct),
                    () => _client.DiscoveryV1.CreateNamespacedEndpointSliceAsync(KubernetesYaml.Deserialize<V1EndpointSlice>(yaml, false), identity.Namespace, cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1EndpointSlice>(yaml, false);
                        item.Metadata.NamespaceProperty = identity.Namespace;
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.DiscoveryV1.ReplaceNamespacedEndpointSliceAsync(item, identity.Name, identity.Namespace, cancellationToken: ct);
                    });
                break;

            // ─── flowcontrol.apiserver.k8s.io/v1 ───
            case ("flowcontrol.apiserver.k8s.io/v1", "FlowSchema"):
                await UpsertClusterAsync(
                    () => _client.FlowcontrolApiserverV1.ReadFlowSchemaAsync(identity.Name, cancellationToken: ct),
                    () => _client.FlowcontrolApiserverV1.CreateFlowSchemaAsync(KubernetesYaml.Deserialize<V1FlowSchema>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1FlowSchema>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.FlowcontrolApiserverV1.ReplaceFlowSchemaAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;
            case ("flowcontrol.apiserver.k8s.io/v1", "PriorityLevelConfiguration"):
                await UpsertClusterAsync(
                    () => _client.FlowcontrolApiserverV1.ReadPriorityLevelConfigurationAsync(identity.Name, cancellationToken: ct),
                    () => _client.FlowcontrolApiserverV1.CreatePriorityLevelConfigurationAsync(KubernetesYaml.Deserialize<V1PriorityLevelConfiguration>(yaml, false), cancellationToken: ct),
                    existing =>
                    {
                        var item = KubernetesYaml.Deserialize<V1PriorityLevelConfiguration>(yaml, false);
                        item.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return _client.FlowcontrolApiserverV1.ReplacePriorityLevelConfigurationAsync(item, identity.Name, cancellationToken: ct);
                    });
                break;

            default:
                await ApplyDiscoveredResourceAsync(identity, yaml, ct);
                break;
        }
    }

    private async Task ApplyDiscoveredResourceAsync(ManifestIdentity identity, string yaml, CancellationToken ct)
    {
        var resource = await DiscoverResourceAsync(identity, ct);
        var item = HelmYaml.DeserializeDictionary(yaml);

        if (resource.Namespaced)
        {
            await UpsertNamespacedAsync(
                () => _client.CustomObjects.GetNamespacedCustomObjectAsync(resource.Group, resource.Version, identity.Namespace, resource.Plural, identity.Name, ct),
                () => _client.CustomObjects.CreateNamespacedCustomObjectAsync(item, resource.Group, resource.Version, identity.Namespace, resource.Plural, null, _fieldManager, null, null, ct),
                existing =>
                {
                    SetResourceVersion(item, existing);
                    return _client.CustomObjects.ReplaceNamespacedCustomObjectAsync(item, resource.Group, resource.Version, identity.Namespace, resource.Plural, identity.Name, null, _fieldManager, null, ct);
                });
            return;
        }

        await UpsertClusterAsync(
            () => _client.CustomObjects.GetClusterCustomObjectAsync(resource.Group, resource.Version, resource.Plural, identity.Name, ct),
            () => _client.CustomObjects.CreateClusterCustomObjectAsync(item, resource.Group, resource.Version, resource.Plural, null, _fieldManager, null, null, ct),
            existing =>
            {
                SetResourceVersion(item, existing);
                return _client.CustomObjects.ReplaceClusterCustomObjectAsync(item, resource.Group, resource.Version, resource.Plural, identity.Name, null, _fieldManager, null, ct);
            });
    }

    private async Task<DiscoveredResource> DiscoverResourceAsync(ManifestIdentity identity, CancellationToken ct)
    {
        var cacheKey = (identity.ApiVersion, identity.Kind);
        if (_discoveredResources.TryGetValue(cacheKey, out var cached))
            return cached;

        var (group, version) = SplitApiVersion(identity.ApiVersion);
        var resources = string.IsNullOrEmpty(group)
            ? await _client.CoreV1.GetAPIResourcesAsync(ct)
            : await _client.CustomObjects.GetAPIResourcesAsync(group, version, ct);
        var match = resources.Resources?.SingleOrDefault(resource =>
            string.Equals(resource.Kind, identity.Kind, StringComparison.Ordinal) &&
            !resource.Name.Contains('/', StringComparison.Ordinal));

        if (match is null || string.IsNullOrWhiteSpace(match.Name))
            throw new KubernetesApiResourceNotFoundException(identity.ApiVersion, identity.Kind);

        var discovered = new DiscoveredResource(group, version, match.Name, match.Namespaced == true);
        _discoveredResources.Add(cacheKey, discovered);
        return discovered;
    }

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

    private async Task DeleteOneAsync(ManifestIdentity identity, string? propagationPolicy, CancellationToken ct)
    {
        try
        {
            var deleteOptions = string.IsNullOrWhiteSpace(propagationPolicy)
                ? null
                : new V1DeleteOptions { PropagationPolicy = propagationPolicy };
            switch (identity.ApiVersion, identity.Kind)
            {
                // Core v1
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
                case ("autoscaling/v2beta2", "HorizontalPodAutoscaler"):
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
                    await DeleteDiscoveredResourceAsync(identity, deleteOptions, ct);
                    break;
            }
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            // Already gone.
        }
    }

    private async Task DeleteDiscoveredResourceAsync(ManifestIdentity identity, V1DeleteOptions? deleteOptions, CancellationToken ct)
    {
        DiscoveredResource resource;
        try
        {
            resource = await DiscoverResourceAsync(identity, ct);
        }
        catch (KubernetesApiResourceNotFoundException)
        {
            // A removed CRD can remove its resource kind before uninstall reaches
            // an older stored manifest. The object is no longer addressable.
            return;
        }
        if (resource.Namespaced)
        {
            await _client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                resource.Group, resource.Version, identity.Namespace, resource.Plural, identity.Name,
                deleteOptions, null, null, null, null, ct);
            return;
        }

        await _client.CustomObjects.DeleteClusterCustomObjectAsync(
            resource.Group, resource.Version, resource.Plural, identity.Name,
            deleteOptions, null, null, null, null, ct);
    }

    private static async Task UpsertNamespacedAsync<T>(
        Func<Task<T>> read,
        Func<Task<T>> create,
        Func<T, Task<T>> replace)
    {
        try
        {
            var existing = await read();
            await replace(existing);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            await create();
        }
    }

    private static Task UpsertClusterAsync<T>(
        Func<Task<T>> read,
        Func<Task<T>> create,
        Func<T, Task<T>> replace)
        => UpsertNamespacedAsync(read, create, replace);

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

    public static IEnumerable<string> SplitDocumentsPublic(string manifest)
        => SplitDocuments(manifest);
}

internal sealed record DiscoveredResource(string Group, string Version, string Plural, bool Namespaced);

internal sealed class KubernetesApiResourceNotFoundException : Exception
{
    public KubernetesApiResourceNotFoundException(string apiVersion, string kind)
        : base($"Kubernetes API discovery did not find resource {apiVersion}/{kind}.")
    {
    }
}

public sealed record ManifestIdentity(string ApiVersion, string Kind, string Name, string Namespace)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Namespace)
        ? $"{Kind}/{Name}"
        : $"{Kind}/{Namespace}/{Name}";

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
        if (kind is "Namespace" or "ClusterRole" or "ClusterRoleBinding" or "PersistentVolume"
            or "StorageClass" or "CSIDriver" or "CSINode" or "VolumeAttachment"
            or "PriorityClass" or "CustomResourceDefinition" or "MutatingWebhookConfiguration"
            or "ValidatingWebhookConfiguration" or "APIService" or "IngressClass"
            or "RuntimeClass" or "FlowSchema" or "PriorityLevelConfiguration")
            ns = string.Empty;

        return new ManifestIdentity(apiVersion, kind, name, ns);
    }
}

