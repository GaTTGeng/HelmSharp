# HelmSharp

[![CI](https://github.com/GaTTGeng/HelmSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/GaTTGeng/HelmSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![NuGet Downloads](https://img.shields.io/nuget/dt/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![License](https://img.shields.io/github/license/GaTTGeng/HelmSharp.svg)](LICENSE)

[简体中文](README.zh-CN.md)

HelmSharp is a managed .NET library for rendering Helm-style charts and driving Kubernetes release workflows without invoking the `helm` executable. It is intended for applications that need Helm-like behavior inside a .NET process: template rendering, values merging, chart packaging, repository operations, and release lifecycle operations against Kubernetes.

The project is under active development. The API currently implements a practical Helm-compatible subset rather than a byte-for-byte replacement for Helm CLI.

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

> **Last updated:** 2026-06-22 · **Helm version:** v3.12.3 · **Test framework:** net10.0

### Summary

| Chart | Version | Helm Docs | Templates | Passed | Failed | Per-Template Rate | Full Render | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **podinfo** | 6.14.0 | 5 | 21 | 20 | 1 | 95.2% | ❌ Exception | **Partial** |
| **metrics-server** | 3.13.1 | 9 | 18 | 17 | 1 | 94.4% | ❌ Exception | **Partial** |
| **external-dns** | 1.21.1 | 5 | 7 | 6 | 1 | 85.7% | ❌ Exception | **Partial** |
| **ingress-nginx** | 4.12.1 | 19 | 42 | 41 | 1 | 97.6% | ❌ Exception | **Partial** |
| **cert-manager** | 1.17.1 | 52 | 41 | 38 | 3 | 92.7% | ❌ Exception | **Partial** |
| **Total** | — | **90** | **129** | **122** | **7** | **94.6%** | — | — |

### Per-Chart Breakdown

```
podinfo          ████████████████████░  95.2%  (20/21 templates)
metrics-server   ███████████████████░░  94.4%  (17/18 templates)
external-dns     █████████████████░░░░  85.7%  ( 6/ 7 templates)
ingress-nginx    ████████████████████░  97.6%  (41/42 templates)
cert-manager     ██████████████████░░░  92.7%  (38/41 templates)
                 ─────────────────────
                 ████████████████████░  94.6%  (122/129 templates overall)
```

### Error Analysis

The 7 failing templates across all charts are caused by two parser edge cases in the managed renderer:

| Error Type | Occurrences | Affected Charts | Root Cause |
| --- | --- | --- | --- |
| `Template block 'if' is missing an end marker` | 6 | podinfo, metrics-server, ingress-nginx, cert-manager | Nested `if`/`range`/`with` blocks inside `define` bodies where whitespace-trimming markers (`{{-` / `-}}`) interact with block-boundary detection |
| `Helm template function 'empty)' is not supported` | 1 | external-dns | Parenthesized sub-expression `(empty .x)` inside an `or` pipeline where the closing `)` is not stripped before function dispatch |

**Key insight:** When individual templates are rendered in isolation (without the full chart context), **94.6% of all templates across all 5 charts render successfully**. The full-chart render hits a parser exception early, preventing downstream templates from being evaluated — so the per-template pass rate represents the engine's true template-level coverage.

### Verdict Legend

| Verdict | Meaning |
| --- | --- |
| **Pass** | Byte-for-byte identical output after normalization (line endings, source comments). |
| **Partial** | Structurally compatible — same document count, or most individual templates render correctly while a few hit known parser gaps. |
| **Fail** | The renderer cannot produce output for any template in this chart. |

## Known Scope

HelmSharp is not a full Helm CLI clone. Some advanced Helm behaviors, edge-case template functions, plugins, provenance verification flows, OCI authentication flows, and uncommon Kubernetes resource types may need additional implementation. Contributions that add compatibility with focused tests are welcome.

## Documentation

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
- `.github/workflows/release-nuget.yml` packs release packages and can publish them to NuGet.org.

NuGet.org publishing is handled by maintainers through the release workflow.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Security

Please report security issues privately. See [SECURITY.md](SECURITY.md).

## License

HelmSharp is licensed under the [MIT License](LICENSE).
