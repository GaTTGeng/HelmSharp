# HelmSharp.Chart

`HelmSharp.Chart` owns chart inputs. It loads a directory or `.tgz` archive, exposes chart metadata and files, resolves packaged subcharts, and builds the values dictionary used by a renderer.

```powershell
dotnet add package HelmSharp.Chart --version 1.3.1
```

## Use this package for the input half of rendering

| Type | Role |
| --- | --- |
| `HelmChartLoader` | Load `Chart.yaml`, templates, files, CRDs, values, dependencies, and archives. |
| `HelmChart` | The loaded chart object passed to values and rendering APIs. |
| `HelmValues` | Merge defaults, values files, inline YAML, and set-style overrides. |
| `HelmYaml` | Read or write YAML-compatible values. |
| `HelmChartDependency` / `HelmChartLockEntry` | Inspect dependency and lock metadata. |

The package has no Kubernetes dependency and does not render templates. Pair it with `HelmSharp.Engine` for preview work, or install `HelmSharp.Action` for a full lifecycle.

## Dependency detail that affects values

Packaged charts under `charts/` are loaded as subcharts. A `Chart.lock` entry identifies the selected version when multiple aliases or versions exist. An alias changes the values key: a dependency named `redis` with alias `cache` receives values under `cache:`.

See [Values and overrides](../guide/values.md) for merge semantics and [Chart delivery](../guide/chart-distribution.md) for update/build behavior. The [generated Chart API](../api/generated/chart.md) lists all members.
