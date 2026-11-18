# Helm Compatibility

HelmSharp is not trying to be a command-line emulator. It is trying to give .NET applications the Helm behavior they need when rendering charts and managing releases from inside a process.

Helm CLI v4.2.2 is the test oracle for new compatibility validation and is pinned in CI and release workflows. It is not required at runtime by consumers.

## Validation scope

Focused fixture charts and selected public charts are compared with `helm template` in CI. They exercise helpers, nested values, `.Files`, capabilities, and formatting patterns that small examples miss. This is regression coverage for those behaviors, not a universal compatibility certification. Validate your own chart when it relies on an uncommon Helm behavior.

For the exact template-function surface, use the maintained [template-function compatibility matrix](template-function-compatibility.md). It records the upstream versions, every Helm-visible function, and the explicit disposition for gaps.

## Compatibility contract

A behavior is treated as supported when it:

- is reachable through a documented managed API;
- has focused automated coverage;
- behaves consistently across `net8.0`, `net9.0`, and `net10.0`;
- matches Helm where the rendered output, release state, or failure behavior is observable by users.

Exact CLI colors, progress text, terminal formatting, and plugin execution are not compatibility goals unless they affect chart output or automation.

## Capability snapshot

| Area | Current level | What it means for users |
| --- | --- | --- |
| Chart loading from directories and `.tgz` archives | Supported | Safe starting point for render and packaging tools. Covered by fixture and public-chart tests. |
| Values files and `--set`-style overrides | Partial | Common flows work; edge-case coercion and list syntax still tracked by golden tests. |
| Helm-style template rendering | Supported | Common parsing, control flow, named templates, built-in objects, whitespace/trim markers, and selected Sprig functions have focused coverage. An unimplemented function fails with a path-aware rendering diagnostic. |
| Template control flow (`if`/`else if`/`else`/`range`/`with`) | Supported | All control-flow constructs render correctly against `helm template` output, validated by dedicated fixture charts and real-chart golden tests. |
| Named templates and helpers | Supported | Cross-template `define`/`template`/`include` calls are covered by dedicated fixtures and public charts with extensive helper usage. |
| Built-in objects (`.Release`, `.Chart`, `.Values`, `.Files`, `.Capabilities`, `.Template`) | Supported | All built-in objects are populated and render consistently with Helm CLI output. |
| Chart packaging and repositories | Partial | Useful APIs exist; archive and repository edge cases remain. |
| Install, upgrade, rollback, uninstall | Supported | Managed lifecycle flows persist deployed, superseded, failed, and retained-uninstall revisions; support operation-wide timeouts, atomic recovery, rollback reservations, and deterministic hooks. Unsupported Helm options fail before mutation. |
| Kubernetes apply, delete, wait | Partial | Typed operations cover common resources. Other API resources are discovered from the target cluster for apply/delete; readiness behavior remains selective. |
| Release history in Kubernetes Secrets | Supported | Release records can be persisted without Helm CLI. |
| OCI registry | Planned | Registry authentication and chart push/pull parity are not complete. |
| Chart provenance | Partial | OpenPGP signing and trusted-key verification are available; see [Chart provenance and signing](guide/chart-provenance.md) for requirements and limits. |

### Kubernetes apply and cleanup contract

