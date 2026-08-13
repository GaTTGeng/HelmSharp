# HelmSharp.Action

`HelmSharp.Action` is the application-facing package for Helm-style operations. Install it for an API, worker, operator, or CLI that owns more than manifest text.

```powershell
dotnet add package HelmSharp.Action --version 1.3.1
```

It brings in the chart, renderer, Kubernetes, release, repository, registry, storage, and post-renderer layers. Use `HelmSharp.Chart` plus `HelmSharp.Engine` instead when an application only renders YAML.

## The entry point

`HelmClient` implements `IHelmClient` and accepts an `IHelmOptionsProvider`. Keep product defaults—namespace, field manager, and timeout—in that provider. `TemplateAsync` takes target capabilities from `HelmTemplateRequest.KubeVersion` and `ApiVersions`, so set those fields on each rendering request. Methods return `CommandResult`, so callers can handle output and errors without parsing exceptions into a command-line shape.

| Operation | Request or method | Read first |
| --- | --- | --- |
| Render a chart | `TemplateAsync(HelmTemplateRequest)` | [Render a chart](../guide/first-render.md) |
| Install or upgrade | `UpgradeInstallAsync(HelmUpgradeInstallRequest)` | [Release workflow](../guide/release-workflows.md) |
| Roll back or uninstall | `RollbackAsync`, `UninstallAsync` | [Release workflow](../guide/release-workflows.md) |
| Inspect a stored release | `StatusAsync`, `HistoryAsync`, `GetManifestAsync`, `GetValuesAsync` | [Release workflow](../guide/release-workflows.md) |
| Package, index, pull, or resolve dependencies | Request-object methods such as `PackageAsync` and `DependencyBuildAsync` | [Chart delivery](../guide/chart-distribution.md) |

Inspection reads stored revisions; it does not re-render today's version of a chart. Revision `0` selects the latest stored record, including a retained uninstall. `ListReleasesAsync` lists deployed revisions and supports comma-separated exact `key=value` label selectors.

## Important lifecycle constraints

Use dry run for review. Install, upgrade, rollback, and retained uninstalls leave Secret-backed lifecycle evidence for later inspection; a default uninstall purges its history. Successful upgrades and rollbacks supersede the prior deployed revision. Once a lifecycle record has been constructed and persistence handling is active, a failure retains a failed revision; validation, chart loading or rendering, client/history initialization, and namespace-creation failures can occur earlier and leave no revision. Unsupported options fail before cluster mutation rather than being silently ignored.

Traditional HTTP repositories and local dependencies are supported. Full OCI authentication, provenance verification, and every Helm CLI switch are not `1.3.1` guarantees; check [Compatibility](../helm-compatibility.md) for the current boundary.

## Plugin names and storage boundaries

`HelmPluginManager` treats its configured plugin directory as a security boundary. Pass a single portable plugin name, not a path: names may contain ASCII letters, digits, dots, underscores, and hyphens, and must begin and end with a letter or digit. Absolute paths, directory separators, `.` and `..`, whitespace, and linked plugin directories are rejected by install, uninstall, and run operations.

For all public members, use the [generated Action API](../api/generated/action.md).
