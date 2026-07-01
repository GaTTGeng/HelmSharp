# API Overview

Start with the workflow, then choose the smallest package. The detailed member index lives under [API Reference](api/index.md); this page is the decision guide.

## Package decision table

| You want to... | Start with | Next page |
| --- | --- | --- |
| Render manifests only | `HelmSharp.Chart` + `HelmSharp.Engine` | [First Render](guide/first-render.md) |
| Build a preview API | `HelmSharp.Chart` + `HelmSharp.Engine` | [Render Preview API](examples/render-preview-api.md) |
| Offer dry-run and apply | `HelmSharp.Action` | [Release Workflows](guide/release-workflows.md) |
| Apply already-rendered YAML | `HelmSharp.Kube` | [Kubernetes Operations](guide/kubernetes-operations.md) |
| Manage release history directly | `HelmSharp.Release` | [Release package](packages/release.md) |
| Search or pull from chart repos | `HelmSharp.Repo` | [Repo package](packages/repo.md) |

## Core workflow shape

```mermaid
flowchart LR
    A["HelmChartLoader"] --> B["HelmValues.BuildAsync"]
    B --> C["HelmTemplateRenderer"]
    C --> D["Manifest preview"]
    C --> E["HelmClient dry-run/apply"]
```

## Most-used public types

| Type | Package | Use |
| --- | --- | --- |
| `HelmClient` | `HelmSharp.Action` | Command-like facade for template and release operations. |
| `HelmTemplateRequest` | `HelmSharp.Action` | Render request for high-level previews. |
| `HelmUpgradeInstallRequest` | `HelmSharp.Action` | Install/upgrade request, including dry-run. |
| `IHelmOptionsProvider` | `HelmSharp.Action` | Centralize environment defaults. |
| `HelmChartLoader` | `HelmSharp.Chart` | Load a chart directory or archive. |
| `HelmValues` | `HelmSharp.Chart` | Merge chart defaults and overrides. |
| `HelmTemplateRenderer` | `HelmSharp.Engine` | Render manifests and NOTES. |
| `KubernetesManifestApplier` | `HelmSharp.Kube` | Apply/delete rendered manifests. |

## Generated API reference

The generated reference lists public types, properties, and methods by package:

- [Action API](api/generated/action.md)
- [Chart API](api/generated/chart.md)
- [Engine API](api/generated/engine.md)
- [Kube API](api/generated/kube.md)
- [Release API](api/generated/release.md)
- [Repo API](api/generated/repo.md)

## Error handling model

High-level `HelmClient` operations return `CommandResult`. Lower-level loading, values, and rendering APIs throw .NET exceptions when they cannot load, parse, or evaluate input. See [Error Handling](guide/error-handling.md).
