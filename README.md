# HelmSharp

[![CI](https://github.com/GaTTGeng/HelmSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/GaTTGeng/HelmSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![NuGet Downloads](https://img.shields.io/nuget/dt/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![License](https://img.shields.io/github/license/GaTTGeng/HelmSharp.svg)](LICENSE)

[简体中文](README.zh-CN.md)

HelmSharp is a managed .NET library for rendering Helm-style charts and driving Kubernetes release workflows without invoking the `helm` executable. It is intended for applications that need Helm-like behavior inside a .NET process: template rendering, values merging, chart packaging, repository operations, and release lifecycle operations against Kubernetes.

The project is under active development. M1 (Helm template parity) is complete — the template engine achieves byte-for-byte identical output with `helm template` across 129 templates from five real-world charts. M2+ features expand into release lifecycle and Kubernetes operations.

Documentation site: [https://gattgeng.github.io/HelmSharp/](https://gattgeng.github.io/HelmSharp/)

The documentation now includes workflow guides, realistic integration examples, package-by-package guidance, and generated API reference pages for public types and members.

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

> **Last updated:** 2026-07-01 · **HelmSharp version:** 1.1.0 · **Helm version:** v3.12.3 · **Test framework:** net10.0

### Summary

| Chart | Version | Helm Docs | Templates | Passed | Failed | Per-Template Rate | Full Render | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **podinfo** | 6.14.0 | 5 | 21 | 21 | 0 | 100% | ✅ Success | **Pass** |
| **metrics-server** | 3.13.1 | 9 | 18 | 18 | 0 | 100% | ✅ Success | **Pass** |
| **external-dns** | 1.21.1 | 5 | 7 | 7 | 0 | 100% | ✅ Success | **Pass** |
| **ingress-nginx** | 4.12.1 | 19 | 42 | 42 | 0 | 100% | ✅ Success | **Pass** |
| **cert-manager** | 1.17.1 | 52 | 41 | 41 | 0 | 100% | ✅ Success | **Pass** |
| **Total** | — | **90** | **129** | **129** | **0** | **100%** | — | — |

### Per-Chart Breakdown

```
podinfo          █████████████████████  100%  (21/21 templates)
metrics-server   █████████████████████  100%  (18/18 templates)
external-dns     █████████████████████  100%  ( 7/ 7 templates)
ingress-nginx    █████████████████████  100%  (42/42 templates)
cert-manager     █████████████████████  100%  (41/41 templates)
                 ─────────────────────
                 █████████████████████  100%  (129/129 templates overall)
```

### Error Analysis

All 129 templates across 5 real-world charts render without parser exceptions and achieve **byte-for-byte identical output** with `helm template` after normalization. All five charts now carry the **Pass** verdict — no remaining content-level formatting diffs, no parser gaps, and no error categories.

Key parity milestones closed since the 1.0.3 release:

- **#109 / #111 / #113** — Block right-trim now preserves action-line indent matching Go `text/template` behavior; cert-manager golden test restored from 45/52 to 52/52 exact match.
- **#112** — `ParseDefine` now applies right-trim to define body, matching `ParseBlock` behavior.
- **#97** — Completed Sprig function parity for remaining gaps (`empty`, `keys`, `mergeOverwrite`, `mustRegexMatch`, `mustRegexReplaceAll`, etc.).
- **#102** — Resolved cert-manager remaining content diffs (YAML tag, octal values, merge keys, block scalars, comment trimming).
- **#96 / #99 / #108** — Resolved real-chart golden test content diffs to achieve Pass verdicts across all five charts.

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
- [Guide: installation and rendering](docs/guide/installation.md)
- [Examples: render preview API](docs/examples/render-preview-api.md)
- [Package guide: HelmSharp.Action](docs/packages/action.md)
- [Generated API reference](docs/api/index.md)
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
- `.github/workflows/deploy-docs.yml` builds the VitePress documentation site and deploys it to GitHub Pages on pushes to `master`.
- `.github/workflows/release-nuget.yml` packs release packages and can publish them to NuGet.org.

NuGet.org publishing is handled by maintainers through the release workflow.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Security

Please report security issues privately. See [SECURITY.md](SECURITY.md).

## License

HelmSharp is licensed under the [MIT License](LICENSE).
