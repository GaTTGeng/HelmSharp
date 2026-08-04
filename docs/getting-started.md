# Quickstart: render a chart

This quickstart renders a local Helm chart from a .NET console application. It does not require a cluster or the Helm CLI.

## Create the project

```powershell
dotnet new console --name RenderChart
cd RenderChart
dotnet add package HelmSharp.Chart --version 1.3.1
dotnet add package HelmSharp.Engine --version 1.3.1
```

## Render a chart directory

Replace `Program.cs` with the following code. Run it with the path to a directory containing `Chart.yaml`.

```csharp
using HelmSharp.Chart;
using HelmSharp.Engine;

var chartPath = args.Length == 1
    ? args[0]
    : throw new ArgumentException("Pass the path to a Helm chart.");

var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: null,
    valuesContent: null,
    setValues: null,
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: CancellationToken.None);

var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);

Console.WriteLine(renderer.Render());
```

```powershell
dotnet run -- ../charts/my-chart
```

The manifest is written to standard output. Redirect it to a file, return it from an HTTP endpoint, send it to a policy engine, or commit it to a GitOps repository. `renderer.RenderNotes()` is available when the chart has `NOTES.txt` and your application needs to show it separately.

## Where to go from here

| You want to… | Read |
| --- | --- |
| Supply `values.yaml`, `--set`, JSON, or string-preserving overrides | [Values and overrides](guide/values.md) |
| Render conditional templates for a known Kubernetes version | [Render for a target cluster](guide/template-rendering.md) |
| Build a real preview endpoint | [Render-preview endpoint](examples/render-preview-api.md) |
| Apply a chart and save release history | [Install and upgrade releases](guide/release-workflows.md) |
| Choose another package | [Choose a package](api-overview.md) |

Before relying on a chart feature in production, see the [compatibility contract](helm-compatibility.md) and [template-function matrix](template-function-compatibility.md) for supported behavior and known limits.
