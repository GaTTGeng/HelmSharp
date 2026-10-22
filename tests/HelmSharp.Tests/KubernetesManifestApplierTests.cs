using System.Net;
using System.Text.Json;
using HelmSharp.Chart;
using HelmSharp.Kube;
using k8s.Autorest;

namespace HelmSharp.Tests;

/// <summary>
/// Manifest identity and apply behavior: namespace parsing (including cluster-scoped
/// built-ins and custom kinds), namespace creation on demand, and create-vs-replace apply semantics.
/// </summary>
public sealed class KubernetesManifestApplierTests
{
    [Theory]
    [InlineData("admissionregistration.k8s.io")]
    [InlineData("apps")]
    [InlineData("extensions")]
    [InlineData("networking.k8s.io")]
    public void SchemaLookupFallback_RecognizesBuiltInApiGroups(string group)
    {
        Assert.True(KubernetesManifestApplier.IsKnownKubernetesApiGroup(group));
    }

    [Fact]
    public void SchemaLookupFallback_LeavesCustomApiGroupsOnCustomResourceSemantics()
    {
        Assert.False(KubernetesManifestApplier.IsKnownKubernetesApiGroup("widgets.example.com"));
    }

    [Theory]
    [InlineData("scheduling.k8s.io/v1beta1", "scheduling.k8s.io/v1", "PriorityClass", false)]
    [InlineData("coordination.k8s.io/v1beta1", "coordination.k8s.io/v1", "Lease", true)]
    [InlineData("node.k8s.io/v1beta1", "node.k8s.io/v1", "RuntimeClass", false)]
    [InlineData("apiregistration.k8s.io/v1beta1", "apiregistration.k8s.io/v1", "APIService", false)]
    [InlineData("networking.k8s.io/v1beta1", "networking.k8s.io/v1", "IngressClass", false)]
    [InlineData("flowcontrol.apiserver.k8s.io/v1beta3", "flowcontrol.apiserver.k8s.io/v1", "FlowSchema", false)]
    [InlineData("flowcontrol.apiserver.k8s.io/v1beta3", "flowcontrol.apiserver.k8s.io/v1", "PriorityLevelConfiguration", false)]
    public void SchemaConverter_MigratesSchemaCompatibleBuiltIns(
        string sourceApiVersion,
        string targetApiVersion,
        string kind,
        bool namespaced)
    {
        var identity = new ManifestIdentity(sourceApiVersion, kind, "sample", "release-ns");
        var manifest = $"apiVersion: {sourceApiVersion}\nkind: {kind}\nmetadata:\n  name: sample\nspec:\n  marker: retained\n";

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            manifest, identity, targetApiVersion, out var converted));
        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);

        Assert.Equal(targetApiVersion, document["apiVersion"]);
        Assert.True(KubernetesManifestApplier.TryGetTypedResourceScope(identity, out var actualNamespaced));
        Assert.Equal(namespaced, actualNamespaced);
        Assert.Contains("marker: retained", converted, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaConverter_DefaultsLegacyDaemonSetStrategyToOnDelete()
    {
        const string source = """
            apiVersion: extensions/v1beta1
            kind: DaemonSet
            metadata:
              name: agent
            spec:
              selector:
                matchLabels:
                  app: agent
            """;
        var identity = new ManifestIdentity("extensions/v1beta1", "DaemonSet", "agent", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            source, identity, "apps/v1", out var converted));

        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);
        var spec = Assert.IsType<Dictionary<string, object?>>(document["spec"]);
        var strategy = Assert.IsType<Dictionary<string, object?>>(spec["updateStrategy"]);
        Assert.Equal("OnDelete", strategy["type"]);
    }

    [Fact]
    public void SchemaConverter_DefaultsLegacyStatefulSetStrategyToOnDelete()
    {
        const string source = """
            apiVersion: apps/v1beta1
            kind: StatefulSet
            metadata:
              name: database
            spec:
              selector:
                matchLabels:
                  app: database
            """;
        var identity = new ManifestIdentity("apps/v1beta1", "StatefulSet", "database", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            source, identity, "apps/v1", out var converted));

        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);
        var spec = Assert.IsType<Dictionary<string, object?>>(document["spec"]);
        var strategy = Assert.IsType<Dictionary<string, object?>>(spec["updateStrategy"]);
        Assert.Equal("OnDelete", strategy["type"]);
    }

    [Fact]
    public void SchemaConverter_DoesNotDefaultAppsV1Beta2DaemonSetToOnDelete()
    {
        const string source = """
            apiVersion: apps/v1beta2
            kind: DaemonSet
            metadata:
              name: agent
            spec:
              selector:
                matchLabels:
                  app: agent
            """;
        var identity = new ManifestIdentity("apps/v1beta2", "DaemonSet", "agent", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            source, identity, "apps/v1", out var converted));

        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);
        var spec = Assert.IsType<Dictionary<string, object?>>(document["spec"]);
        Assert.False(spec.ContainsKey("updateStrategy"));
    }

    [Fact]
    public void SchemaConverter_MaterializesAppsV1Beta1DeploymentRevisionHistoryDefault()
    {
        const string source = """
            apiVersion: apps/v1beta1
            kind: Deployment
            metadata:
              name: portal
            spec:
              selector:
                matchLabels:
                  app: portal
            """;
        var identity = new ManifestIdentity("apps/v1beta1", "Deployment", "portal", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            source, identity, "apps/v1", out var converted));

        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);
        var spec = Assert.IsType<Dictionary<string, object?>>(document["spec"]);
        Assert.Equal(2L, Convert.ToInt64(spec["revisionHistoryLimit"]));
    }

    [Fact]
    public void SchemaConverter_MaterializesExtensionsV1Beta1DeploymentRevisionHistoryDefault()
    {
        const string source = """
            apiVersion: extensions/v1beta1
            kind: Deployment
            metadata:
              name: portal
            spec:
              selector:
                matchLabels:
                  app: portal
            """;
        var identity = new ManifestIdentity("extensions/v1beta1", "Deployment", "portal", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            source, identity, "apps/v1", out var converted));

        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);
        var spec = Assert.IsType<Dictionary<string, object?>>(document["spec"]);
        Assert.Equal(int.MaxValue, Convert.ToInt32(spec["revisionHistoryLimit"]));
    }

    [Fact]
    public void SchemaConverter_DoesNotForceCronJobToUnservedStableVersionWithoutAttemptedTarget()
    {
        var identity = new ManifestIdentity("batch/v1beta1", "CronJob", "backup", "release-ns");

        Assert.Null(KubernetesManifestSchemaConverter.GetCanonicalApiVersion(identity));
    }

    [Fact]
    public void SchemaConverter_DoesNotForceEndpointSliceBetaToUnservedStableVersionWithoutAttemptedTarget()
    {
        var identity = new ManifestIdentity("discovery.k8s.io/v1beta1", "EndpointSlice", "endpoints", "release-ns");

        Assert.Null(KubernetesManifestSchemaConverter.GetCanonicalApiVersion(identity));
    }

    [Fact]
    public void SchemaConverter_MaterializesLegacyExtensionsDeploymentRollingUpdateDefaults()
    {
        const string source = """
            apiVersion: extensions/v1beta1
            kind: Deployment
            metadata:
              name: portal
            spec:
              selector:
                matchLabels:
                  app: portal
            """;
        var identity = new ManifestIdentity("extensions/v1beta1", "Deployment", "portal", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            source, identity, "apps/v1", out var converted));

        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);
        var spec = Assert.IsType<Dictionary<string, object?>>(document["spec"]);
        var strategy = Assert.IsType<Dictionary<string, object?>>(spec["strategy"]);
        var rollingUpdate = Assert.IsType<Dictionary<string, object?>>(strategy["rollingUpdate"]);
        Assert.Equal(1L, Convert.ToInt64(rollingUpdate["maxSurge"]));
        Assert.Equal(1L, Convert.ToInt64(rollingUpdate["maxUnavailable"]));
    }

    [Fact]
    public void SchemaConverter_MaterializesBetaCsiDriverStorageCapacityDefault()
    {
        const string source = """
            apiVersion: storage.k8s.io/v1beta1
            kind: CSIDriver
            metadata:
              name: csi.example
            spec: {}
            """;
        var identity = new ManifestIdentity("storage.k8s.io/v1beta1", "CSIDriver", "csi.example", string.Empty);

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            source, identity, "storage.k8s.io/v1", out var converted));

        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);
        var spec = Assert.IsType<Dictionary<string, object?>>(document["spec"]);
        Assert.Equal(false, spec["storageCapacity"]);
    }

    [Fact]
    public void SchemaConverter_DoesNotForceCsiStorageCapacityToUnservedV1WithoutAttemptedTarget()
    {
        var identity = new ManifestIdentity("storage.k8s.io/v1beta1", "CSIStorageCapacity", "capacity", "release-ns");

        Assert.Null(KubernetesManifestSchemaConverter.GetCanonicalApiVersion(identity));
    }

    [Fact]
    public void SchemaConverter_DoesNotForceBetaCsiDriverToUnservedV1WithoutAttemptedTarget()
    {
        var identity = new ManifestIdentity("storage.k8s.io/v1beta1", "CSIDriver", "csi.example", string.Empty);

        Assert.Null(KubernetesManifestSchemaConverter.GetCanonicalApiVersion(identity));
    }

    [Theory]
    [InlineData("flowcontrol.apiserver.k8s.io/v1beta1")]
    [InlineData("flowcontrol.apiserver.k8s.io/v1beta2")]
    [InlineData("flowcontrol.apiserver.k8s.io/v1beta3")]
    public void SchemaConverter_DoesNotForceFlowControlBetaToStableVersion(string apiVersion)
    {
        var identity = new ManifestIdentity(apiVersion, "FlowSchema", "sample", "ignored");

        Assert.Null(KubernetesManifestSchemaConverter.GetCanonicalApiVersion(identity));
    }

    [Fact]
    public void SchemaConverter_RenamesFlowControlConcurrencySharesFromBeta2()
    {
        const string manifest = """
            apiVersion: flowcontrol.apiserver.k8s.io/v1beta2
            kind: PriorityLevelConfiguration
            metadata:
              name: limited
            spec:
              type: Limited
              limited:
                assuredConcurrencyShares: 12
            """;
        var identity = new ManifestIdentity(
            "flowcontrol.apiserver.k8s.io/v1beta2", "PriorityLevelConfiguration", "limited", "ignored");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            manifest, identity, "flowcontrol.apiserver.k8s.io/v1", out var converted));
        Assert.Contains("nominalConcurrencyShares: 12", converted, StringComparison.Ordinal);
        Assert.DoesNotContain("assuredConcurrencyShares", converted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flowcontrol.apiserver.k8s.io/v1beta1")]
    [InlineData("flowcontrol.apiserver.k8s.io/v1beta2")]
    public void SchemaConverter_ConvertsBetaDefaultedZeroConcurrencyShares(string apiVersion)
    {
        var manifest = $$"""
            apiVersion: {{apiVersion}}
            kind: PriorityLevelConfiguration
            metadata:
              name: limited
            spec:
              type: Limited
              limited:
                assuredConcurrencyShares: 0
            """;
        var identity = new ManifestIdentity(apiVersion, "PriorityLevelConfiguration", "limited", "ignored");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            manifest, identity, "flowcontrol.apiserver.k8s.io/v1", out var converted));

        Assert.Contains("nominalConcurrencyShares: 30", converted, StringComparison.Ordinal);
        Assert.DoesNotContain("assuredConcurrencyShares", converted, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaConverter_MovesEndpointSliceBetaTopologyToV1Fields()
    {
        const string manifest = """
            apiVersion: discovery.k8s.io/v1beta1
            kind: EndpointSlice
            metadata:
              name: endpoints
            endpoints:
              - addresses: [10.0.0.1]
                nodeName: node-a
                topology:
                  kubernetes.io/hostname: node-a
                  topology.kubernetes.io/zone: zone-a
                  example.com/rack: rack-a
            """;
        var identity = new ManifestIdentity("discovery.k8s.io/v1beta1", "EndpointSlice", "endpoints", "ignored");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            manifest, identity, "discovery.k8s.io/v1", out var converted));
        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);
        var endpoint = (IDictionary<string, object?>)((IList<object?>)document["endpoints"]!)[0]!;
        var deprecatedTopology = (IDictionary<string, object?>)endpoint["deprecatedTopology"]!;

        Assert.Equal("zone-a", endpoint["zone"]);
        Assert.Equal("rack-a", deprecatedTopology["example.com/rack"]);
        Assert.DoesNotContain("topology.kubernetes.io/zone", deprecatedTopology.Keys);
        Assert.DoesNotContain("kubernetes.io/hostname", deprecatedTopology.Keys);
        Assert.Equal("discovery.k8s.io/v1", document["apiVersion"]);
    }

    [Fact]
    public void SchemaConverter_MovesEndpointSliceHostnameTopologyToNodeName()
    {
        const string manifest = """
            apiVersion: discovery.k8s.io/v1beta1
            kind: EndpointSlice
            metadata:
              name: endpoints
            endpoints:
              - addresses: [10.0.0.1]
                topology:
                  kubernetes.io/hostname: node-a
            """;
        var identity = new ManifestIdentity("discovery.k8s.io/v1beta1", "EndpointSlice", "endpoints", "ignored");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            manifest, identity, "discovery.k8s.io/v1", out var converted));
        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);
        var endpoint = (IDictionary<string, object?>)((IList<object?>)document["endpoints"]!)[0]!;

        Assert.Equal("node-a", endpoint["nodeName"]);
        Assert.False(endpoint.ContainsKey("deprecatedTopology"));
    }

    [Theory]
    [InlineData("MutatingWebhookConfiguration")]
    [InlineData("ValidatingWebhookConfiguration")]
    public void SchemaConverter_ConvertsBetaWebhookDefaults(string kind)
    {
        var manifest = $$"""
            apiVersion: admissionregistration.k8s.io/v1beta1
            kind: {{kind}}
            metadata:
              name: sample
            webhooks:
              - name: hook.example.com
                sideEffects: None
                clientConfig:
                  service:
                    name: hook
                    namespace: default
                rules: []
            """;
        var identity = new ManifestIdentity("admissionregistration.k8s.io/v1beta1", kind, "sample", "ignored");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            manifest, identity, "admissionregistration.k8s.io/v1", out var converted));
        var document = HelmYaml.DeserializeDictionary(converted, StringComparer.Ordinal);
        var webhook = (IDictionary<string, object?>)((IList<object?>)document["webhooks"]!)[0]!;

        Assert.Equal("Ignore", webhook["failurePolicy"]);
        Assert.Equal("Exact", webhook["matchPolicy"]);
        Assert.Equal(30L, Convert.ToInt64(webhook["timeoutSeconds"]));
        Assert.Equal(["v1beta1"], (IList<object?>)webhook["admissionReviewVersions"]!);
        Assert.Equal("None", webhook["sideEffects"]);
    }

    [Fact]
    public void SchemaConverter_RejectsWebhookSideEffectsThatV1CannotRepresent()
    {
        const string manifest = """
            apiVersion: admissionregistration.k8s.io/v1beta1
            kind: MutatingWebhookConfiguration
            metadata:
              name: sample
            webhooks:
              - name: hook.example.com
                sideEffects: Some
            """;
        var identity = new ManifestIdentity(
            "admissionregistration.k8s.io/v1beta1", "MutatingWebhookConfiguration", "sample", "ignored");

        Assert.False(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            manifest, identity, "admissionregistration.k8s.io/v1", out var converted));
        Assert.Equal(manifest, converted);
    }

    [Fact]
    public void SchemaConverter_ConvertsBeta3DefaultedZeroConcurrencySharesToEffectiveValue()
    {
        const string manifest = """
            apiVersion: flowcontrol.apiserver.k8s.io/v1beta3
            kind: PriorityLevelConfiguration
            metadata:
              name: limited
            spec:
              type: Limited
              limited:
                nominalConcurrencyShares: 0
            """;
        var identity = new ManifestIdentity(
            "flowcontrol.apiserver.k8s.io/v1beta3", "PriorityLevelConfiguration", "limited", "ignored");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            manifest, identity, "flowcontrol.apiserver.k8s.io/v1", out var converted));

        Assert.Contains("nominalConcurrencyShares: 30", converted, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaConverter_TreatsNullV1HpaCpuTargetAsOmitted()
    {
        const string manifest = """
            apiVersion: autoscaling/v1
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 3
              targetCPUUtilizationPercentage: null
            """;
        var identity = new ManifestIdentity("autoscaling/v1", "HorizontalPodAutoscaler", "scaler", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            manifest, identity, "autoscaling/v2", out var converted));

        Assert.DoesNotContain("targetCPUUtilizationPercentage", converted, StringComparison.Ordinal);
        Assert.DoesNotContain("metrics:", converted, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaConverter_DoesNotForceCanonicalVersionForAutoscalingV1Hpa()
    {
        var identity = new ManifestIdentity("autoscaling/v1", "HorizontalPodAutoscaler", "scaler", "release-ns");

        Assert.Null(KubernetesManifestSchemaConverter.GetCanonicalApiVersion(identity));
    }

    [Fact]
    public void ManifestIdentity_ParseUsesDefaultAndExplicitNamespaces()
    {
        var defaultNamespaceIdentity = ManifestIdentity.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            """, "release-ns");
        var explicitNamespaceIdentity = ManifestIdentity.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
              namespace: chart-ns
            """, "release-ns");

        Assert.NotNull(defaultNamespaceIdentity);
        Assert.NotNull(explicitNamespaceIdentity);
        Assert.Equal("release-ns", defaultNamespaceIdentity.Namespace);
        Assert.Equal("chart-ns", explicitNamespaceIdentity.Namespace);
        Assert.Equal("ConfigMap/chart-ns/settings", explicitNamespaceIdentity.DisplayName);
    }

    [Fact]
    public void ManifestIdentity_ParseClearsNamespaceForClusterScopedBuiltIns()
    {
        var identity = ManifestIdentity.Parse("""
            apiVersion: rbac.authorization.k8s.io/v1
            kind: ClusterRole
            metadata:
              name: reader
              namespace: ignored
            """, "release-ns");

        Assert.NotNull(identity);
        Assert.Equal(string.Empty, identity.Namespace);
        Assert.Equal("ClusterRole/reader", identity.DisplayName);
    }

    [Theory]
    [InlineData("MutatingWebhookConfiguration")]
    [InlineData("ValidatingWebhookConfiguration")]
    public void ManifestIdentity_ParseClearsNamespaceForBetaWebhookConfigurations(string kind)
    {
        var identity = ManifestIdentity.Parse($"""
            apiVersion: admissionregistration.k8s.io/v1beta1
            kind: {kind}
            metadata:
              name: sample
              namespace: ignored
            """, "release-ns");

        Assert.NotNull(identity);
        Assert.Equal(string.Empty, identity.Namespace);
        Assert.Equal($"{kind}/sample", identity.DisplayName);
    }

    [Fact]
    public void ManifestIdentity_ParseRetainsNamespaceForCustomKindMatchingClusterScopedBuiltIn()
    {
        var identity = ManifestIdentity.Parse("""
            apiVersion: example.com/v1
            kind: ClusterRole
            metadata:
              name: custom-reader
              namespace: tenant-a
            """, "release-ns");

        Assert.NotNull(identity);
        Assert.Equal("tenant-a", identity.Namespace);
        Assert.Equal("ClusterRole/tenant-a/custom-reader", identity.DisplayName);
    }

    [Fact]
    public async Task EnsureNamespaceAsync_CreatesNamespaceWhenReadReturnsNotFound()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns", HttpStatusCode.NotFound)
            .Respond(HttpMethod.Post, "/api/v1/namespaces", HttpStatusCode.Created, """
                { "apiVersion": "v1", "kind": "Namespace", "metadata": { "name": "release-ns" } }
                """);

        await KubernetesManifestApplier.EnsureNamespaceAsync(
            KubernetesTestClientBuilder.Create(handler), "release-ns", CancellationToken.None);

        var namespaceRequests = handler.Requests
            .Select(request => (request.Method, request.PathAndQuery, request.Content))
            .ToArray();
        Assert.Equal(2, namespaceRequests.Length);
        Assert.Equal((HttpMethod.Get, "/api/v1/namespaces/release-ns"),
            (namespaceRequests[0].Method, namespaceRequests[0].PathAndQuery));
        Assert.Equal((HttpMethod.Post, "/api/v1/namespaces"),
            (namespaceRequests[1].Method, namespaceRequests[1].PathAndQuery));
        Assert.Contains("release-ns", namespaceRequests[1].Content);
    }

    [Fact]
    public async Task ApplyAsync_CreatesNamespacedConfigMapWhenAbsent()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.NotFound)
            .Respond(HttpMethod.Post, "/api/v1/namespaces/release-ns/configmaps", HttpStatusCode.Created, """
                { "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "settings" } }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var applied = await AsyncEnumerableTestExtensions.CollectAsync(applier.ApplyAsync("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              mode: enabled
            """, "release-ns"));

        Assert.Equal(["ConfigMap/release-ns/settings"], applied);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal((HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings"), (request.Method, request.PathAndQuery)),
            request => Assert.Equal((HttpMethod.Post, "/api/v1/namespaces/release-ns/configmaps"), (request.Method, request.PathAndQuery)));
    }

    [Fact]
    public async Task ApplyAsync_ReplacesExistingConfigMapInItsExplicitNamespace()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/chart-ns/configmaps/settings", HttpStatusCode.OK, """
                { "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "settings", "resourceVersion": "42" } }
                """)
            .Respond(HttpMethod.Put, "/api/v1/namespaces/chart-ns/configmaps/settings", HttpStatusCode.OK, """
                { "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "settings", "resourceVersion": "43" } }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyAsync("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
              namespace: chart-ns
            """, "release-ns"));

        var requests = handler.Requests
            .Select(request => (request.Method, request.PathAndQuery, request.Content))
            .ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal((HttpMethod.Get, "/api/v1/namespaces/chart-ns/configmaps/settings"),
            (requests[0].Method, requests[0].PathAndQuery));
        Assert.Equal((HttpMethod.Put, "/api/v1/namespaces/chart-ns/configmaps/settings"),
            (requests[1].Method, requests[1].PathAndQuery));
        Assert.Contains("\"resourceVersion\":\"42\"", requests[1].Content);
        Assert.Contains("\"namespace\":\"chart-ns\"", requests[1].Content);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_ClearsMergeBaselineBeforeApplyingAtomicRestore()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              mode: previous
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              mode: failed-upgrade
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.OK, """
                { "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "settings", "resourceVersion": "42" }, "data": { "mode": "previous" } }
                """)
            .Respond(HttpMethod.Put, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.OK)
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.OK, """
                { "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "settings", "resourceVersion": "43" }, "data": { "mode": "failed-upgrade" } }
                """)
            .Respond(HttpMethod.Put, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(
            applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"));
        await AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyAsync(previousManifest, "release-ns"));

        var replaceRequests = handler.Requests.Where(request => request.Method == HttpMethod.Put).ToArray();
        Assert.Equal(2, replaceRequests.Length);
        Assert.Contains("\"mode\":\"failed-upgrade\"", replaceRequests[0].Content);
        Assert.Contains("\"mode\":\"previous\"", replaceRequests[1].Content);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_UsesLastDuplicatePreviousDocumentAsMergeBaseline()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              value: one
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              value: two
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              value: two
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.OK, """
                { "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "settings", "resourceVersion": "42" }, "data": { "value": "three" } }
                """)
            .Respond(HttpMethod.Put, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(
            applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"));

        var replacement = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains("\"value\":\"three\"", replacement.Content);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_PreservesLiveFieldsWhenApiVersionChangesWithinGroup()
    {
        const string previousManifest = """
            apiVersion: autoscaling/v1
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 3
              targetCPUUtilizationPercentage: 70
            """;
        const string upgradedManifest = """
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 5
              metrics:
                - type: Resource
                  resource:
                    name: cpu
                    target:
                      type: Utilization
                      averageUtilization: 70
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/autoscaling/v2/namespaces/release-ns/horizontalpodautoscalers/scaler", HttpStatusCode.OK, """
                {
                  "apiVersion": "autoscaling/v2",
                  "kind": "HorizontalPodAutoscaler",
                  "metadata": { "name": "scaler", "resourceVersion": "42", "annotations": { "operator.example/live": "keep" } },
                  "spec": { "minReplicas": 1, "maxReplicas": 3, "metrics": [] }
                }
                """)
            .Respond(HttpMethod.Put, "/apis/autoscaling/v2/namespaces/release-ns/horizontalpodautoscalers/scaler", HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(
            applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"));

        var replacement = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains("\"apiVersion\":\"autoscaling/v2\"", replacement.Content);
        Assert.Contains("\"operator.example/live\":\"keep\"", replacement.Content);
    }

    [Fact]
    public void SchemaConverter_ConvertsHpaV1CpuTargetBeforeMerge()
    {
        const string previousManifest = """
            apiVersion: autoscaling/v1
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 3
              targetCPUUtilizationPercentage: 70
            """;
        const string upgradedManifest = """
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 5
              metrics:
                - type: Resource
                  resource:
                    name: cpu
                    target:
                      type: Utilization
                      averageUtilization: 70
            """;
        const string liveManifest = """
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 3
              metrics:
                - type: Resource
                  resource:
                    name: cpu
                    target:
                      type: Utilization
                      averageUtilization: 80
            """;
        var identity = new ManifestIdentity("autoscaling/v1", "HorizontalPodAutoscaler", "scaler", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "autoscaling/v2", out var convertedPrevious));
        var merged = HelmYaml.DeserializeDictionary(ThreeWayMerge.MergeResource(
            convertedPrevious, upgradedManifest, liveManifest));
        var spec = Assert.IsType<Dictionary<string, object?>>(merged["spec"]);
        var metrics = Assert.IsType<List<object?>>(spec["metrics"]);
        var metric = Assert.IsType<Dictionary<string, object?>>(Assert.Single(metrics));
        var resource = Assert.IsType<Dictionary<string, object?>>(metric["resource"]);
        var target = Assert.IsType<Dictionary<string, object?>>(resource["target"]);

        Assert.Equal(80L, target["averageUtilization"]);
        Assert.Equal(5L, spec["maxReplicas"]);
    }

    [Fact]
    public void SchemaConverter_ConvertsHpaV2Beta1MetricsBeforeMerge()
    {
        const string previousManifest = """
            apiVersion: autoscaling/v2beta1
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 3
              metrics:
                - type: Resource
                  resource:
                    name: cpu
                    targetAverageUtilization: 70
                - type: External
                  external:
                    metricName: queue_depth
                    metricSelector:
                      matchLabels:
                        queue: jobs
                    targetValue: 20
                - type: Object
                  object:
                    target:
                      apiVersion: networking.k8s.io/v1
                      kind: Ingress
                      name: gateway
                    metricName: requests_per_second
                    selector:
                      matchLabels:
                        ingress: gateway
                    targetValue: 999
                    averageValue: 100
                - type: ContainerResource
                  containerResource:
                    name: memory
                    container: web
                    targetAverageUtilization: 75
            """;
        const string upgradedManifest = """
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 5
              metrics:
                - type: Resource
                  resource:
                    name: cpu
                    target:
                      type: Utilization
                      averageUtilization: 70
                - type: External
                  external:
                    metric:
                      name: queue_depth
                      selector:
                        matchLabels:
                          queue: jobs
                    target:
                      type: Value
                      value: 20
                - type: Object
                  object:
                    describedObject:
                      apiVersion: networking.k8s.io/v1
                      kind: Ingress
                      name: gateway
                    metric:
                      name: requests_per_second
                      selector:
                        matchLabels:
                          ingress: gateway
                    target:
                      type: AverageValue
                      averageValue: 100
                - type: ContainerResource
                  containerResource:
                    name: memory
                    container: web
                    target:
                      type: Utilization
                      averageUtilization: 75
            """;
        const string liveManifest = """
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 3
              metrics:
                - type: Resource
                  resource:
                    name: cpu
                    target:
                      type: Utilization
                      averageUtilization: 85
                - type: External
                  external:
                    metric:
                      name: queue_depth
                      selector:
                        matchLabels:
                          queue: jobs
                    target:
                      type: Value
                      value: 35
                - type: Object
                  object:
                    describedObject:
                      apiVersion: networking.k8s.io/v1
                      kind: Ingress
                      name: gateway
                    metric:
                      name: requests_per_second
                      selector:
                        matchLabels:
                          ingress: gateway
                    target:
                      type: AverageValue
                      averageValue: 125
              behavior:
                scaleDown:
                  stabilizationWindowSeconds: 60
            """;
        var identity = new ManifestIdentity("autoscaling/v2beta1", "HorizontalPodAutoscaler", "scaler", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "autoscaling/v2", out var convertedPrevious));
        var convertedSpec = Assert.IsType<Dictionary<string, object?>>(HelmYaml.DeserializeDictionary(convertedPrevious)["spec"]);
        var convertedMetrics = Assert.IsType<List<object?>>(convertedSpec["metrics"]);
        var convertedContainerMetric = Assert.IsType<Dictionary<string, object?>>(convertedMetrics[3]);
        var convertedContainerResource = Assert.IsType<Dictionary<string, object?>>(convertedContainerMetric["containerResource"]);
        var convertedContainerTarget = Assert.IsType<Dictionary<string, object?>>(convertedContainerResource["target"]);
        var merged = HelmYaml.DeserializeDictionary(ThreeWayMerge.MergeResource(
            convertedPrevious, upgradedManifest, liveManifest));
        var spec = Assert.IsType<Dictionary<string, object?>>(merged["spec"]);
        var metrics = Assert.IsType<List<object?>>(spec["metrics"]);
        var resourceMetric = Assert.IsType<Dictionary<string, object?>>(metrics[0]);
        var resource = Assert.IsType<Dictionary<string, object?>>(resourceMetric["resource"]);
        var resourceTarget = Assert.IsType<Dictionary<string, object?>>(resource["target"]);
        var externalMetric = Assert.IsType<Dictionary<string, object?>>(metrics[1]);
        var external = Assert.IsType<Dictionary<string, object?>>(externalMetric["external"]);
        var externalTarget = Assert.IsType<Dictionary<string, object?>>(external["target"]);
        var objectMetric = Assert.IsType<Dictionary<string, object?>>(metrics[2]);
        var objectSource = Assert.IsType<Dictionary<string, object?>>(objectMetric["object"]);
        var describedObject = Assert.IsType<Dictionary<string, object?>>(objectSource["describedObject"]);
        var objectIdentifier = Assert.IsType<Dictionary<string, object?>>(objectSource["metric"]);
        var objectTarget = Assert.IsType<Dictionary<string, object?>>(objectSource["target"]);

        Assert.Equal(85L, resourceTarget["averageUtilization"]);
        Assert.Equal(35L, externalTarget["value"]);
        Assert.Equal("gateway", describedObject["name"]);
        Assert.Equal("requests_per_second", objectIdentifier["name"]);
        Assert.Equal("AverageValue", objectTarget["type"]);
        Assert.Equal(125L, objectTarget["averageValue"]);
        Assert.Equal("web", convertedContainerResource["container"]);
        Assert.Equal(75L, convertedContainerTarget["averageUtilization"]);
        Assert.True(spec.ContainsKey("behavior"));
        Assert.Equal(5L, spec["maxReplicas"]);
    }

    [Fact]
    public void SchemaConverter_ConvertsHpaV2Beta1PodsSelector()
    {
        const string previousManifest = """
            apiVersion: autoscaling/v2beta1
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              metrics:
                - type: Pods
                  pods:
                    metricName: active_sessions
                    selector:
                      matchLabels:
                        app: web
                    targetAverageValue: 4
            """;
        var identity = new ManifestIdentity("autoscaling/v2beta1", "HorizontalPodAutoscaler", "scaler", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "autoscaling/v2", out var converted));
        var document = HelmYaml.DeserializeDictionary(converted);
        var spec = Assert.IsType<Dictionary<string, object?>>(document["spec"]);
        var metrics = Assert.IsType<List<object?>>(spec["metrics"]);
        var metric = Assert.IsType<Dictionary<string, object?>>(Assert.Single(metrics));
        var pods = Assert.IsType<Dictionary<string, object?>>(metric["pods"]);
        var identifier = Assert.IsType<Dictionary<string, object?>>(pods["metric"]);
        var selector = Assert.IsType<Dictionary<string, object?>>(identifier["selector"]);
        var matchLabels = Assert.IsType<Dictionary<string, object?>>(selector["matchLabels"]);

        Assert.Equal("active_sessions", identifier["name"]);
        Assert.Equal("web", matchLabels["app"]);
        Assert.False(pods.ContainsKey("selector"));
    }

    [Fact]
    public void SchemaConverter_PreservesHpaV2Beta2MetricsWhenMovingToV2()
    {
        const string previousManifest = """
            apiVersion: autoscaling/v2beta2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              metrics:
                - type: Pods
                  pods:
                    metric:
                      name: requests_per_second
                    target:
                      type: AverageValue
                      averageValue: 10
              behavior:
                scaleDown:
                  stabilizationWindowSeconds: 60
            """;
        var identity = new ManifestIdentity("autoscaling/v2beta2", "HorizontalPodAutoscaler", "scaler", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "autoscaling/v2", out var converted));
        var document = HelmYaml.DeserializeDictionary(converted);
        var spec = Assert.IsType<Dictionary<string, object?>>(document["spec"]);

        Assert.Equal("autoscaling/v2", HelmYaml.GetString(document, "apiVersion"));
        Assert.True(spec.ContainsKey("metrics"));
        Assert.True(spec.ContainsKey("behavior"));
    }

    [Fact]
    public void SchemaConverter_LeavesHpaWithUnsupportedMetricsUnconverted()
    {
        const string previousManifest = """
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              metrics:
                - type: Resource
                  resource:
                    name: memory
                    target:
                      type: AverageValue
                      averageValue: 1Gi
            """;
        var identity = new ManifestIdentity("autoscaling/v2", "HorizontalPodAutoscaler", "scaler", "release-ns");

        Assert.False(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "autoscaling/v1", out var converted));
        Assert.Equal(previousManifest, converted);
    }

    [Theory]
    [InlineData("Role")]
    [InlineData("RoleBinding")]
    [InlineData("ClusterRole")]
    [InlineData("ClusterRoleBinding")]
    public void SchemaConverter_PreservesLiveFieldsWhenRbacMovesToV1(string kind)
    {
        var previousManifest = $$"""
            apiVersion: rbac.authorization.k8s.io/v1beta1
            kind: {{kind}}
            metadata:
              name: reader
            """;
        var upgradedManifest = $$"""
            apiVersion: rbac.authorization.k8s.io/v1
            kind: {{kind}}
            metadata:
              name: reader
            """;
        var liveManifest = $$"""
            apiVersion: rbac.authorization.k8s.io/v1
            kind: {{kind}}
            metadata:
              name: reader
              annotations:
                operator.example/live: keep
            """;
        var identity = new ManifestIdentity("rbac.authorization.k8s.io/v1beta1", kind, "reader", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "rbac.authorization.k8s.io/v1", out var convertedPrevious));
        var merged = HelmYaml.DeserializeDictionary(ThreeWayMerge.MergeResource(
            convertedPrevious, upgradedManifest, liveManifest));
        var metadata = Assert.IsType<Dictionary<string, object?>>(merged["metadata"]);
        var annotations = Assert.IsType<Dictionary<string, object?>>(metadata["annotations"]);

        Assert.Equal("keep", annotations["operator.example/live"]);
        Assert.Equal("rbac.authorization.k8s.io/v1", merged["apiVersion"]);
    }

    [Theory]
    [InlineData("StorageClass")]
    [InlineData("CSIDriver")]
    [InlineData("CSINode")]
    [InlineData("VolumeAttachment")]
    [InlineData("CSIStorageCapacity")]
    public void SchemaConverter_PreservesLiveFieldsWhenStorageResourceMovesToV1(string kind)
    {
        var previousManifest = $$"""
            apiVersion: storage.k8s.io/v1beta1
            kind: {{kind}}
            metadata:
              name: sample
            """;
        var upgradedManifest = $$"""
            apiVersion: storage.k8s.io/v1
            kind: {{kind}}
            metadata:
              name: sample
            """;
        var liveManifest = $$"""
            apiVersion: storage.k8s.io/v1
            kind: {{kind}}
            metadata:
              name: sample
              annotations:
                operator.example/live: keep
            """;
        var identity = new ManifestIdentity("storage.k8s.io/v1beta1", kind, "sample", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "storage.k8s.io/v1", out var convertedPrevious));
        var merged = HelmYaml.DeserializeDictionary(ThreeWayMerge.MergeResource(
            convertedPrevious, upgradedManifest, liveManifest));
        var metadata = Assert.IsType<Dictionary<string, object?>>(merged["metadata"]);
        var annotations = Assert.IsType<Dictionary<string, object?>>(metadata["annotations"]);

        Assert.Equal("keep", annotations["operator.example/live"]);
        Assert.Equal("storage.k8s.io/v1", merged["apiVersion"]);
    }

    [Theory]
    [InlineData("apps/v1beta1")]
    [InlineData("apps/v1beta2")]
    public void SchemaConverter_PreservesLiveFieldsWhenStatefulSetMovesToAppsV1(string previousApiVersion)
    {
        var previousManifest = $$"""
            apiVersion: {{previousApiVersion}}
            kind: StatefulSet
            metadata:
              name: database
            spec:
              serviceName: database
              template:
                metadata:
                  labels:
                    app: database
                spec:
                  containers:
                    - name: database
                      image: example/database:old
            """;
        const string upgradedManifest = """
            apiVersion: apps/v1
            kind: StatefulSet
            metadata:
              name: database
            spec:
              serviceName: database
              selector:
                matchLabels:
                  app: database
              template:
                metadata:
                  labels:
                    app: database
                spec:
                  containers:
                    - name: database
                      image: example/database:new
            """;
        const string liveManifest = """
            apiVersion: apps/v1
            kind: StatefulSet
            metadata:
              name: database
              annotations:
                operator.example/live: keep
            spec:
              serviceName: database
              selector:
                matchLabels:
                  app: database
              template:
                metadata:
                  labels:
                    app: database
                spec:
                  containers:
                    - name: database
                      image: example/database:old
            """;
        var identity = new ManifestIdentity(previousApiVersion, "StatefulSet", "database", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "apps/v1", out var convertedPrevious));
        var merged = HelmYaml.DeserializeDictionary(ThreeWayMerge.MergeResource(
            convertedPrevious, upgradedManifest, liveManifest));
        var metadata = Assert.IsType<Dictionary<string, object?>>(merged["metadata"]);
        var annotations = Assert.IsType<Dictionary<string, object?>>(metadata["annotations"]);

        Assert.Equal("keep", annotations["operator.example/live"]);
        Assert.Equal("apps/v1", merged["apiVersion"]);
    }

    [Fact]
    public void SchemaConverter_PreservesLiveFieldsWhenCronJobMovesToBatchV1()
    {
        const string previousManifest = """
            apiVersion: batch/v1beta1
            kind: CronJob
            metadata:
              name: cleanup
            spec:
              schedule: "0 * * * *"
              jobTemplate:
                spec:
                  template:
                    spec:
                      restartPolicy: Never
                      containers:
                        - name: cleanup
                          image: example/cleanup:old
            """;
        const string upgradedManifest = """
            apiVersion: batch/v1
            kind: CronJob
            metadata:
              name: cleanup
            spec:
              schedule: "0 * * * *"
              jobTemplate:
                spec:
                  template:
                    spec:
                      restartPolicy: Never
                      containers:
                        - name: cleanup
                          image: example/cleanup:new
            """;
        const string liveManifest = """
            apiVersion: batch/v1
            kind: CronJob
            metadata:
              name: cleanup
              annotations:
                operator.example/live: keep
            spec:
              schedule: "0 * * * *"
              jobTemplate:
                spec:
                  template:
                    spec:
                      restartPolicy: Never
                      containers:
                        - name: cleanup
                          image: example/cleanup:old
            """;
        var identity = new ManifestIdentity("batch/v1beta1", "CronJob", "cleanup", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "batch/v1", out var convertedPrevious));
        var merged = HelmYaml.DeserializeDictionary(ThreeWayMerge.MergeResource(
            convertedPrevious, upgradedManifest, liveManifest));
        var metadata = Assert.IsType<Dictionary<string, object?>>(merged["metadata"]);
        var annotations = Assert.IsType<Dictionary<string, object?>>(metadata["annotations"]);

        Assert.Equal("keep", annotations["operator.example/live"]);
        Assert.Equal("batch/v1", merged["apiVersion"]);
    }

    [Fact]
    public void SchemaConverter_PreservesLiveFieldsWhenPodDisruptionBudgetMovesToPolicyV1()
    {
        const string previousManifest = """
            apiVersion: policy/v1beta1
            kind: PodDisruptionBudget
            metadata:
              name: web
            spec:
              minAvailable: 1
              selector:
                matchLabels:
                  app: web
            """;
        const string upgradedManifest = """
            apiVersion: policy/v1
            kind: PodDisruptionBudget
            metadata:
              name: web
            spec:
              minAvailable: 1
              selector:
                matchLabels:
                  app: web
            """;
        const string liveManifest = """
            apiVersion: policy/v1
            kind: PodDisruptionBudget
            metadata:
              name: web
              annotations:
                operator.example/live: keep
            spec:
              minAvailable: 1
              selector:
                matchLabels:
                  app: web
            """;
        var identity = new ManifestIdentity("policy/v1beta1", "PodDisruptionBudget", "web", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "policy/v1", out var convertedPrevious));
        var merged = HelmYaml.DeserializeDictionary(ThreeWayMerge.MergeResource(
            convertedPrevious, upgradedManifest, liveManifest));
        var metadata = Assert.IsType<Dictionary<string, object?>>(merged["metadata"]);
        var annotations = Assert.IsType<Dictionary<string, object?>>(metadata["annotations"]);

        Assert.Equal("keep", annotations["operator.example/live"]);
        Assert.Equal("policy/v1", merged["apiVersion"]);
    }

    [Fact]
    public void SchemaConverter_PreservesEmptyPodDisruptionBudgetSelectorSemantics()
    {
        const string previousManifest = """
            apiVersion: policy/v1beta1
            kind: PodDisruptionBudget
            metadata:
              name: web
            spec:
              minAvailable: 1
              selector: {}
            """;
        var identity = new ManifestIdentity("policy/v1beta1", "PodDisruptionBudget", "web", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "policy/v1", out var convertedPrevious));
        var converted = HelmYaml.DeserializeDictionary(convertedPrevious);
        var spec = Assert.IsType<Dictionary<string, object?>>(converted["spec"]);

        Assert.Null(spec["selector"]);
    }

    [Fact]
    public void SchemaConverter_AddsRequiredSelectorWhenLegacyWorkloadSelectorIsNull()
    {
        const string previousManifest = """
            apiVersion: apps/v1beta1
            kind: Deployment
            metadata:
              name: portal
            spec:
              selector: null
              template:
                metadata:
                  labels:
                    app: portal
                spec:
                  containers: []
            """;
        var identity = new ManifestIdentity("apps/v1beta1", "Deployment", "portal", "release-ns");

        Assert.True(KubernetesManifestSchemaConverter.TryConvertToApiVersion(
            previousManifest, identity, "apps/v1", out var convertedPrevious));
        var converted = HelmYaml.DeserializeDictionary(convertedPrevious);
        var spec = Assert.IsType<Dictionary<string, object?>>(converted["spec"]);
        var selector = Assert.IsType<Dictionary<string, object?>>(spec["selector"]);
        var matchLabels = Assert.IsType<Dictionary<string, object?>>(selector["matchLabels"]);

        Assert.Equal("portal", matchLabels["app"]);
        Assert.Equal("apps/v1", converted["apiVersion"]);
    }

    [Theory]
    [InlineData("apps/v1beta1")]
    [InlineData("apps/v1beta2")]
    public async Task ApplyUpgradeAsync_PreservesLiveFieldsWhenWorkloadMovesToAppsV1(string previousApiVersion)
    {
        var previousManifest = $$"""
            apiVersion: {{previousApiVersion}}
            kind: Deployment
            metadata:
              name: portal
            spec:
              template:
                metadata:
                  labels:
                    app: portal
                spec:
                  containers:
                    - name: portal
                      image: example/portal:old
            """;
        const string upgradedManifest = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: portal
            spec:
              selector:
                matchLabels:
                  app: portal
              template:
                metadata:
                  labels:
                    app: portal
                spec:
                  containers:
                    - name: portal
                      image: example/portal:new
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/apps/v1/namespaces/release-ns/deployments/portal", HttpStatusCode.OK, """
                {
                  "apiVersion": "apps/v1",
                  "kind": "Deployment",
                  "metadata": { "name": "portal", "resourceVersion": "42", "annotations": { "operator.example/live": "keep" } },
                  "spec": { "selector": { "matchLabels": { "app": "portal" } }, "template": { "metadata": { "labels": { "app": "portal" } }, "spec": { "containers": [{ "name": "portal", "image": "example/portal:old" }] } } }
                }
                """)
            .Respond(HttpMethod.Put, "/apis/apps/v1/namespaces/release-ns/deployments/portal", HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(
            applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"));

        var replacement = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains("\"operator.example/live\":\"keep\"", replacement.Content);
        Assert.Contains("\"selector\":{\"matchLabels\":{\"app\":\"portal\"}}", replacement.Content);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_PreservesLiveFieldsWhenIngressMovesOutOfExtensionsGroup()
    {
        const string previousManifest = """
            apiVersion: extensions/v1beta1
            kind: Ingress
            metadata:
              name: portal
            spec:
              backend:
                serviceName: portal
                servicePort: 80
            """;
        const string upgradedManifest = """
            apiVersion: networking.k8s.io/v1
            kind: Ingress
            metadata:
              name: portal
            spec:
              defaultBackend:
                service:
                  name: portal
                  port:
                    number: 80
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/networking.k8s.io/v1/namespaces/release-ns/ingresses/portal", HttpStatusCode.OK, """
                {
                  "apiVersion": "networking.k8s.io/v1",
                  "kind": "Ingress",
                  "metadata": { "name": "portal", "resourceVersion": "42", "annotations": { "operator.example/live": "keep" } },
                  "spec": { "defaultBackend": { "service": { "name": "operator-portal", "port": { "number": 8080 } } }, "rules": [] }
                }
                """)
            .Respond(HttpMethod.Put, "/apis/networking.k8s.io/v1/namespaces/release-ns/ingresses/portal", HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(
            applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"));

        var replacement = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains("\"apiVersion\":\"networking.k8s.io/v1\"", replacement.Content);
        Assert.Contains("\"operator.example/live\":\"keep\"", replacement.Content);
        Assert.Contains("\"name\":\"operator-portal\"", replacement.Content);
        Assert.Contains("\"number\":8080", replacement.Content);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_PreservesLiveFieldsWhenPodSecurityPolicyMovesOutOfExtensionsGroup()
    {
        const string previousManifest = """
            apiVersion: extensions/v1beta1
            kind: PodSecurityPolicy
            metadata:
              name: restricted
            spec:
              privileged: false
            """;
        const string upgradedManifest = """
            apiVersion: policy/v1beta1
            kind: PodSecurityPolicy
            metadata:
              name: restricted
            spec:
              privileged: false
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/policy/v1beta1", HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "APIResourceList",
                  "groupVersion": "policy/v1beta1",
                  "resources": [{ "name": "podsecuritypolicies", "kind": "PodSecurityPolicy", "namespaced": false }]
                }
                """)
            .Respond(HttpMethod.Get, "/apis/policy/v1beta1/podsecuritypolicies/restricted", HttpStatusCode.OK, """
                {
                  "apiVersion": "policy/v1beta1",
                  "kind": "PodSecurityPolicy",
                  "metadata": { "name": "restricted", "resourceVersion": "42", "annotations": { "operator.example/live": "keep" } },
                  "spec": { "privileged": false }
                }
                """)
            .Respond(HttpMethod.Put, "/apis/policy/v1beta1/podsecuritypolicies/restricted", HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(
            applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"));

        var replacement = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains("\"apiVersion\":\"policy/v1beta1\"", replacement.Content);
        Assert.Contains("\"operator.example/live\":\"keep\"", replacement.Content);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_RemovesDeletedSecretStringDataAndKeepsLiveOnlyData()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: Secret
            metadata:
              name: credentials
            stringData:
              retired: removed
              retained: retained
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: Secret
            metadata:
              name: credentials
            stringData:
              retained: retained
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/secrets/credentials", HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Secret",
                  "metadata": { "name": "credentials", "resourceVersion": "42" },
                  "data": {
                    "retired": "cmVtb3ZlZA==",
                    "retained": "cmV0YWluZWQ=",
                    "live-only": "b3BlcmF0b3Itb25seQ=="
                  }
                }
                """)
            .Respond(HttpMethod.Put, "/api/v1/namespaces/release-ns/secrets/credentials", HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(
            applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"));

        var replace = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        using var json = JsonDocument.Parse(replace.Content!);
        var data = json.RootElement.GetProperty("data");
        Assert.False(data.TryGetProperty("retired", out _));
        Assert.Equal("cmV0YWluZWQ=", data.GetProperty("retained").GetString());
        Assert.Equal("b3BlcmF0b3Itb25seQ==", data.GetProperty("live-only").GetString());
    }

    [Fact]
    public async Task ApplyAsync_RecognizesAmbiguousSecretCreateWithStringData()
    {
        const string manifest = """
            apiVersion: v1
            kind: Secret
            metadata:
              name: credentials
            stringData:
              password: secret-value
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/secrets/credentials", HttpStatusCode.NotFound)
            .Respond(HttpMethod.Post, "/api/v1/namespaces/release-ns/secrets", HttpStatusCode.InternalServerError, """
                { "kind": "Status", "apiVersion": "v1", "status": "Failure", "code": 500 }
                """)
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/secrets/credentials", HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Secret",
                  "metadata": { "name": "credentials" },
                  "data": { "password": "c2VjcmV0LXZhbHVl" }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        await using var applied = applier.ApplyAsync(manifest, "release-ns").GetAsyncEnumerator();

        Assert.True(await applied.MoveNextAsync());
        Assert.Equal("Secret/release-ns/credentials", applied.Current);
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => applied.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ApplyAsync_RecognizesAmbiguousPodCreateWhenAdmissionAddsContainer()
    {
        const string manifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: admitted
            spec:
              containers:
                - name: app
                  image: example/app:v1
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/pods/admitted", HttpStatusCode.NotFound)
            .Respond(HttpMethod.Post, "/api/v1/namespaces/release-ns/pods", HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/pods/admitted", HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "admitted", "namespace": "release-ns" },
                  "spec": { "containers": [
                    { "name": "app", "image": "example/app:v1" },
                    { "name": "mesh-proxy", "image": "example/proxy:v2" }
                  ] }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        await using var applied = applier.ApplyAsync(manifest, "release-ns").GetAsyncEnumerator();

        Assert.True(await applied.MoveNextAsync());
        Assert.Equal("Pod/release-ns/admitted", applied.Current);
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => applied.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ApplyAsync_DoesNotMatchCaseChangedCustomResourceKeyAfterAmbiguousCreate()
    {
        const string manifest = """
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            spec:
              foo: requested
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v1",
                  "resources": [{ "name": "widgets", "kind": "Widget", "namespaced": true }]
                }
                """)
            .Respond(HttpMethod.Get, "/apis/example.com/v1/namespaces/release-ns/widgets/sample", HttpStatusCode.NotFound)
            .Respond(HttpMethod.Post, "/apis/example.com/v1/namespaces/release-ns/widgets", HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, "/apis/example.com/v1/namespaces/release-ns/widgets/sample", HttpStatusCode.OK, """
                {
                  "apiVersion": "example.com/v1",
                  "kind": "Widget",
                  "metadata": { "name": "sample", "namespace": "release-ns" },
                  "spec": { "Foo": "requested" }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(async () =>
        {
            await foreach (var resource in applier.ApplyAsync(manifest, "release-ns"))
                progress.Add(resource);
        });

        Assert.Empty(progress);
    }

    [Fact]
    public async Task ApplyAsync_DoesNotConfirmAmbiguousCustomResourceCreateWhenRootStatusDiffersWithoutSubresource()
    {
        const string manifest = """
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            spec:
              value: requested
            status:
              phase: requested
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v1",
                  "resources": [{ "name": "widgets", "kind": "Widget", "namespaced": true }]
                }
                """)
            .Respond(HttpMethod.Get, "/apis/apiextensions.k8s.io/v1/customresourcedefinitions/widgets.example.com", HttpStatusCode.OK, """
                {
                  "apiVersion": "apiextensions.k8s.io/v1",
                  "kind": "CustomResourceDefinition",
                  "metadata": { "name": "widgets.example.com" },
                  "spec": { "versions": [{ "name": "v1", "served": true, "schema": {
                    "openAPIV3Schema": { "type": "object", "properties": {
                      "spec": { "type": "object", "properties": { "value": { "type": "string" } } },
                      "status": { "type": "object", "properties": { "phase": { "type": "string" } } }
                    } }
                  } }] }
                }
                """)
            .Respond(HttpMethod.Get, "/apis/example.com/v1/namespaces/release-ns/widgets/sample", HttpStatusCode.NotFound)
            .Respond(HttpMethod.Post, "/apis/example.com/v1/namespaces/release-ns/widgets", HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, "/apis/example.com/v1/namespaces/release-ns/widgets/sample", HttpStatusCode.OK, """
                {
                  "apiVersion": "example.com/v1",
                  "kind": "Widget",
                  "metadata": { "name": "sample", "namespace": "release-ns" },
                  "spec": { "value": "requested" },
                  "status": { "phase": "controller" }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(async () =>
        {
            await foreach (var resource in applier.ApplyAsync(manifest, "release-ns"))
                progress.Add(resource);
        });

        Assert.Empty(progress);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_ConfirmsAmbiguousReplaceWhenDeploymentReplicasDefaultBackToOne()
    {
        const string previousManifest = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: app
            spec:
              replicas: 2
              selector:
                matchLabels:
                  app: app
              template:
                metadata:
                  labels:
                    app: app
                spec:
                  containers:
                    - name: app
                      image: example/app:v1
            """;
        const string upgradedManifest = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: app
            spec:
              selector:
                matchLabels:
                  app: app
              template:
                metadata:
                  labels:
                    app: app
                spec:
                  containers:
                    - name: app
                      image: example/app:v1
            """;
        const string path = "/apis/apps/v1/namespaces/release-ns/deployments/app";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "apps/v1",
                  "kind": "Deployment",
                  "metadata": { "name": "app", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "replicas": 2, "selector": { "matchLabels": { "app": "app" } }, "template": { "metadata": { "labels": { "app": "app" } }, "spec": { "containers": [{ "name": "app", "image": "example/app:v1" }] } } }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "apps/v1",
                  "kind": "Deployment",
                  "metadata": { "name": "app", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "replicas": 1, "selector": { "matchLabels": { "app": "app" } }, "template": { "metadata": { "labels": { "app": "app" } }, "spec": { "containers": [{ "name": "app", "image": "example/app:v1" }] } } }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await using var applied = applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns").GetAsyncEnumerator();

        Assert.True(await applied.MoveNextAsync());
        Assert.Equal("Deployment/release-ns/app", applied.Current);
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => applied.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ApplyUpgradeAsync_ConfirmsAmbiguousReplaceWhenRestartPolicyDefaultsBackToAlways()
    {
        const string previousManifest = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: app
            spec:
              selector:
                matchLabels:
                  app: app
              template:
                metadata:
                  labels:
                    app: app
                spec:
                  restartPolicy: Always
                  containers:
                    - name: app
                      image: example/app:v1
            """;
        const string upgradedManifest = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: app
            spec:
              selector:
                matchLabels:
                  app: app
              template:
                metadata:
                  labels:
                    app: app
                spec:
                  containers:
                    - name: app
                      image: example/app:v2
            """;
        const string path = "/apis/apps/v1/namespaces/release-ns/deployments/app";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "apps/v1",
                  "kind": "Deployment",
                  "metadata": { "name": "app", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "selector": { "matchLabels": { "app": "app" } }, "template": { "metadata": { "labels": { "app": "app" } }, "spec": { "restartPolicy": "Always", "containers": [{ "name": "app", "image": "example/app:v1" }] } } }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "apps/v1",
                  "kind": "Deployment",
                  "metadata": { "name": "app", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "selector": { "matchLabels": { "app": "app" } }, "template": { "metadata": { "labels": { "app": "app" } }, "spec": { "restartPolicy": "Always", "containers": [{ "name": "app", "image": "example/app:v2" }] } } }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await using var applied = applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns").GetAsyncEnumerator();

        Assert.True(await applied.MoveNextAsync());
        Assert.Equal("Deployment/release-ns/app", applied.Current);
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => applied.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ApplyUpgradeAsync_DoesNotConfirmAmbiguousReplaceWhenStorageClassParameterKeyCaseChanges()
    {
        const string previousManifest = "apiVersion: storage.k8s.io/v1\nkind: StorageClass\nmetadata:\n  name: custom\nparameters:\n  Foo: chart\n";
        const string upgradedManifest = "apiVersion: storage.k8s.io/v1\nkind: StorageClass\nmetadata:\n  name: custom\nparameters:\n  foo: chart\n";
        const string path = "/apis/storage.k8s.io/v1/storageclasses/custom";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "storage.k8s.io/v1",
                  "kind": "StorageClass",
                  "metadata": { "name": "custom", "resourceVersion": "42" },
                  "provisioner": "example.csi",
                  "parameters": { "Foo": "chart" }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "storage.k8s.io/v1",
                  "kind": "StorageClass",
                  "metadata": { "name": "custom", "resourceVersion": "43" },
                  "provisioner": "example.csi",
                  "parameters": { "Foo": "chart" }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(async () =>
        {
            await foreach (var resource in applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"))
                progress.Add(resource);
        });

        Assert.Empty(progress);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_DoesNotConfirmAmbiguousReplaceWhenRemovedFieldRemains()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              retired: obsolete
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data: {}
            """;
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "ConfigMap",
                  "metadata": { "name": "settings", "namespace": "release-ns", "resourceVersion": "42" },
                  "data": { "retired": "obsolete" }
                }
                """)
            .Respond(HttpMethod.Put, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "ConfigMap",
                  "metadata": { "name": "settings", "namespace": "release-ns", "resourceVersion": "43" },
                  "data": { "retired": "obsolete" }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(async () =>
        {
            await foreach (var resource in applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"))
                progress.Add(resource);
        });

        Assert.Empty(progress);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_UsesCaseSensitiveKeysInsideTypedConfigMapData()
    {
        const string previousManifest = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: settings\ndata:\n  Foo: chart\n";
        const string upgradedManifest = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: settings\ndata:\n  foo: chart\n";
        const string path = "/api/v1/namespaces/release-ns/configmaps/settings";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "ConfigMap",
                  "metadata": { "name": "settings", "namespace": "release-ns", "resourceVersion": "42" },
                  "data": { "Foo": "chart" }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "ConfigMap",
                  "metadata": { "name": "settings", "namespace": "release-ns", "resourceVersion": "43" },
                  "data": { "Foo": "chart" }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(async () =>
        {
            await foreach (var resource in applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"))
                progress.Add(resource);
        });

        Assert.Empty(progress);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_DoesNotConfirmAmbiguousReplaceWithCaseInsensitiveServiceSelector()
    {
        const string previousManifest = "apiVersion: v1\nkind: Service\nmetadata:\n  name: app\nspec:\n  selector:\n    Foo: chart\n";
        const string upgradedManifest = "apiVersion: v1\nkind: Service\nmetadata:\n  name: app\nspec:\n  selector:\n    foo: chart\n";
        const string path = "/api/v1/namespaces/release-ns/services/app";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Service",
                  "metadata": { "name": "app", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "selector": { "Foo": "chart" } }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Service",
                  "metadata": { "name": "app", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "selector": { "Foo": "chart" } }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(async () =>
        {
            await foreach (var resource in applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"))
                progress.Add(resource);
        });

        Assert.Empty(progress);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_DoesNotConfirmAmbiguousReplaceWhenRemovedListItemRemains()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: removed-container
            spec:
              containers:
                - name: app
                  image: example/app:v1
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: removed-container
            spec:
              containers: []
            """;
        var path = "/api/v1/namespaces/release-ns/pods/removed-container";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "removed-container", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "containers": [{ "name": "app", "image": "example/app:v1" }] }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "removed-container", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "containers": [{ "name": "app", "image": "example/app:v1" }] }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(async () =>
        {
            await foreach (var resource in applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"))
                progress.Add(resource);
        });

        Assert.Empty(progress);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_DoesNotConfirmAmbiguousReplaceWhenRemovedListItemIdentityRemainsWithChangedFields()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: removed-container-edited
            spec:
              containers:
                - name: app
                  image: example/app:v1
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: removed-container-edited
            spec:
              containers: []
            """;
        var path = "/api/v1/namespaces/release-ns/pods/removed-container-edited";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "removed-container-edited", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "containers": [{ "name": "app", "image": "example/app:v1" }] }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "removed-container-edited", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "containers": [{ "name": "app", "image": "example/app:v2" }] }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(async () =>
        {
            await foreach (var resource in applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"))
                progress.Add(resource);
        });

        Assert.Empty(progress);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_DoesNotConfirmRemovedServicePortWhenDefaultProtocolIsOmitted()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: Service
            metadata:
              name: web
            spec:
              ports:
                - name: retired
                  port: 80
                  targetPort: 8080
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: Service
            metadata:
              name: web
            spec:
              ports:
                - name: https
                  port: 443
                  targetPort: 8443
            """;
        const string path = "/api/v1/namespaces/release-ns/services/web";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Service",
                  "metadata": { "name": "web", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "ports": [
                    { "name": "retired", "port": 80, "targetPort": 8080, "protocol": "TCP", "nodePort": 30080 }
                  ] }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Service",
                  "metadata": { "name": "web", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "ports": [
                    { "name": "retired", "port": 80, "targetPort": 8080, "protocol": "TCP", "nodePort": 31080 },
                    { "name": "https", "port": 443, "targetPort": 8443, "protocol": "TCP" }
                  ] }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(async () =>
        {
            await foreach (var resource in applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"))
                progress.Add(resource);
        });

        Assert.Empty(progress);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_DoesNotConfirmAmbiguousReplaceWhenRemovedFieldInsideRetainedListItemRemains()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: retained-container-field
            spec:
              containers:
                - name: app
                  image: example/app:v1
                  command: ["run"]
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: retained-container-field
            spec:
              containers:
                - name: app
                  image: example/app:v1
            """;
        const string path = "/api/v1/namespaces/release-ns/pods/retained-container-field";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "retained-container-field", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "containers": [{ "name": "app", "image": "example/app:v1", "command": ["run"] }] }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "retained-container-field", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "containers": [{ "name": "app", "image": "example/app:v1", "command": ["run"] }] }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(async () =>
        {
            await foreach (var resource in applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"))
                progress.Add(resource);
        });

        Assert.Empty(progress);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_ConfirmsAmbiguousReplaceWithEquivalentQuantity()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: quantity
            spec:
              containers:
                - name: app
                  image: example/app:v1
                  resources:
                    requests:
                      cpu: "1"
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: quantity
            spec:
              containers:
                - name: app
                  image: example/app:v1
                  resources:
                    requests:
                      cpu: "1.5"
            """;
        var path = "/api/v1/namespaces/release-ns/pods/quantity";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "quantity", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "containers": [{ "name": "app", "image": "example/app:v1", "resources": { "requests": { "cpu": "1" } } }] }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "quantity", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "containers": [{ "name": "app", "image": "example/app:v1", "resources": { "requests": { "cpu": "1500m" } } }] }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await using var applied = applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns").GetAsyncEnumerator();

        Assert.True(await applied.MoveNextAsync());
        Assert.Equal("Pod/release-ns/quantity", applied.Current);
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => applied.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ApplyUpgradeAsync_ConfirmsAmbiguousHpaReplaceWithEquivalentQuantity()
    {
        const string previousManifest = """
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 3
              metrics:
                - type: Resource
                  resource:
                    name: cpu
                    target:
                      type: AverageValue
                      averageValue: "1"
            """;
        const string upgradedManifest = """
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 3
              metrics:
                - type: Resource
                  resource:
                    name: cpu
                    target:
                      type: AverageValue
                      averageValue: "1.5"
            """;
        const string path = "/apis/autoscaling/v2/namespaces/release-ns/horizontalpodautoscalers/scaler";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "autoscaling/v2",
                  "kind": "HorizontalPodAutoscaler",
                  "metadata": { "name": "scaler", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "minReplicas": 1, "maxReplicas": 3, "metrics": [{ "type": "Resource", "resource": { "name": "cpu", "target": { "type": "AverageValue", "averageValue": "1" } } }] }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "autoscaling/v2",
                  "kind": "HorizontalPodAutoscaler",
                  "metadata": { "name": "scaler", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "minReplicas": 1, "maxReplicas": 3, "metrics": [{ "type": "Resource", "resource": { "name": "cpu", "target": { "type": "AverageValue", "averageValue": "1500m" } } }] }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await using var applied = applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns").GetAsyncEnumerator();

        Assert.True(await applied.MoveNextAsync());
        Assert.Equal("HorizontalPodAutoscaler/release-ns/scaler", applied.Current);
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => applied.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ApplyUpgradeAsync_ConfirmsAmbiguousResourceQuotaReplaceWithEquivalentQuantity()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: ResourceQuota
            metadata:
              name: limits
            spec:
              hard:
                requests.cpu: "1"
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: ResourceQuota
            metadata:
              name: limits
            spec:
              hard:
                requests.cpu: "1.5"
            """;
        const string path = "/api/v1/namespaces/release-ns/resourcequotas/limits";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "ResourceQuota",
                  "metadata": { "name": "limits", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "hard": { "requests.cpu": "1" } }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "ResourceQuota",
                  "metadata": { "name": "limits", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "hard": { "requests.cpu": "1500m" } }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await using var applied = applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns").GetAsyncEnumerator();

        Assert.True(await applied.MoveNextAsync());
        Assert.Equal("ResourceQuota/release-ns/limits", applied.Current);
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => applied.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ApplyUpgradeAsync_ConfirmsAmbiguousLimitRangeReplaceWithEquivalentQuantity()
    {
        const string previousManifest = """
            apiVersion: v1
            kind: LimitRange
            metadata:
              name: limits
            spec:
              limits:
                - type: Container
                  default:
                    cpu: "1"
            """;
        const string upgradedManifest = """
            apiVersion: v1
            kind: LimitRange
            metadata:
              name: limits
            spec:
              limits:
                - type: Container
                  default:
                    cpu: "1.5"
            """;
        const string path = "/api/v1/namespaces/release-ns/limitranges/limits";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "LimitRange",
                  "metadata": { "name": "limits", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "limits": [{ "type": "Container", "default": { "cpu": "1" } }] }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "LimitRange",
                  "metadata": { "name": "limits", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "limits": [{ "type": "Container", "default": { "cpu": "1500m" } }] }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await using var applied = applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns").GetAsyncEnumerator();

        Assert.True(await applied.MoveNextAsync());
        Assert.Equal("LimitRange/release-ns/limits", applied.Current);
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => applied.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ApplyUpgradeAsync_ConfirmsAmbiguousReplaceWithEquivalentScalarQuantity()
    {
        const string manifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: scalar-quantity
            spec:
              containers:
                - name: app
                  image: example/app:v1
              volumes:
                - name: scratch
                  emptyDir:
                    sizeLimit: "1.5"
            """;
        const string path = "/api/v1/namespaces/release-ns/pods/scalar-quantity";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "scalar-quantity", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": { "containers": [{ "name": "app", "image": "example/app:v1" }], "volumes": [{ "name": "scratch", "emptyDir": { "sizeLimit": "1" } }] }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.InternalServerError)
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Pod",
                  "metadata": { "name": "scalar-quantity", "namespace": "release-ns", "resourceVersion": "43" },
                  "spec": { "containers": [{ "name": "app", "image": "example/app:v1" }], "volumes": [{ "name": "scratch", "emptyDir": { "sizeLimit": "1500m" } }] }
                }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await using var applied = applier.ApplyUpgradeAsync(manifest, manifest.Replace("1.5", "1", StringComparison.Ordinal), "release-ns").GetAsyncEnumerator();

        Assert.True(await applied.MoveNextAsync());
        Assert.Equal("Pod/release-ns/scalar-quantity", applied.Current);
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => applied.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ApplyAsync_ReadsBackAfterWriteCancellationWithUncanceledToken()
    {
        const string manifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: canceled-write
            data:
              value: committed
            """;
        using var cancellation = new CancellationTokenSource();
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/canceled-write", HttpStatusCode.NotFound)
            .Respond(HttpMethod.Post, "/api/v1/namespaces/release-ns/configmaps", HttpStatusCode.Created)
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/canceled-write", HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "ConfigMap",
                  "metadata": { "name": "canceled-write", "namespace": "release-ns" },
                  "data": { "value": "committed" }
                }
                """);
        handler.CancelAfterRequestMethod = HttpMethod.Post;
        handler.CancellationToTrigger = cancellation;
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        var progress = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var resource in applier.ApplyAsync(manifest, "release-ns", cancellation.Token))
                progress.Add(resource);
        });

        Assert.Equal(["ConfigMap/release-ns/canceled-write"], progress);
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Get));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyUpgradeAsync_PreservesCaseSensitiveCustomResourceFields(bool namespaced)
    {
        var resourcePath = namespaced
            ? "/apis/example.com/v1/namespaces/release-ns/widgets/sample"
            : "/apis/example.com/v1/widgets/sample";
        var replacePath = namespaced
            ? "/apis/example.com/v1/namespaces/release-ns/widgets/sample"
            : "/apis/example.com/v1/widgets/sample";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, $$"""
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v1",
                  "resources": [{ "name": "widgets", "kind": "Widget", "namespaced": {{namespaced.ToString().ToLowerInvariant()}} }]
                }
                """)
            .Respond(HttpMethod.Get, "/apis/apiextensions.k8s.io/v1/customresourcedefinitions/widgets.example.com", HttpStatusCode.OK, """
                {
                  "apiVersion": "apiextensions.k8s.io/v1",
                  "kind": "CustomResourceDefinition",
                  "metadata": { "name": "widgets.example.com" },
                  "spec": {
                    "versions": [{
                      "name": "v1",
                      "served": true,
                      "schema": { "openAPIV3Schema": {
                        "type": "object",
                        "properties": { "spec": { "type": "object", "properties": {
                          "config": { "type": "object" },
                          "containers": {
                            "type": "array",
                            "x-kubernetes-list-type": "atomic",
                            "items": { "type": "object", "properties": { "name": { "type": "string" }, "value": { "type": "string" } } }
                          },
                          "entries": {
                            "type": "array",
                            "x-kubernetes-list-type": "map",
                            "x-kubernetes-list-map-keys": ["name"],
                            "items": { "type": "object", "properties": { "name": { "type": "string" }, "value": { "type": "string" } } }
                          },
                          "groups": {
                            "type": "object",
                            "additionalProperties": { "type": "object", "properties": {
                              "entries": {
                                "type": "array",
                                "x-kubernetes-list-type": "map",
                                "x-kubernetes-list-map-keys": ["name"],
                                "items": { "type": "object", "properties": { "name": { "type": "string" }, "value": { "type": "string" } } }
                              }
                            } }
                          }
                        } } }
                      } }
                    }]
                  }
                }
                """)
            .Respond(HttpMethod.Get, resourcePath, HttpStatusCode.OK, """
                {
                  "apiVersion": "example.com/v1",
                  "kind": "Widget",
                  "metadata": { "name": "sample", "resourceVersion": "42" },
                  "status": { "phase": "previous" },
                  "spec": {
                    "config": { "FOO": "chart", "foo": "operator" },
                    "containers": [{ "name": "chart", "value": "before" }, { "name": "injected", "value": "retained" }],
                    "entries": [{ "name": "chart", "value": "before" }, { "name": "operator", "value": "retained" }],
                    "groups": { "one": { "entries": [{ "name": "chart", "value": "before" }, { "name": "operator", "value": "retained" }] } }
                  }
                }
                """)
            .Respond(HttpMethod.Put, replacePath, HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        const string previousManifest = """
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            spec:
              config:
                FOO: chart
              containers:
                - name: chart
                  value: before
              entries:
                - name: chart
                  value: before
              groups:
                one:
                  entries:
                    - name: chart
                      value: before
            status:
              phase: previous
            """;
        const string upgradedManifest = """
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            spec:
              config:
                FOO: chart
              containers:
                - name: chart
                  value: after
              entries:
                - name: chart
                  value: after
              groups:
                one:
                  entries:
                    - name: chart
                      value: after
            status:
              phase: chart
            """;

        await AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyUpgradeAsync(upgradedManifest, previousManifest, "release-ns"));

        var replace = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        using var json = JsonDocument.Parse(replace.Content!);
        var config = json.RootElement.GetProperty("spec").GetProperty("config");
        Assert.Equal("chart", config.GetProperty("FOO").GetString());
        Assert.True(json.RootElement.TryGetProperty("status", out var status), replace.Content);
        Assert.Equal("chart", status.GetProperty("phase").GetString());
        Assert.Equal("operator", config.GetProperty("foo").GetString());
        var containers = json.RootElement.GetProperty("spec").GetProperty("containers");
        Assert.Single(containers.EnumerateArray());
        Assert.Equal("after", containers[0].GetProperty("value").GetString());
        var entries = json.RootElement.GetProperty("spec").GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("after", entries[0].GetProperty("value").GetString());
        Assert.Equal("operator", entries[1].GetProperty("name").GetString());
        var nestedEntries = json.RootElement.GetProperty("spec").GetProperty("groups").GetProperty("one").GetProperty("entries");
        Assert.Equal(2, nestedEntries.GetArrayLength());
        Assert.Equal("operator", nestedEntries[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ApplyAsync_UsesDiscoveryForNamespacedCustomResources()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v1",
                  "resources": [{ "name": "widgets", "kind": "Widget", "namespaced": true }]
                }
                """)
            .Respond(HttpMethod.Post, "/apis/example.com/v1/namespaces/release-ns/widgets?fieldManager=helmsharp-test", HttpStatusCode.Created)
            .Respond(HttpMethod.Post, "/apis/example.com/v1/namespaces/release-ns/widgets?fieldManager=helmsharp-test", HttpStatusCode.Created);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyAsync("""
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            ---
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: another
            """, "release-ns"));

        Assert.Equal(1, handler.Requests.Count(request =>
            request.Method == HttpMethod.Get && request.PathAndQuery == "/apis/example.com/v1"));
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Get && request.PathAndQuery == "/apis/example.com/v1/namespaces/release-ns/widgets/sample");
        Assert.Equal(2, handler.Requests.Count(request =>
            request.Method == HttpMethod.Post && request.PathAndQuery == "/apis/example.com/v1/namespaces/release-ns/widgets?fieldManager=helmsharp-test"));
    }

    [Fact]
    public async Task ApplyAsync_UsesNamespacedEndpointForCustomKindMatchingClusterScopedBuiltIn()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v1",
                  "resources": [{ "name": "customroles", "kind": "ClusterRole", "namespaced": true }]
                }
                """)
            .Respond(
                HttpMethod.Post,
                "/apis/example.com/v1/namespaces/tenant-a/customroles?fieldManager=helmsharp-test",
                HttpStatusCode.Created);
        var applier = new KubernetesManifestApplier(
            KubernetesTestClientBuilder.Create(handler),
            "helmsharp-test");

        var applied = await AsyncEnumerableTestExtensions.CollectAsync(applier.ApplyAsync("""
            apiVersion: example.com/v1
            kind: ClusterRole
            metadata:
              name: custom-reader
              namespace: tenant-a
            """, "release-ns"));

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get &&
            request.PathAndQuery == "/apis/example.com/v1/namespaces/tenant-a/customroles/custom-reader");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post &&
            request.PathAndQuery == "/apis/example.com/v1/namespaces/tenant-a/customroles?fieldManager=helmsharp-test");
        Assert.Equal(["ClusterRole/tenant-a/custom-reader"], applied);
    }

    [Fact]
    public async Task ApplyAsync_UsesClusterEndpointForClusterScopedCustomResources()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v1",
                  "resources": [{ "name": "clustersettings", "kind": "ClusterSetting", "namespaced": false }]
                }
                """)
            .Respond(HttpMethod.Post, "/apis/example.com/v1/clustersettings?fieldManager=helmsharp-test", HttpStatusCode.Created);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var applied = await AsyncEnumerableTestExtensions.CollectAsync(applier.ApplyAsync("""
            apiVersion: example.com/v1
            kind: ClusterSetting
            metadata:
              name: shared
              namespace: ignored
            """, "release-ns"));

        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Get && request.PathAndQuery == "/apis/example.com/v1/clustersettings/shared");
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Post && request.PathAndQuery == "/apis/example.com/v1/clustersettings?fieldManager=helmsharp-test");
        Assert.DoesNotContain(handler.Requests, request => request.PathAndQuery.Contains("/namespaces/", StringComparison.Ordinal));
        Assert.Equal(["ClusterSetting/shared"], applied);
        var create = handler.Requests.Last(request => request.Method == HttpMethod.Post);
        Assert.DoesNotContain("namespace", create.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_RetriesDiscoveryUntilCustomResourceKindRegisters()
    {
        // First discovery misses the kind (CRD endpoint not registered yet); the second sees it.
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                { "kind": "APIResourceList", "apiVersion": "v1", "groupVersion": "example.com/v1", "resources": [] }
                """)
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v1",
                  "resources": [{ "name": "widgets", "kind": "Widget", "namespaced": true }]
                }
                """)
            .Respond(HttpMethod.Post, "/apis/example.com/v1/namespaces/release-ns/widgets?fieldManager=helmsharp-test", HttpStatusCode.Created);
        var delayCalls = new List<TimeSpan>();
        var applier = new KubernetesManifestApplier(
            KubernetesTestClientBuilder.Create(handler),
            "helmsharp-test",
            (delay, _) =>
            {
                delayCalls.Add(delay);
                return Task.CompletedTask;
            });

        var applied = await AsyncEnumerableTestExtensions.CollectAsync(applier.ApplyAsync("""
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            """, "release-ns"));

        Assert.Equal(["Widget/release-ns/sample"], applied);
        Assert.NotEmpty(delayCalls);
        Assert.Equal(2, handler.Requests.Count(request =>
            request.Method == HttpMethod.Get && request.PathAndQuery == "/apis/example.com/v1"));
    }

    [Fact]
    public async Task ApplyAsync_FailsAfterDiscoveryRetriesWhenKindRemainsAbsent()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                { "kind": "APIResourceList", "apiVersion": "v1", "groupVersion": "example.com/v1", "resources": [] }
                """)
            .RespondAlways(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                { "kind": "APIResourceList", "apiVersion": "v1", "groupVersion": "example.com/v1", "resources": [] }
                """);
        var applier = new KubernetesManifestApplier(
            KubernetesTestClientBuilder.Create(handler),
            "helmsharp-test",
            (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<KubernetesResourceOperationException>(() =>
            AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyAsync("""
            apiVersion: example.com/v1
            kind: Missing
            metadata:
              name: sample
            """, "release-ns")));

        Assert.IsType<KubernetesApiResourceNotFoundException>(exception.InnerException);
    }

    [Fact]
    public async Task ApplyAsync_UsesDeclaredV2Beta2EndpointThroughDiscovery()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/autoscaling/v2beta2", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "autoscaling/v2beta2",
                  "resources": [{ "name": "horizontalpodautoscalers", "kind": "HorizontalPodAutoscaler", "namespaced": true }]
                }
                """)
            .Respond(
                HttpMethod.Post,
                "/apis/autoscaling/v2beta2/namespaces/release-ns/horizontalpodautoscalers?fieldManager=helmsharp-test",
                HttpStatusCode.Created);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyAsync("""
            apiVersion: autoscaling/v2beta2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            spec:
              minReplicas: 1
              maxReplicas: 2
            """, "release-ns"));

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get &&
            request.PathAndQuery == "/apis/autoscaling/v2beta2/namespaces/release-ns/horizontalpodautoscalers/scaler");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post &&
            request.PathAndQuery == "/apis/autoscaling/v2beta2/namespaces/release-ns/horizontalpodautoscalers?fieldManager=helmsharp-test");
        Assert.DoesNotContain(handler.Requests, request =>
            request.PathAndQuery.StartsWith("/apis/autoscaling/v2/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_PreservesAssignedServiceNetworkingFieldsAndHonorsExplicitUpdates()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/services/api", HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Service",
                  "metadata": { "name": "api", "resourceVersion": "42" },
                  "spec": {
                    "clusterIP": "10.0.0.10",
                    "clusterIPs": ["10.0.0.10"],
                    "ipFamilyPolicy": "SingleStack",
                    "healthCheckNodePort": 30001
                  }
                }
                """)
            .Respond(HttpMethod.Put, "/api/v1/namespaces/release-ns/services/api", HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyAsync("""
            apiVersion: v1
            kind: Service
            metadata:
              name: api
            spec:
              selector:
                app: api
              ports:
                - port: 80
              ipFamilyPolicy: RequireDualStack
              healthCheckNodePort: 30002
            """, "release-ns"));

        var replace = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains("\"resourceVersion\":\"42\"", replace.Content);
        Assert.Contains("\"clusterIP\":\"10.0.0.10\"", replace.Content);
        Assert.Contains("\"ipFamilyPolicy\":\"RequireDualStack\"", replace.Content);
        Assert.Contains("\"healthCheckNodePort\":30002", replace.Content);
    }

    [Fact]
    public async Task ApplyAsync_DoesNotRetainHealthCheckNodePortForServicesThatDoNotSupportIt()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/services/api", HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Service",
                  "metadata": { "name": "api", "resourceVersion": "42" },
                  "spec": {
                    "clusterIP": "10.0.0.10",
                    "clusterIPs": ["10.0.0.10"],
                    "type": "LoadBalancer",
                    "externalTrafficPolicy": "Local",
                    "healthCheckNodePort": 30001
                  }
                }
                """)
            .Respond(HttpMethod.Put, "/api/v1/namespaces/release-ns/services/api", HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyAsync("""
            apiVersion: v1
            kind: Service
            metadata:
              name: api
            spec:
              type: ClusterIP
              selector:
                app: api
              ports:
                - port: 80
            """, "release-ns"));

        var replace = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.DoesNotContain("healthCheckNodePort", replace.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyUpgradeAsync_DoesNotRestoreClusterIpsForExternalNameService()
    {
        const string path = "/api/v1/namespaces/release-ns/services/api";
        const string previousManifest = "apiVersion: v1\nkind: Service\nmetadata:\n  name: api\nspec:\n  type: ClusterIP\n  ports:\n  - port: 80\n";
        const string upgradedManifest = "apiVersion: v1\nkind: Service\nmetadata:\n  name: api\nspec:\n  type: ExternalName\n  externalName: api.example.test\n";
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, path, HttpStatusCode.OK, """
                {
                  "apiVersion": "v1",
                  "kind": "Service",
                  "metadata": { "name": "api", "namespace": "release-ns", "resourceVersion": "42" },
                  "spec": {
                    "type": "ClusterIP",
                    "clusterIP": "10.0.0.10",
                    "clusterIPs": ["10.0.0.10"],
                    "ipFamilyPolicy": "SingleStack",
                    "ports": [{ "port": 80 }]
                  }
                }
                """)
            .Respond(HttpMethod.Put, path, HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyUpgradeAsync(
            upgradedManifest, previousManifest, "release-ns"));

        var replace = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains("\"type\":\"ExternalName\"", replace.Content);
        Assert.DoesNotContain("clusterIP", replace.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clusterIPs", replace.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ipFamilyPolicy", replace.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"ports\"", replace.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Background")]
    [InlineData("Foreground")]
    [InlineData("Orphan")]
    public async Task DeleteAsync_DeletesResourcesInReverseManifestOrderWithPropagationPolicy(string propagationPolicy)
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Delete, "/api/v1/namespaces/release-ns/secrets/credentials", HttpStatusCode.OK)
            .Respond(HttpMethod.Delete, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var deleted = await AsyncEnumerableTestExtensions.CollectAsync(applier.DeleteAsync("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            ---
            apiVersion: v1
            kind: Secret
            metadata:
              name: credentials
            """, "release-ns", propagationPolicy));

        Assert.Equal(["Secret/release-ns/credentials", "ConfigMap/release-ns/settings"], deleted);
        var deleteRequests = handler.Requests.Where(request => request.Method == HttpMethod.Delete).ToList();
        Assert.Equal(
            ["/api/v1/namespaces/release-ns/secrets/credentials", "/api/v1/namespaces/release-ns/configmaps/settings"],
            deleteRequests.Select(request => request.PathAndQuery));
        Assert.All(deleteRequests, request => Assert.Contains(propagationPolicy, request.Content));
    }

    [Fact]
    public async Task DeleteAsync_ReportsMissingDiscoveredResourceIdentity()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                { "kind": "APIResourceList", "apiVersion": "v1", "groupVersion": "example.com/v1", "resources": [] }
                """)
            .Respond(HttpMethod.Get, "/apis/", HttpStatusCode.OK, """
                { "kind": "APIGroupList", "apiVersion": "v1", "groups": [] }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var exception = await Assert.ThrowsAsync<KubernetesResourceOperationException>(() =>
            AsyncEnumerableTestExtensions.DrainAsync(applier.DeleteAsync("""
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            """, "release-ns")));

        Assert.Contains("example.com/v1 Widget/release-ns/sample", exception.Message);
        Assert.IsType<KubernetesApiResourceNotFoundException>(exception.InnerException);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal((HttpMethod.Get, "/apis/example.com/v1"), (request.Method, request.PathAndQuery)),
            request => Assert.Equal((HttpMethod.Get, "/apis/"), (request.Method, request.PathAndQuery)));
    }

    [Fact]
    public async Task DeleteAsync_UsesAlternateServedVersionWhenDeclaredVersionIsObsolete()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.NotFound, "{ \"code\": 404 }")
            .Respond(HttpMethod.Get, "/apis/", HttpStatusCode.OK, """
                {
                  "kind": "APIGroupList",
                  "apiVersion": "v1",
                  "groups": [{
                    "name": "example.com",
                    "versions": [{ "groupVersion": "example.com/v2", "version": "v2" }],
                    "preferredVersion": { "groupVersion": "example.com/v2", "version": "v2" }
                  }]
                }
                """)
            .Respond(HttpMethod.Get, "/apis/example.com/v2", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v2",
                  "resources": [{ "name": "widgets", "kind": "Widget", "namespaced": true }]
                }
                """)
            .Respond(
                HttpMethod.Delete,
                "/apis/example.com/v2/namespaces/release-ns/widgets/sample",
                HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var deleted = await AsyncEnumerableTestExtensions.CollectAsync(applier.DeleteAsync("""
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            """, "release-ns"));

        Assert.Equal(["Widget/release-ns/sample"], deleted);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete &&
            request.PathAndQuery == "/apis/example.com/v2/namespaces/release-ns/widgets/sample");
    }

    [Fact]
    public async Task DeleteAsync_RejectsUnsupportedCoreResourcesWithoutUsingCustomObjectEndpoints()
    {
        var handler = new KubernetesApiHandler();
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var exception = await Assert.ThrowsAsync<KubernetesResourceOperationException>(() =>
            AsyncEnumerableTestExtensions.DrainAsync(applier.DeleteAsync("""
            apiVersion: v1
            kind: Event
            metadata:
              name: sample
            """, "release-ns")));

        Assert.Contains("v1 Event/release-ns/sample", exception.Message);
        Assert.IsType<KubernetesApiResourceUnsupportedException>(exception.InnerException);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DeleteAsync_UsesClusterEndpointForNamespaceAndTreatsNotFoundAsSuccess()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Delete, "/api/v1/namespaces/tenant", HttpStatusCode.NotFound);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var deleted = await AsyncEnumerableTestExtensions.CollectAsync(applier.DeleteAsync("""
            apiVersion: v1
            kind: Namespace
            metadata:
              name: tenant
              namespace: ignored
            """, "release-ns"));

        Assert.Equal(["Namespace/tenant"], deleted);
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Delete, "/api/v1/namespaces/tenant"), (request.Method, request.PathAndQuery));
        Assert.Contains("Background", request.Content);
    }

    [Fact]
    public async Task DeleteAsync_DiscoversNamespacedAndClusterScopedCustomResourceEndpoints()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v1",
                  "resources": [
                    { "name": "widgets", "kind": "Widget", "namespaced": true },
                    { "name": "clustersettings", "kind": "ClusterSetting", "namespaced": false }
                  ]
                }
                """)
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v1",
                  "resources": [
                    { "name": "widgets", "kind": "Widget", "namespaced": true },
                    { "name": "clustersettings", "kind": "ClusterSetting", "namespaced": false }
                  ]
                }
                """)
            .Respond(HttpMethod.Delete, "/apis/example.com/v1/clustersettings/shared", HttpStatusCode.OK)
            .Respond(HttpMethod.Delete, "/apis/example.com/v1/namespaces/chart-ns/widgets/sample", HttpStatusCode.NotFound);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var deleted = await AsyncEnumerableTestExtensions.CollectAsync(applier.DeleteAsync("""
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
              namespace: chart-ns
            ---
            apiVersion: example.com/v1
            kind: ClusterSetting
            metadata:
              name: shared
              namespace: ignored
            """, "release-ns", "Orphan"));

        Assert.Equal(["ClusterSetting/shared", "Widget/chart-ns/sample"], deleted);
        Assert.Equal(2, handler.Requests.Count(request =>
            request.Method == HttpMethod.Get && request.PathAndQuery == "/apis/example.com/v1"));
        var deleteRequests = handler.Requests.Where(request => request.Method == HttpMethod.Delete).ToList();
        Assert.Equal(
            ["/apis/example.com/v1/clustersettings/shared", "/apis/example.com/v1/namespaces/chart-ns/widgets/sample"],
            deleteRequests.Select(request => request.PathAndQuery));
        Assert.All(deleteRequests, request => Assert.Contains("Orphan", request.Content));
    }

    [Fact]
    public async Task DeleteAsync_ReportsResolvedClusterScopedCustomResourceIdentityOnApiFailure()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "example.com/v1",
                  "resources": [{ "name": "clustersettings", "kind": "ClusterSetting", "namespaced": false }]
                }
                """)
            .Respond(HttpMethod.Delete, "/apis/example.com/v1/clustersettings/shared", HttpStatusCode.InternalServerError, """
                { "kind": "Status", "apiVersion": "v1", "status": "Failure", "code": 500 }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var exception = await Assert.ThrowsAsync<KubernetesResourceOperationException>(() =>
            AsyncEnumerableTestExtensions.DrainAsync(applier.DeleteAsync("""
                apiVersion: example.com/v1
                kind: ClusterSetting
                metadata:
                  name: shared
                  namespace: ignored
                """, "release-ns")));

        Assert.Contains("example.com/v1 ClusterSetting/shared", exception.Message);
        Assert.DoesNotContain("release-ns", exception.Message);
        Assert.IsType<HttpOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task DeleteAsync_UsesDeclaredV2Beta2EndpointThroughDiscovery()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/autoscaling/v2beta2", HttpStatusCode.OK, """
                {
                  "kind": "APIResourceList",
                  "apiVersion": "v1",
                  "groupVersion": "autoscaling/v2beta2",
                  "resources": [{ "name": "horizontalpodautoscalers", "kind": "HorizontalPodAutoscaler", "namespaced": true }]
                }
                """)
            .Respond(
                HttpMethod.Delete,
                "/apis/autoscaling/v2beta2/namespaces/release-ns/horizontalpodautoscalers/scaler",
                HttpStatusCode.OK);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(applier.DeleteAsync("""
            apiVersion: autoscaling/v2beta2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            """, "release-ns"));

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete &&
            request.PathAndQuery == "/apis/autoscaling/v2beta2/namespaces/release-ns/horizontalpodautoscalers/scaler");
        Assert.DoesNotContain(handler.Requests, request =>
            request.PathAndQuery.StartsWith("/apis/autoscaling/v2/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteAsync_ThrowsCancellationBeforeSendingARequest()
    {
        var handler = new KubernetesApiHandler();
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AsyncEnumerableTestExtensions.DrainAsync(applier.DeleteAsync("""
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: settings
                """, "release-ns", cancellationSource.Token)));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DeleteAsync_ReportsKubernetesApiFailureForAffectedResource()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Delete, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.InternalServerError, """
                { "kind": "Status", "apiVersion": "v1", "status": "Failure", "code": 500 }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var exception = await Assert.ThrowsAsync<KubernetesResourceOperationException>(() =>
            AsyncEnumerableTestExtensions.DrainAsync(applier.DeleteAsync("""
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: settings
                """, "release-ns")));

        Assert.Contains("v1 ConfigMap/release-ns/settings", exception.Message);
        Assert.IsType<HttpOperationException>(exception.InnerException);
        Assert.Equal(
            "/api/v1/namespaces/release-ns/configmaps/settings",
            Assert.Single(handler.Requests).PathAndQuery);
    }

    [Fact]
    public async Task DeleteAsync_RejectsInvalidPropagationBeforeMutation()
    {
        var handler = new KubernetesApiHandler();
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            AsyncEnumerableTestExtensions.DrainAsync(applier.DeleteAsync("""
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: settings
                """, "release-ns", "Cascade")));

        Assert.Contains("Background, Foreground, and Orphan", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ApplyAsync_ThrowsCancellationBeforeSendingAMutationRequest()
    {
        var handler = new KubernetesApiHandler();
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyAsync("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            """, "release-ns", cancellationSource.Token)));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ApplyAsync_PropagatesKubernetesApiFailureForTheAffectedResourceEndpoint()
    {
        var handler = new KubernetesApiHandler()
            .RespondAlways(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.InternalServerError, """
                { "kind": "Status", "apiVersion": "v1", "status": "Failure", "code": 500 }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var exception = await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyAsync("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            """, "release-ns")));

        Assert.Contains("ConfigMap/release-ns/settings", exception.Message);
        Assert.IsType<HttpOperationException>(exception.InnerException);
        Assert.Equal(
            "/api/v1/namespaces/release-ns/configmaps/settings",
            Assert.Single(handler.Requests).PathAndQuery);
    }

    [Fact]
    public async Task ApplyAsync_RejectsUnsupportedCoreResourcesWithoutUsingTheCustomObjectEndpoint()
    {
        var handler = new KubernetesApiHandler();
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => AsyncEnumerableTestExtensions.DrainAsync(applier.ApplyAsync("""
            apiVersion: v1
            kind: Event
            metadata:
              name: sample
            """, "release-ns")));

        Assert.Contains("Event/release-ns/sample", exception.Message);
        Assert.Contains("v1/Event", exception.InnerException?.Message);
        Assert.Empty(handler.Requests);
    }
}
