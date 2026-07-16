# HelmSharp.Action

## Package responsibility

`HelmSharp.Action` is the high-level facade for applications that want Helm-like operations from managed code: template, install/upgrade, uninstall, rollback, status, history, get, lint, package, repository, and registry-oriented commands.

## When to install

Install this package when your product thinks in release workflows or command-style results:

```powershell
dotnet add package HelmSharp.Action --version 1.1.1
```

::: warning Version availability
1.1.1 is the latest published package. The M2 request types and package, pull, repository-index, and dependency workflows below reflect the current `master` branch and are planned for 1.2.0; they are not available from this 1.1.1 install command.
:::

## Dependencies

This package references the rendering, chart, Kubernetes, release, repository, registry, storage, and post-renderer packages.

## Main types

| Type | Use it for |
| --- | --- |
| `HelmClient` / `IHelmClient` | Command-like SDK entry point. |
| `HelmTemplateRequest` | Render a chart without applying resources. |
| `HelmUpgradeInstallRequest` | Install or upgrade, including dry-run. |
| `HelmUninstallRequest` | Remove release resources. |
| `HelmPackageRequest` | Package a chart with metadata and dependency options. |
| `HelmDependencyUpdateRequest` | Resolve dependencies and update `Chart.lock`. |
| `HelmDependencyBuildRequest` | Restore exact versions from `Chart.lock`. |
| `HelmExecutionOptions` | Centralized environment defaults. |
| `IHelmOptionsProvider` | Provide options from config, DI, or tenant context. |
| `CommandResult` | Capture stdout, stderr, and exit code. |

## Common combinations

- `TemplateAsync` for preview pages.
- `UpgradeInstallAsync` with `DryRun = true` for review workflows.
- `UpgradeInstallAsync` with `DryRun = false` only after approval.
- `StatusAsync`, `HistoryAsync`, `GetManifestAsync`, and `GetValuesAsync` for release inspection.
- `PackageAsync`, `PullAsync`, and `RepoIndexAsync` for chart distribution.
- `DependencyListAsync`, `DependencyUpdateAsync`, and `DependencyBuildAsync` for dependency lifecycle management.

See [Chart Packaging and Repository Workflows](../guide/chart-distribution.md) for complete request examples, lock-file behavior, repository isolation, and compatibility boundaries.

## Current boundaries

HelmSharp does not shell out to `helm`. M2 covers traditional HTTP repositories and local file dependencies; provenance and full OCI authentication/pull/push parity remain later compatibility work.
