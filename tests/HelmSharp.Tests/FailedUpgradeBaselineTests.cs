using System.Net;
using System.Text;
using System.Text.Json;
using HelmSharp.Action;
using HelmSharp.Kube;
using k8s;

namespace HelmSharp.Tests;

/// <summary>Regression coverage for retries after an upgrade changed resources before failing.</summary>
public sealed class FailedUpgradeBaselineTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "helmsharp-failed-upgrade-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeInstallStreamAsync_PartialFailure_RetryRestoresOnlyResourcesAppliedByFailedAttempt(bool atomic)
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

        state.ConfigMapWriteFailuresToSkip["unattempted-state"] = 0;
        if (atomic)
            state.ConfigMapWriteFailuresToSkip["retry-state"] = 1;
        await Assert.ThrowsAsync<KubernetesResourceOperationException>(() => DrainAsync(client.UpgradeInstallStreamAsync(
            new HelmUpgradeInstallRequest
            {
                ReleaseName = "retry-baseline",
                Chart = chartDirectory,
                Atomic = atomic,
                ValuesContent = "value: B\n"
            })));
        Assert.Equal("B", ReadValue(state.ConfigMaps["retry-state"]));
        Assert.Equal("A", ReadValue(state.ConfigMaps["unattempted-state"]));

        await DrainAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
        {
            ReleaseName = "retry-baseline",
            Chart = chartDirectory,
            ValuesContent = "value: A\n"
        }));

        Assert.Equal("A", ReadValue(state.ConfigMaps["retry-state"]));
        Assert.Equal("A", ReadValue(state.ConfigMaps["unattempted-state"]));
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

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private static string? ReadValue(string configMap)
        => JsonDocument.Parse(configMap).RootElement.GetProperty("data").GetProperty("value").GetString();

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
    }

    private sealed class TestOptionsProvider : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HelmExecutionOptions { DefaultNamespace = "test-ns" });
    }

}
