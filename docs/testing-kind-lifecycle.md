# Kind lifecycle test lane

The kind lifecycle lane proves Kubernetes apply, wait, hook, rollback, uninstall, and release-Secret behavior against a real cluster and real controllers. It is separate from the fast unit, renderer-golden, package-contract, and documentation checks.

## What the lane covers

| Case | What is asserted |
| --- | --- |
| Install through uninstall | Install, status/history, upgrade, rollback, and purge against one fixture chart |
| Namespace and identity | Namespace creation, namespaced resource identity, and cluster-scoped CRDs without a namespace |
| Update and CRD discovery | Chart-owned fields change on upgrade; custom resources are applied through cluster discovery |
| Readiness and hooks | Deployment readiness, hook Job completion, and controlled hook Job failure |
| Rollback after failure | A failed upgrade records a failed revision; rollback restores the previous resources |
| Retained uninstall | `KeepHistory` retains release Secrets; purge removes them |
| Wait timeout | A pull-failing workload records a failed revision instead of leaving an ambiguous state |
| Release Secret contract | Helm v3 `sh.helm.release.v1.<name>.v<rev>` Secrets use the expected type, labels, and payload key |

Helm CLI may be used only as a test oracle. It is never an SDK runtime dependency.

## How maintainers request the lane

1. Push to `master` — the lane runs automatically.
2. Manually dispatch **Kind Lifecycle** from the Actions tab.
3. On a pull request, add the `kind-lifecycle` label. The job runs on the next synchronize or labeled event.

Promote `Kind lifecycle integration` to a required status check in branch protection once the lane is stable across a few releases.

## Run locally

Requirements:

- Docker
- [kind](https://kind.sigs.k8s.io/)
- .NET 8 SDK or newer
- A disposable cluster (do not point this at production)

```powershell
kind create cluster --name helmsharp-kind
$env:HELM_SHARP_KIND_TESTS = "1"
dotnet test tests/HelmSharp.Tests/HelmSharp.Tests.csproj --configuration Release --filter "Category=Kind"
kind delete cluster --name helmsharp-kind
```

Tests skip with a clear message when `HELM_SHARP_KIND_TESTS` is not `1`, so the default suite stays cluster-independent.

## Failure artifacts

On failure the workflow uploads `kind-lifecycle-diagnostics` containing:

- `kubectl get` / `describe` output for workloads, events, and pod logs
- Helm release Secrets (labels and payloads from the disposable kind cluster)
- CRD inventory
- the TRX result file

Artifacts never include kubeconfig contents or registry credentials. The kind cluster is destroyed with the runner.

## Bounds and reruns

- The job has a 25-minute timeout.
- Each test uses a uniquely named release and namespace and cleans up best-effort after itself.
- The lane is safe to re-run; leftover objects from an interrupted run die with the cluster.
