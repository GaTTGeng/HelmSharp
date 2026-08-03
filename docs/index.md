---
layout: home

hero:
  name: HelmSharp
  text: Use Helm charts from .NET
  tagline: Render, inspect, and release Helm charts in managed code. HelmSharp does not start the Helm CLI at runtime.
  image:
    src: /logo.svg
    alt: HelmSharp logo
  actions:
    - theme: brand
      text: Run the quickstart
      link: /getting-started
    - theme: alt
      text: Browse examples
      link: /examples/render-preview-api
    - theme: alt
      text: Compare output
      link: /compare

features:
  - title: Render in-process
    details: Load a chart, merge values, and produce manifests without making the Helm executable part of your deployment.
  - title: Deploy deliberately
    details: Preview first, then use managed install, upgrade, rollback, and release-history operations when your application owns deployment.
  - title: Know the boundary
    details: The compatibility pages state what is tested, what is partial, and what still needs chart-specific verification.
---

## Start with the job you need to do

<div class="docs-paths">
  <a class="docs-path" href="./guide/first-render"><strong>Render a chart</strong><span>Generate manifests for a preview, policy check, or GitOps commit. No cluster access is required.</span></a>
  <a class="docs-path" href="./guide/release-workflows"><strong>Run a release workflow</strong><span>Dry-run, apply, wait, inspect revisions, roll back, or uninstall from a service that owns the deployment.</span></a>
  <a class="docs-path" href="./guide/chart-distribution"><strong>Manage chart delivery</strong><span>Package charts, generate an index, pull from an HTTP repository, and resolve dependencies in .NET.</span></a>
</div>

## The smallest useful render

Install the chart and renderer packages:

```powershell
dotnet add package HelmSharp.Chart --version 1.3.1
dotnet add package HelmSharp.Engine --version 1.3.1
```

Then load a chart directory, build its values, and render it. The [quickstart](getting-started.md) contains a copyable program and explains the three objects involved.

```csharp
var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
var values = await HelmValues.BuildAsync(chart, null, null, null, null, null, null, CancellationToken.None);
var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);

var manifest = renderer.Render();
```

<div class="key-point"><strong>Use the lower-level path for previews.</strong> It only reads chart inputs and returns strings. Use <code>HelmSharp.Action</code> when you intentionally need release state or Kubernetes mutation.</div>

## Choose the right entry point

| If you are building… | Start with | Read next |
| --- | --- | --- |
| A preview, validator, or GitOps generator | `HelmSharp.Chart` + `HelmSharp.Engine` | [Render a chart](guide/first-render.md) |
| A deployment service or operator | `HelmSharp.Action` | [Install and upgrade releases](guide/release-workflows.md) |
| A Kubernetes controller with existing YAML | `HelmSharp.Kube` | [Apply manifests directly](guide/kubernetes-operations.md) |
| A chart repository or packaging pipeline | `HelmSharp.Action` + `HelmSharp.Repo` | [Chart delivery](guide/chart-distribution.md) |

The [package decision guide](api-overview.md) explains these boundaries in more detail and links to each generated API surface.

## Before using an existing chart

HelmSharp follows Helm behavior where that behavior is implemented and tested; it is not a promise that every chart or plugin will work unchanged. Check the [compatibility contract](helm-compatibility.md) and the [template-function matrix](template-function-compatibility.md) before treating a Helm edge case as a production dependency.

[HelmCompare](compare.md) is also available for side-by-side output inspection.
