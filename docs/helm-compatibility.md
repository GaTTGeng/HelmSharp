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
| Kubernetes apply, delete, wait | Supported (selective readiness) | Typed endpoints and cluster discovery cover apply and delete; upgrades use three-way ownership merge; 12 resource kinds have explicit Helm-aligned readiness predicates while other kinds pass through after apply. Server-side apply and operator-specific CR readiness remain unsupported. |
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

For cleanup failure handling and lifecycle options, see [release workflows](guide/release-workflows.md); for direct calls and discovery errors, see [Kubernetes operations](guide/kubernetes-operations.md); for a compiled end-to-end configuration, see [configure waits, Jobs, and cleanup](examples/kubernetes-wait-and-cleanup.md).

### Kubernetes readiness, wait, timeout, and cancellation contract

`KubernetesResourceWaiter` implements deterministic polling aligned with Helm's `ReadyChecker` ([#148](https://github.com/GaTTGeng/HelmSharp/issues/148), [#233](https://github.com/GaTTGeng/HelmSharp/issues/233), [#234](https://github.com/GaTTGeng/HelmSharp/issues/234), and [#259](https://github.com/GaTTGeng/HelmSharp/issues/259)). As delivered in [#259](https://github.com/GaTTGeng/HelmSharp/issues/259), a Pod in `CrashLoopBackOff` remains pending rather than failing immediately.

#### Waited resource kinds

