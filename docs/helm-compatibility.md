# Helm Compatibility

HelmSharp aims to implement a practical Helm-compatible subset for .NET applications. It is not a byte-for-byte replacement for the Helm CLI.

## Supported areas

| Area | Status |
| --- | --- |
| Chart loading from directories and `.tgz` archives | Supported |
| `values.yaml` and `--set`-style overrides | Supported |
| Common Helm template control flow and functions | Partially supported |
| Chart package creation | Supported |
| Repository index, pull, and search helpers | Supported |
| Kubernetes apply/delete/wait helpers | Supported for common resource workflows |
| Release history in Kubernetes Secrets | Supported |
| Install, upgrade, uninstall, rollback, status, history, manifest, values, hooks, notes | Supported through managed APIs |

## Known gaps

- Full Sprig and Helm template function parity is still in progress.
- Plugin execution is intentionally not implemented as Helm CLI plugin loading.
- OCI authentication and registry flows are extension points first and will need focused parity work.
- Provenance verification is not complete Helm CLI parity.
- Less common Kubernetes resource wait semantics may need additional implementation.

Use focused tests when adding compatibility so behavior can be compared with Helm CLI output.

## Compatibility Milestones

GitHub milestones group parity work by user-visible Helm behavior:

| Milestone | Scope |
| --- | --- |
| [M1: Helm Template Parity](https://github.com/MattGRP/HelmSharp/milestone/1) | `helm template`, values merge behavior, built-in template objects, common functions, subcharts, dependencies, and CLI output comparison tests. |
| [M2: Chart Packaging and Repository Parity](https://github.com/MattGRP/HelmSharp/milestone/2) | `helm package`, `helm repo index`, `helm pull`, dependency lock/update/build, and chart archive layout. |
| [M3: Release Lifecycle Parity](https://github.com/MattGRP/HelmSharp/milestone/6) | install, upgrade, rollback, uninstall, status, history, manifest, values, hooks, notes, and release Secret state transitions. |
| [M4: Kubernetes Apply and Wait Semantics](https://github.com/MattGRP/HelmSharp/milestone/5) | resource identity, namespace handling, apply/delete behavior, wait semantics, Jobs, hook cleanup policies, and workload readiness. |
| [M5: OCI and Provenance](https://github.com/MattGRP/HelmSharp/milestone/4) | OCI registry login/logout/pull/push, chart signing, provenance verification, and authentication flows. |
| [M6: Public SDK Hardening](https://github.com/MattGRP/HelmSharp/milestone/3) | XML docs, API review, examples, docs site readiness, analyzer warnings, nullable cleanup, and compatibility matrix. |
| [M7: Compatibility Expansion Research](https://github.com/MattGRP/HelmSharp/milestone/7) | `netstandard` and .NET Framework feasibility, dependency constraints, multi-targeting costs, and consumer compatibility requirements. |

## Command Parity Matrix

Status values:

- Supported: implemented for common production use.
- Partial: implemented for common cases, with known parity gaps.
- Planned: not implemented or only present as an extension point.
- Research: needs design validation before implementation.

| Helm CLI area | HelmSharp API or package | Status | Milestone | Notes |
| --- | --- | --- | --- | --- |
| `helm template` | `HelmSharp.Action.TemplateAsync`, `HelmSharp.Engine` | Partial | M1 | Rendering, values, and common functions exist; deeper Helm/Sprig parity needs comparison tests. |
| Values merging, `--set`, `--set-string`, `--set-json`, `--set-file` | `HelmSharp.Chart.HelmValues` | Partial | M1 | Common override forms exist; edge cases should be tested against Helm CLI. |
| Chart dependencies and subcharts | `HelmSharp.Chart` | Partial | M1/M2 | Loader handles subcharts; dependency update/build parity needs focused work. |
| `helm lint` | `HelmSharp.Action.LintAsync` | Partial | M1/M2 | Basic checks exist; Helm lint rule parity should be expanded. |
| `helm package` | `HelmSharp.Action.PackageAsync` | Partial | M2 | Package creation exists; archive metadata and dependency handling need parity tests. |
| `helm repo index` | `HelmSharp.Repo`, `HelmSharp.Action.RepoIndexAsync` | Partial | M2 | Index generation exists; merge and edge-case behavior should be compared with Helm. |
| `helm pull` | `HelmSharp.Repo`, `HelmSharp.Action.PullAsync` | Partial | M2 | Repository pull exists; auth, provenance, and OCI cases are separate work. |
| `helm dependency update/build/list` | `HelmSharp.Action.Dependency*` | Planned | M2 | APIs exist, but full Helm dependency behavior should be hardened. |
| `helm install` / `helm upgrade --install` | `HelmSharp.Action.UpgradeInstallAsync` | Partial | M3/M4 | Managed release workflow exists; state transitions, hooks, waits, and rollback semantics need parity tests. |
| `helm upgrade` | `HelmSharp.Action.UpgradeInstallAsync` | Partial | M3/M4 | Includes install-or-upgrade flow; upgrade-specific behavior should be split into parity cases. |
| `helm rollback` | `HelmSharp.Action.RollbackAsync` | Partial | M3 | Release history rollback exists; cleanup and status transitions need coverage. |
| `helm uninstall` | `HelmSharp.Action.UninstallAsync` | Partial | M3/M4 | Managed uninstall exists; resource deletion and history behavior need comparison tests. |
| `helm status` | `HelmSharp.Action.StatusAsync` | Partial | M3 | Status output exists, but CLI formatting parity is not the primary SDK goal. |
| `helm history` | `HelmSharp.Action.HistoryAsync` | Partial | M3 | Kubernetes Secret-backed history exists. |
| `helm get manifest/values/hooks/notes/all` | `HelmSharp.Action.Get*Async` | Partial | M3 | APIs exist; output shape and revision behavior need parity cases. |
| Hook execution and delete policies | `HelmSharp.Action.HelmHookExecutor` | Partial | M3/M4 | Common hook annotations are parsed; wait and cleanup semantics need hardening. |
| Kubernetes apply/delete/wait | `HelmSharp.Kube` | Partial | M4 | Common resource workflows exist; readiness coverage should expand by resource kind. |
| `helm registry login/logout` | `HelmSharp.Action.Registry*`, `HelmSharp.Registry` | Planned | M5 | Extension points exist; full OCI auth flow is future work. |
| OCI chart pull/push | `HelmSharp.Registry` | Planned | M5 | Needs implementation and auth coverage. |
| Provenance verification | `HelmSharp.Action.HelmProvenance` | Planned | M5 | Not complete Helm CLI parity. |
| Plugins | `HelmSharp.Action.HelmPluginManager` | Research | M6 | SDK should not blindly execute Helm CLI plugins; define safe extension model first. |

## Issue Filing Guidance

When opening a parity issue, include:

- Helm CLI version and HelmSharp version.
- Minimal chart, template, and values needed to reproduce the behavior.
- Exact Helm CLI command and output.
- HelmSharp API call and output.
- Expected milestone from the table above.
