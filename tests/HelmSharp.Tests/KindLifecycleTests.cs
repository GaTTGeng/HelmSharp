using System.Text.Json;
using HelmSharp.Action;
using HelmSharp.Release;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace HelmSharp.Tests;

/// <summary>
/// Kind-backed Kubernetes lifecycle tests. Each test owns a uniquely named release and
/// namespace so the suite is safe to re-run against a shared disposable cluster. Failures
/// are designed to leave actionable state for the workflow diagnostic collector.
/// </summary>
[Trait("Category", "Kind")]
public class KindLifecycleTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static string NewName(string prefix)
    {
        // DNS-1123: lowercase alphanumerics and '-', must start/end alphanumeric.
        var suffix = Guid.NewGuid().ToString("N");
        return $"{prefix}-{suffix}"[..32];
    }

    private static HelmClient CreateClient(string defaultNamespace)
        => new(new StaticHelmOptionsProvider(new HelmExecutionOptions
        {
            DefaultNamespace = defaultNamespace,
            TimeoutSeconds = 180,
            FieldManager = "helmsharp-kind-tests"
        }));

    private static Task<Kubernetes> CreateClusterClientAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig()));
    }

    private static string ChartPath => TestFixtures.ChartPath("kind-lifecycle");

    private static async Task<string> ReadStdoutAsync(HelmClient client, Func<CancellationToken, Task<CommandResult>> action, CancellationToken cancellationToken)
    {
        var result = await action(cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command failed ({result.ExitCode}).\nSTDOUT:\n{result.StandardOutput}\nSTDERR:\n{result.StandardError}");
        return result.StandardOutput;
    }

    private static async Task DrainStreamAsync(IAsyncEnumerable<string> stream, CancellationToken cancellationToken)
    {
        await foreach (var _ in stream.WithCancellation(cancellationToken))
        {
        }
    }

    private static async Task<IReadOnlyList<string>> CollectStreamAsync(IAsyncEnumerable<string> stream, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        await foreach (var line in stream.WithCancellation(cancellationToken))
            lines.Add(line);
        return lines;
    }

    private static async Task<T> WaitForAsync<T>(
        string description,
        Func<CancellationToken, Task<T>> probe,
        Func<T, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = await probe(cancellationToken);
                if (value is not null && predicate(value))
                    return value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"Timed out after {timeout.TotalSeconds:0}s waiting for {description}.",
            lastError);
    }

    [KindFact]
    public async Task Install_StatusHistory_Upgrade_Rollback_Uninstall_CoversLifecycle()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var release = NewName("kl-life");
        var ns = release;
        var client = CreateClient(ns);
        using var k8s = await CreateClusterClientAsync(cts.Token);

        try
        {
            // --- Install ---
            await DrainStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
            {
                ReleaseName = release,
                Namespace = ns,
                Chart = ChartPath,
                CreateNamespace = true,
                Wait = true,
                WaitForJobs = true,
                TimeoutSeconds = 180,
                SetValues = new Dictionary<string, string>
                {
                    ["message"] = "hello"
                }
            }, cts.Token), cts.Token);

            var status = await ReadStdoutAsync(client, ct => client.StatusAsync(release, ns, ct), cts.Token);
            Assert.Contains("deployed", status, StringComparison.OrdinalIgnoreCase);

            var history = await ReadStdoutAsync(client, ct => client.HistoryAsync(release, ns, ct), cts.Token);
            Assert.Contains("1", history);

            // Namespace-scoped identity: resources live in the release namespace.
            var config = await k8s.CoreV1.ReadNamespacedConfigMapAsync($"{release}-config", ns, cancellationToken: cts.Token);
            Assert.Equal("hello", config.Data["message"]);
            var deployment = await k8s.AppsV1.ReadNamespacedDeploymentAsync($"{release}-web", ns, cancellationToken: cts.Token);
            Assert.Equal(1, deployment.Spec.Replicas);
            var service = await k8s.CoreV1.ReadNamespacedServiceAsync($"{release}-web", ns, cancellationToken: cts.Token);
            Assert.Equal(ns, service.Metadata.NamespaceProperty);

            // Hook Job completed and was cleaned up by before-hook-creation / hook-succeeded path.
            await Assert.ThrowsAsync<HttpOperationException>(async () =>
                await k8s.BatchV1.ReadNamespacedJobAsync($"{release}-setup", ns, cancellationToken: cts.Token));

            // --- Upgrade: chart-owned change takes effect ---
            await DrainStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
            {
                ReleaseName = release,
                Namespace = ns,
                Chart = ChartPath,
                CreateNamespace = true,
                Wait = true,
                WaitForJobs = true,
                TimeoutSeconds = 180,
                SetValues = new Dictionary<string, string>
                {
                    ["message"] = "upgraded",
                    ["replicaCount"] = "1"
                }
            }, cts.Token), cts.Token);

            var upgraded = await k8s.CoreV1.ReadNamespacedConfigMapAsync($"{release}-config", ns, cancellationToken: cts.Token);
            Assert.Equal("upgraded", upgraded.Data["message"]);

            history = await ReadStdoutAsync(client, ct => client.HistoryAsync(release, ns, ct), cts.Token);
            Assert.Contains("2", history);

            // --- Rollback to revision 1 ---
            await ReadStdoutAsync(client, ct => client.RollbackAsync(release, 1, ns, ct), cts.Token);
            var rolledBack = await k8s.CoreV1.ReadNamespacedConfigMapAsync($"{release}-config", ns, cancellationToken: cts.Token);
            Assert.Equal("hello", rolledBack.Data["message"]);

            // --- Uninstall (purge) ---
            await ReadStdoutAsync(
                client,
                ct => client.UninstallAsync(new HelmUninstallRequest
                {
                    ReleaseName = release,
                    Namespace = ns,
                    KeepHistory = false,
                    Wait = true,
                    TimeoutSeconds = 120
                }, ct),
                cts.Token);

            await Assert.ThrowsAsync<HttpOperationException>(async () =>
                await k8s.CoreV1.ReadNamespacedConfigMapAsync($"{release}-config", ns, cancellationToken: cts.Token));
            await Assert.ThrowsAsync<HttpOperationException>(async () =>
                await k8s.AppsV1.ReadNamespacedDeploymentAsync($"{release}-web", ns, cancellationToken: cts.Token));

            // Purge removes release Secrets.
            var secrets = await k8s.CoreV1.ListNamespacedSecretAsync(ns, labelSelector: "owner=helm", cancellationToken: cts.Token);
            Assert.Empty(secrets.Items);
        }
        finally
        {
            await CleanupAsync(release, ns, cts.Token);
        }
    }

    [KindFact]
    public async Task Install_CreatesNamespace_AndScopedResourceIdentity()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var release = NewName("kl-ns");
        var ns = release;
        var client = CreateClient(ns);
        using var k8s = await CreateClusterClientAsync(cts.Token);

        try
        {
            Assert.False(await NamespaceExistsAsync(k8s, ns, cts.Token));

            await DrainStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
            {
                ReleaseName = release,
                Namespace = ns,
                Chart = ChartPath,
                CreateNamespace = true,
                Wait = true,
                TimeoutSeconds = 180
            }, cts.Token), cts.Token);

            Assert.True(await NamespaceExistsAsync(k8s, ns, cts.Token));
            var config = await k8s.CoreV1.ReadNamespacedConfigMapAsync($"{release}-config", ns, cancellationToken: cts.Token);
            Assert.Equal(ns, config.Metadata.NamespaceProperty);

            // Cluster-scoped CRD carries no namespace.
            var crdName = $"widgets.{release}.example.test";
            var crd = await k8s.ApiextensionsV1.ReadCustomResourceDefinitionAsync(crdName, cancellationToken: cts.Token);
            Assert.True(string.IsNullOrEmpty(crd.Metadata.NamespaceProperty));
            Assert.Equal($"{release}.example.test", crd.Spec.Group);

            // Custom resource is applied through cluster discovery of the CRD kind.
            var widget = await k8s.CustomObjects.GetNamespacedCustomObjectAsync(
                $"{release}.example.test",
                "v1",
                ns,
                "widgets",
                $"{release}-widget",
                cancellationToken: cts.Token);
            Assert.NotNull(widget);
            var widgetJson = JsonSerializer.Serialize(widget);
            Assert.Contains($"{release}-widget", widgetJson, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(release, ns, cts.Token);
        }
    }

    [KindFact]
    public async Task Upgrade_UpdatesChartOwnedFields_AndDiscoversCustomResources()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var release = NewName("kl-upd");
        var ns = release;
        var client = CreateClient(ns);
        using var k8s = await CreateClusterClientAsync(cts.Token);

        try
        {
            await DrainStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
            {
                ReleaseName = release,
                Namespace = ns,
                Chart = ChartPath,
                CreateNamespace = true,
                Wait = true,
                TimeoutSeconds = 180,
                SetValues = new Dictionary<string, string>
                {
                    ["message"] = "v1",
                    ["customResource.size"] = "small"
                }
            }, cts.Token), cts.Token);

            // Upgrade chart-owned values and custom-resource spec.
            await DrainStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
            {
                ReleaseName = release,
                Namespace = ns,
                Chart = ChartPath,
                CreateNamespace = true,
                Wait = true,
                TimeoutSeconds = 180,
                SetValues = new Dictionary<string, string>
                {
                    ["message"] = "v2",
                    ["customResource.size"] = "large"
                }
            }, cts.Token), cts.Token);

            var config = await k8s.CoreV1.ReadNamespacedConfigMapAsync($"{release}-config", ns, cancellationToken: cts.Token);
            Assert.Equal("v2", config.Data["message"]);

            await WaitForAsync(
                "custom resource spec update",
                async ct =>
                {
                    var obj = await k8s.CustomObjects.GetNamespacedCustomObjectAsync(
                        $"{release}.example.test",
                        "v1",
                        ns,
                        "widgets",
                        $"{release}-widget",
                        cancellationToken: ct);
                    return JsonSerializer.Serialize(obj);
                },
                json => json.Contains("\"size\":\"large\"", StringComparison.Ordinal)
                    || json.Contains("\"size\": \"large\"", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                cts.Token);
        }
        finally
        {
            await CleanupAsync(release, ns, cts.Token);
        }
    }

    [KindFact]
    public async Task DeploymentReadiness_And_HookJobCompletion_AreAsserted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var release = NewName("kl-ready");
        var ns = release;
        var client = CreateClient(ns);
        using var k8s = await CreateClusterClientAsync(cts.Token);

        try
        {
            var lines = await CollectStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
            {
                ReleaseName = release,
                Namespace = ns,
                Chart = ChartPath,
                CreateNamespace = true,
                Wait = true,
                WaitForJobs = true,
                TimeoutSeconds = 180
            }, cts.Token), cts.Token);

            Assert.Contains(lines, line => line.Contains("Waiting for resources to be ready", StringComparison.Ordinal));

            var deployment = await WaitForAsync(
                "Deployment ready replicas",
                async ct => await k8s.AppsV1.ReadNamespacedDeploymentAsync($"{release}-web", ns, cancellationToken: ct),
                d => d.Status is { ReadyReplicas: >= 1, ObservedGeneration: >= 1 },
                TimeSpan.FromSeconds(60),
                cts.Token);
            Assert.True(deployment.Status.ReadyReplicas >= 1);
        }
        finally
        {
            await CleanupAsync(release, ns, cts.Token);
        }
    }

    [KindFact]
    public async Task HookJobFailure_ProducesFailedRevision_AndRollbackRestores()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var release = NewName("kl-hookfail");
        var ns = release;
        var client = CreateClient(ns);
        using var k8s = await CreateClusterClientAsync(cts.Token);

        try
        {
            // Revision 1 succeeds.
            await DrainStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
            {
                ReleaseName = release,
                Namespace = ns,
                Chart = ChartPath,
                CreateNamespace = true,
                Wait = true,
                WaitForJobs = true,
                TimeoutSeconds = 180,
                SetValues = new Dictionary<string, string> { ["message"] = "stable" }
            }, cts.Token), cts.Token);

            // Revision 2 fails in the pre-upgrade hook (controlled failure).
            var failed = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await DrainStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
                {
                    ReleaseName = release,
                    Namespace = ns,
                    Chart = ChartPath,
                    CreateNamespace = true,
                    Wait = true,
                    WaitForJobs = true,
                    TimeoutSeconds = 180,
                    SetValues = new Dictionary<string, string>
                    {
                        ["message"] = "broken",
                        ["failUpgrade"] = "true"
                    }
                }, cts.Token), cts.Token));
            Assert.NotNull(failed);

            var status = await ReadStdoutAsync(client, ct => client.StatusAsync(release, ns, ct), cts.Token);
            Assert.Contains("failed", status, StringComparison.OrdinalIgnoreCase);

            // Rollback to the last good revision.
            await ReadStdoutAsync(client, ct => client.RollbackAsync(release, 1, ns, ct), cts.Token);
            var config = await k8s.CoreV1.ReadNamespacedConfigMapAsync($"{release}-config", ns, cancellationToken: cts.Token);
            Assert.Equal("stable", config.Data["message"]);

            status = await ReadStdoutAsync(client, ct => client.StatusAsync(release, ns, ct), cts.Token);
            Assert.Contains("deployed", status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAsync(release, ns, cts.Token);
        }
    }

    [KindFact]
    public async Task RetainedUninstall_KeepsHistory_AndPurgeRemovesIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var release = NewName("kl-retain");
        var ns = release;
        var client = CreateClient(ns);
        using var k8s = await CreateClusterClientAsync(cts.Token);

        try
        {
            await DrainStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
            {
                ReleaseName = release,
                Namespace = ns,
                Chart = ChartPath,
                CreateNamespace = true,
                Wait = true,
                TimeoutSeconds = 180
            }, cts.Token), cts.Token);

            await ReadStdoutAsync(
                client,
                ct => client.UninstallAsync(new HelmUninstallRequest
                {
                    ReleaseName = release,
                    Namespace = ns,
                    KeepHistory = true,
                    Wait = true,
                    TimeoutSeconds = 120
                }, ct),
                cts.Token);

            // Resources are gone but release Secrets remain (retained uninstall).
            await Assert.ThrowsAsync<HttpOperationException>(async () =>
                await k8s.CoreV1.ReadNamespacedConfigMapAsync($"{release}-config", ns, cancellationToken: cts.Token));
            var retained = await k8s.CoreV1.ListNamespacedSecretAsync(ns, labelSelector: "owner=helm,name=" + release, cancellationToken: cts.Token);
            Assert.NotEmpty(retained.Items);

            // Purge removes retained history.
            await ReadStdoutAsync(
                client,
                ct => client.UninstallAsync(new HelmUninstallRequest
                {
                    ReleaseName = release,
                    Namespace = ns,
                    KeepHistory = false,
                    Wait = true,
                    TimeoutSeconds = 120
                }, ct),
                cts.Token);
            var purged = await k8s.CoreV1.ListNamespacedSecretAsync(ns, labelSelector: "owner=helm,name=" + release, cancellationToken: cts.Token);
            Assert.Empty(purged.Items);
        }
        finally
        {
            await CleanupAsync(release, ns, cts.Token);
        }
    }

    [KindFact]
    public async Task WaitTimeout_PersistsFailedReleaseState()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var release = NewName("kl-timeout");
        var ns = release;
        var client = CreateClient(ns);
        using var k8s = await CreateClusterClientAsync(cts.Token);

        try
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await DrainStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
                {
                    ReleaseName = release,
                    Namespace = ns,
                    Chart = ChartPath,
                    CreateNamespace = true,
                    Wait = true,
                    // Short timeout plus a pull-failing image: readiness cannot complete.
                    TimeoutSeconds = 8,
                    SetValues = new Dictionary<string, string>
                    {
                        ["useUnreadyImage"] = "true"
                    }
                }, cts.Token), cts.Token));
            Assert.NotNull(ex);

            // Documented post-operation state: the attempted revision is recorded as failed.
            var store = new HelmReleaseStore(k8s);
            var history = await store.HistoryAsync(release, ns, cts.Token);
            var latest = Assert.Single(history);
            Assert.Equal("failed", latest.Status);
        }
        finally
        {
            await CleanupAsync(release, ns, cts.Token);
        }
    }

    [KindFact]
    public async Task HelmV3ReleaseSecrets_UseExpectedStorageContract()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var release = NewName("kl-secret");
        var ns = release;
        var client = CreateClient(ns);
        using var k8s = await CreateClusterClientAsync(cts.Token);

        try
        {
            await DrainStreamAsync(client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
            {
                ReleaseName = release,
                Namespace = ns,
                Chart = ChartPath,
                CreateNamespace = true,
                Wait = true,
                TimeoutSeconds = 180
            }, cts.Token), cts.Token);

            var secretName = $"sh.helm.release.v1.{release}.v1";
            var secret = await k8s.CoreV1.ReadNamespacedSecretAsync(secretName, ns, cancellationToken: cts.Token);

            // Helm v3 storage contract: name, type, labels, and payload key.
            Assert.Equal("helm.sh/release.v1", secret.Type);
            Assert.Equal("helm", secret.Metadata.Labels["owner"]);
            Assert.Equal(release, secret.Metadata.Labels["name"]);
            Assert.Equal("deployed", secret.Metadata.Labels["status"]);
            Assert.True(secret.Data.ContainsKey("release"));

            // HelmSharp can decode its own Secret payload back into a release record.
            var store = new HelmReleaseStore(k8s);
            var history = await store.HistoryAsync(release, ns, cts.Token);
            var record = Assert.Single(history);
            Assert.Equal(release, record.Name);
            Assert.Equal(ns, record.Namespace);
            Assert.Equal(1, record.Revision);
            Assert.Equal("deployed", record.Status);
            Assert.False(string.IsNullOrWhiteSpace(record.Manifest));
        }
        finally
        {
            await CleanupAsync(release, ns, cts.Token);
        }
    }

    private static async Task<bool> NamespaceExistsAsync(Kubernetes k8s, string ns, CancellationToken cancellationToken)
    {
        try
        {
            await k8s.CoreV1.ReadNamespaceAsync(ns, cancellationToken: cancellationToken);
            return true;
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort cleanup so a failed assertion does not leak releases into later tests.
    /// Diagnostics for the original failure remain in the test output.
    /// </summary>
    private static async Task CleanupAsync(string release, string ns, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var client = CreateClient(ns);
            await client.UninstallAsync(new HelmUninstallRequest
            {
                ReleaseName = release,
                Namespace = ns,
                KeepHistory = false,
                Wait = false,
                TimeoutSeconds = 45,
                DisableHooks = true
            }, cts.Token);
        }
        catch
        {
            // Cleanup is best effort; the kind cluster is disposable and workflow
            // diagnostics capture leftover objects when a test fails.
        }
    }

    private sealed class StaticHelmOptionsProvider(HelmExecutionOptions options) : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(options);
    }
}
