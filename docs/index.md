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
      text: 🔬 Live Compare
      link: /compare
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
    details: Helm CLI output is used as a test oracle. The current real-chart suite renders 129/129 templates across five public charts. <br><br> **[🔬 Try the live comparison →](/compare)** to see how close HelmSharp gets on your own charts.
---

---

## 🔬 See HelmSharp in Action

The fastest way to understand HelmSharp's compatibility is to **see it yourself**. Upload any Helm chart and watch HelmSharp and the real Helm CLI render it side-by-side — same input, same values, instant diff.

<div class="compare-cta">
  <a class="compare-cta-btn" href="/compare">
    <span class="compare-cta-icon">🔬</span>
    <span class="compare-cta-label">Launch Live Comparison</span>
    <span class="compare-cta-arrow">→</span>
  </a>
</div>

<style>
.compare-cta {
  text-align: center;
  margin: 2rem 0;
}
.compare-cta-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.85rem 2rem;
  font-size: 1.1rem;
  font-weight: 700;
  color: #fff;
  background: linear-gradient(135deg, var(--vp-c-brand-1) 0%, #0f766e 100%);
  border-radius: 10px;
  text-decoration: none;
  transition: transform 0.15s, box-shadow 0.15s;
  box-shadow: 0 2px 12px rgba(37, 99, 235, 0.3);
}
.compare-cta-btn:hover {
  transform: translateY(-1px);
  box-shadow: 0 4px 20px rgba(37, 99, 235, 0.45);
  text-decoration: none !important;
  color: #fff;
}
.compare-cta-icon {
  font-size: 1.3rem;
}
.compare-cta-arrow {
  font-size: 1.1rem;
  transition: transform 0.15s;
}
.compare-cta-btn:hover .compare-cta-arrow {
  transform: translateX(3px);
}
</style>

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

The current golden suite renders **129/129 templates** across `podinfo`, `metrics-server`, `external-dns`, `ingress-nginx`, and `cert-manager` without parser exceptions. That is the baseline HelmSharp uses to grow compatibility against real charts, not only hand-written fixtures.

HelmSharp already covers chart loading, values merging, managed template rendering, chart packaging, repository helpers, Kubernetes apply/delete/wait helpers, and release history backed by Kubernetes Secrets.

It is still an SDK with explicit compatibility boundaries. Some advanced Sprig behavior, output formatting details, OCI authentication flows, provenance verification, uncommon readiness cases, and plugin execution remain planned or active work. Check the compatibility page before depending on a specific Helm edge case.
