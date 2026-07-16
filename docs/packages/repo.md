# HelmSharp.Repo

## Package responsibility

`HelmSharp.Repo` handles chart repository metadata: add, remove, list, search, pull, push-to-OCI placeholder behavior, and index generation.

::: warning Version availability
1.1.1 is the latest published package. The complete M2 configuration, cache, search, semantic-version pull, digest, and extraction behavior described below reflects the current `master` branch and is planned for 1.2.0; installing 1.1.1 does not provide that complete surface.
:::

## Repository configuration and cache

`HelmChartRepository` persists repository definitions in a Helm-compatible `repositories.yaml` file. By default it follows Helm-style environment settings: `HELM_REPOSITORY_CONFIG` for the file, `HELM_CONFIG_HOME` (or `XDG_CONFIG_HOME`) for the config home, and `HELM_REPOSITORY_CACHE` / `HELM_CACHE_HOME` (or `XDG_CACHE_HOME`) for cached repository indexes and chart downloads. `HELM_CACHE_HOME` is treated as a cache home, with repository files stored in its `repository` subdirectory. Platform fallbacks match Helm: Linux uses `~/.config/helm` and `~/.cache/helm`, macOS uses `~/Library/Preferences/helm` and `~/Library/Caches/helm`, and Windows uses `%APPDATA%\helm` and `%TEMP%\helm` for config and cache homes respectively.

Use `HelmRepositoryOptions` to isolate an application or test from the user's Helm state:

```csharp
var repository = new HelmChartRepository(new HelmRepositoryOptions
{
    ConfigDirectory = @"C:\app-data\helm-config",
    CacheDirectory = @"C:\app-data\helm-cache"
});
```

The existing `HelmChartRepository(cacheDirectory)` overload remains available and uses that directory for both config and cache isolation. Explicit `HelmRepositoryOptions.ConfigDirectory` and `CacheDirectory` take precedence over ambient Helm environment variables. Repository index caches normally use Helm's `<repository-name>-index.yaml` filename; names requiring portable filename sanitization receive a deterministic identity suffix, as do uppercase names on Windows and macOS, so distinct repository identities cannot overwrite one another. Helm-compatible names may include spaces, `@`, and leading dots, but cannot be empty or contain `/`. Adding an existing exact name with different settings throws an error, while duplicate URLs and case-distinct names retain separate identities. Credentials remain stored only when supplied, in the same plaintext form as earlier HelmSharp releases; configuration replacement preserves existing Unix permissions and creates new credential-bearing files owner-only.

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

For an end-to-end add/list/update/search, index-generation, and pull example, including isolated config/cache paths, see [Chart Packaging and Repository Workflows](../guide/chart-distribution.md).

## Current boundaries

Traditional HTTP repository configuration, cached search, semantic-version pull, digest verification, and safe extraction are covered. Provenance verification and complete OCI registry workflows remain later milestone work.
