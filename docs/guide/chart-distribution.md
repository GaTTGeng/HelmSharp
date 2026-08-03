# Package charts and manage dependencies

HelmSharp supports the traditional HTTP chart-repository workflow in managed code: package a chart, produce `index.yaml`, manage isolated repository state, pull an archive, and resolve dependencies. It does not require the Helm CLI at runtime.

::: warning Scope of this guide
OCI authentication and push/pull parity, provenance files, signing, and signature verification are not part of this workflow. See [Compatibility](../helm-compatibility.md) before building a production repository service around those capabilities.
:::

## Package a chart

Use the request overload when the build needs metadata overrides or a dependency refresh:

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#package-chart{csharp}

`Version` and `AppVersion` change the packaged `Chart.yaml`, not the source file. The archive is named `<chart-name>-<version>.tgz`, has one chart root, includes nested charts and CRDs, and rejects symbolic links. `.helmignore` supports file, directory, `*`, `?`, character-class, rooted, and `!` negation patterns; `**` is rejected explicitly.

## Produce repository metadata

Place the chart archives in a directory and create its index:

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#repository-index{csharp}

`Url` is the package base URL. `MergeIndexPath` retains historical entries that are no longer in the directory. Set `FailOnInvalidPackage` when an invalid archive must stop a publish; otherwise inspect diagnostics for skipped packages. `OutputPath` defaults to `index.yaml` under `DirectoryPath`.

## Isolate repository state in a service

Do not let tenants, tests, or concurrent jobs share repository config and cache directories.

```csharp
using var repository = new HelmChartRepository(new HelmRepositoryOptions
{
    RepositoryConfigPath = Path.Combine(tenantRoot, "repositories.yaml"),
    CacheDirectory = Path.Combine(tenantRoot, "cache")
});
```

The repository config stores definitions; cached indexes use `<repository-name>-index.yaml`. If paths are omitted, Helm-compatible environment variables and platform defaults are used. Repository search is cache-only: refresh an index before you need current remote results.

## Pull a chart safely

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#pull-chart{csharp}

The pull request accepts `repo/chart`, a chart name plus `RepositoryUrl`, or a direct `https://…tgz` URL. `Untar` extracts under `Destination`; extraction rejects entries that escape its destination. Credentials stay on the repository origin by default. Enable `PassCredentialsAll` only when a trusted repository intentionally redirects archives to another authenticated origin.

## Make dependency builds reproducible

Declare aliases and local references in `Chart.yaml` as usual:

```yaml
dependencies:
  - name: redis
    alias: cache
    version: ~18.0.0
    repository: "@stable"
  - name: shared-templates
    version: 1.2.3
    repository: file://../shared-templates
```

An alias changes both the subchart identity and its values key, so the first dependency receives values under `cache:`, not `redis:`. `DependencyUpdateAsync` resolves constraints, refreshes dependencies, and writes `Chart.lock`. `DependencyBuildAsync` is the CI path: it verifies the lock against `Chart.yaml`, restores the exact locked versions, and does not rewrite the lock.

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dependency-update{csharp}

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#dependency-build{csharp}

Run `DependencyListAsync` before packaging when you need to surface missing, wrong-version, unpacked, or inconsistent dependencies to a user. High-level client methods return `CommandResult`; lower-level repository methods throw exceptions. [Troubleshoot failures](error-handling.md) explains how to preserve both kinds of diagnostics.
