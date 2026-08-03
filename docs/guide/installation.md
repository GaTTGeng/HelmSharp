# Install HelmSharp

HelmSharp targets `net8.0`, `net9.0`, and `net10.0`. Every package in an application should use the same HelmSharp version; the examples below use the current package release, `1.3.1`.

## Pick the smallest package for the job

| You need to… | Install | What it owns |
| --- | --- | --- |
| Load a chart, merge values, and render YAML | `HelmSharp.Chart` and `HelmSharp.Engine` | Chart files, values, and template rendering. |
| Run template, install, upgrade, rollback, or history operations | `HelmSharp.Action` | The high-level client and its dependent packages. |
| Apply YAML that is already rendered | `HelmSharp.Kube` | Kubernetes apply, delete, identity, and readiness helpers. |
| Search, pull, or index traditional HTTP chart repositories | `HelmSharp.Repo` | Repository configuration, cache, search, and pull operations. |

Most services that own a deployment start with `HelmSharp.Action`:

```powershell
dotnet add package HelmSharp.Action --version 1.3.1
```

A preview tool should depend on the smaller rendering pair instead:

```powershell
dotnet add package HelmSharp.Chart --version 1.3.1
dotnet add package HelmSharp.Engine --version 1.3.1
```

`HelmSharp.Action` already references the rendering, Kubernetes, release, repository, storage, registry, and post-renderer packages. Do not add those packages separately unless your code needs their lower-level APIs.

## What HelmSharp needs at runtime

Rendering needs a readable chart directory or `.tgz` archive. It does not invoke, bundle, or require the `helm` executable.

Cluster-changing operations need the same prerequisites as any Kubernetes .NET client: a reachable API server, credentials, and RBAC permissions for the resources and release Secrets they manage. Supply these through your application's `IHelmOptionsProvider` and request objects; do not make a web request choose arbitrary local kubeconfig paths.

## Verify the installation

Follow [Render a chart](first-render.md) for a program that reads a local chart and writes manifests to standard output. If the application will deploy, continue with [Install and upgrade releases](release-workflows.md) to put dry-run and result handling around that operation.

## Direct-use packages

These packages are usually extension points, not first choices for an application:

| Package | Use it directly when… |
| --- | --- |
| `HelmSharp.Release` | You need the release model or store independently of `HelmClient`. |
| `HelmSharp.Storage` | You are implementing a custom `IHelmReleaseStore`. |
| `HelmSharp.PostRenderer` | You are implementing a deterministic manifest transformation. |
| `HelmSharp.Registry` | You are integrating an experimental OCI registry client. |

Their [package pages](../packages/action.md) document the contracts and current limitations.
