using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HelmSharp.Action;
using HelmSharp.Chart;
using HelmSharp.Kube;
using HelmSharp.Release;
using k8s;

namespace HelmSharp.Tests;

/// <summary>Regression coverage for retries after a lifecycle changed resources before failing.</summary>
public sealed class FailedUpgradeBaselineTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "helmsharp-failed-upgrade-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task UpgradeInstallStreamAsync_PartialFailure_RetryRestoresOnlyResourcesAppliedByFailedAttempt(bool atomic, bool failRestoration)
    {
        var chartDirectory = Path.Combine(_tempDirectory, "retry-chart");
        Directory.CreateDirectory(Path.Combine(chartDirectory, "templates"));
        await File.WriteAllTextAsync(Path.Combine(chartDirectory, "Chart.yaml"), "apiVersion: v2\nname: retry-chart\nversion: 0.1.0\n");
        await File.WriteAllTextAsync(Path.Combine(chartDirectory, "values.yaml"), "value: A\n");
        await File.WriteAllTextAsync(Path.Combine(chartDirectory, "templates", "configmap.yaml"), """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: retry-state
            data:
              value: {{ .Values.value | quote }}
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: unattempted-state
            data:
              value: {{ .Values.value | quote }}
            """);

        var state = new ClusterState();
        var client = new HelmClient(
            new TestOptionsProvider(),
            (_, _, _, _) => Task.FromResult(new Kubernetes(
                new KubernetesClientConfiguration { Host = "https://helmsharp.test", SkipTlsVerify = true },
                new ClusterHandler(state))));

        await DrainAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
        {
            ReleaseName = "retry-baseline",
            Chart = chartDirectory
        }));
        Assert.Equal("A", ReadValue(state.ConfigMaps["retry-state"]));
        Assert.Equal("A", ReadValue(state.ConfigMaps["unattempted-state"]));

        var liveConfigMap = JsonNode.Parse(state.ConfigMaps["retry-state"])!;
        liveConfigMap["data"]!["operator-only"] = "preserve-me";
        state.ConfigMaps["retry-state"] = liveConfigMap.ToJsonString();
        var unattemptedConfigMap = JsonNode.Parse(state.ConfigMaps["unattempted-state"])!;
        unattemptedConfigMap["data"]!["operator-only"] = "preserve-unattempted";
        state.ConfigMaps["unattempted-state"] = unattemptedConfigMap.ToJsonString();

        state.ConfigMapWriteFailuresToSkip["unattempted-state"] = 0;
        if (atomic)
        {
            if (failRestoration)
                state.ConfigMapWriteFailuresToSkip["retry-state"] = 1;
            state.ReleaseSecretReplaceFailuresRemaining = 1;
        }
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => DrainAsync(client.UpgradeInstallStreamAsync(
            new HelmUpgradeInstallRequest
            {
                ReleaseName = "retry-baseline",
                Chart = chartDirectory,
                Atomic = atomic,
                ValuesContent = "value: B\n"
            })));
        Assert.Equal(atomic && !failRestoration ? "A" : "B", ReadValue(state.ConfigMaps["retry-state"]));
        Assert.Equal("A", ReadValue(state.ConfigMaps["unattempted-state"]));
        Assert.Equal("preserve-me", ReadDataValue(state.ConfigMaps["retry-state"], "operator-only"));
        Assert.Equal("preserve-unattempted", ReadDataValue(state.ConfigMaps["unattempted-state"], "operator-only"));
        if (atomic && !failRestoration)
        {
            var failedRevisionSecret = state.Secrets["sh.helm.release.v1.retry-baseline.v2"];
            var encodedRelease = JsonDocument.Parse(failedRevisionSecret).RootElement
                .GetProperty("data").GetProperty("release").GetString()!;
            var failedRecord = HelmV3ReleaseCodec.Decode(Encoding.UTF8.GetString(Convert.FromBase64String(encodedRelease)));
            Assert.True(
                (failedRecord.AppliedManifest ?? string.Empty).Contains("value: \"A\"", StringComparison.Ordinal),
                failedRecord.AppliedManifest);
        }

        await DrainAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
        {
            ReleaseName = "retry-baseline",
            Chart = chartDirectory,
            ValuesContent = "value: A\n"
        }));

        Assert.Equal("A", ReadValue(state.ConfigMaps["retry-state"]));
        Assert.Equal("A", ReadValue(state.ConfigMaps["unattempted-state"]));
        Assert.Equal("preserve-me", ReadDataValue(state.ConfigMaps["retry-state"], "operator-only"));
        Assert.Equal("preserve-unattempted", ReadDataValue(state.ConfigMaps["unattempted-state"], "operator-only"));
    }

    [Fact]
    public async Task UpgradeInstallStreamAsync_PartialInitialInstallFailure_RetryPreservesLiveOnlyFields()
    {
        var chartDirectory = Path.Combine(_tempDirectory, "partial-install-chart");
        Directory.CreateDirectory(Path.Combine(chartDirectory, "templates"));
        await File.WriteAllTextAsync(Path.Combine(chartDirectory, "Chart.yaml"), "apiVersion: v2\nname: partial-install-chart\nversion: 0.1.0\n");
        await File.WriteAllTextAsync(Path.Combine(chartDirectory, "values.yaml"), "value: A\n");
        await File.WriteAllTextAsync(Path.Combine(chartDirectory, "templates", "configmap.yaml"), """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: applied-before-failure
            data:
              value: {{ .Values.value | quote }}
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: fails-first-install
            data:
              value: {{ .Values.value | quote }}
            """);

        var state = new ClusterState();
        var client = new HelmClient(
            new TestOptionsProvider(),
            (_, _, _, _) => Task.FromResult(new Kubernetes(
                new KubernetesClientConfiguration { Host = "https://helmsharp.test", SkipTlsVerify = true },
                new ClusterHandler(state))));
        state.ConfigMapWriteFailuresToSkip["fails-first-install"] = 0;

        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => DrainAsync(client.UpgradeInstallStreamAsync(
            new HelmUpgradeInstallRequest
            {
                ReleaseName = "partial-install-retry",
                Chart = chartDirectory
            })));

        var liveConfigMap = JsonNode.Parse(state.ConfigMaps["applied-before-failure"])!;
        liveConfigMap["data"]!["operator-only"] = "preserve-me";
        state.ConfigMaps["applied-before-failure"] = liveConfigMap.ToJsonString();

        await DrainAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
        {
            ReleaseName = "partial-install-retry",
            Chart = chartDirectory,
            ValuesContent = "value: B\n"
        }));

        Assert.Equal("B", ReadValue(state.ConfigMaps["applied-before-failure"]));
        Assert.Equal("preserve-me", ReadDataValue(state.ConfigMaps["applied-before-failure"], "operator-only"));
        Assert.Equal("B", ReadValue(state.ConfigMaps["fails-first-install"]));
    }

    [Fact]
    public void GetSuccessfullyAppliedManifest_MatchesClusterScopedProgressName()
    {
        const string manifest = """
            apiVersion: example.com/v1
            kind: ClusterSetting
            metadata:
              name: shared
              namespace: ignored-by-cluster-scope
            spec:
              mode: enabled
            """;

        var applied = HelmClient.GetSuccessfullyAppliedManifest(
            manifest,
            ["Applied ClusterSetting/shared"],
            "test-ns");

        Assert.Contains("kind: ClusterSetting", applied, StringComparison.Ordinal);
        Assert.Contains("mode: enabled", applied, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertRestorationApiVersions_UsesAttemptedVersionAndPreviousDesiredFields()
    {
        const string previousManifest = """
            apiVersion: extensions/v1beta1
            kind: Ingress
            metadata:
              name: portal
            spec:
              rules:
                - host: portal.example.com
                  http:
                    paths:
                      - path: /
                        backend:
                          serviceName: portal
                          servicePort: 8080
            """;
        const string attemptedManifest = """
            apiVersion: networking.k8s.io/v1
            kind: Ingress
            metadata:
              name: portal
            spec:
              defaultBackend:
                resource:
                  apiGroup: example.com
                  kind: StorageBucket
                  name: current
            """;

        var restorationManifest = HelmClient.ConvertRestorationApiVersions(
            previousManifest,
            attemptedManifest,
            "test-ns");
        var baselineManifest = HelmClient.ComposeAtomicRestorationBaseline(restorationManifest, string.Empty, "test-ns");
        var restored = HelmYaml.DeserializeDictionary(baselineManifest);

        Assert.Equal("networking.k8s.io/v1", HelmYaml.GetString(restored, "apiVersion"));
        var spec = Assert.IsType<Dictionary<string, object?>>(restored["spec"]);
        var rules = Assert.IsType<List<object?>>(spec["rules"]);
        var rule = Assert.IsType<Dictionary<string, object?>>(rules[0]);
        var http = Assert.IsType<Dictionary<string, object?>>(rule["http"]);
        var paths = Assert.IsType<List<object?>>(http["paths"]);
        var path = Assert.IsType<Dictionary<string, object?>>(paths[0]);
        Assert.Equal("ImplementationSpecific", HelmYaml.GetString(path, "pathType"));
        var backend = Assert.IsType<Dictionary<string, object?>>(path["backend"]);
        var service = Assert.IsType<Dictionary<string, object?>>(backend["service"]);
        Assert.Equal("portal", HelmYaml.GetString(service, "name"));
        var port = Assert.IsType<Dictionary<string, object?>>(service["port"]);
        Assert.Equal(8080L, port["number"]);
        Assert.False(spec.ContainsKey("defaultBackend"));
    }

    [Fact]
    public void ConvertRestorationApiVersions_AddsRequiredSelectorForLegacyDeployment()
    {
        const string previousManifest = """
            apiVersion: extensions/v1beta1
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
        const string attemptedManifest = """
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

        var restored = HelmYaml.DeserializeDictionary(HelmClient.ConvertRestorationApiVersions(
            previousManifest,
            attemptedManifest,
            "test-ns"));
        var spec = Assert.IsType<Dictionary<string, object?>>(restored["spec"]);
        var selector = Assert.IsType<Dictionary<string, object?>>(spec["selector"]);
        var labels = Assert.IsType<Dictionary<string, object?>>(selector["matchLabels"]);

        Assert.Equal("apps/v1", HelmYaml.GetString(restored, "apiVersion"));
        Assert.Equal("portal", HelmYaml.GetString(labels, "app"));
    }

    [Fact]
    public void ConvertRestorationApiVersions_LeavesUnknownCustomResourceSchemaUntouched()
    {
        const string previousManifest = """
            apiVersion: example.com/v1alpha1
            kind: CustomWidget
            metadata:
              name: widget
            spec:
              legacySetting: keep
            """;
        const string attemptedManifest = """
            apiVersion: example.com/v1
            kind: CustomWidget
            metadata:
              name: widget
            spec:
              currentSetting: changed
            """;

        var restoredManifest = HelmClient.ConvertRestorationApiVersions(
            previousManifest,
            attemptedManifest,
            "test-ns");
        var restored = HelmYaml.DeserializeDictionary(restoredManifest);

        Assert.Equal("example.com/v1alpha1", HelmYaml.GetString(restored, "apiVersion"));
        Assert.Contains("legacySetting: keep", restoredManifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAttemptedOnlyManifestAsync_DoesNotClassifyLegacyIngressMigrationAsNew()
    {
        const string previousManifest = """
            apiVersion: extensions/v1beta1
            kind: Ingress
            metadata:
              name: portal
            """;
        const string attemptedManifest = """
            apiVersion: networking.k8s.io/v1
            kind: Ingress
            metadata:
              name: portal
            """;
        var applier = new KubernetesManifestApplier(
            KubernetesTestClientBuilder.Create(new KubernetesApiHandler()),
            "helmsharp-test");

        var attemptedOnly = await HelmClient.GetAttemptedOnlyManifestAsync(
            applier,
            previousManifest,
            attemptedManifest,
            "test-ns",
            CancellationToken.None);

        Assert.True(string.IsNullOrWhiteSpace(attemptedOnly));
    }

    [Fact]
    public async Task GetAttemptedOnlyManifestAsync_DoesNotClassifyLegacyPodSecurityPolicyMigrationAsNew()
    {
        const string previousManifest = """
            apiVersion: extensions/v1beta1
            kind: PodSecurityPolicy
            metadata:
              name: restricted
            """;
        const string attemptedManifest = """
            apiVersion: policy/v1beta1
            kind: PodSecurityPolicy
            metadata:
              name: restricted
            """;
        var applier = new KubernetesManifestApplier(
            KubernetesTestClientBuilder.Create(new KubernetesApiHandler()),
            "helmsharp-test");

        var attemptedOnly = await HelmClient.GetAttemptedOnlyManifestAsync(
            applier,
            previousManifest,
            attemptedManifest,
            "test-ns",
            CancellationToken.None);

        Assert.True(string.IsNullOrWhiteSpace(attemptedOnly));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private static string? ReadValue(string configMap)
        => JsonDocument.Parse(configMap).RootElement.GetProperty("data").GetProperty("value").GetString();

    private static string? ReadDataValue(string configMap, string key)
        => JsonDocument.Parse(configMap).RootElement.GetProperty("data").GetProperty(key).GetString();

    private static async Task DrainAsync(IAsyncEnumerable<string> output)
    {
        await foreach (var _ in output)
        {
        }
    }

    private sealed class ClusterHandler(ClusterState state) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path.EndsWith("/secrets", StringComparison.Ordinal))
                return Respond(request, HttpStatusCode.OK, $$"""
                    { "kind": "SecretList", "apiVersion": "v1", "metadata": {}, "items": [{{string.Join(',', state.Secrets.Values)}}] }
                    """);

            if (request.Method == HttpMethod.Get && path.Contains("/secrets/", StringComparison.Ordinal))
            {
                var name = path[(path.LastIndexOf('/') + 1)..];
                return state.Secrets.TryGetValue(name, out var secret)
                    ? Respond(request, HttpStatusCode.OK, secret)
                    : Respond(request, HttpStatusCode.NotFound, "{ \"code\": 404 }");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/secrets", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var secretName = JsonDocument.Parse(body).RootElement.GetProperty("metadata").GetProperty("name").GetString()!;
                state.Secrets[secretName] = body;
                return Respond(request, HttpStatusCode.Created, body);
            }

            if (request.Method == HttpMethod.Put && path.Contains("/secrets/", StringComparison.Ordinal))
            {
                if (state.ReleaseSecretReplaceFailuresRemaining > 0)
                {
                    state.ReleaseSecretReplaceFailuresRemaining--;
                    return Respond(request, HttpStatusCode.InternalServerError, "{ \"code\": 500 }");
                }

                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var secretName = JsonDocument.Parse(body).RootElement.GetProperty("metadata").GetProperty("name").GetString()!;
                state.Secrets[secretName] = body;
                return Respond(request, HttpStatusCode.OK, body);
            }

            if (request.Method == HttpMethod.Get && path.Contains("/configmaps/", StringComparison.Ordinal))
            {
                var name = path[(path.LastIndexOf('/') + 1)..];
                return state.ConfigMaps.TryGetValue(name, out var liveConfigMap)
                    ? Respond(request, HttpStatusCode.OK, liveConfigMap)
                    : Respond(request, HttpStatusCode.NotFound, "{ \"code\": 404 }");
            }

            if ((request.Method == HttpMethod.Post || request.Method == HttpMethod.Put) &&
                path.Contains("/configmaps", StringComparison.Ordinal))
            {
                var configMap = await request.Content!.ReadAsStringAsync(cancellationToken);
                var name = JsonDocument.Parse(configMap).RootElement.GetProperty("metadata").GetProperty("name").GetString()!;
                if (state.ConfigMapWriteFailuresToSkip.TryGetValue(name, out var successfulWritesBeforeFailure))
                {
                    if (successfulWritesBeforeFailure == 0)
                    {
                        state.ConfigMapWriteFailuresToSkip.Remove(name);
                        return Respond(request, HttpStatusCode.InternalServerError, "{ \"code\": 500 }");
                    }

                    state.ConfigMapWriteFailuresToSkip[name] = successfulWritesBeforeFailure - 1;
                }
                state.ConfigMaps[name] = configMap;
                return Respond(request, request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK, configMap);
            }

            return Respond(request, HttpStatusCode.OK, "{}");
        }

        private static HttpResponseMessage Respond(HttpRequestMessage request, HttpStatusCode statusCode, string body)
            => new(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

    private sealed class ClusterState
    {
        internal Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> ConfigMaps { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> ConfigMapWriteFailuresToSkip { get; } = new(StringComparer.Ordinal);
        internal int ReleaseSecretReplaceFailuresRemaining { get; set; }
    }

    private sealed class TestOptionsProvider : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HelmExecutionOptions { DefaultNamespace = "test-ns" });
    }

}
