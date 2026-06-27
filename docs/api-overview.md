# API Overview

Use the smallest layer that matches the problem you are solving. HelmSharp is split so render-only tools do not have to reference Kubernetes release workflow code.

## Which package should I start with?

| You want to... | Start with | Why |
| --- | --- | --- |
| Render a chart to YAML | `HelmSharp.Chart` + `HelmSharp.Engine` | Smallest path: load chart, build values, render manifests and NOTES. |
| Offer Helm-like commands from an app | `HelmSharp.Action` | One facade for template, install, upgrade, rollback, uninstall, status, package, repo, and registry-style calls. |
| Load and inspect charts | `HelmSharp.Chart` | Chart model, archive loading, values merging, dependency metadata, YAML helpers. |
| Apply rendered resources | `HelmSharp.Kube` | Resource identity, create/update/delete helpers, wait behavior. |
| Store release history | `HelmSharp.Release` | Helm-style release records backed by Kubernetes Secrets. |
| Work with chart repositories | `HelmSharp.Repo` | Index, pull, and repository helper behavior. |

## Render-only path

```csharp
using HelmSharp.Chart;
using HelmSharp.Engine;

var chart = await HelmChartLoader.LoadAsync("/charts/my-chart", ct);
var values = await HelmValues.BuildAsync(chart, null, null, null, null, null, null, ct);
var renderer = new HelmTemplateRenderer(chart, "demo", "default", values);

var manifests = renderer.Render();
var notes = renderer.RenderNotes();
```

This is the right path for preview APIs, validation systems, GitOps generators, and tools that never mutate a cluster.

## Command-like path

`HelmClient` returns `CommandResult` for most operations. That shape is useful when your product already thinks in commands, stdout/stderr, exit codes, or dry-run output.

```csharp
using HelmSharp.Action;

var client = new HelmClient(optionsProvider);

var template = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "/charts/my-chart"
});

var dryRun = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = "/charts/my-chart",
    DryRun = true
});
```

## Request objects worth knowing

| Request | Use it for |
| --- | --- |
| `HelmTemplateRequest` | Render manifests without applying them. |
| `HelmUpgradeInstallRequest` | Install or upgrade a release, including dry runs. |
| `HelmUninstallRequest` | Delete release resources and update release history. |
| `HelmRollbackRequest` | Move a release back to an earlier revision. |
| `HelmPackageRequest` | Create a chart archive. |
| `HelmPullRequest` | Pull charts from repository-oriented sources. |

Common render fields include `ReleaseName`, `Namespace`, `Chart`, `ValuesFile`, `ValuesFiles`, `ValuesContent`, `SetValues`, `SetStringValues`, `SetJsonValues`, `SetFileValues`, `IncludeCRDs`, `ShowNotes`, `KubeVersion`, and `ApiVersions`.

## Options provider

`IHelmOptionsProvider` is intentionally outside individual requests. It lets an application centralize defaults such as field manager, namespace policy, kubeconfig selection, and tenant-specific settings.

For a production service, implement it from configuration or request context. For a small tool, a static provider is enough and can stay hidden near program startup.

## Error handling

High-level operations report failures through `CommandResult.ExitCode` and `CommandResult.StandardError`. Lower-level rendering APIs throw ordinary .NET exceptions for parse or compatibility failures.

When diagnosing a chart difference, capture the chart path, values inputs, HelmSharp output, Helm CLI output, HelmSharp version, and Helm CLI version. That gives compatibility work a reproducible target.
