# API reference

Use this section for parameter and member lookup after choosing a workflow. The generated pages are deliberately factual: they mirror public types, properties, and methods from source rather than trying to teach a workflow a second time.

The reference is generated from the current source branch and can differ from a published NuGet package. For a version-pinned application, use that package version's release notes and source.

## Start with the package guide

| Package | Curated guide | Generated reference |
| --- | --- | --- |
| `HelmSharp.Action` | [Package guide](../packages/action.md) | [API](generated/action.md) |
| `HelmSharp.Chart` | [Package guide](../packages/chart.md) | [API](generated/chart.md) |
| `HelmSharp.Engine` | [Package guide](../packages/engine.md) | [API](generated/engine.md) |
| `HelmSharp.Kube` | [Package guide](../packages/kube.md) | [API](generated/kube.md) |
| `HelmSharp.Release` | [Package guide](../packages/release.md) | [API](generated/release.md) |
| `HelmSharp.Repo` | [Package guide](../packages/repo.md) | [API](generated/repo.md) |
| `HelmSharp.Registry` | [Package guide](../packages/registry.md) | [API](generated/registry.md) |
| `HelmSharp.Storage` | [Package guide](../packages/storage.md) | [API](generated/storage.md) |
| `HelmSharp.PostRenderer` | [Package guide](../packages/post-renderer.md) | [API](generated/postrenderer.md) |

## Read a page in this order

1. Start with a [package guide](../api-overview.md) to confirm the abstraction belongs in your application.
2. Use the generated page to find the exact member, request property, return type, and source location.
3. Return to a guide or example when a member changes cluster state, values precedence, or compatibility behavior.

## Template function APIs

Types under `HelmSharp.Engine.Functions` and `HelmSharp.Engine.Utilities` are documented as part of the engine reference, but they primarily support Helm/Sprig template execution. Application code should normally call `HelmTemplateRenderer`, not those helper functions directly.

## Regenerate after public API changes

```powershell
pwsh docs/scripts/generate-api-reference.ps1
```
