using HelmSharp.Action;
using HelmSharp.Chart;
using HelmSharp.Engine;
using HelmSharp.Kube;
using HelmSharp.Repo;

namespace HelmSharp.DocsSnippets;

public static class Snippets
{
    // #region render-first-chart
    public static async Task<RenderedChartPreview> RenderFirstChartAsync(
        string chartPath,
        string valuesPath,
        CancellationToken cancellationToken)
    {
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);

        var values = await HelmValues.BuildAsync(
            chart: chart,
            valuesFiles: [valuesPath],
            valuesContent: null,
            setValues: new Dictionary<string, string>
            {
                ["image.tag"] = "1.25.3",
                ["replicaCount"] = "2"
            },
            setFileValues: null,
            setStringValues: null,
            setJsonValues: null,
            cancellationToken: cancellationToken);

        var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);

        return new RenderedChartPreview(
            Manifest: renderer.Render(),
            Notes: renderer.RenderNotes());
    }
    // #endregion render-first-chart

    // #region values-precedence
    public static async Task<Dictionary<string, object?>> BuildProductionValuesAsync(
        HelmChart chart,
        string baseValuesPath,
        string productionValuesPath,
        string licenseFilePath,
        CancellationToken cancellationToken)
    {
        var licenseContent = await File.ReadAllTextAsync(licenseFilePath, cancellationToken);

        return await HelmValues.BuildAsync(
            chart: chart,
            valuesFiles: [baseValuesPath, productionValuesPath],
            valuesContent: """
                global:
                  environment: production
                """,
            setValues: new Dictionary<string, string>
            {
                ["replicaCount"] = "3"
            },
            setFileValues: new Dictionary<string, string>
            {
                ["license.text"] = licenseContent
            },
            setStringValues: new Dictionary<string, string>
            {
                ["image.tag"] = "001"
            },
            setJsonValues: new Dictionary<string, string>
            {
                ["service.ports"] = """[{"name":"http","port":80}]"""
            },
            cancellationToken: cancellationToken);
    }
    // #endregion values-precedence

    // #region template-with-capabilities
    public static async Task<string> RenderForTargetClusterAsync(
        string chartPath,
        CancellationToken cancellationToken)
    {
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
        var values = await HelmValues.BuildAsync(
            chart,
            valuesFiles: null,
            valuesContent: null,
            setValues: null,
            setFileValues: null,
            setStringValues: null,
            setJsonValues: null,
            cancellationToken: cancellationToken);

        var renderer = new HelmTemplateRenderer(
            chart,
            releaseName: "preview",
            releaseNamespace: "platform",
            values: values,
            kubeVersion: "1.30.0",
            apiVersions:
            [
                "monitoring.coreos.com/v1",
                "policy/v1"
            ],
            isUpgrade: false);

        return renderer.Render();
    }
    // #endregion template-with-capabilities

    // #region dry-run-release
    public static async Task<CommandResult> DryRunReleaseAsync(
        string chartPath,
        IHelmOptionsProvider optionsProvider,
        CancellationToken cancellationToken)
    {
        var client = new HelmClient(optionsProvider);

        return await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
        {
            ReleaseName = "demo",
            Namespace = "default",
            Chart = chartPath,
            ValuesFiles = ["values.production.yaml"],
            CreateNamespace = true,
            Wait = true,
            TimeoutSeconds = 300,
            DryRun = true
        }, cancellationToken);
    }
    // #endregion dry-run-release

    // #region apply-release
    public static async Task<CommandResult> ApplyReleaseAfterApprovalAsync(
        string chartPath,
        IHelmOptionsProvider optionsProvider,
        CancellationToken cancellationToken)
    {
        var client = new HelmClient(optionsProvider);

        return await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
        {
            ReleaseName = "demo",
            Namespace = "default",
            Chart = chartPath,
            ValuesFiles = ["values.production.yaml"],
            CreateNamespace = true,
            Wait = true,
            WaitForJobs = true,
            TimeoutSeconds = 300,
            DryRun = false
        }, cancellationToken);
    }
    // #endregion apply-release

    // #region manifest-applier
    public static async Task<IReadOnlyList<string>> ApplyRenderedManifestAsync(
        k8s.Kubernetes kubernetes,
        string manifest,
        string @namespace,
        CancellationToken cancellationToken)
    {
        var applier = new KubernetesManifestApplier(kubernetes, fieldManager: "helmsharp");
        var applied = new List<string>();

        await foreach (var resource in applier.ApplyAsync(manifest, @namespace, cancellationToken))
        {
            applied.Add(resource);
        }

        return applied;
    }
    // #endregion manifest-applier

    // #region repository
    public static async Task<IReadOnlyList<HelmChartSearchResult>> SearchRepositoryAsync(
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        using var repository = new HelmChartRepository(cacheDirectory);

        await repository.AddRepositoryAsync(
            name: "bitnami",
            url: "https://charts.bitnami.com/bitnami",
            cancellationToken: cancellationToken);

        return await repository.SearchRepoAsync(
            repoUrl: "https://charts.bitnami.com/bitnami",
            keyword: "nginx",
            cancellationToken: cancellationToken);
    }
    // #endregion repository
}

// #region options-provider
public sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
{
    public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new HelmExecutionOptions
        {
            DefaultNamespace = "default",
            FieldManager = "helmsharp",
            TimeoutSeconds = 300,
            KubeVersion = "1.30.0",
            ApiVersions =
            [
                "apps/v1",
                "batch/v1",
                "policy/v1"
            ]
        });
}
// #endregion options-provider

public sealed record RenderedChartPreview(string Manifest, string Notes);
