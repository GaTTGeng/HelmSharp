# HelmSharp.Action

`HelmSharp.Action` is the application-facing package for Helm-style operations. Install it for an API, worker, operator, or CLI that owns more than manifest text.

```powershell
dotnet add package HelmSharp.Action --version 1.3.1
```

It brings in the chart, renderer, Kubernetes, release, repository, registry, storage, and post-renderer layers. Use `HelmSharp.Chart` plus `HelmSharp.Engine` instead when an application only renders YAML.

## The entry point

`HelmClient` implements `IHelmClient` and accepts an `IHelmOptionsProvider`. Keep product defaults—namespace, field manager, timeout, and target capabilities—in that provider. Methods return `CommandResult`, so callers can handle output and errors without parsing exceptions into a command-line shape.

| Operation | Request or method | Read first |
| --- | --- | --- |
| Render a chart | `TemplateAsync(HelmTemplateRequest)` | [Render a chart](../guide/first-render.md) |
| Install or upgrade | `UpgradeInstallAsync(HelmUpgradeInstallRequest)` | [Release workflow](../guide/release-workflows.md) |
| Roll back or uninstall | `RollbackAsync`, `UninstallAsync` | [Release workflow](../guide/release-workflows.md) |
| Inspect a stored release | `StatusAsync`, `HistoryAsync`, `GetManifestAsync`, `GetValuesAsync` | [Release workflow](../guide/release-workflows.md) |
| Package, index, pull, or resolve dependencies | Request-object methods such as `PackageAsync` and `DependencyBuildAsync` | [Chart delivery](../guide/chart-distribution.md) |

Inspection reads stored revisions; it does not re-render today's version of a chart. Revision `0` selects the latest stored record, including a retained uninstall. `ListReleasesAsync` lists deployed revisions and supports comma-separated exact `key=value` label selectors.

## Important lifecycle constraints

Use dry run for review. Non-dry-run lifecycle requests that reach release persistence leave a Secret-backed revision for later inspection. Successful upgrades and rollbacks supersede the prior deployed revision; failures retain a failed revision. Unsupported options fail before cluster mutation rather than being silently ignored.

Traditional HTTP repositories and local dependencies are supported. Full OCI authentication, provenance verification, and every Helm CLI switch are not `1.3.1` guarantees; check [Compatibility](../helm-compatibility.md) for the current boundary.

For all public members, use the [generated Action API](../api/generated/action.md).
