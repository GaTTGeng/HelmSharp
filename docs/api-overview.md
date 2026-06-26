# API Overview

HelmSharp is split into small packages so applications can depend on only the layer they need. Start with `HelmSharp.Action` for command-like release workflows, and move down the stack when you need focused rendering, chart, repository, or storage behavior.

## Package Map

| Package | Main area | Typical use |
| --- | --- | --- |
| [`HelmSharp.Action`](https://www.nuget.org/packages/HelmSharp.Action) | High-level client operations | Template, install, upgrade, uninstall, rollback, status, history, get, show, package, repo, and registry-oriented calls. |
| [`HelmSharp.Chart`](https://www.nuget.org/packages/HelmSharp.Chart) | Chart model and values | Load chart directories or archives, read `Chart.yaml`, merge values, inspect dependencies, and work with YAML. |
| [`HelmSharp.Engine`](https://www.nuget.org/packages/HelmSharp.Engine) | Template rendering | Render Helm-style templates and NOTES output with common built-in objects and functions. |
| [`HelmSharp.Kube`](https://www.nuget.org/packages/HelmSharp.Kube) | Kubernetes operations | Apply, delete, identify, and wait for Kubernetes resources used by release workflows. |
| [`HelmSharp.Release`](https://www.nuget.org/packages/HelmSharp.Release) | Release records | Persist Helm-style release history in Kubernetes Secrets. |
| [`HelmSharp.Repo`](https://www.nuget.org/packages/HelmSharp.Repo) | Chart repositories | Work with repository indexes, chart pull, and search helpers. |
| [`HelmSharp.PostRenderer`](https://www.nuget.org/packages/HelmSharp.PostRenderer) | Post-rendering | Extension point contracts for manifest transformations. |
| [`HelmSharp.Registry`](https://www.nuget.org/packages/HelmSharp.Registry) | OCI registries | Extension point contracts for registry-oriented behavior. |
| [`HelmSharp.Storage`](https://www.nuget.org/packages/HelmSharp.Storage) | Storage | Extension point contracts for release storage. |

## High-Level Client

`HelmClient` is the main facade. It returns `CommandResult` for most operations so applications can handle `ExitCode`, `StandardOutput`, and `StandardError` consistently.

```csharp
using HelmSharp.Action;

var client = new HelmClient(new StaticHelmOptionsProvider());

var template = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart"
});

var release = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = @"C:\charts\my-chart",
    DryRun = true
});
```

The client currently covers:

| Area | Representative APIs |
| --- | --- |
| Release lifecycle | `UpgradeInstallAsync`, `UpgradeInstallStreamAsync`, `UninstallAsync`, `RollbackAsync`, `ListReleasesAsync` |
| Release inspection | `StatusAsync`, `HistoryAsync`, `GetValuesAsync`, `GetManifestAsync`, `GetNotesAsync`, `GetHooksAsync`, `GetAllAsync` |
| Rendering | `TemplateAsync`, `TemplateWithNotesAsync`, `DiffAsync` |
| Chart operations | `LintAsync`, `Show*Async`, `PackageAsync`, `CreateAsync`, `PullAsync`, `Dependency*Async` |
| Repository operations | `RepoAddAsync`, `RepoRemoveAsync`, `RepoListAsync`, `RepoIndexAsync`, `RepoUpdateAsync`, `SearchRepoAsync` |
| Registry operations | `RegistryLoginAsync`, `RegistryLogoutAsync`, `PushAsync` |

## Request Objects

`HelmTemplateRequest` describes render-only behavior. Common properties include:

| Property | Purpose |
| --- | --- |
| `ReleaseName` | Release name exposed to templates through `.Release.Name`. |
| `Chart` | Chart directory, `.tgz`, or `.tar.gz` path. |
| `Namespace` | Namespace exposed to templates and used by release operations. |
| `ValuesFile` / `ValuesFiles` | One or more values files. Later files override earlier files. |
| `ValuesContent` | Inline YAML values content. |
| `SetValues`, `SetStringValues`, `SetJsonValues`, `SetFileValues` | Helm-style command-line values overrides. |
| `IncludeCRDs`, `ShowNotes`, `KubeVersion`, `ApiVersions` | Rendering options for CRDs, NOTES, and capabilities. |

`HelmUpgradeInstallRequest` extends the workflow surface with properties such as `DryRun`, `CreateNamespace`, `Wait`, `WaitForJobs`, `TimeoutSeconds`, `Atomic`, `CleanupOnFail`, `ReuseValues`, `ResetValues`, `SkipCRDs`, `DisableHooks`, `KubeConfigPath`, and `KubeConfigContent`.

## Options Provider

`HelmClient` receives an `IHelmOptionsProvider`. A provider centralizes defaults such as namespace, field manager, and environment-specific settings:

```csharp
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

Applications can implement this interface with configuration, dependency injection, tenant settings, or per-request context.

## Lower-Level Rendering

When you only need rendering, use the chart and engine packages directly:

```csharp
using HelmSharp.Chart;
using HelmSharp.Engine;

var chart = await HelmChartLoader.LoadAsync(@"C:\charts\my-chart", CancellationToken.None);
var values = await HelmValues.BuildAsync(chart, null, null, null, null, null, null, CancellationToken.None);
var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);

var manifest = renderer.Render();
var notes = renderer.RenderNotes();
```

This keeps Kubernetes and release storage out of the application path.

## Error Handling

High-level operations report failures through `CommandResult.ExitCode` and `CommandResult.StandardError`. Template parse failures are surfaced with template context where possible so callers can diagnose chart-specific compatibility gaps.

For compatibility-sensitive behavior, include the chart, values, HelmSharp version, Helm CLI version, and the expected Helm output when opening an issue.
