# Install and upgrade releases

Use `HelmSharp.Action` when a component owns a deployment, not just its YAML. `HelmClient.UpgradeInstallAsync` combines chart loading, values, rendering, hooks, Kubernetes apply, optional readiness waiting, and release-history persistence.

```powershell
dotnet add package HelmSharp.Action --version 1.3.1
```

## Start with a dry run

Give `HelmClient` an `IHelmOptionsProvider` owned by your application. It is where you centralize defaults such as namespace, field manager, Kubernetes version, API versions, and timeout rather than letting each caller invent them.

```csharp
var request = new HelmUpgradeInstallRequest
{
    ReleaseName = "demo",
    Namespace = "default",
    Chart = chartPath,
    ValuesFiles = ["values.production.yaml"],
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true
};

var result = await client.UpgradeInstallAsync(request, cancellationToken);

if (!result.Succeeded)
{
    logger.LogWarning("Release preview failed: {Error}", result.StandardError);
    return;
}

Console.WriteLine(result.StandardOutput);
```

A dry run renders and validates the request without applying resources or creating a release revision. Make the preview, values inputs, and result part of the approval record in your own product.

## Apply the approved request

After an explicit approval, issue the same request with `DryRun = false`. Do not silently change values, chart version, target namespace, or capability inputs between preview and apply. `HelmUpgradeInstallRequest` is mutable, so either create a new request from the approved data or flip the flag only when the request instance is not shared.

```csharp
request.DryRun = false;
var applyResult = await client.UpgradeInstallAsync(request, cancellationToken);

if (!applyResult.Succeeded)
    throw new InvalidOperationException(applyResult.StandardError);
```

## Set lifecycle behavior deliberately

| Setting | Meaning |
| --- | --- |
| `Install = false` | A missing release is an error; use this for upgrade-only endpoints. |
| `ReuseValues = true` | Start from the stored release values, then overlay this request's values. |
| `ResetValues = true` | Start from chart defaults. It cannot be combined with `ReuseValues`. |
| `Wait = true` | Wait for supported resource readiness after apply. |
| `WaitForJobs = true` | Also wait for Jobs; it requires `Wait` or `Atomic`. |
| `TimeoutSeconds` | One limit for applying resources, hooks, readiness waiting, and cancellation. |
| `Atomic = true` | Wait and recover on failure. |
| `DisableHooks = true` | Do not execute chart hooks. |
| `MaxHistory` | Retain at most this many stored revisions; `0` means no limit. |

HelmSharp stores successful, superseded, failed, and retained-uninstall revisions in Kubernetes Secrets. A default uninstall purges release history; a retained uninstall records an `uninstalled` revision. Use `StatusAsync`, `HistoryAsync`, `GetManifestAsync`, `GetValuesAsync`, and the revision-specific inspection methods to read what was actually stored; inspection does not re-render the current chart.

## Hooks and readiness are part of the operation

Hooks run in weight and then name order. Job and Pod hooks are observed for completion within the timeout; other hook kinds are applied without a completion observer. The supported cleanup policies are `before-hook-creation`, `hook-succeeded`, and `hook-failed`.

The built-in readiness waiter covers common workload resources. A CRD can be applied, but its domain-specific readiness is not inferred. Add a product-specific health check when a deployment is not ready merely because Kubernetes accepted the object.

## Permissions and error handling

The Kubernetes identity needs permission for the rendered resource kinds, namespaces, CRDs where used, hooks, and the release Secret records. High-level operations return `CommandResult`; inspect `Succeeded`, `ExitCode`, `StandardOutput`, and `StandardError` before reporting a result to users. [Troubleshoot failures](error-handling.md) covers the two failure models and the diagnostic context worth retaining.
