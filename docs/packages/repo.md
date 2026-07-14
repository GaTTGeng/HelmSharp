# HelmSharp.Repo

## Package responsibility

`HelmSharp.Repo` handles chart repository metadata: add, remove, list, search, pull, push-to-OCI placeholder behavior, and index generation.

## When to install

Install directly when building repository management tools:

```powershell
dotnet add package HelmSharp.Repo --version 1.1.1
```

## Dependencies

This package references `HelmSharp.Chart` and `HelmSharp.Registry`.

## Main types

| Type | Use it for |
| --- | --- |
| `HelmChartRepository` | Manage local repo config, search indexes, and pull charts. |
| `HelmRepoIndexer` | Generate repository `index.yaml`. |
| `HelmRepository` | Repository configuration entry. |
| `HelmRepoIndex` | Parsed repository index. |
| `HelmChartVersion` | Chart version metadata from an index. |
| `HelmChartSearchResult` | Search result projection. |

## Common combinations

Use repository helpers before calling `HelmChartLoader` or high-level `HelmClient` operations.

## Current boundaries

Authentication and OCI flows are intentionally limited in 1.1.1 compared with the full Helm CLI.
