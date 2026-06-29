---
layout: home

hero:
  name: HelmSharp
  text: Render Helm charts inside .NET
  tagline: Load charts, merge values, and produce Kubernetes manifests from managed code. No helm binary. No Process.Start. No shell boundary in your application path.
  actions:
    - theme: brand
      text: Start rendering
      link: /getting-started
    - theme: alt
      text: Check compatibility
      link: /helm-compatibility

features:
  - title: In-process chart rendering
    details: Use Helm-style charts from services, controllers, desktop tools, CI extensions, or internal platforms without spawning the Helm CLI at runtime.
  - title: Values that feel familiar
    details: Combine values files, inline YAML, and set-style overrides so application code can produce the same deployment variants operators expect.
  - title: Release workflows when you need them
    details: Start with rendering, then opt into install, upgrade, rollback, uninstall, Kubernetes apply, wait behavior, and release history APIs.
  - title: Compatibility measured against Helm
    details: Helm CLI output is used as a test oracle. The current real-chart suite renders 127/129 templates across five public charts.
---

## The job HelmSharp is built for

You have a .NET application that needs the output of a Helm chart, but you do not want a runtime dependency on the `helm` executable. Maybe the caller is a web service, an operator, a build agent, or a product feature that needs to preview manifests before anything touches a cluster.

HelmSharp gives that code a managed path: load the chart, build values, render manifests, and optionally move into Kubernetes release operations.

## Quick Example

```csharp
var chart = await HelmChartLoader.LoadAsync("/charts/my-chart", ct);
var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);
var manifests = renderer.Render();
```

That is the core idea: chart in, manifests out, from your process.

::: details Prefer a command-like client?

Use `HelmSharp.Action` when you want a higher-level facade for template, install, upgrade, uninstall, rollback, status, package, repo, and registry-oriented operations.

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

The options provider is where an application centralizes defaults such as namespace, field manager, kubeconfig, and environment policy.

:::

## Install

Most applications start with the high-level package:

```powershell
dotnet add package HelmSharp.Action
```

Use narrower packages when your application only needs one layer:

```powershell
dotnet add package HelmSharp.Chart
dotnet add package HelmSharp.Engine
```

## Current Scope

The current golden suite renders **127/129 templates** across `podinfo`, `metrics-server`, `external-dns`, `ingress-nginx`, and `cert-manager`; the remaining two `ingress-nginx` templates are tracked by the real-chart reports. That is the baseline HelmSharp uses to grow compatibility against real charts, not only hand-written fixtures.

HelmSharp already covers chart loading, values merging, managed template rendering, chart packaging, repository helpers, Kubernetes apply/delete/wait helpers, and release history backed by Kubernetes Secrets.

It is still an SDK with explicit compatibility boundaries. Some advanced Sprig behavior, output formatting details, OCI authentication flows, provenance verification, uncommon readiness cases, and plugin execution remain planned or active work. Check the compatibility page before depending on a specific Helm edge case.