This matrix describes current behavior. [#258](https://github.com/GaTTGeng/HelmSharp/issues/258) was delivered by [PR #270](https://github.com/GaTTGeng/HelmSharp/pull/270); the ownership behavior below is available subject to its baseline and list limits. See the [Action API](api/generated/action.md) and [Kube API](api/generated/kube.md) for the referenced options and methods.

| Area | Current behavior and boundary | Evidence |
| --- | --- | --- |
| Namespace and scope | An explicit manifest namespace wins over the default; cluster-scoped resources have no namespace. `CreateNamespace` defaults to true and creates a missing target namespace. | [Identity and namespace tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L481-L577) |
| Discovery | Common kinds use typed endpoints; other kinds use cluster discovery and its scope. Apply uses the declared API version and briefly retries registration lag. Delete can use another served version in the same group; unsupported core kinds and exhausted direct-delete discovery fail. | [Discovery tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L2739-L2908), [delete tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L3090-L3200) |
| Create and update | The applier creates absent objects and replaces existing ones. Direct `ApplyAsync` has no prior release baseline. Upgrades with a prior manifest use the three-way behavior below; new resources and reinstall after uninstall have no such baseline. | [Applier tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L578-L635), [merge tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ThreeWayMergeTests.cs), [lifecycle tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L902) |
| Apply modes | Lifecycle operations reject `HelmExecutionOptions.ServerSideApply = true` before mutation; a nonempty request `ServerSideApply`, `Force`, and `TakeOwnership` are also rejected. Field manager is sent for discovered create/replace paths, not typed resources; it does not enable server-side apply. | [Lifecycle validation test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1497), [applier tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L2739) |
| Delete and propagation | Direct deletion and release cleanup process manifests in reverse order. Direct deletion defaults to `Background`; uninstall's `DeletionPropagation` also supports `Foreground` and `Orphan`. Rollback and hook cleanup use background. An object 404 is success; other API errors stop deletion with resource identity. | [Delete tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L3062-L3358), [lifecycle tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1935) |
| Keep policy | Uninstall and rollback cleanup filter `helm.sh/resource-policy: keep`; direct `DeleteAsync` does not interpret it. The annotation cannot prevent Kubernetes cascading deletion when an owner or namespace is removed. | [Lifecycle tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L671-L767), [uninstall test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1756) |
| Hook cleanup | Policies are `before-hook-creation` (default), `hook-succeeded`, and `hook-failed`; cleanup waits for absence and uses background propagation. Successful hooks are cleaned in reverse after the batch; failures and cancellation trigger bounded finalization. CRD hooks are never deleted by policy. `DisableHooks` skips execution and cleanup. | [Hook tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/HelmHookTests.cs#L389-L669), [failure tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/HelmHookTests.cs#L760-L947), [disable test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1796) |

For cleanup failure handling and lifecycle options, see [release workflows](guide/release-workflows.md); for direct calls and discovery errors, see [Kubernetes operations](guide/kubernetes-operations.md). Readiness and timeout coverage remains tracked by [#315](https://github.com/GaTTGeng/HelmSharp/issues/315).

### Upgrade resource ownership

For resources present in the previously deployed release manifest, `HelmClient` upgrades compare that recorded manifest, the newly rendered chart manifest, and the live Kubernetes object. Live-only fields, live deletions, and live edits to fields unchanged by the chart are preserved. Chart changes take effect, and a chart-removed field is removed even when its live value was edited. Kubernetes-managed fields such as `resourceVersion`, `uid`, and `status` remain cluster-authoritative. Lists at known Kubernetes map-list paths and CRD paths declared with list-map keys merge item by item; other lists are atomic and follow chart output. Resources without a prior manifest entry, reinstall-after-uninstall, and direct `KubernetesManifestApplier.ApplyAsync` calls use the desired manifest for replacement and do not have a three-way ownership baseline. This behavior is covered through the install/upgrade lifecycle path; see the focused merge and lifecycle tests.

## Known boundaries

These are the areas to check before betting a production workflow on exact Helm behavior:

- obscure values coercion and edge-case list syntax;
- OCI authentication and registry flows;
- provenance interoperability outside the documented signing and verification workflows;
- Sprig functions or Go-template behaviors outside the implemented surface;
- readiness for uncommon Kubernetes resource kinds;
- safe replacement for Helm plugin execution.

Golden tests demonstrate only the behavior they exercise. New charts can surface unimplemented functions, parsing edge cases, or manifest differences, so compatibility reports should include a minimal chart and the equivalent Helm CLI command.

## How to report a gap

Open a compatibility issue with:

- Helm CLI and HelmSharp versions;
- a minimal chart and values input;
- the exact Helm command and output;
- the equivalent HelmSharp API call and output;
- whether the difference affects rendering, release state, or cluster mutation.

Small reproducible charts are more useful than screenshots or large private charts.

## Continuous validation

Golden test results are validated on every push and pull request through the [CI workflow](https://github.com/GaTTGeng/HelmSharp/blob/master/.github/workflows/ci.yml). The CI runner installs Helm CLI (`v4.2.2`) alongside the .NET SDKs and executes the full golden test suite, including both fixture-chart and real-chart comparisons. JSON reports are published as workflow artifacts for each run.

Kubernetes apply, wait, hook, rollback, uninstall, and release-Secret behavior is additionally proven against a disposable kind cluster. See the [kind lifecycle test lane](testing-kind-lifecycle.md) for coverage, how to trigger it, and how to interpret failure artifacts.