| API version and kind | Ready condition | Pending / terminal failure boundary | Evidence |
| --- | --- | --- | --- |
| `apps/v1` `Deployment` | `spec.paused == true`, or `observedGeneration == generation`, a controller-owned ReplicaSet matching `spec.template` (ignoring `pod-template-hash`) has observed its generation, and `readyReplicas >= desired - maxUnavailable` (`Recreate` requires all replicas; `RollingUpdate` defaults to 25% surge up / 25% unavailable down, forcing 1 unavailable if both resolve to 0). | Stale generation, missing ReplicaSet, insufficient ready replicas, and `ProgressDeadlineExceeded` stay pending until timeout; no immediate terminal failure. | [Deployment parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L62-L85), [kind readiness test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KindLifecycleTests.cs#L318-L355) |
| `apps/v1` `StatefulSet` | `observedGeneration == generation` and either `updateStrategy.type != "RollingUpdate"` (for example `OnDelete`), or `updatedReplicas >= max(0, desired - partition)`, `readyReplicas == desired`, and (when `partition == 0`) `currentRevision == updateRevision`. | Stale generation, incomplete partition rollout, unready replicas, or mismatched revisions stay pending until timeout. | [StatefulSet parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L96-L103) |
| `apps/v1` `DaemonSet` | `observedGeneration == generation` and either `updateStrategy.type != "RollingUpdate"`, or `updatedNumberScheduled == desiredNumberScheduled` and `numberReady >= desired - maxUnavailable` (default 1 when `desired > 0`, percentages rounded up, unparseable value falls back to `desired`). | Stale generation, unscheduled updates, or insufficient ready pods stay pending until timeout. | [DaemonSet parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L86-L95) |
| `apps/v1` `ReplicaSet` | `observedGeneration == generation` and every Pod matching `spec.selector` reports `Ready=True`. | Stale generation or any unready selected Pod stays pending until timeout. | [ReplicaSet parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L104-L109) |
| `v1` `ReplicationController` | `observedGeneration == generation` and every Pod matching `spec.selector` reports `Ready=True`. | Stale generation or any unready selected Pod stays pending until timeout. | [ReplicationController parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L110-L115) |
| `v1` `Pod` | Condition `Ready=True`. | **Terminal failure**: `status.phase == "Failed"`. **Pending ([#259](https://github.com/GaTTGeng/HelmSharp/issues/259))**: `CrashLoopBackOff` and other non-Ready states keep polling until `Ready=True` or timeout; timeout diagnostics preserve `Pod in CrashLoopBackOff`. | [Pod parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L116-L119), [CrashLoopBackOff tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L114-L144) |
| `batch/v1` `Job` | Skipped immediately (without querying the API) when `WaitForJobs` is `false`. When `WaitForJobs` is `true`: ready on `Complete=True`, unset `spec.completions`, or `status.succeeded >= spec.completions`. | **Terminal failure** (when `WaitForJobs` is `true`): `status.failed > spec.backoffLimit` (default 6) or condition `Failed=True`. Otherwise pending until completions are met. | [Job parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L177-L196), [WaitForJobs tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L69-L111) |
| `v1` `PersistentVolumeClaim` | `status.phase == "Bound"`. | **Terminal failure**: `status.phase == "Lost"`. Other phases stay pending until timeout. | [PVC parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L120-L122) |
| `v1` `Service` | `ExternalName` is ready immediately; other types require non-empty `spec.clusterIP`; `LoadBalancer` also requires `spec.externalIPs` or `status.loadBalancer.ingress`. | Missing `clusterIP` or pending LoadBalancer ingress stays pending until timeout. | [Service parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L128-L141), [waiter Service test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L162-L192) |
| `v1` `Endpoints` | At least one subset contains one or more ready `addresses`. | Empty subsets or only `notReadyAddresses` stays pending until timeout. | [Endpoints parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L123-L124) |
| `apiextensions.k8s.io/v1` & `v1beta1` `CustomResourceDefinition` | Read through its declared API version; ready on `Established=True` or `NamesAccepted=False` (matching Helm so naming conflicts do not hang install). | Other conditions or absent conditions stay pending until timeout. | [CRD parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L146-L173), [beta CRD test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L195-L251) |
| `autoscaling/v2` `HorizontalPodAutoscaler` | Condition `ScalingActive=True`. | **Terminal failure**: `ScalingActive=False`. Missing conditions stay pending. `autoscaling/v1` HPA is not polled and passes through as ready immediately. | [HPA parity tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L198-L214) |
| All other kinds (`ConfigMap`, `Secret`, `ServiceAccount`, `Ingress`, `CronJob`, `autoscaling/v1` HPA, `PodDisruptionBudget`, `EndpointSlice`, RBAC, custom resource instances, unknown kinds) | Accepted as ready immediately once applied; never queried by `WaitForReadyAsync`. | No readiness signal is inferred for operator-managed custom resources; add application-level health checks where needed. | [Non-waitable skip tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L23-L66) |

#### Wait controls, hooks, timeout, cancellation, and cleanup boundaries

| Area | Current behavior and limitation | Evidence |
| --- | --- | --- |
| Wait strategy and API retries | `WaitForReadyAsync` polls every 3s; `WaitForDeletedAsync` and Pod hook completion poll every 1s. Helm's legacy status-watcher strategy is not implemented or exposed. A `404` during readiness waiting resets error counts and stays pending (`Resource not found yet`). Other read errors back off linearly (`min(30s, 3s * consecutiveErrors)`) and throw `KubernetesResourceOperationException` after more than 10 consecutive failures; `WaitForDeletedAsync` throws immediately on non-`404`/non-missing-kind API errors. | [Waiter polling and retry tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L254-L324), [deletion wait tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L327-L611) |
| Job and Pod hook waits | Hooks run sequentially by weight, then name/kind/path. Job hooks always wait for completion (`waitForJobs: true`). Pod hooks poll every 1s until `status.phase == "Succeeded"` or `"Failed"` (completion, not `Ready=True`). Other hook kinds are applied without a completion observer. `DisableHooks = true` skips hook execution and cleanup. | [Hook Job and Pod tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/HelmHookTests.cs#L520-L791), [Pod hook completion test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/HelmHookTests.cs#L948-L974), [kind hook tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KindLifecycleTests.cs#L318-L414) |
| Timeout controls | `TimeoutSeconds` must be `> 0` on `KubernetesResourceWaiter`, `HelmUpgradeInstallRequest`, and `HelmRollbackRequest` (zero/negative throws `ArgumentOutOfRangeException`); omitted uses `HelmExecutionOptions.TimeoutSeconds` (default 300s). `WaitForJobs = true` requires `Wait` or `Atomic` on install/upgrade (`Wait` on rollback). In `HelmClient`, one linked operation timeout covers apply, hooks, and waiting, so expiration throws `TimeoutException` (with last observed status) or `OperationCanceledException` (with caller token not canceled). On `UninstallAsync`, omitting `TimeoutSeconds` (`null`) skips the operation-wide token while hooks and `Wait = true` fall back to `HelmExecutionOptions.TimeoutSeconds`; a non-positive `TimeoutSeconds` also skips the operation-wide token and falls back for hooks, but throws `ArgumentOutOfRangeException` during `Wait = true` after resource deletion (before release-history persistence). On `TestAsync`, timeout marks the stalled hook `FAILED` and stops remaining hooks. | [Timeout validation and diagnostics tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L13-L20), [uninstall timeout test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L2934-L2977), [rollback timeout test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1083-L1137), [test-hook timeout test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1652-L1693), [kind wait-timeout test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KindLifecycleTests.cs#L472-L509) |
| Caller cancellation | The caller `CancellationToken` flows into every Kubernetes read and polling delay and propagates as `OperationCanceledException` rather than succeeding. On install/upgrade and rollback, cancellation before final persistence records a `failed` revision (and install/upgrade runs `Atomic`/`CleanupOnFail` recovery with `CancellationToken.None`). Once rollback finishes applying resources, its final `deployed` revision save completes independently of the operation token so history cannot retain two `deployed` revisions. | [Waiter cancellation tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L280-L295), [deletion cancellation test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L596-L611), [rollback cancellation tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1195-L1317) |
| Hook and deletion cleanup limits | Hook failure/cancellation finalization shares one bounded window clamped to 2–10s across the cleanup batch and continues past individual delete failures, attaching cleanup errors to `Exception.Data["HelmSharp.HookCleanupError"]` without replacing the root failure. CRD hooks are never deleted by policy. `WaitForDeletedAsync` treats `404`, removed discovered CRDs/APIs, and unsupported core kinds as absent; `helm.sh/resource-policy: keep` cannot stop Kubernetes namespace or owner cascading deletion. | [Hook finalization tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/HelmHookTests.cs#L793-L946), [uninstall wait test](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L2883-L2932), [removed-API deletion wait tests](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L467-L547) |

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
