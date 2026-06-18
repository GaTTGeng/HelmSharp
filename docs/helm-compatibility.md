# Helm Compatibility

HelmSharp provides a managed, Helm-compatible SDK for .NET. It aims to match user-visible Helm behavior where that behavior matters to rendering charts and managing Kubernetes releases, but it is not a byte-for-byte CLI replacement.

> No Helm executable is required by consumers. The Helm CLI may be used in the test suite only as a compatibility reference.

See the [roadmap](roadmap.md) for delivery order and the [GitHub milestones](https://github.com/GaTTGeng/HelmSharp/milestones) for live progress.

## Compatibility Contract

HelmSharp considers a behavior supported when it:

- works through a documented managed API;
- is covered by a focused automated test;
- behaves consistently across `net8.0`, `net9.0`, and `net10.0`;
- matches Helm semantics for meaningful output, state, and failure behavior.

Exact CLI wording, color, spacing, and plugin execution are not compatibility goals unless they affect chart output or automation.

## Capability Snapshot

| Area | Current level | Primary package |
| --- | --- | --- |
| Chart loading from directories and `.tgz` archives | **Supported** | `HelmSharp.Chart` |
| Values files and `--set`-style overrides | **Partial** | `HelmSharp.Chart` |
| Helm-style template rendering | **Partial** | `HelmSharp.Engine` |
| Chart packaging and repository operations | **Partial** | `HelmSharp.Action`, `HelmSharp.Repo` |
| Install, upgrade, rollback, and uninstall workflows | **Partial** | `HelmSharp.Action` |
| Kubernetes apply, delete, and wait helpers | **Partial** | `HelmSharp.Kube` |
| Release history backed by Kubernetes Secrets | **Supported** | `HelmSharp.Release` |
| OCI registry and provenance workflows | **Planned** | `HelmSharp.Registry` |

## Status Legend

| Status | Meaning |
| --- | --- |
| **Supported** | Covered for common production use with no known major semantic gap. |
| **Partial** | Useful implementation exists, but known Helm edge cases remain. |
| **Planned** | API surface may exist, but behavior is incomplete or not production-ready. |
| **Research** | The correct SDK design must be validated before implementation. |

## Command and Behavior Matrix

| Helm area | HelmSharp API | Status | Track | Remaining work |
| --- | --- | --- | --- | --- |
| `helm template` | `TemplateAsync`, `HelmTemplateRenderer` | **Partial** | M1 | Golden comparisons, built-in objects, functions, whitespace, and errors. |
| Values merging and `--set*` | `HelmValues` | **Partial** | M1 | Precedence, type coercion, list syntax, and edge cases. |
| Subcharts and dependencies | `HelmSharp.Chart` | **Partial** | M1/M2 | Value scope, globals, conditions, tags, and dependency workflows. |
| `helm lint` | `LintAsync` | **Partial** | M1/M2 | Expand rule and failure parity. |
| `helm package` | `PackageAsync` | **Partial** | M2 | Archive layout, metadata, and dependency cases. |
| `helm repo index` | `RepoIndexAsync`, `HelmSharp.Repo` | **Partial** | M2 | Merge behavior and index edge cases. |
| `helm pull` | `PullAsync`, `HelmSharp.Repo` | **Partial** | M2/M5 | Authentication, provenance, and OCI cases. |
| `helm dependency update/build/list` | `Dependency*Async` | **Planned** | M2 | Lock files, repository resolution, and archive placement. |
| `helm install` / `upgrade` | `UpgradeInstallAsync` | **Partial** | M3/M4 | State transitions, hooks, waits, failures, and rollback behavior. |
| `helm rollback` | `RollbackAsync` | **Partial** | M3 | Revision transitions, cleanup, and failure recovery. |
| `helm uninstall` | `UninstallAsync` | **Partial** | M3/M4 | Resource deletion, hooks, and retained history. |
| `helm status` / `history` | `StatusAsync`, `HistoryAsync` | **Partial** | M3 | Revision selection and state reporting. |
| `helm get` | `Get*Async` | **Partial** | M3 | Output shape, revision behavior, hooks, and notes. |
| Hook execution | `HelmSharp.Action` hook pipeline | **Partial** | M3/M4 | Ordering, waits, failure handling, and delete policies. |
| Kubernetes apply/delete/wait | `HelmSharp.Kube` | **Partial** | M4 | Readiness by kind, Jobs, namespaces, and deletion semantics. |
| Registry login/logout and OCI pull/push | `Registry*`, `HelmSharp.Registry` | **Planned** | M5 | Secure credential handling and complete OCI flows. |
| Provenance verification | `HelmProvenance` | **Planned** | M5 | Signing and Helm-compatible verification. |
| Helm plugins | `HelmPluginManager` | **Research** | M6 | Define a safe managed extension model instead of executing CLI plugins. |

## Known Boundaries

- Full Sprig function parity is still in progress.
- OCI authentication and registry operations are extension points rather than complete workflows.
- Provenance verification is not yet Helm-compatible end to end.
- Readiness behavior for less common Kubernetes resource kinds needs broader coverage.
- Helm CLI plugins will not be loaded or executed blindly inside consumer processes.

## Verification Approach

Compatibility work should use the smallest useful test:

1. Unit tests for parsing, values, template functions, and deterministic helpers.
2. Golden tests that compare normalized HelmSharp output with `helm template`.
3. Kubernetes integration tests for apply, wait, hooks, and release state behavior.

Normalization must remove only non-semantic differences, such as line endings or source comments. It must not hide YAML, ordering, whitespace, or value differences that consumers can observe.

## Reporting a Gap

[Open a compatibility issue](https://github.com/GaTTGeng/HelmSharp/issues/new) and include:

- Helm CLI and HelmSharp versions;
- a minimal chart, template, and values file;
- the exact Helm command and output;
- the equivalent HelmSharp API call and output;
- the expected behavior and likely milestone.
