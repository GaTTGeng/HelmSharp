# Release storage

`HelmSharp.Action` stores lifecycle revisions through `HelmSharp.Release`. The default store uses Kubernetes Secrets so a release can be inspected by name and revision after an operation completes.

## What a revision represents

A revision records lifecycle evidence: the release name and namespace, chart and values data, rendered manifest, revision number, status, and operation metadata. It is not a live inventory of cluster state.

A failed revision can contain the attempted manifest even when Kubernetes applied none or only some resources. Inspect it as evidence of the operation, not as a replacement for querying the cluster.

## Lifecycle behavior

| Operation | Stored result |
| --- | --- |
| Install or upgrade | Creates the next revision and records the resulting status. |
| Rollback | Creates a new revision that represents the rollback result. |
| Uninstall with `KeepHistory = true` | Retains an `uninstalled` revision. |
| Default uninstall | Purges release history after resource deletion. |

Use `StatusAsync`, `HistoryAsync`, `GetManifestAsync`, and `GetValuesAsync` to inspect stored revisions. These APIs read recorded data; they do not re-render the current chart.

## Coexistence with Helm

Secret-backed history follows Helm v3 release-storage conventions. Before sharing a release name with another tool, validate the behavior required by your chart and workflow against the [compatibility contract](../helm-compatibility.md). Do not assume that reading a revision proves the current cluster objects still match its manifest.

See [Install and upgrade releases](../guide/release-workflows.md) for the high-level workflow.
