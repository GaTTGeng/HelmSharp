# First Render

## What problem this solves

Use the render-only path when your application needs Kubernetes YAML for preview, validation, policy checks, GitOps output, or drift detection without mutating a cluster.

## Packages to install

```powershell
dotnet add package HelmSharp.Chart --version 1.1.0
dotnet add package HelmSharp.Engine --version 1.1.0
```

## Minimal complete code

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#render-first-chart{csharp}

## Why these APIs

`HelmChartLoader.LoadAsync` loads `Chart.yaml`, `values.yaml`, templates, CRDs, files, dependencies, and subcharts. `HelmValues.BuildAsync` applies Helm-style values precedence. `HelmTemplateRenderer.Render()` returns rendered manifests, while `RenderNotes()` returns `NOTES.txt`.

## Production notes

- Pass absolute chart paths from your service boundary so diagnostics are reproducible.
- Capture the values inputs used for each preview; the manifest is only explainable with the values set that produced it.
- Use [Live Compare](../compare.md) or the Helm CLI golden tests when investigating chart-specific differences.

## Next step

Read [Values](values.md) to model user overrides correctly.
