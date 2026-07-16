# API Reference

Use the API reference after you have chosen a package and workflow. The generated pages list public types, properties, and methods from source so the reference can be refreshed as the SDK changes.

::: warning Source and release versions
Generated pages reflect the current `master` source tree and may include unreleased APIs. The latest published packages are 1.1.1; M2 request and distribution APIs are planned for 1.2.0 and must not be assumed available from a 1.1.1 install command.
:::

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

## Reading generated pages

Generated pages intentionally stay factual: type kind, source file, properties, methods, and a short usage note. They do not replace the guide pages because raw member lists cannot explain workflow boundaries.

## Template function APIs

Types under `HelmSharp.Engine.Functions` and `HelmSharp.Engine.Utilities` are documented as part of the engine reference, but they primarily support Helm/Sprig template execution. Application code should normally call `HelmTemplateRenderer`, not those helper functions directly.

## Regenerating

```powershell
pwsh docs/scripts/generate-api-reference.ps1
```
