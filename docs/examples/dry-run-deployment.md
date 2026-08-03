# Turn a review into a deployment

Use one release request for the review and another, equivalent request for the approved apply. Before rendering the review, resolve the current release state from an application-owned release store so the preview has the same install/upgrade state and next revision as the apply. After approval, only `DryRun` changes; chart identity, values, namespace, release state, and the options-provider configuration stay fixed.

```csharp
var currentRelease = await releases.GetLatestAsync(
    releaseName,
    targetNamespace,
    cancellationToken);

var preview = new HelmUpgradeInstallRequest
{
    ReleaseName = releaseName,
    Namespace = targetNamespace,
    Chart = approvedChartPath,
    ValuesFiles = approvedValuesFiles,
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true,
    DryRunIsUpgrade = currentRelease is not null,
    DryRunRevision = currentRelease is null ? 1 : currentRelease.Revision + 1
};

var previewResult = await client.UpgradeInstallAsync(preview, cancellationToken);
if (!previewResult.Succeeded)
    return Results.BadRequest("The release preview failed.");

await approvals.SaveAsync(preview, previewResult.StandardOutput, cancellationToken);
```

When the approval service has verified that record, rebuild the request from the persisted fields and apply it:

```csharp
var apply = await approvals.CreateApprovedRequestAsync(approvalId, cancellationToken);
apply.DryRun = false;

var result = await client.UpgradeInstallAsync(apply, cancellationToken);
if (!result.Succeeded)
    return Results.Problem("The approved release could not be applied.", statusCode: 409);

return Results.Ok(new { result.StandardOutput });
```

Do not trust a browser to send a second copy of the values or chart version. The service that applies must read the reviewed record itself. Persist the preview's resolved release state with the approval and reject it if the release state has changed before apply; otherwise re-render and require a new approval. Keep `CommandResult.StandardError`, exit code, and operation ID in a restricted operation record; return a generic failure to untrusted callers.

`Wait`, `WaitForJobs`, `Atomic`, and `TimeoutSeconds` determine the operation's completion contract. See [Install and upgrade releases](../guide/release-workflows.md) before selecting them.
