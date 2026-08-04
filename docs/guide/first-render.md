# Render a chart

Use this path for a preview endpoint, policy check, CI artifact, or GitOps generator. It reads chart inputs and returns text; it does not contact a Kubernetes cluster or create release history.

## 1. Install the renderer

```powershell
dotnet add package HelmSharp.Chart --version 1.3.1
dotnet add package HelmSharp.Engine --version 1.3.1
```

The path passed to the loader must point at a chart directory containing `Chart.yaml`, or at a packaged chart archive.

## 2. Load values and render

Put the following in a console application's `Program.cs`, then pass the chart path as the first argument.

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

var renderer = new HelmTemplateRenderer(
    chart,
    releaseName: "demo",
    releaseNamespace: "default",
    values: values);

Console.WriteLine(renderer.Render());
```

The code has three deliberately separate stages:

1. `HelmChartLoader` reads `Chart.yaml`, templates, default values, chart files, CRDs, and packaged dependencies.
2. `HelmValues.BuildAsync` produces the dictionary exposed as `.Values` to templates.
3. `HelmTemplateRenderer` supplies release context and evaluates the templates. `Render()` returns manifests; `RenderNotes()` returns `NOTES.txt` separately when the caller needs it.

## 3. Add a values file or override

Supply one or more values files in order. A later file wins when both files set the same key.

```csharp
var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: ["values.base.yaml", "values.production.yaml"],
    valuesContent: null,
    setValues: new Dictionary<string, string> { ["image.tag"] = "1.25.3" },
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken: cancellationToken);
```

Read [Values and overrides](values.md) before accepting these inputs from users. It explains precedence, string preservation, JSON values, and the difference between a file path and `--set-file` content.

## 4. Decide whether to move up a level

Stay on this API when the output belongs to your application. Move to `HelmClient.TemplateAsync` when a command-style `CommandResult` is easier for your interface, and to `UpgradeInstallAsync` only when the application is also responsible for applying the result.

If an existing chart renders differently from Helm, check the [compatibility contract](../helm-compatibility.md) and the [template-function matrix](../template-function-compatibility.md). Use [HelmCompare](../compare.md) to inspect a concrete difference.
