# HelmSharp

[![CI](https://github.com/MattGRP/HelmSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/MattGRP/HelmSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![NuGet Downloads](https://img.shields.io/nuget/dt/HelmSharp.Action.svg)](https://www.nuget.org/packages/HelmSharp.Action)
[![License](https://img.shields.io/github/license/MattGRP/HelmSharp.svg)](LICENSE)

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

## Known Scope

HelmSharp is not a full Helm CLI clone. Some advanced Helm behaviors, edge-case template functions, plugins, provenance verification flows, OCI authentication flows, and uncommon Kubernetes resource types may need additional implementation. Contributions that add compatibility with focused tests are welcome.

## Documentation

- [Getting started](docs/getting-started.md)
- [API overview](docs/api-overview.md)
- [Helm compatibility](docs/helm-compatibility.md)
- [Roadmap](docs/roadmap.md)
- [Support](SUPPORT.md)
- [GitHub releases](https://github.com/MattGRP/HelmSharp/releases)

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
