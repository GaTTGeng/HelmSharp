# Configure Kubernetes waits, Jobs, timeouts, and cleanup

Use public `HelmExecutionOptions`, `HelmUpgradeInstallRequest`, and `HelmUninstallRequest` settings together so namespace creation, upgrade ownership, Job completion, operation timeouts, caller cancellation, and cleanup stay explicit.

## Pin execution defaults

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#deployment-options-provider{csharp}

`ServerSideApply = false` keeps the supported client-side apply path (lifecycle operations reject `ServerSideApply = true` before mutation). On upgrades with a prior deployed manifest, `HelmClient` performs a three-way merge that preserves live-only fields while applying chart updates. `FieldManager` is forwarded on dynamically discovered create/replace paths such as custom resources.

## Deploy with Job waits, timeout, cancellation, and recovery

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#kubernetes-wait-and-cleanup{csharp}

- **Namespace**: `Namespace` sets the default namespace for namespaced manifests without `metadata.namespace`, while `CreateNamespace = true` creates it if absent. Explicit manifest namespaces still win, and cluster-scoped resources remain cluster-scoped.
- **Waits and Jobs**: `Wait = true` polls the 12 supported readiness kinds on a 3-second cadence; other kinds (such as `ConfigMap`, `Secret`, `Ingress`, `CronJob`, `autoscaling/v1` HPA, and custom resource instances) are accepted once applied without readiness polling. `WaitForJobs = true` additionally waits for `batch/v1` Jobs and requires `Wait = true` or `Atomic = true`.
- **CrashLoopBackOff status ([#259](https://github.com/GaTTGeng/HelmSharp/issues/259))**: A Pod reporting `CrashLoopBackOff` stays pending while polling for `Ready=True`. If it recovers before the deadline, the wait succeeds; if not, the wait times out with the last observed status (`Pod in CrashLoopBackOff`). Only a `Failed` Pod phase is a terminal Pod failure.
- **Timeout vs. caller cancellation**: One positive `TimeoutSeconds` value covers chart loading, hooks, apply, and readiness waiting. Caller cancellation always propagates as `OperationCanceledException`. An elapsed timeout surfaces as `TimeoutException` (from the waiter deadline) or `OperationCanceledException` with `cancellationToken.IsCancellationRequested == false` (when the linked operation timeout expires during an API read or poll delay).
- **Failure recovery**: `Atomic = true` and `CleanupOnFail = true` record a `failed` revision and run recovery before rethrowing. On a failed upgrade with a prior deployed revision, `CleanupOnFail` deletes resources introduced only by the failed attempt and `Atomic` also restores the previous deployed manifest. On a failed initial install, both delete the attempted manifest.

## Uninstall with foreground propagation and deletion wait

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#kubernetes-safe-cleanup{csharp}

`UninstallAsync` deletes release resources in reverse manifest order, honors `helm.sh/resource-policy: keep` for direct delete requests, and with `Wait = true` polls through `KubernetesResourceWaiter.WaitForDeletedAsync` until every deleted resource is observed absent. Setting `KeepHistory = true` records an `uninstalled` revision instead of purging release history.

For the full per-kind readiness predicates, hook wait and cleanup bounds, and test evidence, see the [Kubernetes compatibility contract](../helm-compatibility.md#kubernetes-readiness-wait-timeout-and-cancellation-contract), the [Action API](../api/generated/action.md), and the [Kube API](../api/generated/kube.md).
