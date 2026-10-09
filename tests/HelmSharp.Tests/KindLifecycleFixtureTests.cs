using HelmSharp.Action;

namespace HelmSharp.Tests;

/// <summary>
/// Cluster-independent checks that the kind-lifecycle fixture chart stays renderable.
/// The kind lane depends on this chart; keep it valid in the fast suite as well.
/// </summary>
public class KindLifecycleFixtureTests
{
    private sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HelmExecutionOptions { DefaultNamespace = "fixture-ns" });
    }

    [Fact]
    public async Task DryRun_RendersWorkloadHookCrdAndCustomResource()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        var lines = new List<string>();
        await foreach (var line in client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
        {
            ReleaseName = "fixture",
            Namespace = "fixture-ns",
            Chart = TestFixtures.ChartPath("kind-lifecycle"),
            DryRun = true,
            SetValues = new Dictionary<string, string> { ["message"] = "hello" }
        }))
        {
            lines.Add(line);
        }

        var manifest = string.Join('\n', lines);
        Assert.Contains("kind: ConfigMap", manifest, StringComparison.Ordinal);
        Assert.Contains("kind: Deployment", manifest, StringComparison.Ordinal);
        Assert.Contains("kind: Service", manifest, StringComparison.Ordinal);
        Assert.Contains("kind: CustomResourceDefinition", manifest, StringComparison.Ordinal);
        Assert.Contains("kind: Widget", manifest, StringComparison.Ordinal);
        Assert.Contains("kind: Job", manifest, StringComparison.Ordinal);
        Assert.Contains("helm.sh/hook", manifest, StringComparison.Ordinal);
        Assert.Contains("message: \"hello\"", manifest, StringComparison.Ordinal);
        Assert.Contains("Release fixture dry run complete", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRun_FailUpgradeHook_UsesFailureCommand()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        var lines = new List<string>();
        await foreach (var line in client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
        {
            ReleaseName = "fixture",
            Namespace = "fixture-ns",
            Chart = TestFixtures.ChartPath("kind-lifecycle"),
            DryRun = true,
            SetValues = new Dictionary<string, string> { ["failUpgrade"] = "true" }
        }))
        {
            lines.Add(line);
        }

        var manifest = string.Join('\n', lines);
        Assert.Contains("controlled upgrade failure", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRun_UnreadyImage_UsesPullFailingReference()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        var lines = new List<string>();
        await foreach (var line in client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
        {
            ReleaseName = "fixture",
            Namespace = "fixture-ns",
            Chart = TestFixtures.ChartPath("kind-lifecycle"),
            DryRun = true,
            SetValues = new Dictionary<string, string> { ["useUnreadyImage"] = "true" }
        }))
        {
            lines.Add(line);
        }

        var manifest = string.Join('\n', lines);
        Assert.Contains("invalid.example/never-pulls:1", manifest, StringComparison.Ordinal);
    }
}
