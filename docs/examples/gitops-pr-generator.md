# GitOps PR Generator

## What problem this solves

A GitOps workflow can render a chart in-process, write the resulting YAML to a repository, and open a pull request for review.

## Packages to install

```powershell
dotnet add package HelmSharp.Chart --version 1.1.1
dotnet add package HelmSharp.Engine --version 1.1.1
```

## Minimal complete code

```csharp
var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: ["values.yaml", "values.production.yaml"],
    valuesContent: null,
    setValues: new Dictionary<string, string> { ["image.tag"] = imageTag },
    setFileValues: null,
    setStringValues: null,
    setJsonValues: null,
    cancellationToken);

var renderer = new HelmTemplateRenderer(chart, releaseName, "apps", values);
var manifest = renderer.Render();

var outputPath = Path.Combine(repoRoot, "apps", releaseName, "manifest.yaml");
await File.WriteAllTextAsync(outputPath, manifest, cancellationToken);
```

## Why these APIs

GitOps systems usually want deterministic files and reviewable diffs, not direct cluster mutation. `HelmTemplateRenderer` gives the exact manifest that should be committed.

## Production notes

- Keep generated manifest paths stable.
- Commit values alongside manifests when reviewers need to understand why output changed.
- Use HelmSharp 1.1.0 compatibility results as the baseline, then add your own golden tests for critical internal charts.

## Next step

Use [Values](../guide/values.md) to model environment overlays explicitly.
