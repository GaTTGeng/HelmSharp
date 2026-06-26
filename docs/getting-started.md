# Getting Started

This guide shows the fastest path from an empty .NET application to rendering a chart and running a dry-run release workflow with HelmSharp.

HelmSharp does not require the `helm` executable at runtime. The SDK loads charts, merges values, renders templates, and can apply release manifests through managed .NET APIs.

## Prerequisites

- .NET 8 or later for application development.
- A Kubernetes cluster and kubeconfig only when you apply releases instead of using dry runs.
- A Helm chart directory, `.tgz`, or `.tar.gz` archive.

The repository itself targets `net8.0`, `net9.0`, and `net10.0`, so contributors need all three SDKs to build the full solution.

## Install the High-Level Client

For most applications, install `HelmSharp.Action`:

```powershell
dotnet add package HelmSharp.Action
```

This package brings in the chart, template, release, and Kubernetes layers used by the high-level client.

## Render a Chart

Use `TemplateAsync` when you want `helm template`-style output without touching a cluster:

```csharp
using HelmSharp.Action;

var client = new HelmClient(new StaticHelmOptionsProvider());

var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart",
    ValuesFiles = ["values.production.yaml"],
    SetValues = new Dictionary<string, string>
    {
        ["image.repository"] = "nginx",
        ["image.tag"] = "1.25",
        ["replicaCount"] = "2"
    }
});

if (result.ExitCode != 0)
{
    Console.Error.WriteLine(result.StandardError);
    return;
}

Console.WriteLine(result.StandardOutput);

sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
{
    public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new HelmExecutionOptions
        {
            DefaultNamespace = "default",
            FieldManager = "helmsharp"
        });
}
```

`SetValues`, `SetStringValues`, `SetJsonValues`, `SetFileValues`, `ValuesFile`, `ValuesFiles`, and `ValuesContent` mirror the common Helm values override shapes.

## Render with Lower-Level APIs

Use `HelmSharp.Chart` and `HelmSharp.Engine` directly when you need chart rendering but not release operations:

```csharp
using HelmSharp.Chart;
using HelmSharp.Engine;

var chart = await HelmChartLoader.LoadAsync(@"C:\charts\my-chart", CancellationToken.None);

var values = await HelmValues.BuildAsync(
    chart: chart,
    valuesFiles: null,
    valuesContent: null,
    setValues: new Dictionary<string, string>
    {
        ["image.tag"] = "1.25"
    },
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: CancellationToken.None);

var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);

Console.WriteLine(renderer.Render());
Console.WriteLine(renderer.RenderNotes());
```

## Dry-Run an Install or Upgrade

`UpgradeInstallAsync` is the main entry point for install and upgrade workflows. Set `DryRun = true` to render the release without applying resources:

```csharp
var result = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart",
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true
});

Console.WriteLine(result.StandardOutput);
```

Remove `DryRun = true` when you are ready to submit resources to the configured Kubernetes cluster.

## Run the Examples

The repository includes console examples:

```powershell
dotnet run --project examples/RenderChart -- examples/sample-chart
dotnet run --project examples/InstallRelease -- examples/sample-chart demo
dotnet run --project examples/InstallRelease -- examples/sample-chart demo --apply
```

The install example defaults to dry-run mode. Passing `--apply` submits resources to the current Kubernetes context.

## Next Steps

- Review the [API overview](api-overview.md) to choose the right package layer.
- Check [Helm compatibility](helm-compatibility.md) before relying on advanced Helm behavior.
- Follow the [roadmap](roadmap.md) for current implementation priorities.
