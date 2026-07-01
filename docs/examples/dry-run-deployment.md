# Dry-run Deployment

## What problem this solves

Products that deploy to Kubernetes should separate preview from apply. HelmSharp lets you run the same high-level release workflow with `DryRun = true`, then submit the release only after approval.

## Packages to install

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

## Minimal complete code

```csharp
var dryRun = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "payments",
    Namespace = "apps",
    Chart = "/charts/payments",
    ValuesFiles = ["values.production.yaml"],
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true
}, cancellationToken);

if (dryRun.ExitCode != 0)
    return Results.BadRequest(dryRun.StandardError);

// Later, after explicit user approval:
var apply = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = "payments",
    Namespace = "apps",
    Chart = "/charts/payments",
    ValuesFiles = ["values.production.yaml"],
    Wait = true,
    WaitForJobs = true,
    TimeoutSeconds = 300,
    DryRun = false
}, cancellationToken);
```

## Why these APIs

`UpgradeInstallAsync` is the same entry point for install and upgrade. Keeping request construction similar between preview and apply reduces drift between what users review and what the product deploys.

## Production notes

- Persist an approval record that includes chart version, values inputs, release name, namespace, and dry-run output hash.
- Re-render immediately before apply if chart contents or values can change.
- Treat `DryRun = false` as the only point where the request may mutate a cluster.

## Next step

Read [Release Workflows](../guide/release-workflows.md) for lifecycle behavior.
