# API Overview

HelmSharp is split into small packages so applications can depend on the layer they need.

| Package | Main area |
| --- | --- |
| [`HelmSharp.Action`](https://www.nuget.org/packages/HelmSharp.Action) | High-level client operations such as template, install, upgrade, uninstall, status, history, show, repo, and registry-oriented commands. |
| [`HelmSharp.Chart`](https://www.nuget.org/packages/HelmSharp.Chart) | Chart loading, `Chart.yaml` metadata, values merging, YAML helpers, dependencies, and chart archive handling. |
| [`HelmSharp.Engine`](https://www.nuget.org/packages/HelmSharp.Engine) | Helm-style template rendering and common template functions. |
| [`HelmSharp.Kube`](https://www.nuget.org/packages/HelmSharp.Kube) | Kubernetes apply, delete, and wait helpers used by release workflows. |
| [`HelmSharp.Release`](https://www.nuget.org/packages/HelmSharp.Release) | Kubernetes Secret-backed release records. |
| [`HelmSharp.Repo`](https://www.nuget.org/packages/HelmSharp.Repo) | Chart repository index, pull, and search helpers. |
| [`HelmSharp.PostRenderer`](https://www.nuget.org/packages/HelmSharp.PostRenderer) | Post-renderer extension point contracts. |
| [`HelmSharp.Registry`](https://www.nuget.org/packages/HelmSharp.Registry) | OCI registry extension point contracts. |
| [`HelmSharp.Storage`](https://www.nuget.org/packages/HelmSharp.Storage) | Release storage extension point contracts. |

The recommended entry point for applications is `HelmSharp.Action.HelmClient`. Use the lower-level packages for focused rendering, packaging, repository, or storage scenarios.
