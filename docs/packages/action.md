# HelmSharp.Action

## Package responsibility

`HelmSharp.Action` is the high-level facade for applications that want Helm-like operations from managed code: template, install/upgrade, uninstall, rollback, status, history, get, lint, package, repository, and registry-oriented commands.

## When to install

Install this package when your product thinks in release workflows or command-style results:

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

## Dependencies

This package references the rendering, chart, Kubernetes, release, repository, registry, storage, and post-renderer packages.

## Main types

| Type | Use it for |
| --- | --- |
| `HelmClient` / `IHelmClient` | Command-like SDK entry point. |
| `HelmTemplateRequest` | Render a chart without applying resources. |
| `HelmUpgradeInstallRequest` | Install or upgrade, including dry-run. |
| `HelmUninstallRequest` | Remove release resources. |
| `HelmExecutionOptions` | Centralized environment defaults. |
| `IHelmOptionsProvider` | Provide options from config, DI, or tenant context. |
| `CommandResult` | Capture stdout, stderr, and exit code. |

## Common combinations

- `TemplateAsync` for preview pages.
- `UpgradeInstallAsync` with `DryRun = true` for review workflows.
- `UpgradeInstallAsync` with `DryRun = false` only after approval.
- `StatusAsync`, `HistoryAsync`, `GetManifestAsync`, and `GetValuesAsync` for release inspection.

## Current boundaries

HelmSharp does not shell out to `helm`. Some advanced plugin, provenance, OCI auth, and uncommon Kubernetes edge cases remain explicit compatibility boundaries.
