using System.Net;
using HelmSharp.Kube;
using k8s.Autorest;

namespace HelmSharp.Tests;

public sealed class KubernetesResourceWaiterTests
{
    [Fact]
    public async Task WaitForReadyAsync_ReturnsImmediatelyWhenManifestHasNoWaitableResources()
    {
        var handler = new KubernetesApiHandler();
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        var messages = await AsyncEnumerableTestExtensions.CollectAsync(waiter.WaitForReadyAsync("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            """, "release-ns"));

        Assert.Equal(["No waitable resources found"], messages);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WaitForReadyAsync_ReadsOnlyWaitableResourcesInTheManifestNamespace()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/apps/v1/namespaces/chart-ns/deployments/web", HttpStatusCode.OK, ReadyDeployment("web"));
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        var messages = await AsyncEnumerableTestExtensions.CollectAsync(waiter.WaitForReadyAsync("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: web
              namespace: chart-ns
            """, "release-ns"));

        Assert.Contains("  Deployment/chart-ns/web is ready", messages);
        Assert.Contains("All 1 resources are ready", messages);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal((HttpMethod.Get, "/apis/apps/v1/namespaces/chart-ns/deployments/web"), (request.Method, request.PathAndQuery)));
    }

    [Fact]
    public async Task WaitForReadyAsync_DoesNotReadJobsWhenWaitForJobsIsDisabled()
    {
        var handler = new KubernetesApiHandler();
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        var messages = await AsyncEnumerableTestExtensions.CollectAsync(waiter.WaitForReadyAsync("""
            apiVersion: batch/v1
            kind: Job
            metadata:
              name: migrate
            """, "release-ns", waitForJobs: false));

        Assert.Contains("  Job/release-ns/migrate is ready", messages);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WaitForReadyAsync_ReadsCompletedJobsWhenWaitForJobsIsEnabled()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/batch/v1/namespaces/release-ns/jobs/migrate", HttpStatusCode.OK, """
                {
                  "apiVersion": "batch/v1",
                  "kind": "Job",
                  "metadata": { "name": "migrate" },
                  "spec": { "completions": 1 },
                  "status": { "conditions": [{ "type": "Complete", "status": "True" }] }
                }
                """);
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        var messages = await AsyncEnumerableTestExtensions.CollectAsync(waiter.WaitForReadyAsync("""
            apiVersion: batch/v1
            kind: Job
            metadata:
              name: migrate
            """, "release-ns", waitForJobs: true));

        Assert.Contains("  Job/release-ns/migrate is ready", messages);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal((HttpMethod.Get, "/apis/batch/v1/namespaces/release-ns/jobs/migrate"), (request.Method, request.PathAndQuery)));
    }

    [Fact]
    public async Task WaitForReadyAsync_ReportsDeterministicTimeoutWithResourceIdentity()
    {
        var handler = new KubernetesApiHandler()
            .RespondAlways(HttpMethod.Get, "/apis/apps/v1/namespaces/release-ns/deployments/web", HttpStatusCode.OK, PendingDeployment("web"));
        var timeProvider = new DeterministicTimeProvider(DateTimeOffset.UnixEpoch);
        var polling = new DeterministicPolling(timeProvider);
        var waiter = new KubernetesResourceWaiter(
            KubernetesTestClientBuilder.Create(handler),
            timeoutSeconds: 1,
            timeProvider,
            polling.DelayAsync);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForReadyAsync("""
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: web
            """, "release-ns")));

        Assert.Contains("Deployment/release-ns/web", exception.Message);
        Assert.Equal([TimeSpan.FromSeconds(3)], polling.Delays);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WaitForReadyAsync_PropagatesCancellationBeforeQueryingTheApi()
    {
        var handler = new KubernetesApiHandler();
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForReadyAsync("""
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: web
            """, "release-ns", cancellationToken: cancellationSource.Token)));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WaitForReadyAsync_RetriesApiFailuresWithoutRealTimeDelays()
    {
        var handler = new KubernetesApiHandler()
            .RespondAlways(HttpMethod.Get, "/apis/apps/v1/namespaces/release-ns/deployments/web", HttpStatusCode.InternalServerError, """
                { "kind": "Status", "apiVersion": "v1", "status": "Failure", "code": 500 }
                """);
        var timeProvider = new DeterministicTimeProvider(DateTimeOffset.UnixEpoch);
        var polling = new DeterministicPolling(timeProvider, advanceClock: false);
        var waiter = new KubernetesResourceWaiter(
            KubernetesTestClientBuilder.Create(handler),
            timeoutSeconds: 30,
            timeProvider,
            polling.DelayAsync);

        var exception = await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForReadyAsync("""
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: web
            """, "release-ns")));

        Assert.Contains("Deployment/release-ns/web", exception.Message);
        Assert.IsType<HttpOperationException>(exception.InnerException);
        Assert.Equal(11, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("/apis/apps/v1/namespaces/release-ns/deployments/web", request.PathAndQuery));
        Assert.Equal(10, polling.Delays.Count);
    }

    [Fact]
    public async Task WaitForDeletedAsync_TreatsNotFoundAsDeletedForTypedResources()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.NotFound);
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        var messages = await AsyncEnumerableTestExtensions.CollectAsync(waiter.WaitForDeletedAsync("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            """, "release-ns"));

        Assert.Contains("  ConfigMap/release-ns/settings deleted", messages);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal((HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings"), (request.Method, request.PathAndQuery)));
    }

    [Fact]
    public async Task WaitForDeletedAsync_PollsNamespaceUntilItIsAbsent()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/api/v1/namespaces/tenant", HttpStatusCode.OK, """
                { "apiVersion": "v1", "kind": "Namespace", "metadata": { "name": "tenant" } }
                """)
            .Respond(HttpMethod.Get, "/api/v1/namespaces/tenant", HttpStatusCode.NotFound);
        var timeProvider = new DeterministicTimeProvider(DateTimeOffset.UnixEpoch);
        var polling = new DeterministicPolling(timeProvider);
        var waiter = new KubernetesResourceWaiter(
            KubernetesTestClientBuilder.Create(handler),
            timeoutSeconds: 30,
            timeProvider,
            polling.DelayAsync);

        var messages = await AsyncEnumerableTestExtensions.CollectAsync(waiter.WaitForDeletedAsync("""
            apiVersion: v1
            kind: Namespace
            metadata:
              name: tenant
            """, "release-ns"));

        Assert.Contains("  Namespace/tenant deleted", messages);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
            Assert.Equal((HttpMethod.Get, "/api/v1/namespaces/tenant"), (request.Method, request.PathAndQuery)));
        Assert.Equal([TimeSpan.FromSeconds(1)], polling.Delays);
    }

    [Fact]
    public async Task WaitForDeletedAsync_UsesDiscoveryAndTheNamespacedCustomResourceEndpoint()
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
            .Respond(HttpMethod.Get, "/apis/example.com/v1/namespaces/release-ns/widgets/sample", HttpStatusCode.NotFound);
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        await AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForDeletedAsync("""
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            """, "release-ns"));

        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Get && request.PathAndQuery == "/apis/example.com/v1");
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Get && request.PathAndQuery == "/apis/example.com/v1/namespaces/release-ns/widgets/sample");
    }

    [Fact]
    public async Task WaitForDeletedAsync_NormalizesClusterScopedCustomResourceIdentity()
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
            .Respond(HttpMethod.Get, "/apis/example.com/v1/clustersettings/shared", HttpStatusCode.NotFound);
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        var messages = await AsyncEnumerableTestExtensions.CollectAsync(waiter.WaitForDeletedAsync("""
            apiVersion: example.com/v1
            kind: ClusterSetting
            metadata:
              name: shared
              namespace: ignored
            """, "release-ns"));

        Assert.Contains("  ClusterSetting/shared deleted", messages);
        Assert.DoesNotContain(messages, line => line.Contains("release-ns", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get &&
            request.PathAndQuery == "/apis/example.com/v1/clustersettings/shared");
        Assert.DoesNotContain(handler.Requests, request =>
            request.PathAndQuery.Contains("/namespaces/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitForDeletedAsync_UsesDeclaredV2Beta2EndpointThroughDiscovery()
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
                HttpMethod.Get,
                "/apis/autoscaling/v2beta2/namespaces/release-ns/horizontalpodautoscalers/scaler",
                HttpStatusCode.NotFound);
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        await AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForDeletedAsync("""
            apiVersion: autoscaling/v2beta2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            """, "release-ns"));

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get &&
            request.PathAndQuery == "/apis/autoscaling/v2beta2/namespaces/release-ns/horizontalpodautoscalers/scaler");
        Assert.DoesNotContain(handler.Requests, request =>
            request.PathAndQuery.StartsWith("/apis/autoscaling/v2/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitForDeletedAsync_TreatsRemovedDiscoveredApiAsDeleted()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/example.com/v1", HttpStatusCode.OK, """
                { "kind": "APIResourceList", "apiVersion": "v1", "groupVersion": "example.com/v1", "resources": [] }
                """)
            .Respond(HttpMethod.Get, "/apis/", HttpStatusCode.OK, """
                { "kind": "APIGroupList", "apiVersion": "v1", "groups": [] }
                """);
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        await AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForDeletedAsync("""
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            """, "release-ns"));

        Assert.Collection(
            handler.Requests,
            request => Assert.Equal((HttpMethod.Get, "/apis/example.com/v1"), (request.Method, request.PathAndQuery)),
            request => Assert.Equal((HttpMethod.Get, "/apis/"), (request.Method, request.PathAndQuery)));
    }

    [Fact]
    public async Task WaitForDeletedAsync_UsesAlternateServedVersionForObsoleteManifestApi()
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
                HttpMethod.Get,
                "/apis/example.com/v2/namespaces/release-ns/widgets/sample",
                HttpStatusCode.NotFound);
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        await AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForDeletedAsync("""
            apiVersion: example.com/v1
            kind: Widget
            metadata:
              name: sample
            """, "release-ns"));

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get &&
            request.PathAndQuery == "/apis/example.com/v2/namespaces/release-ns/widgets/sample");
    }

    [Fact]
    public async Task WaitForDeletedAsync_DoesNotUseCustomObjectEndpointsForUnsupportedCoreResources()
    {
        var handler = new KubernetesApiHandler();
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        await AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForDeletedAsync("""
            apiVersion: v1
            kind: Event
            metadata:
              name: sample
            """, "release-ns"));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WaitForDeletedAsync_UsesApiVersionInItsResourceIdentity()
    {
        var handler = new KubernetesApiHandler()
            .Respond(HttpMethod.Get, "/apis/autoscaling/v2/namespaces/release-ns/horizontalpodautoscalers/scaler", HttpStatusCode.NotFound)
            .Respond(HttpMethod.Get, "/apis/autoscaling/v1/namespaces/release-ns/horizontalpodautoscalers/scaler", HttpStatusCode.NotFound);
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        await AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForDeletedAsync("""
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            ---
            apiVersion: autoscaling/v1
            kind: HorizontalPodAutoscaler
            metadata:
              name: scaler
            """, "release-ns"));

        Assert.Contains(handler.Requests, request => request.PathAndQuery == "/apis/autoscaling/v2/namespaces/release-ns/horizontalpodautoscalers/scaler");
        Assert.Contains(handler.Requests, request => request.PathAndQuery == "/apis/autoscaling/v1/namespaces/release-ns/horizontalpodautoscalers/scaler");
    }

    [Fact]
    public async Task WaitForDeletedAsync_PropagatesKubernetesApiFailureForTheAffectedResourceEndpoint()
    {
        var handler = new KubernetesApiHandler()
            .RespondAlways(HttpMethod.Get, "/api/v1/namespaces/release-ns/configmaps/settings", HttpStatusCode.InternalServerError, """
                { "kind": "Status", "apiVersion": "v1", "status": "Failure", "code": 500 }
                """);
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));

        var exception = await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForDeletedAsync("""
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
    public async Task WaitForDeletedAsync_PropagatesCancellationBeforeQueryingTheApi()
    {
        var handler = new KubernetesApiHandler();
        var waiter = new KubernetesResourceWaiter(KubernetesTestClientBuilder.Create(handler));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForDeletedAsync("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            """, "release-ns", cancellationSource.Token)));

        Assert.Empty(handler.Requests);
    }

    private static string ReadyDeployment(string name) => $$"""
        {
          "apiVersion": "apps/v1",
          "kind": "Deployment",
          "metadata": { "name": "{{name}}" },
          "spec": { "replicas": 1 },
          "status": { "readyReplicas": 1, "updatedReplicas": 1, "availableReplicas": 1 }
        }
        """;

    private static string PendingDeployment(string name) => $$"""
        {
          "apiVersion": "apps/v1",
          "kind": "Deployment",
          "metadata": { "name": "{{name}}" },
          "spec": { "replicas": 1 },
          "status": { "readyReplicas": 0, "updatedReplicas": 0, "availableReplicas": 0 }
        }
        """;
}
