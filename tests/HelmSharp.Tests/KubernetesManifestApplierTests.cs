using System.Net;
using HelmSharp.Kube;
using k8s.Autorest;

namespace HelmSharp.Tests;

public sealed class KubernetesManifestApplierTests
{
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

        Assert.Collection(
            handler.Requests,
            request => Assert.Equal((HttpMethod.Get, "/api/v1/namespaces/release-ns"), (request.Method, request.PathAndQuery)),
            request =>
            {
                Assert.Equal((HttpMethod.Post, "/api/v1/namespaces"), (request.Method, request.PathAndQuery));
                Assert.Contains("release-ns", request.Content);
            });
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

        Assert.Collection(
            handler.Requests,
            request => Assert.Equal((HttpMethod.Get, "/api/v1/namespaces/chart-ns/configmaps/settings"), (request.Method, request.PathAndQuery)),
            request =>
            {
                Assert.Equal((HttpMethod.Put, "/api/v1/namespaces/chart-ns/configmaps/settings"), (request.Method, request.PathAndQuery));
                Assert.Contains("\"resourceVersion\":\"42\"", request.Content);
                Assert.Contains("\"namespace\":\"chart-ns\"", request.Content);
            });
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
            .Respond(HttpMethod.Post, "/apis/example.com/v1/namespaces/release-ns/widgets?fieldManager=helmsharp-test", HttpStatusCode.Created, "{}")
            .Respond(HttpMethod.Post, "/apis/example.com/v1/namespaces/release-ns/widgets?fieldManager=helmsharp-test", HttpStatusCode.Created, "{}");
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
            .Respond(HttpMethod.Post, "/apis/example.com/v1/clustersettings?fieldManager=helmsharp-test", HttpStatusCode.Created, "{}");
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
    public async Task ApplyAsync_PreservesKubernetesAssignedServiceNetworkingFieldsOnReplace()
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
            .Respond(HttpMethod.Put, "/api/v1/namespaces/release-ns/services/api", HttpStatusCode.OK, "{}");
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
            """, "release-ns"));

        var replace = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains("\"resourceVersion\":\"42\"", replace.Content);
        Assert.Contains("\"clusterIP\":\"10.0.0.10\"", replace.Content);
        Assert.Contains("\"ipFamilyPolicy\":\"SingleStack\"", replace.Content);
        Assert.Contains("\"healthCheckNodePort\":30001", replace.Content);
    }

    [Fact]
    public async Task DeleteAsync_DeletesResourcesInReverseManifestOrderWithPropagationPolicy()
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
            """, "release-ns", "Foreground"));

        Assert.Equal(["Secret/release-ns/credentials", "ConfigMap/release-ns/settings"], deleted);
        var deleteRequests = handler.Requests.Where(request => request.Method == HttpMethod.Delete).ToList();
        Assert.Equal(
            ["/api/v1/namespaces/release-ns/secrets/credentials", "/api/v1/namespaces/release-ns/configmaps/settings"],
            deleteRequests.Select(request => request.PathAndQuery));
        Assert.All(deleteRequests, request => Assert.Contains("Foreground", request.Content));
    }

    [Fact]
    public async Task DeleteAsync_TreatsMissingDiscoveredResourcesAsAlreadyGone()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                { "kind": "APIResourceList", "apiVersion": "v1", "groupVersion": "example.com/v1", "resources": [] }
                """);
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(applier.DeleteAsync("""
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            """, "release-ns"));

        Assert.Collection(
            handler.Requests,
            request => Assert.Equal((HttpMethod.Get, "/apis/example.com/v1"), (request.Method, request.PathAndQuery)));
    }

    [Fact]
    public async Task DeleteAsync_DoesNotUseCustomObjectEndpointsForUnsupportedCoreResources()
    {
        var handler = new KubernetesApiHandler();
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");

        await AsyncEnumerableTestExtensions.DrainAsync(applier.DeleteAsync("""
            apiVersion: v1
            kind: Event
            metadata:
              name: sample
            """, "release-ns"));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ApplyAsync_ThrowsCancellationBeforeSendingAMutationRequest()
    {
        var handler = new KubernetesApiHandler();
        var applier = new KubernetesManifestApplier(KubernetesTestClientBuilder.Create(handler), "helmsharp-test");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

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
