---
layout: home

hero:
  name: HelmSharp
  text: Managed Helm workflows for .NET
  tagline: Render Helm-style charts, merge values, package charts, and drive Kubernetes release operations without invoking the helm executable at runtime.
  actions:
    - theme: brand
      text: Get Started
      link: /getting-started
    - theme: alt
      text: API Overview
      link: /api-overview

features:
  - title: Managed chart rendering
    details: Load chart directories or archives and render templates from a .NET process using Helm-style values, built-in objects, and common template functions.
  - title: Release lifecycle APIs
    details: Install, upgrade, uninstall, rollback, inspect, and dry-run releases through a high-level client that exposes command-like results.
  - title: Kubernetes integration
    details: Apply manifests, wait for resources, and persist release history through Kubernetes-native storage without depending on the Helm CLI.
  - title: Compatibility tracked by tests
    details: HelmSharp is validated against focused fixtures and real public charts, with Helm CLI used only as a test oracle.
---

## Why HelmSharp?

HelmSharp is a managed .NET SDK for applications that need Helm-like behavior in-process. It is useful when a service, controller, desktop tool, CI extension, or internal platform needs to render charts or manage release state without shelling out to `helm`.

The project currently targets `net8.0`, `net9.0`, and `net10.0`. It implements a practical Helm-compatible subset and tracks remaining gaps openly in the compatibility documentation.

## Install

Most applications should start with the high-level package:

```powershell
dotnet add package HelmSharp.Action
```

Use lower-level packages when you only need a specific layer:

```powershell
dotnet add package HelmSharp.Chart
dotnet add package HelmSharp.Engine
dotnet add package HelmSharp.Repo
```

## Quick Example

```csharp
using HelmSharp.Action;

var client = new HelmClient(new StaticHelmOptionsProvider());

var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart",
    SetValues = new Dictionary<string, string>
    {
        ["image.tag"] = "1.2.3",
        ["replicaCount"] = "2"
    }
});

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

## Current Scope

HelmSharp supports chart loading, values merging, managed template rendering, chart package creation, repository helpers, Kubernetes apply/delete/wait operations, and release history backed by Kubernetes Secrets.

It is not a byte-for-byte Helm CLI clone. Advanced template edge cases, uncommon Kubernetes readiness behavior, complete OCI authentication flows, provenance verification, and plugin execution remain active or planned compatibility work.
