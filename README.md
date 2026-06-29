# HelmSharp

[![CI](https://github.com/GaTTGeng/HelmSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/GaTTGeng/HelmSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![NuGet Downloads](https://img.shields.io/nuget/dt/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![License](https://img.shields.io/github/license/GaTTGeng/HelmSharp.svg)](LICENSE)

[简体中文](README.zh-CN.md)

HelmSharp is a managed .NET library for rendering Helm-style charts and driving Kubernetes release workflows without invoking the `helm` executable. It is intended for applications that need Helm-like behavior inside a .NET process: template rendering, values merging, chart packaging, repository operations, and release lifecycle operations against Kubernetes.

The project is under active development. The API currently implements a practical Helm-compatible subset rather than a byte-for-byte replacement for Helm CLI.

Documentation site: [https://gattgeng.github.io/HelmSharp/](https://gattgeng.github.io/HelmSharp/)

## Packages

This repository is organized as several NuGet packages:

| Package | Purpose |
| --- | --- |
| [`HelmSharp.Action`](https://www.nuget.org/packages/HelmSharp.Action) | High-level Helm client API and release operations. |
| [`HelmSharp.Chart`](https://www.nuget.org/packages/HelmSharp.Chart) | Chart loading, values merging, YAML helpers, chart metadata. |
| [`HelmSharp.Engine`](https://www.nuget.org/packages/HelmSharp.Engine) | Helm-style template rendering. |
| [`HelmSharp.Kube`](https://www.nuget.org/packages/HelmSharp.Kube) | Kubernetes manifest apply/delete/wait helpers. |
| [`HelmSharp.Release`](https://www.nuget.org/packages/HelmSharp.Release) | Helm release storage records backed by Kubernetes Secrets. |
| [`HelmSharp.Repo`](https://www.nuget.org/packages/HelmSharp.Repo) | Chart repository, index, pull, and search helpers. |
| [`HelmSharp.Registry`](https://www.nuget.org/packages/HelmSharp.Registry) | Registry-related extension point package. |
| [`HelmSharp.Storage`](https://www.nuget.org/packages/HelmSharp.Storage) | Storage extension point package. |
| [`HelmSharp.PostRenderer`](https://www.nuget.org/packages/HelmSharp.PostRenderer) | Post-renderer extension point package. |

For most applications, start with `HelmSharp.Action`.

## Requirements

- .NET 8, .NET 9, and .NET 10 SDKs for building all target frameworks locally.
- A Kubernetes cluster and kubeconfig for release install, upgrade, rollback, uninstall, and status operations.
- No `helm` binary is required.
- Supported package target frameworks: `net8.0`, `net9.0`, and `net10.0`.
- .NET Framework is not supported unless a future release adds a `netstandard` target.

## Installation

```powershell
dotnet add package HelmSharp.Action
```

For lower-level use cases:

```powershell
dotnet add package HelmSharp.Chart
dotnet add package HelmSharp.Engine
```

## Quick Start

Render a local chart:

```csharp
using HelmSharp.Action;

var client = new HelmClient(new StaticHelmOptionsProvider());

var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart",
    SetValues = new Dictionary<string, string>
    {
        ["image.tag"] = "1.2.3",
        ["replicaCount"] = "2"
    }
});

Console.WriteLine(result.StandardOutput);

sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
{
    public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new HelmExecutionOptions
        {
            DefaultNamespace = "default",
            FieldManager = "helmsharp"
        });
}
```

More complete examples are available in [examples](examples/README.md).

Install or upgrade a release:

```csharp
var result = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart",
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300
});

if (result.ExitCode != 0)
{
    Console.Error.WriteLine(result.StandardError);
}
else
{
    Console.WriteLine(result.StandardOutput);
}
```

Run a dry run without mutating the cluster:

```csharp
await foreach (var line in client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart",
    DryRun = true
}))
{
    Console.WriteLine(line);
}
```

## Supported Capabilities

- Chart loading from directories and `.tgz` archives.
- `values.yaml`, inline values, `--set`, `--set-string`, `--set-json`, and `--set-file`-style value overrides.
- Helm-style template rendering for common control flow and functions.
- Chart package creation.
- Repository index generation and repository search/pull helpers.
- Managed Kubernetes apply/delete/wait operations for common Kubernetes resources.
- Release history stored in Kubernetes Secrets.
- Install, upgrade, uninstall, rollback, status, history, manifest, values, hooks, notes, and test-oriented APIs.

## Golden Test Results

HelmSharp's template engine is continuously validated against real-world, publicly-available Helm charts using golden tests. Each chart is rendered by both `helm template` (reference) and HelmSharp's managed renderer; outputs are compared document-by-document after normalization.

> **Last updated:** 2026-06-29 · **HelmSharp version:** 1.0.4 + Unreleased · **Helm version:** v3.12.3 · **Test framework:** net8.0, net9.0, net10.0

CI installs Helm v3.12.3, runs the normal Release test suite, then runs a dedicated Helm golden test step for fixture charts and real public charts. The real-chart step writes `GoldenReports/*.json` artifacts for reviewing per-chart coverage.

### Summary

| Chart | Version | Helm Docs | Templates | Passed | Failed | Per-Template Rate | Full Render | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **podinfo** | 6.14.0 | 5 | 21 | 21 | 0 | 100% | ✅ Success | **Partial** |
| **metrics-server** | 3.13.1 | 9 | 18 | 18 | 0 | 100% | ✅ Success | **Partial** |
| **external-dns** | 1.21.1 | 5 | 7 | 7 | 0 | 100% | ✅ Success | **Partial** |
| **ingress-nginx** | 4.12.1 | 19 | 42 | 40 | 2 | 95% | ❌ Fallback | **Partial** |
| **cert-manager** | 1.17.1 | 52 | 41 | 41 | 0 | 100% | ✅ Success | **Partial** |
| **Total** | — | **90** | **129** | **127** | **2** | **98%** | — | — |

### Per-Chart Breakdown

```
podinfo          █████████████████████  100%  (21/21 templates)
metrics-server   █████████████████████  100%  (18/18 templates)
external-dns     █████████████████████  100%  ( 7/ 7 templates)
ingress-nginx    ████████████████████░   95%  (40/42 templates)
cert-manager     █████████████████████  100%  (41/41 templates)
                 ─────────────────────
                 ████████████████████░   98%  (127/129 templates overall)
```

### Error Analysis

127 of 129 templates across 5 real-world charts render successfully. Four charts complete full-chart rendering; `ingress-nginx` currently falls back to per-template reporting because `controller-deployment.yaml` and `controller-role.yaml` hit `NotSupportedException` during managed rendering. Remaining differences are attributable to:

| Category | Impact | Affected Charts |
| --- | --- | --- |
| Unsupported renderer paths | Two templates are reported as per-template failures | ingress-nginx |
| Whitespace / document formatting | Minor rendering diffs in YAML indentation and blank lines | All 5 charts |
| Values evaluation edge cases | Some conditional branches produce different output | cert-manager, ingress-nginx |
| Printf / string formatting | Slight differences in formatted string output | metrics-server, external-dns |

**Recent parser fixes:** The two parser bugs that previously caused `NotSupportedException` across 7 templates have been resolved:
- **#51** — `SplitPipeline` now tracks parentheses, fixing `(empty .x)` and `($value \| quote \| len)` patterns.
- **#50** — `else if` chain reconstruction now correctly produces balanced template blocks, fixing C# string interpolation escaping (`{{- end }}` vs `{- end }`).

### Verdict Legend

| Verdict | Meaning |
| --- | --- |
| **Pass** | Byte-for-byte identical output after normalization (line endings, source comments). |
| **Partial** | Structurally compatible — same document count, or most individual templates render correctly while a few hit known parser gaps. |
| **Fail** | The renderer cannot produce output for any template in this chart. |

## Known Scope

HelmSharp is not a full Helm CLI clone. Some advanced Helm behaviors, edge-case template functions, plugins, provenance verification flows, OCI authentication flows, and uncommon Kubernetes resource types may need additional implementation. Contributions that add compatibility with focused tests are welcome.

## Documentation

- [Documentation site](https://gattgeng.github.io/HelmSharp/)
- [Getting started](docs/getting-started.md)
- [API overview](docs/api-overview.md)
- [Helm compatibility](docs/helm-compatibility.md)
- [Roadmap](docs/roadmap.md)
- [Support](SUPPORT.md)
- [GitHub releases](https://github.com/GaTTGeng/HelmSharp/releases)

## Build

```powershell
dotnet restore HelmSharp.sln
dotnet build HelmSharp.sln --configuration Release --no-restore
dotnet test HelmSharp.sln --configuration Release --no-build --no-restore
```

## Pack

```powershell
dotnet pack HelmSharp.sln --configuration Release --no-build --output artifacts/packages
```

The NuGet package metadata is defined in `src/Directory.Build.props`. The package README is packed from this file.

## Continuous Integration and Release

This repository includes GitHub Actions workflows:

- `.github/workflows/ci.yml` restores, builds, tests, packs, and uploads package artifacts on pushes and pull requests.
- The CI workflow installs Helm v3.12.3 and runs Helm CLI golden tests explicitly when Helm is available, uploading both `.trx` results and real-chart JSON reports.
- `.github/workflows/deploy-docs.yml` builds the VitePress documentation site and deploys it to GitHub Pages on pushes to `master`.
- `.github/workflows/release-nuget.yml` packs release packages and can publish them to NuGet.org.

NuGet.org publishing is handled by maintainers through the release workflow.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Security

Please report security issues privately. See [SECURITY.md](SECURITY.md).

## License

HelmSharp is licensed under the [MIT License](LICENSE).
