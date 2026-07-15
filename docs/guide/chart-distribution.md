# Chart Packaging and Repository Workflows

M2 covers the traditional HTTP chart workflow entirely in managed .NET: package a chart, generate an `index.yaml`, manage repository configuration and caches, pull an archive, and update or rebuild dependencies. Helm is used by the test suite as an oracle, but it is not a runtime dependency.

## Package a chart

Use the request overload when you need metadata overrides or want dependencies refreshed before packaging:

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#package-chart{csharp}

`Version` and `AppVersion` are written only into the packaged `Chart.yaml`; the source file is not modified. The archive is named `<chart-name>-<version>.tgz`, contains a single `<chart-name>/` root, preserves nested charts and CRDs, and skips symbolic links.

The packager reads `.helmignore` from the chart root. Blank lines and comments are ignored; file, directory, `*`, `?`, character-class, rooted, and `!` negation patterns are supported. Helm's `**` syntax is not supported and produces a clear error. If `DependencyUpdate` is `true`, dependency update must succeed before the archive is written.

## Generate a repository index

Place one or more `.tgz` packages in a directory, then generate the repository metadata:

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#repository-index{csharp}

`Url` becomes the base URL for package entries. `MergeIndexPath` retains older versions that are not present in the current directory. Invalid packages are skipped by default and available through lower-level diagnostics; set `FailOnInvalidPackage` when publishing should be transactional. `OutputPath` defaults to `index.yaml` in `DirectoryPath`.

## Isolate repository state

`HelmChartRepository` uses Helm-compatible configuration and cache locations. For services, tests, and tenants, set explicit paths so concurrent workloads do not share credentials or stale indexes:

```csharp
using var repository = new HelmChartRepository(new HelmRepositoryOptions
{
    RepositoryConfigPath = Path.Combine(tenantRoot, "repositories.yaml"),
    CacheDirectory = Path.Combine(tenantRoot, "cache")
});
```

Definitions are stored in `repositories.yaml`; cached indexes use `<repository-name>-index.yaml`. When paths are not explicit, `HELM_REPOSITORY_CONFIG`, `HELM_CONFIG_HOME`, `HELM_REPOSITORY_CACHE`, `HELM_CACHE_HOME`, and the corresponding XDG locations are considered before platform defaults.

A complete repository lifecycle uses these methods:

1. `AddRepositoryAsync` writes a named repository definition.
2. `ListRepositoriesAsync` reads configured definitions.
3. `FetchRepoIndexAsync` refreshes the selected repository cache.
4. `SearchRepoAsync(keyword)` searches configured caches offline.
5. `RemoveRepositoryAsync` removes both the definition and its cached index.

Repository search is deliberately cache-only. Refresh first when current remote results matter; a missing or damaged cache for one repository does not hide valid results from other repositories.

## Pull a chart

The following example adds a traditional repository, refreshes its index, selects a semantic version, verifies the advertised digest, and extracts the archive:

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#pull-chart{csharp}

Supported pull forms are:

- `repo/chart` for a configured repository and cached index;
- a chart name plus `RepositoryUrl` for an explicit repository;
- a direct `https://.../chart-version.tgz` archive URL.

`Destination` controls where the archive is saved. `Untar` extracts it, and `UntarDirectory` selects the extraction parent. Extraction rejects entries that escape the destination. Repository credentials are sent only to the same origin by default; enable `PassCredentialsAll` only for a trusted cross-origin archive host.

## Declare dependencies

`Chart.yaml` may combine repository aliases, chart aliases, and local references:

```yaml
dependencies:
  - name: redis
    alias: cache
    version: ~18.0.0
    repository: "@stable"
  - name: redis
    alias: session
    version: ~18.0.0
    repository: "alias:stable"
  - name: shared-templates
    version: 1.2.3
    repository: file://../shared-templates
```

`@stable` and `alias:stable` resolve through the named entry in `repositories.yaml`. A relative `file://` path is resolved from the parent chart directory and packaged into `charts/`. The lock keeps the original dependency name and repository reference. A chart alias changes the subchart identity and values key, so values for the first dependency above belong under `cache:`, not `redis:`.

`condition`, `tags`, and `import-values` affect values and rendering. They do not remove a declared dependency from the update/download set.

## Update dependencies

Update resolves version constraints, downloads or packages every declared dependency, removes stale `.tgz` files, and writes a Helm-compatible `Chart.lock`:

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dependency-update{csharp}

Keep `SkipRepositoryRefresh = false` for normal online updates. Set it to `true` only when the required named repository indexes already exist in `RepositoryCachePath`, such as a controlled offline build.

## Build from `Chart.lock`

Build is the reproducible path for CI and releases:

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dependency-build{csharp}

The operation requires `Chart.lock`, verifies that its digest still matches `Chart.yaml`, restores the exact locked versions, and does not rewrite the lock. Configured repository dependencies require their cached indexes. A `file://` dependency also requires the referenced source chart to remain available at the locked version.

Run `DependencyListAsync` to inspect `ok`, `missing`, `wrong version`, `unpacked`, and lock consistency states before packaging.

## Compatibility boundary

This guide covers traditional HTTP chart repositories and local file dependencies. OCI registry authentication and pull/push parity, provenance files, signing, and signature verification belong to the later OCI and provenance milestone. HelmSharp does not invoke the Helm CLI for any workflow described here.

High-level `HelmClient` methods return `CommandResult`; check `Succeeded`, `ExitCode`, `StandardOutput`, and `StandardError`. Lower-level repository methods throw .NET exceptions. See [Error Handling](error-handling.md) for the shared model and [HelmSharp.Action](../packages/action.md), [HelmSharp.Chart](../packages/chart.md), and [HelmSharp.Repo](../packages/repo.md) for package ownership.
