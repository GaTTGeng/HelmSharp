# Installation

## What problem this solves

Use this page to choose the smallest HelmSharp package set for your application. Start from the workflow you need: render-only preview, command-like Helm operations, repository helpers, or Kubernetes release operations.

HelmSharp does not require the `helm` executable at runtime. Kubernetes release operations still require a reachable cluster and kubeconfig.

## Packages to install

For most applications, start with the high-level client:

```powershell
dotnet add package HelmSharp.Action --version 1.1.1
```

For render-only tools, install the lower layers:

```powershell
dotnet add package HelmSharp.Chart --version 1.1.1
dotnet add package HelmSharp.Engine --version 1.1.1
```

## Minimal complete code

```csharp
using HelmSharp.Action;

var client = new HelmClient(new StaticHelmOptionsProvider());

var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "/charts/my-chart",
    SetValues = new Dictionary<string, string>
    {
        ["image.tag"] = "1.1.0",
        ["replicaCount"] = "2"
    }
});

Console.WriteLine(result.StandardOutput);
```

## Why these APIs

`HelmSharp.Action` references the package set needed for command-like workflows. `HelmSharp.Chart` and `HelmSharp.Engine` keep preview tools smaller when you only need manifest output.

## Production notes

- Build all target frameworks locally with .NET 8, .NET 9, and .NET 10 SDKs.
- Use `HelmSharp.Action` only when your application needs release state, Kubernetes apply/delete/wait, repository operations, or command-style results.
- Keep `DryRun = true` for cluster-changing examples until your product has an explicit approval step.

## Next step

Continue with [your first render](first-render.md), then review [package responsibilities](../packages/action.md).
