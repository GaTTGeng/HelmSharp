# Architecture

HelmSharp is a managed .NET implementation of selected Helm behavior. It loads chart inputs, renders templates, and can drive release operations without starting the `helm` executable.

## Choose the layer that owns your operation

| Layer | Responsibility | Use it when |
| --- | --- | --- |
| `HelmSharp.Chart` | Load charts and merge values. | You need chart inputs as .NET objects. |
| `HelmSharp.Engine` | Render templates into manifest text. | Your application previews, validates, or commits YAML. |
| `HelmSharp.Action` | Coordinate rendering, hooks, Kubernetes changes, and release history. | Your service owns install, upgrade, rollback, or uninstall. |
| `HelmSharp.Kube` | Apply, delete, and wait for existing manifest text. | You already have YAML and do not need Helm lifecycle behavior. |
| `HelmSharp.Release` | Model and persist release revisions. | You need to inspect or extend release storage. |

## Data flow

A render-only application uses `Chart` and `Engine`: load a chart, construct effective values, configure release and capability inputs, then return YAML. No Kubernetes client or release record is required.

A lifecycle application enters through `HelmSharp.Action`. `HelmClient` uses the same chart and rendering layers, then runs hooks, applies resources through `Kube`, and records the result through `Release` storage. This is the layer to choose for a deployment service.

## Why HelmSharp does not start Helm CLI

Running in-process gives an application typed request objects, direct error handling, controllable storage and HTTP behavior, and no dependency on a local executable. It does not make HelmSharp a command-line emulator. Check the [compatibility contract](../helm-compatibility.md) before depending on a Helm edge case.

## Next steps

- [Choose a package](../api-overview.md) for a task-to-API decision.
- [Build a preview endpoint](../scenarios/aspnet-core-preview.md) for a render-only integration.
- [Build a deployment service](../scenarios/deployment-service.md) when the application owns cluster changes.
