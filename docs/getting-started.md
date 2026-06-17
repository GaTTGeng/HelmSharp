# Getting Started

HelmSharp provides managed .NET APIs for Helm-style chart rendering and release workflows without invoking the `helm` executable.

## Install

```powershell
dotnet add package HelmSharp.Action
```

Use lower-level packages when you only need part of the stack:

```powershell
dotnet add package HelmSharp.Chart
dotnet add package HelmSharp.Engine
dotnet add package HelmSharp.Repo
```

## Render a chart

```csharp
using HelmSharp.Chart;
using HelmSharp.Engine;

var chart = await HelmChartLoader.LoadAsync("path/to/chart", CancellationToken.None);
var values = await HelmValues.BuildAsync(chart, null, null, new Dictionary<string, string>
{
    ["image.tag"] = "1.25"
});

var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);
Console.WriteLine(renderer.Render());
```

## Use the high-level client

```csharp
using HelmSharp.Action;

var client = new HelmClient(new StaticHelmOptionsProvider());
var result = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "path/to/chart",
    DryRun = true
});

Console.WriteLine(result.StandardOutput);
```

Run the examples in `examples/` for complete console applications.
