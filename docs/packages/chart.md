# HelmSharp.Chart

## Package responsibility

`HelmSharp.Chart` loads chart directories and `.tgz` archives, exposes chart metadata, merges values, and provides YAML helpers.

## When to install

Install this package for any render-only integration:

```powershell
dotnet add package HelmSharp.Chart --version 1.1.1
```

::: warning Version availability
1.1.1 is the latest published package. The M2 lock-file, dependency-alias, and local-dependency behavior described below reflects the current `master` branch and is planned for 1.2.0.
:::

## Dependencies

This package depends on `YamlDotNet` and has no Kubernetes dependency.

## Main types

| Type | Use it for |
| --- | --- |
| `HelmChartLoader` | Load a chart from a directory or archive. |
| `HelmChart` | Inspect chart metadata, templates, files, CRDs, and subcharts. |
| `HelmValues` | Build merged values with Helm precedence. |
| `HelmYaml` | Serialize and deserialize YAML-compatible objects. |
| `HelmChartDependency` | Inspect dependency metadata from `Chart.yaml`. |
| `HelmChartLockEntry` | Inspect entries from `Chart.lock`. |

## Common combinations

Use `HelmChartLoader` with `HelmValues`, then pass both into `HelmTemplateRenderer` from `HelmSharp.Engine`.

Packaged dependencies under `charts/` are loaded as subcharts. When `Chart.lock` selects an exact version, the loader uses that identity to map multiple versions or aliases of the same chart. Alias-scoped defaults and overrides use the alias key. See [Chart Packaging and Repository Workflows](../guide/chart-distribution.md) for `Chart.yaml` examples.

## Current boundaries

This package does not render templates or mutate Kubernetes resources. It is intentionally the chart and values layer.
