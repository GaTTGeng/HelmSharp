# HelmSharp.Repo

## Package responsibility

`HelmSharp.Repo` handles chart repository metadata: add, remove, list, search, pull, push-to-OCI placeholder behavior, and index generation.

## Repository configuration and cache

`HelmChartRepository` persists repository definitions in a Helm-compatible `repositories.yaml` file. By default it follows Helm-style environment settings: `HELM_REPOSITORY_CONFIG` for the file, `HELM_CONFIG_HOME` (or `XDG_CONFIG_HOME`) for the config home, and `HELM_REPOSITORY_CACHE` / `HELM_CACHE_HOME` (or `XDG_CACHE_HOME`) for cached repository indexes and chart downloads. `HELM_CACHE_HOME` is treated as a cache home, with repository files stored in its `repository` subdirectory. On Windows, the fallback homes are under the current user's application-data folders.

Use `HelmRepositoryOptions` to isolate an application or test from the user's Helm state:

```csharp
var repository = new HelmChartRepository(new HelmRepositoryOptions
{
    ConfigDirectory = @"C:\app-data\helm-config",
    CacheDirectory = @"C:\app-data\helm-cache"
});
```

The existing `HelmChartRepository(cacheDirectory)` overload remains available and uses that directory for both config and cache isolation. Explicit `HelmRepositoryOptions.ConfigDirectory` and `CacheDirectory` take precedence over ambient Helm environment variables. Repository index caches use Helm's `<repository-name>-index.yaml` filename. Repository names must contain only letters, digits, `.`, `_`, and `-`; adding an existing name throws an error, matching Helm's non-`--force` behavior. Credentials remain stored only when supplied, in the same plaintext form as earlier HelmSharp releases, so protect the configured file with normal OS file permissions.

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
