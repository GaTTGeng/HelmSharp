---
layout: home

hero:
  name: HelmSharp
  text: Helm-compatible rendering for .NET
  tagline: HelmSharp 1.1.0 renders 129/129 templates from five real-world public charts with byte-for-byte parity against helm template after normalization. No helm binary in your runtime path.
  actions:
    - theme: brand
      text: Start the guide
      link: /getting-started
    - theme: alt
      text: Examples
      link: /examples/render-preview-api
    - theme: alt
      text: Live Compare
      link: /compare

features:
  - title: Real-chart parity baseline
    details: "The 1.1.0 golden suite passes podinfo, metrics-server, external-dns, ingress-nginx, and cert-manager: 129/129 templates."
  - title: Workflow-first docs
    details: Learn install choices, render-only previews, values precedence, release dry-runs, Kubernetes apply/wait, and error handling.
  - title: Package-by-package guidance
    details: Each NuGet package has a role page with install advice, main types, common combinations, and compatibility boundaries.
  - title: Generated API reference
    details: "Public classes, interfaces, records, enums, methods, and properties are indexed from source for maintainable member lookup."
---

## See HelmSharp in Action

Upload a Helm chart and compare HelmSharp output against the real Helm CLI side by side.

<div class="compare-cta">
  <a class="compare-cta-btn" href="./compare">
    <span class="compare-cta-label">Launch Live Comparison</span>
    <span class="compare-cta-arrow">→</span>
  </a>
</div>

## What HelmSharp 1.1.0 is ready for

You have a .NET application that needs Helm chart output without a runtime dependency on the `helm` executable. Maybe the caller is a web service, an operator, a build agent, a GitOps generator, or a product feature that previews manifests before anything touches a cluster.

HelmSharp gives that code a managed path: load charts, build values, render manifests, inspect NOTES, and optionally move into Kubernetes release operations.

## Quick Example

```csharp
var chart = await HelmChartLoader.LoadAsync("/charts/my-chart", ct);
var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);
var manifests = renderer.Render();
```

::: details Prefer a command-like client?

Use `HelmSharp.Action` when you want a higher-level facade for template, dry-run, install, upgrade, uninstall, rollback, status, package, repository, and release history operations.

```csharp
using HelmSharp.Action;

var client = new HelmClient(optionsProvider);

var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "/charts/my-chart",
    SetValues = new Dictionary<string, string>
    {
        ["image.tag"] = "1.2.3",
        ["replicaCount"] = "2"
    }
});

Console.WriteLine(result.StandardOutput);
```

:::

## Documentation Paths

| Path | Start here when... |
| --- | --- |
| [Getting Started](getting-started.md) | You want the shortest path to the first render or dry-run. |
| [Guide](guide/installation.md) | You want step-by-step explanations of install, values, rendering, releases, Kubernetes operations, and errors. |
| [Examples](examples/render-preview-api.md) | You want realistic integration patterns. |
| [Packages](packages/action.md) | You need to choose the right NuGet package boundary. |
| [API Reference](api/index.md) | You need public member lookup generated from source. |

## Install

Most applications start with the high-level package:

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

Use narrower packages when your application only needs rendering:

```powershell
dotnet add package HelmSharp.Chart --version 1.1.0
dotnet add package HelmSharp.Engine --version 1.1.0
```

## Current Scope

The current golden suite renders **129/129 templates** across `podinfo`, `metrics-server`, `external-dns`, `ingress-nginx`, and `cert-manager` with Pass verdicts. That is the compatibility baseline HelmSharp uses to grow against real charts, not only hand-written fixtures.

HelmSharp already covers chart loading, values merging, managed template rendering, chart packaging, repository helpers, Kubernetes apply/delete/wait helpers, and release history backed by Kubernetes Secrets.

It is still an SDK with explicit compatibility boundaries. Advanced plugin behavior, complete provenance verification, OCI authentication parity, and uncommon readiness cases remain planned or active work. Check [Helm Compatibility](helm-compatibility.md) before depending on a specific Helm edge case.
